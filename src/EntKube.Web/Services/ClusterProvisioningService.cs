using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>Outcome of a provisioning attempt, with the accumulated log for the step output.</summary>
public sealed class ProvisioningResult
{
    public bool Success { get; init; }
    public string Log { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// Provisions a Kubernetes cluster on OpenStack using Cluster API + the OpenStack
/// provider (CAPO), from nothing but the tenant's OpenStack credentials.
///
/// Strategy — ephemeral bootstrap + pivot (no permanent management cluster):
///   1. Mint a scoped application credential + an SSH keypair (persisted to the vault).
///   2. Boot a throwaway single-node kubeadm VM (cloud-init) as the bootstrap management cluster.
///   3. clusterctl init CAPO on it, generate + apply the target Cluster manifests.
///   4. Once the target API is reachable, install a CNI so its controllers schedule,
///      then clusterctl init + move the CAPI state INTO the target (self-managed).
///   5. Register the target kubeconfig in the vault and record its nodes.
///   6. Destroy the bootstrap VM.
///
/// Resumable: the bootstrap VM identifiers are checkpointed on the cluster row, so a
/// retried run re-attaches to the in-flight VM rather than creating a second one.
///
/// Requires the <c>clusterctl</c>, <c>kubectl</c> and <c>ssh</c> binaries on the host
/// (alongside the <c>helm</c> the component installer already shells out to).
/// </summary>
public class ClusterProvisioningService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    OpenStackKeystoneClient keystone,
    OpenStackComputeService compute,
    MachineImageBuilder imageBuilder,
    OpenStackDiscoveryService discovery,
    CommandRunner runner,
    ILogger<ClusterProvisioningService> logger)
{
    // Cluster-scoped vault secret names for provisioning artifacts.
    private const string AppCredIdSecret = "openstack-app-credential-id";
    private const string AppCredSecretSecret = "openstack-app-credential-secret";
    private const string CloudsYamlSecret = "openstack-clouds-yaml";
    private const string SshPrivateKeySecret = "bootstrap-ssh-private-key";
    private const string SshPublicKeySecret = "bootstrap-ssh-public-key";

    // A single, reliable single-file CNI manifest to unblock the pivot (the richer
    // CNI catalog component manages it for day-2 once the cluster is registered).
    private const string CalicoManifestUrl =
        "https://raw.githubusercontent.com/projectcalico/calico/v3.28.2/manifests/calico.yaml";

    /// <summary>
    /// Runs the full provision → pivot → register → cleanup sequence for the placeholder
    /// <paramref name="clusterId"/>. Progress is streamed via <paramref name="onProgress"/>.
    /// </summary>
    public async Task<ProvisioningResult> ProvisionAsync(
        Guid tenantId, Guid clusterId, OpenStackProvisioningConfig config,
        Action<string> onProgress, CancellationToken ct = default)
    {
        StringBuilder log = new();
        void Log(string msg)
        {
            logger.LogInformation("Provision[{Cluster}]: {Msg}", clusterId, msg);
            log.AppendLine($"[{DateTime.UtcNow:HH:mm:ss}] {msg}");
            onProgress(log.ToString());
        }

        IReadOnlyList<string> configErrors = config.Validate();
        if (configErrors.Count > 0)
        {
            string joined = string.Join("; ", configErrors);
            return new ProvisioningResult { Success = false, Log = joined, Error = joined };
        }

        string workDir = Path.Combine(Path.GetTempPath(), "entkube-provision", clusterId.ToString("N"));
        Directory.CreateDirectory(workDir);

        BootstrapVm? bootstrapVm = null;
        KeystoneSession? session = null;

        try
        {
            await SetStatusAsync(clusterId, ClusterProvisioningStatus.Provisioning, ct);

            // ── 1. Authenticate ──
            OpenStackConnection connection = await LoadConnectionAsync(tenantId, config.OpenStackConnectionId, ct);
            string? password = await vaultService.GetOpenStackSecretValueAsync(tenantId, connection.Id, "OS_PASSWORD", ct)
                ?? throw new InvalidOperationException("OpenStack password not found in vault for this connection.");
            session = await keystone.AuthenticateAsync(connection, password, ct);
            Log($"Authenticated to OpenStack project {connection.ProjectName ?? connection.ProjectId}.");

            // What this cloud can do decides how the API server is reached. Asked once, here,
            // rather than assumed by a template that only knows one answer.
            OpenStackInventory inventory = await discovery.DiscoverAsync(session, ct);
            OpenStackCapabilities capabilities = inventory.Capabilities;
            if (!capabilities.HasOctavia)
            {
                Log("This cloud advertises no load-balancer service, so the API server will ride a "
                    + "floating IP rather than an Octavia load balancer.");
            }

            // A Kubernetes version is what the operator asked for; an image is what boots. Build
            // one if this cloud has none for that version yet.
            if (string.IsNullOrWhiteSpace(config.NodeImageName))
            {
                OpenStackImage image = await imageBuilder.EnsureImageAsync(new MachineImageBuildRequest
                {
                    TenantId = tenantId,
                    OpenStackConnectionId = config.OpenStackConnectionId,
                    KubernetesVersion = config.KubernetesVersion,
                    BaseImageName = config.BaseImageName,
                    BuildFlavor = config.BootstrapFlavor,
                    NetworkId = config.BootstrapNetworkId,
                    ExternalNetworkId = config.ExternalNetworkId,
                    SshUser = config.BootstrapSshUser
                }, Log, ct);

                config.NodeImageName = image.Name;
            }

            // ── 2. Application credential + clouds.yaml (idempotent) ──
            string cloudsYaml = await EnsureCloudsYamlAsync(tenantId, clusterId, connection, session, config, ct);
            Log("Application credential + clouds.yaml ready.");

            // ── 3. SSH keypair (idempotent) ──
            (string sshPrivateKey, string sshPublicKey) = await EnsureSshKeyAsync(tenantId, clusterId, ct);
            string keyPath = Path.Combine(workDir, "id_rsa");
            await SshKeyFactory.WritePrivateKeyFileAsync(keyPath, sshPrivateKey, ct);

            // ── 4. Bootstrap VM (resume if already created) ──
            bootstrapVm = await LoadBootstrapStateAsync(clusterId, ct);
            if (bootstrapVm is null)
            {
                Log("Allocating floating IP + booting the ephemeral kubeadm bootstrap VM…");
                (string fipId, string fipAddr) = await compute.AllocateFloatingIpAsync(session, config.ExternalNetworkId, ct);
                // The bootstrap node boots the same image the cluster's own nodes do — it needs
                // exactly the same things, and baking them once is what lets it come up without
                // fetching a distribution from the internet.
                string cloudInit = NodeImageRecipe.BootstrapCloudInit(
                    config.KubernetesVersion, fipAddr, config.PodCidr, CalicoManifestUrl);
                bootstrapVm = await compute.CreateBootstrapVmAsync(session, config, sshPublicKey, cloudInit, fipId, fipAddr, ct);
                await SaveBootstrapStateAsync(clusterId, bootstrapVm, ct);
                Log($"Bootstrap VM active at {bootstrapVm.FloatingIp}.");
            }
            else
            {
                Log($"Re-attached to existing bootstrap VM at {bootstrapVm.FloatingIp}.");
            }

            // ── 5. Wait for kubeadm, then fetch the bootstrap kubeconfig over SSH ──
            string bootstrapKubeconfigPath = Path.Combine(workDir, "bootstrap.kubeconfig");
            await FetchBootstrapKubeconfigAsync(config.BootstrapSshUser, bootstrapVm.FloatingIp, keyPath, bootstrapKubeconfigPath, Log, ct);
            Log("Bootstrap cluster reachable.");

            // ── 6. clusterctl init CAPO on the bootstrap cluster ──
            await RunAsync("clusterctl", "init --infrastructure openstack", workDir,
                EnvFor(bootstrapKubeconfigPath), Log, ct, timeout: TimeSpan.FromMinutes(10));

            // ── 7. Author + apply the target Cluster ──
            //
            // Authored here rather than by `clusterctl generate cluster`, which emits exactly one
            // MachineDeployment — every worker pool then collapsed into one of the wrong flavor.
            // It also fetches its templates over the network, which a closed customer network
            // cannot do.
            string clusterYamlPath = Path.Combine(workDir, "cluster.yaml");

            ClusterManifestInputs manifestInputs = new()
            {
                NodeImageName = config.NodeImageName,
                CloudSecretName = CapiTemplateInputs.CloudSecretName,
                CloudName = CapiTemplateInputs.CloudName,
                ApiEndpoint = capabilities.ApiEndpoint,
                ControlPlaneDiskGb = config.ControlPlaneDiskGb
            };

            await File.WriteAllTextAsync(clusterYamlPath, CapiManifestBuilder.Build(config, manifestInputs), ct);
            Log($"Authored manifests for {config.ControlPlaneCount} control-plane node(s) and "
                + $"{config.WorkerPools.Count} worker pool(s): "
                + string.Join(", ", config.WorkerPools.Select(pl => $"{pl.Name}×{pl.Count} ({pl.Flavor})")));

            // CAPO authenticates as the cluster's own application credential, from a secret the
            // identityRef in those manifests points at.
            await ApplyCloudIdentitySecretAsync(cloudsYaml, bootstrapKubeconfigPath, workDir, Log, ct);

            await RunAsync("kubectl", $"apply -f {clusterYamlPath}", workDir, EnvFor(bootstrapKubeconfigPath), Log, ct);
            Log("Target Cluster manifests applied; waiting for the control plane to come up…");

            // ── 8. Wait for the target kubeconfig, then pivot ──
            string targetKubeconfigPath = Path.Combine(workDir, "target.kubeconfig");
            await WaitForTargetKubeconfigAsync(config.ClusterName, bootstrapKubeconfigPath, targetKubeconfigPath, workDir, Log, ct);
            Log("Target control plane is serving; installing CNI so its controllers can schedule…");

            await RunAsync("kubectl", $"apply -f {CalicoManifestUrl}", workDir, EnvFor(targetKubeconfigPath), Log, ct,
                timeout: TimeSpan.FromMinutes(5));

            // Write the cloud-config Secret the CCM + Cinder CSI component steps consume.
            await WriteCloudConfigSecretAsync(tenantId, clusterId, connection, config, targetKubeconfigPath, workDir, Log, ct);
            Log("Wrote cloud-config secret (kube-system) for cloud-controller-manager / Cinder CSI.");

            // Pivot: init CAPO on the target, then move CAPI state into it (self-managed).
            await RunAsync("clusterctl", "init --infrastructure openstack", workDir, EnvFor(targetKubeconfigPath), Log, ct,
                timeout: TimeSpan.FromMinutes(10));
            await RunAsync("clusterctl", $"move --to-kubeconfig {targetKubeconfigPath}", workDir, EnvFor(bootstrapKubeconfigPath), Log, ct,
                timeout: TimeSpan.FromMinutes(10));
            Log("CAPI state pivoted into the target cluster (self-managed).");

            // ── 9. Register the target cluster ──
            string targetKubeconfig = await File.ReadAllTextAsync(targetKubeconfigPath, ct);
            string apiServerUrl = ExtractApiServer(targetKubeconfig) ?? $"https://{config.ClusterName}:6443";
            await RegisterProvisionedClusterAsync(tenantId, clusterId, apiServerUrl, targetKubeconfig, ct);
            Log($"Registered cluster kubeconfig (API server {apiServerUrl}).");

            // ── 10. Record nodes as ClusterServer inventory ──
            await RecordNodesAsync(clusterId, config, targetKubeconfigPath, workDir, Log, ct);

            // ── 11. Tear down the ephemeral bootstrap VM ──
            Log("Destroying ephemeral bootstrap VM…");
            await compute.DeleteBootstrapVmAsync(session, bootstrapVm, ct);
            await ClearBootstrapStateAsync(clusterId, ct);
            await SetStatusAsync(clusterId, ClusterProvisioningStatus.Provisioned, ct);
            Log("Provisioning complete.");

            return new ProvisioningResult { Success = true, Log = log.ToString() };
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            await SetStatusAsync(clusterId, ClusterProvisioningStatus.Failed, ct);
            // Intentionally leave the bootstrap VM in place on failure so a retry can
            // re-attach and resume; it is torn down only on success (or manual cleanup).
            return new ProvisioningResult { Success = false, Log = log.ToString(), Error = ex.Message };
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    // ──────── Credentials & keys ────────

    private async Task<string> EnsureCloudsYamlAsync(
        Guid tenantId, Guid clusterId, OpenStackConnection connection,
        KeystoneSession session, OpenStackProvisioningConfig config, CancellationToken ct)
    {
        string? existing = await vaultService.GetClusterSecretValueAsync(tenantId, clusterId, CloudsYamlSecret, ct);
        if (existing is not null) return existing;

        ApplicationCredential appCred = await keystone.CreateApplicationCredentialAsync(
            session, connection.AuthUrl, $"entkube-{config.ClusterName}", ct);
        string cloudsYaml = CapiTemplateInputs.BuildCloudsYaml(connection, appCred);

        await vaultService.SetClusterSecretAsync(tenantId, clusterId, AppCredIdSecret, appCred.Id, ct);
        await vaultService.SetClusterSecretAsync(tenantId, clusterId, AppCredSecretSecret, appCred.Secret, ct);
        await vaultService.SetClusterSecretAsync(tenantId, clusterId, CloudsYamlSecret, cloudsYaml, ct);
        return cloudsYaml;
    }

    private async Task<(string PrivateKey, string PublicKey)> EnsureSshKeyAsync(Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        string? priv = await vaultService.GetClusterSecretValueAsync(tenantId, clusterId, SshPrivateKeySecret, ct);
        string? pub = await vaultService.GetClusterSecretValueAsync(tenantId, clusterId, SshPublicKeySecret, ct);
        if (priv is not null && pub is not null) return (priv, pub);

        SshKeyPair generated = SshKeyFactory.Create("entkube-bootstrap");
        string privatePem = generated.PrivateKeyPem;
        string publicOpenSsh = generated.PublicKeyOpenSsh;

        await vaultService.SetClusterSecretAsync(tenantId, clusterId, SshPrivateKeySecret, privatePem, ct);
        await vaultService.SetClusterSecretAsync(tenantId, clusterId, SshPublicKeySecret, publicOpenSsh, ct);
        return (privatePem, publicOpenSsh);
    }

    /// <summary>
    /// Puts clouds.yaml on the management plane as the secret the OpenStackCluster's identityRef
    /// names. CAPO reads its credentials from there rather than from the environment the generator
    /// ran in, which is what lets the cluster keep reconciling itself after the pivot.
    /// </summary>
    private async Task ApplyCloudIdentitySecretAsync(
        string cloudsYaml, string kubeconfigPath, string workDir, Action<string> log, CancellationToken ct)
    {
        string cloudsPath = Path.Combine(workDir, "clouds.yaml");
        await File.WriteAllTextAsync(cloudsPath, cloudsYaml, ct);

        string secretPath = Path.Combine(workDir, "cloud-identity.yaml");
        CliResult rendered = await RunAsync(
            "kubectl",
            $"create secret generic {CapiTemplateInputs.CloudSecretName} "
            + $"--namespace {CapiManifestBuilder.Namespace} "
            + $"--from-file=clouds.yaml={cloudsPath} --dry-run=client -o yaml",
            workDir, EnvFor(kubeconfigPath), _ => { }, ct, quiet: true);

        if (!rendered.Success)
        {
            throw new InvalidOperationException("Could not render the CAPO cloud identity secret — see log.");
        }

        await File.WriteAllTextAsync(secretPath, rendered.Stdout, ct);
        await RunAsync("kubectl", $"apply -f {secretPath}", workDir, EnvFor(kubeconfigPath), log, ct);
    }

    // ──────── Bootstrap VM cloud-init & SSH ────────


    /// <summary>
    /// Waits for the bootstrap node to finish <c>kubeadm init</c>, untaint itself and bring up a
    /// CNI, then fetches its admin kubeconfig.
    ///
    /// <para>Three things differ from the k3s bootstrap this replaces, and each was a way to get a
    /// cluster that looks up and is not: kubeadm's kubeconfig is root-only so it comes through
    /// <c>sudo</c>; it names the node's private address, which is unreachable from here, so it is
    /// rewritten to the floating IP that the certificate was issued to cover; and the marker is
    /// only written after the CNI is up, because a control plane with no pod network accepts
    /// <c>clusterctl init</c> and then never schedules cert-manager.</para>
    /// </summary>
    private async Task FetchBootstrapKubeconfigAsync(
        string sshUser, string floatingIp, string keyPath, string outPath, Action<string> log, CancellationToken ct)
    {
        string ssh = $"{CommandRunner.SshOptions(keyPath)} {sshUser}@{floatingIp}";
        string workDir = Path.GetDirectoryName(keyPath)!;

        // kubeadm init plus image pulls plus the CNI going Ready: minutes, not seconds.
        for (int attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            CliResult ready = await runner.RunAsync(
                "ssh", $"{ssh} \"test -f {NodeImageRecipe.BootstrapReadyMarker} && echo READY\"",
                workDir, new(), _ => { }, ct, timeout: TimeSpan.FromSeconds(30), quiet: true);

            if (ready.Success && ready.Stdout.Contains("READY", StringComparison.Ordinal))
            {
                CliResult conf = await runner.RunAsync(
                    "ssh", $"{ssh} sudo cat {NodeImageRecipe.AdminConfPath}",
                    workDir, new(), _ => { }, ct, timeout: TimeSpan.FromSeconds(30), quiet: true);

                if (!conf.Success || !conf.Stdout.Contains("apiVersion", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The bootstrap node reported ready but {NodeImageRecipe.AdminConfPath} could not be read.");
                }

                await File.WriteAllTextAsync(outPath, RewriteServerAddress(conf.Stdout, floatingIp), ct);
                return;
            }

            if (attempt % 4 == 0 && attempt > 0)
            {
                log($"Waiting for kubeadm on the bootstrap VM… ({attempt / 4} minutes)");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }

        throw new TimeoutException(
            "The kubeadm bootstrap cluster did not become ready within 15 minutes. "
            + $"Check /var/log/entkube-bootstrap.log on {floatingIp}.");
    }

    /// <summary>
    /// Points a kubeconfig's server at an address we can actually reach. Only the server line is
    /// touched: a blanket string replace would corrupt any certificate data that happened to
    /// contain the same bytes.
    /// </summary>
    public static string RewriteServerAddress(string kubeconfig, string address)
    {
        string[] lines = kubeconfig.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("server:", StringComparison.Ordinal))
            {
                string indent = lines[i][..(lines[i].Length - trimmed.Length)];
                lines[i] = $"{indent}server: https://{address}:6443";
            }
        }
        return string.Join('\n', lines);
    }

    private async Task WaitForTargetKubeconfigAsync(
        string clusterName, string bootstrapKubeconfig, string outPath, string workDir, Action<string> log, CancellationToken ct)
    {
        // CAPI writes the workload kubeconfig secret once the control plane is initialized.
        for (int attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            CliResult r = await RunAsync("clusterctl", $"get kubeconfig {clusterName}", workDir,
                EnvFor(bootstrapKubeconfig), _ => { }, ct, timeout: TimeSpan.FromSeconds(30), quiet: true);

            if (r.Success && r.Stdout.Contains("apiVersion", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(outPath, r.Stdout, ct);

                // Confirm the target API actually answers before proceeding to the pivot.
                CliResult ping = await RunAsync("kubectl", "get --raw=/readyz", workDir, EnvFor(outPath), _ => { }, ct,
                    timeout: TimeSpan.FromSeconds(20), quiet: true);
                if (ping.Success) return;
            }

            if (attempt % 4 == 0) log($"Waiting for the target control plane… (attempt {attempt + 1})");
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
        throw new TimeoutException("Target control plane did not become ready within 20 minutes.");
    }

    // ──────── Registration & inventory ────────

    private async Task RegisterProvisionedClusterAsync(
        Guid tenantId, Guid clusterId, string apiServerUrl, string kubeconfig, CancellationToken ct)
    {
        (bool ok, string? error, _) = await vaultService.SetClusterKubeconfigAsync(
            tenantId, clusterId, new KubeconfigBundle
            {
                ConfigYaml = kubeconfig,
                ApiServerUrl = apiServerUrl,
                ContextName = null
            }, updatedBy: "provisioner", ct);
        if (!ok) throw new InvalidOperationException(error ?? "Failed to store the provisioned kubeconfig.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster cluster = await db.KubernetesClusters.FirstAsync(c => c.Id == clusterId, ct);
        cluster.ApiServerUrl = apiServerUrl;
        await db.SaveChangesAsync(ct);
    }

    private async Task WriteCloudConfigSecretAsync(
        Guid tenantId, Guid clusterId, OpenStackConnection connection, OpenStackProvisioningConfig config,
        string targetKubeconfigPath, string workDir, Action<string> log, CancellationToken ct)
    {
        string? credId = await vaultService.GetClusterSecretValueAsync(tenantId, clusterId, AppCredIdSecret, ct);
        string? credSecret = await vaultService.GetClusterSecretValueAsync(tenantId, clusterId, AppCredSecretSecret, ct);
        if (credId is null || credSecret is null)
        {
            log("cloud-config secret skipped: application credential not found in vault.");
            return;
        }

        ApplicationCredential appCred = new() { Id = credId, Name = "entkube", Secret = credSecret };
        string cloudConf = CapiTemplateInputs.BuildCloudConf(connection, appCred, config);
        string cloudConfPath = Path.Combine(workDir, "cloud.conf");
        await File.WriteAllTextAsync(cloudConfPath, cloudConf, ct);

        // Render the Secret then apply it, so this is idempotent across retries.
        string secretYamlPath = Path.Combine(workDir, "cloud-config-secret.yaml");
        CliResult rendered = await RunAsync(
            "kubectl",
            $"create secret generic cloud-config -n kube-system --from-file=cloud.conf={cloudConfPath} --dry-run=client -o yaml",
            workDir, EnvFor(targetKubeconfigPath), _ => { }, ct, quiet: true);
        if (!rendered.Success) throw new InvalidOperationException("Failed to render cloud-config secret — see log.");
        await File.WriteAllTextAsync(secretYamlPath, rendered.Stdout, ct);

        await RunAsync("kubectl", $"apply -f {secretYamlPath}", workDir, EnvFor(targetKubeconfigPath), _ => { }, ct);
    }

    private async Task RecordNodesAsync(
        Guid clusterId, OpenStackProvisioningConfig config, string targetKubeconfig, string workDir, Action<string> log, CancellationToken ct)
    {
        try
        {
            CliResult r = await RunAsync(
                "kubectl",
                "get nodes -o jsonpath={range .items[*]}{.metadata.name}{\"|\"}{.status.addresses[?(@.type==\"InternalIP\")].address}{\"\\n\"}{end}",
                workDir, EnvFor(targetKubeconfig), _ => { }, ct, timeout: TimeSpan.FromSeconds(30), quiet: true);
            if (!r.Success) { log("Node inventory skipped (kubectl get nodes failed)."); return; }

            using ApplicationDbContext db = dbFactory.CreateDbContext();
            foreach (string line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|', 2);
                string nodeName = parts[0].Trim();
                if (nodeName.Length == 0) continue;
                string? ip = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : null;

                bool exists = await db.Set<ClusterServer>().AnyAsync(s => s.ClusterId == clusterId && s.NodeName == nodeName, ct);
                if (exists) continue;

                db.Set<ClusterServer>().Add(new ClusterServer
                {
                    Id = Guid.NewGuid(),
                    ClusterId = clusterId,
                    NodeName = nodeName,
                    DisplayName = nodeName,
                    IpAddress = ip,
                    Provider = ServerProvider.CloudVm,
                    Location = config.FailureDomain,
                    SshUser = config.BootstrapSshUser
                });
            }
            await db.SaveChangesAsync(ct);
            log("Recorded node inventory.");
        }
        catch (Exception ex)
        {
            log($"Node inventory skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a provisioned cluster by summoning a management plane to do it.
    ///
    /// <para>A self-managed cluster cannot delete itself — the controllers that would tear down its
    /// OpenStack resources run on the machines being torn down, so the moment the first
    /// control-plane node goes there is nothing left to remove the load balancer, the floating IPs
    /// or the volumes. So the same ephemeral kubeadm VM the initial bootstrap uses is booted, CAPO
    /// installed on it, the cluster's CAPI state moved <em>out</em> to it, and the Cluster deleted
    /// there. CAPO then unwinds the cloud properly, and the VM is destroyed after.</para>
    ///
    /// <para>This is what having no permanent seed costs, and it is paid here — once, at deletion —
    /// rather than by every cluster keeping a management plane alive for the day it might be
    /// deleted.</para>
    /// </summary>
    public async Task DeleteProvisionedClusterAsync(
        Guid tenantId,
        ProvisionedCluster spec,
        KeystoneSession session,
        Action<string> log,
        CancellationToken ct = default)
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"entkube-teardown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        BootstrapVm? plane = null;

        try
        {
            string targetKubeconfig = await LoadClusterKubeconfigAsync(spec, ct)
                ?? throw new InvalidOperationException(
                    "This cluster has no stored kubeconfig, so its CAPI state cannot be moved out to be "
                    + "deleted. Delete with force to forget it here and clean the cloud up by hand.");

            string targetPath = Path.Combine(workDir, "target.kubeconfig");
            await File.WriteAllTextAsync(targetPath, targetKubeconfig, ct);

            string? cloudsYaml = await vaultService.GetClusterSecretValueAsync(
                tenantId, spec.KubernetesClusterId!.Value, CloudsYamlSecret, ct)
                ?? throw new InvalidOperationException(
                    "The cloud credentials this cluster was built with are no longer in the vault, so CAPO "
                    + "cannot be given what it needs to tear it down.");

            SshKeyPair key = SshKeyFactory.Create("entkube-teardown");
            string keyPath = Path.Combine(workDir, "id_rsa");
            await SshKeyFactory.WritePrivateKeyFileAsync(keyPath, key.PrivateKeyPem, ct);

            OpenStackProvisioningConfig config = ProvisionedClusterService.ToConfig(spec);

            log("Booting the ephemeral management plane…");
            (string fipId, string fipAddress) =
                await compute.AllocateFloatingIpAsync(session, config.ExternalNetworkId, ct);

            plane = await compute.CreateEphemeralVmAsync(
                session,
                name: $"{spec.Name}-teardown",
                imageName: config.EffectiveBootstrapImageName,
                flavor: config.BootstrapFlavor,
                networkId: config.BootstrapNetworkId,
                sshPublicKey: key.PublicKeyOpenSsh,
                cloudInitUserData: NodeImageRecipe.BootstrapCloudInit(
                    config.KubernetesVersion, fipAddress, config.PodCidr, CalicoManifestUrl),
                ingressPorts: [22, 6443],
                floatingIpId: fipId,
                floatingIpAddress: fipAddress,
                ct: ct);

            string planePath = Path.Combine(workDir, "plane.kubeconfig");
            await FetchBootstrapKubeconfigAsync(config.BootstrapSshUser, plane.FloatingIp, keyPath, planePath, log, ct);

            log("Installing CAPO on it…");
            await RunAsync("clusterctl", "init --infrastructure openstack", workDir, EnvFor(planePath), log, ct,
                timeout: TimeSpan.FromMinutes(10));

            await ApplyCloudIdentitySecretAsync(cloudsYaml, planePath, workDir, log, ct);

            // Out of the cluster being deleted, into the plane that will outlive it.
            log("Moving the cluster's Cluster API state out to the management plane…");
            await RunAsync("clusterctl", $"move --to-kubeconfig {planePath}", workDir, EnvFor(targetPath), log, ct,
                timeout: TimeSpan.FromMinutes(10));

            log("Deleting the cluster. CAPO now unwinds the machines, load balancer, ports and volumes…");
            await RunAsync("kubectl",
                $"delete cluster {spec.Name} --namespace {CapiManifestBuilder.Namespace} --wait=true --timeout=30m",
                workDir, EnvFor(planePath), log, ct, timeout: TimeSpan.FromMinutes(35));

            log("Cluster deleted.");
        }
        finally
        {
            if (plane is not null)
            {
                log("Destroying the ephemeral management plane.");
                await compute.DeleteBootstrapVmAsync(session, plane, CancellationToken.None);
            }
            TryDeleteDirectory(workDir);
        }
    }

    private async Task<string?> LoadClusterKubeconfigAsync(ProvisionedCluster spec, CancellationToken ct)
    {
        if (spec.KubernetesClusterId is not Guid clusterId)
        {
            return null;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KubernetesClusters
            .Where(c => c.Id == clusterId)
            .Select(c => c.Kubeconfig)
            .FirstOrDefaultAsync(ct);
    }

    // ──────── Cluster-row state helpers ────────

    private async Task<OpenStackConnection> LoadConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Set<OpenStackConnection>()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("OpenStack connection not found for this tenant.");
    }

    private async Task SetStatusAsync(Guid clusterId, ClusterProvisioningStatus status, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? c = await db.KubernetesClusters.FirstOrDefaultAsync(x => x.Id == clusterId, ct);
        if (c is null) return;
        c.ProvisioningStatus = status;
        await db.SaveChangesAsync(ct);
    }

    private async Task<BootstrapVm?> LoadBootstrapStateAsync(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        string? json = await db.KubernetesClusters.Where(c => c.Id == clusterId)
            .Select(c => c.ProvisioningStateJson).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<BootstrapVm>(json);
    }

    private async Task SaveBootstrapStateAsync(Guid clusterId, BootstrapVm vm, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster c = await db.KubernetesClusters.FirstAsync(x => x.Id == clusterId, ct);
        c.ProvisioningStateJson = JsonSerializer.Serialize(vm);
        await db.SaveChangesAsync(ct);
    }

    private async Task ClearBootstrapStateAsync(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster c = await db.KubernetesClusters.FirstAsync(x => x.Id == clusterId, ct);
        c.ProvisioningStateJson = null;
        await db.SaveChangesAsync(ct);
    }

    // ──────── CLI plumbing ────────

    private static Dictionary<string, string> EnvFor(string kubeconfigPath) => new() { ["KUBECONFIG"] = kubeconfigPath };

    /// <summary>
    /// Thin pass-through to the shared <see cref="CommandRunner"/>. Kept as a local name because
    /// this file drives external tools on nearly every line, and the indirection reads worse than
    /// the call does.
    /// </summary>
    private Task<CliResult> RunAsync(
        string program, string arguments, string workDir, Dictionary<string, string> env,
        Action<string> log, CancellationToken ct, TimeSpan? timeout = null, bool quiet = false)
        => runner.RunAsync(program, arguments, workDir, env, log, ct, timeout, quiet);

    // ──────── Small helpers ────────

    private static string? ExtractApiServer(string kubeconfig)
    {
        foreach (string line in kubeconfig.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
                return t["server:".Length..].Trim();
        }
        return null;
    }

    private void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to clean provisioning work dir {Dir}", dir); }
    }
}
