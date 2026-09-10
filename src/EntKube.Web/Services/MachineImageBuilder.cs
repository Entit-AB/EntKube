namespace EntKube.Web.Services;

/// <summary>What to bake, and on what.</summary>
public sealed class MachineImageBuildRequest
{
    public required Guid TenantId { get; init; }
    public required Guid OpenStackConnectionId { get; init; }

    /// <summary>Kubernetes version the image is for, e.g. "v1.31.4".</summary>
    public required string KubernetesVersion { get; init; }

    /// <summary>An Ubuntu cloud image already in Glance — the base the bake starts from.</summary>
    public required string BaseImageName { get; init; }

    /// <summary>Flavor for the temporary build VM. Modest is fine; this machine only runs apt.</summary>
    public required string BuildFlavor { get; init; }

    /// <summary>Tenant network the build VM attaches to. Needs outbound internet to fetch packages.</summary>
    public required string NetworkId { get; init; }

    /// <summary>External network the build VM's floating IP comes from.</summary>
    public required string ExternalNetworkId { get; init; }

    /// <summary>Default login user of the base image. Ubuntu cloud images use "ubuntu".</summary>
    public string SshUser { get; init; } = "ubuntu";
}

public sealed record MachineImageBuildResult(string ImageId, string ImageName, string KubernetesVersion);

