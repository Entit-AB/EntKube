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
///   2. Boot a throwaway k3s VM (cloud-init) as the bootstrap management cluster.
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

            // ── 2. Application credential + clouds.yaml (idempotent) ──
            string cloudsYaml = await EnsureCloudsYamlAsync(tenantId, clusterId, connection, session, config, ct);
            Log("Application credential + clouds.yaml ready.");

            // ── 3. SSH keypair (idempotent) ──
            (string sshPrivateKey, string sshPublicKey) = await EnsureSshKeyAsync(tenantId, clusterId, ct);
            string keyPath = Path.Combine(workDir, "id_rsa");
            await WritePrivateKeyFileAsync(keyPath, sshPrivateKey, ct);

            // ── 4. Bootstrap VM (resume if already created) ──
            bootstrapVm = await LoadBootstrapStateAsync(clusterId, ct);
            if (bootstrapVm is null)
            {
                Log("Allocating floating IP + booting ephemeral k3s bootstrap VM…");
                (string fipId, string fipAddr) = await compute.AllocateFloatingIpAsync(session, config.ExternalNetworkId, ct);
                string cloudInit = BuildK3sCloudInit(fipAddr);
                bootstrapVm = await compute.CreateBootstrapVmAsync(session, config, sshPublicKey, cloudInit, fipId, fipAddr, ct);
                await SaveBootstrapStateAsync(clusterId, bootstrapVm, ct);
                Log($"Bootstrap VM active at {bootstrapVm.FloatingIp}.");
            }
            else
            {
                Log($"Re-attached to existing bootstrap VM at {bootstrapVm.FloatingIp}.");
            }

            // ── 5. Fetch the k3s kubeconfig over SSH ──
            string bootstrapKubeconfigPath = Path.Combine(workDir, "bootstrap.kubeconfig");
            await FetchK3sKubeconfigAsync(config.BootstrapSshUser, bootstrapVm.FloatingIp, keyPath, bootstrapKubeconfigPath, Log, ct);
            Log("Bootstrap cluster reachable.");

            // ── 6. clusterctl init CAPO on the bootstrap cluster ──
            await RunAsync("clusterctl", "init --infrastructure openstack", workDir,
                EnvFor(bootstrapKubeconfigPath), Log, ct, timeout: TimeSpan.FromMinutes(10));

            // ── 7. Generate + apply the target Cluster ──
            string clusterYamlPath = Path.Combine(workDir, "cluster.yaml");
            Dictionary<string, string> capiEnv = EnvFor(bootstrapKubeconfigPath);
            foreach ((string k, string v) in CapiTemplateInputs.BuildEnv(config, cloudsYaml)) capiEnv[k] = v;

            string generateArgs =
                $"generate cluster {config.ClusterName} " +
                $"--kubernetes-version {config.KubernetesVersion} " +
                $"--control-plane-machine-count {config.ControlPlaneCount} " +
                $"--worker-machine-count {config.TotalWorkerCount}";
            CliResult generated = await RunAsync("clusterctl", generateArgs, workDir, capiEnv, Log, ct);
            if (!generated.Success) throw new InvalidOperationException("clusterctl generate cluster failed — see log.");
            await File.WriteAllTextAsync(clusterYamlPath, generated.Stdout, ct);

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

        using RSA rsa = RSA.Create(3072);
        string privatePem = rsa.ExportPkcs8PrivateKeyPem();
        string publicOpenSsh = ToOpenSshPublicKey(rsa, "entkube-bootstrap");

        await vaultService.SetClusterSecretAsync(tenantId, clusterId, SshPrivateKeySecret, privatePem, ct);
        await vaultService.SetClusterSecretAsync(tenantId, clusterId, SshPublicKeySecret, publicOpenSsh, ct);
        return (privatePem, publicOpenSsh);
    }

    private static async Task WritePrivateKeyFileAsync(string path, string pem, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, pem, ct);
        // ssh refuses world-readable private keys.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ──────── Bootstrap VM cloud-init & SSH ────────

    private static string BuildK3sCloudInit(string floatingIp) =>
        // Single-node k3s; traefik/servicelb disabled (unused for a management cluster);
        // the floating IP is added as a TLS SAN so our fetched kubeconfig validates.
        $"""
        #cloud-config
        runcmd:
          - curl -sfL https://get.k3s.io | INSTALL_K3S_EXEC="--disable traefik --disable servicelb --write-kubeconfig-mode 644 --tls-san {floatingIp}" sh -
        """;

    private async Task FetchK3sKubeconfigAsync(
        string sshUser, string floatingIp, string keyPath, string outPath, Action<string> log, CancellationToken ct)
    {
        string sshArgs =
            $"-i {keyPath} -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null " +
            $"-o ConnectTimeout=15 -o BatchMode=yes {sshUser}@{floatingIp} sudo cat /etc/rancher/k3s/k3s.yaml";

        // k3s + cloud-init take a while; poll until the kubeconfig is available.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            CliResult r = await RunAsync("ssh", sshArgs, Path.GetDirectoryName(keyPath)!, new(), _ => { }, ct,
                timeout: TimeSpan.FromSeconds(30), quiet: true);

            if (r.Success && r.Stdout.Contains("apiVersion", StringComparison.Ordinal))
            {
                // k3s writes the kubeconfig with server https://127.0.0.1:6443 — point it at the floating IP.
                string rewritten = r.Stdout
                    .Replace("127.0.0.1", floatingIp)
                    .Replace("localhost", floatingIp);
                await File.WriteAllTextAsync(outPath, rewritten, ct);
                return;
            }

            if (attempt % 5 == 0) log($"Waiting for k3s on the bootstrap VM… (attempt {attempt + 1})");
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        throw new TimeoutException("Bootstrap k3s cluster did not become reachable over SSH within 10 minutes.");
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

    private sealed record CliResult(bool Success, int ExitCode, string Stdout, string Stderr);

    private static Dictionary<string, string> EnvFor(string kubeconfigPath) => new() { ["KUBECONFIG"] = kubeconfigPath };

    private async Task<CliResult> RunAsync(
        string program, string arguments, string workDir, Dictionary<string, string> env,
        Action<string> log, CancellationToken ct, TimeSpan? timeout = null, bool quiet = false)
    {
        ProcessStartInfo psi = new()
        {
            FileName = program,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["HOME"] = workDir;
        foreach ((string k, string v) in env) psi.EnvironmentVariables[k] = v;

        using Process process = new() { StartInfo = psi };
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

        try
        {
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (!quiet)
            {
                string tail = (stdout.Trim() + "\n" + stderr.Trim()).Trim();
                if (tail.Length > 0) log($"$ {program} {Redact(arguments)}\n{tail}");
                else log($"$ {program} {Redact(arguments)}");
            }

            return new CliResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"'{program} {Redact(arguments)}' timed out.");
        }
    }

    private static string Redact(string arguments) =>
        // Arguments here never carry secrets (creds go via files/env), but keep tokens out of logs defensively.
        arguments.Length > 400 ? arguments[..400] + "…" : arguments;

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

    /// <summary>Encodes an RSA public key in OpenSSH authorized_keys ("ssh-rsa AAAA… comment") format.</summary>
    private static string ToOpenSshPublicKey(RSA rsa, string comment)
    {
        RSAParameters p = rsa.ExportParameters(false);
        using MemoryStream ms = new();

        void WriteBytes(byte[] b)
        {
            Span<byte> len = stackalloc byte[4];
            len[0] = (byte)(b.Length >> 24);
            len[1] = (byte)(b.Length >> 16);
            len[2] = (byte)(b.Length >> 8);
            len[3] = (byte)b.Length;
            ms.Write(len);
            ms.Write(b);
        }

        static byte[] ToMpint(byte[] b)
        {
            // SSH mpint: prepend a zero byte if the MSB is set, to keep it non-negative.
            if (b.Length > 0 && (b[0] & 0x80) != 0)
            {
                byte[] padded = new byte[b.Length + 1];
                Array.Copy(b, 0, padded, 1, b.Length);
                return padded;
            }
            return b;
        }

        WriteBytes(Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteBytes(ToMpint(p.Exponent!));
        WriteBytes(ToMpint(p.Modulus!));

        return $"ssh-rsa {Convert.ToBase64String(ms.ToArray())} {comment}";
    }

    private void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to clean provisioning work dir {Dir}", dir); }
    }
}