/// <summary>
/// Bakes the node image every cluster — and every bootstrap cluster — boots from: a temporary VM,
/// a scripted install of a pinned Kubernetes and its container runtime, generalize, snapshot,
/// throw the VM away.
///
/// <para><b>Why EntKube builds this at all.</b> Cluster API's model is "boot this image, run this
/// cloud-init", so an image with kubeadm already on it is not optional; the only question is who
/// makes it. Installing at first boot instead would mean every node depends on package
/// repositories being reachable and unchanged — slower, unrepeatable, and impossible in a network
/// that cannot reach them. Baking once gives identical nodes, a version that can be asked for by
/// name, and an upgrade that is "build the new image, roll the machines".</para>
///
/// <para>Every OpenStack resource here is temporary and released in <c>finally</c>, including when
/// the bake fails — a half-built image is worth nothing, but a leaked VM bills forever.</para>
/// </summary>
public class MachineImageBuilder(
    OpenStackKeystoneClient keystone,
    OpenStackComputeService compute,
    OpenStackDiscoveryService discovery,
    CommandRunner runner,
    ILogger<MachineImageBuilder> logger)
{
    public async Task<MachineImageBuildResult> BuildAsync(
        MachineImageBuildRequest request, Action<string> log, CancellationToken ct = default)
    {
        string version = MachineImageNaming.Normalize(request.KubernetesVersion);
        DateTime builtAt = DateTime.UtcNow;
        string imageName = MachineImageNaming.ImageName(version, builtAt);

        string workDir = Path.Combine(Path.GetTempPath(), $"entkube-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        KeystoneSession session = await keystone.AuthenticateAsync(request.TenantId, request.OpenStackConnectionId, ct);
        BootstrapVm? vm = null;

        try
        {
            SshKeyPair key = SshKeyFactory.Create("entkube-image-builder");
            string keyPath = Path.Combine(workDir, "id_rsa");
            await SshKeyFactory.WritePrivateKeyFileAsync(keyPath, key.PrivateKeyPem, ct);

            log($"Baking a Kubernetes {version} node image from '{request.BaseImageName}'.");

            (string fipId, string fipAddress) =
                await compute.AllocateFloatingIpAsync(session, request.ExternalNetworkId, ct);

            vm = await compute.CreateEphemeralVmAsync(
                session,
                name: $"entkube-imagebuild-{builtAt:yyyyMMddHHmm}",
                imageName: request.BaseImageName,
                flavor: request.BuildFlavor,
                networkId: request.NetworkId,
                sshPublicKey: key.PublicKeyOpenSsh,
                cloudInitUserData: NodeImageRecipe.BuildCloudInit(version),
                // Only SSH: this machine never serves anything.
                ingressPorts: [22],
                floatingIpId: fipId,
                floatingIpAddress: fipAddress,
                ct: ct);

            log($"Build VM up at {vm.FloatingIp}; installing containerd and Kubernetes {version}…");

            await WaitForBakeAsync(request.SshUser, vm.FloatingIp, keyPath, workDir, log, ct);
            log("Install finished. Stripping instance identity before the snapshot…");

            await RunSshAsync(
                request.SshUser, vm.FloatingIp, keyPath, workDir,
                NodeImageRecipe.GeneralizeScript(request.SshUser), log, ct);

            log("Stopping the build VM and snapshotting it…");
            await compute.StopServerAsync(session, vm.ServerId, ct);

            string imageId = await compute.SnapshotServerAsync(session, vm.ServerId, imageName, ct);

            await compute.SetImagePropertiesAsync(
                session, imageId, MachineImageNaming.Properties(version, request.BaseImageName, builtAt), ct);

            log($"Image '{imageName}' ({imageId}) is ready for Kubernetes {version}.");
            logger.LogInformation("Built node image {ImageName} ({ImageId}) for {Version}", imageName, imageId, version);

            return new MachineImageBuildResult(imageId, imageName, version);
        }
        finally
        {
            if (vm is not null)
            {
                // The snapshot is taken; the machine it came from has no further purpose, and a
                // leaked build VM bills for as long as nobody notices it.
                await compute.DeleteBootstrapVmAsync(session, vm, CancellationToken.None);
            }
            TryDelete(workDir);
        }
    }

    /// <summary>
    /// The newest usable image for a version, building one if there is none. This is what the
    /// provisioning path calls: asking for a Kubernetes version and getting an image back is the
    /// whole point of tagging them.
    /// </summary>
    public async Task<OpenStackImage> EnsureImageAsync(
        MachineImageBuildRequest request, Action<string> log, CancellationToken ct = default)
    {
        KeystoneSession session = await keystone.AuthenticateAsync(request.TenantId, request.OpenStackConnectionId, ct);
        IReadOnlyList<OpenStackImage> images = await discovery.ListImagesAsync(session, ct);

        string version = MachineImageNaming.Normalize(request.KubernetesVersion);
        OpenStackImage? existing = images
            .Where(i => i.IsUsable && i.KubernetesVersion == version)
            .MaxBy(i => i.BuiltAt ?? DateTime.MinValue);

        if (existing is not null)
        {
            log($"Using existing node image '{existing.Name}' for Kubernetes {version}.");
            return existing;
        }

        MachineImageBuildResult built = await BuildAsync(request, log, ct);
        return new OpenStackImage(built.ImageId, built.ImageName, "active", version, DateTime.UtcNow);
    }

    /// <summary>
    /// Waits for the bake to finish, distinguishing "still working" from "failed". Without the
    /// failure marker a broken install is indistinguishable from a slow one until the timeout, and
    /// by then the console log has scrolled.
    /// </summary>
    private async Task WaitForBakeAsync(
        string sshUser, string floatingIp, string keyPath, string workDir, Action<string> log, CancellationToken ct)
    {
        string probe =
            $"test -f {NodeImageRecipe.ReadyMarker} && echo READY; " +
            $"test -f {NodeImageRecipe.FailedMarker} && echo FAILED; true";

        // apt plus a control-plane image pull is comfortably ten minutes on a small flavor.
        for (int attempt = 0; attempt < 80; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            CliResult r = await runner.RunAsync(
                "ssh", $"{CommandRunner.SshOptions(keyPath)} {sshUser}@{floatingIp} \"{probe}\"",
                workDir, new(), _ => { }, ct, timeout: TimeSpan.FromSeconds(30), quiet: true);

            if (r.Success && r.Stdout.Contains("READY", StringComparison.Ordinal))
            {
                return;
            }

            if (r.Success && r.Stdout.Contains("FAILED", StringComparison.Ordinal))
            {
                string tail = await ReadBuildLogTailAsync(sshUser, floatingIp, keyPath, workDir, ct);
                throw new InvalidOperationException(
                    "The node image build failed on the build VM. Last lines of /var/log/entkube-bake.log:\n" + tail);
            }

            if (attempt % 8 == 0 && attempt > 0)
            {
                log($"Still installing on the build VM… ({attempt / 4} minutes)");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }

        throw new TimeoutException("The node image build did not finish within 20 minutes.");
    }

    private async Task<string> ReadBuildLogTailAsync(
        string sshUser, string floatingIp, string keyPath, string workDir, CancellationToken ct)
    {
        CliResult r = await runner.RunAsync(
            "ssh", $"{CommandRunner.SshOptions(keyPath)} {sshUser}@{floatingIp} \"tail -n 40 /var/log/entkube-bake.log\"",
            workDir, new(), _ => { }, ct, timeout: TimeSpan.FromSeconds(30), quiet: true);

        return r.Success && r.Stdout.Length > 0 ? r.Stdout : "(the build log could not be read)";
    }

    private async Task RunSshAsync(
        string sshUser, string floatingIp, string keyPath, string workDir,
        string script, Action<string> log, CancellationToken ct)
    {
        // Fed over stdin rather than as an argument, so quoting in the script cannot break the
        // command line — the generalize step is full of globs and paths.
        string scriptPath = Path.Combine(workDir, "remote.sh");
        await File.WriteAllTextAsync(scriptPath, script, ct);

        CliResult r = await runner.RunAsync(
            "/bin/sh",
            $"-c \"ssh {CommandRunner.SshOptions(keyPath)} {sshUser}@{floatingIp} sudo bash -s < {scriptPath}\"",
            workDir, new(), log, ct, timeout: TimeSpan.FromMinutes(5));

        if (!r.Success)
        {
            throw new InvalidOperationException($"Remote script failed on the build VM: {r.Stderr.Trim()}");
        }
    }

    private void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not clean image build work dir {Dir}", dir); }
    }
}
