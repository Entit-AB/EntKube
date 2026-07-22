using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services.ClusterChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Services;

/// <summary>A tenant certificate that can be chosen as the source of a distribution.</summary>
public sealed record DistributableCertificate(Guid SecretId, string Name, bool HasPrivateKey, CertificateInfo? Info);

/// <summary>Outcome of mirroring a certificate distribution to one cluster.</summary>
public sealed record CertificateDistributionResult(bool Success, int NamespaceCount, string Output);

/// <summary>A customer app that can be chosen as an app-scoped distribution target.</summary>
public sealed record TargetableApp(Guid Id, string Name, string CustomerName);

/// <summary>A tenant environment, for optionally narrowing an app-scoped distribution.</summary>
public sealed record TargetEnvironment(Guid Id, string Name);

/// <summary>
/// Mirrors a vault certificate into a Kubernetes Secret in every selected namespace on a cluster.
/// trust-manager can only carry public CA material, so this is the second distribution mechanism:
/// a cert+key becomes a <c>kubernetes.io/tls</c> Secret; a cert-only distribution becomes an
/// Opaque Secret with just the public certificate. All mutations go through the cluster-change gate;
/// a background reconciler re-applies periodically to reach new namespaces and pick up renewals.
/// </summary>
public class CertificateDistributionService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    IClusterChangeGate gate,
    ILogger<CertificateDistributionService> logger)
{
    // Namespaces never targeted by an "all namespaces" distribution.
    private static readonly HashSet<string> SystemNamespaces =
        new(StringComparer.OrdinalIgnoreCase) { "kube-system", "kube-public", "kube-node-lease" };

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<List<CertificateDistribution>> GetDistributionsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.CertificateDistributions
            .Where(d => d.TenantId == tenantId && d.ClusterId == clusterId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
    }

    public async Task<CertificateDistribution?> GetDistributionAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.CertificateDistributions.FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    /// <summary>Lists every delivery target configured for one certificate (across app and tenant-wide scopes).</summary>
    public async Task<List<CertificateDistribution>> GetDistributionsForCertAsync(
        Guid tenantId, Guid vaultSecretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.CertificateDistributions
            .Where(d => d.TenantId == tenantId && d.VaultSecretId == vaultSecretId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
    }

    public async Task<CertificateDistribution> CreateDistributionAsync(
        CertificateDistribution dist, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        dist.Id = dist.Id == Guid.Empty ? Guid.NewGuid() : dist.Id;
        dist.TargetSecretName = TrustBundleService.SanitizeName(dist.TargetSecretName, "entkube-cert");
        dist.CreatedAt = dist.UpdatedAt = DateTime.UtcNow;
        db.CertificateDistributions.Add(dist);
        await db.SaveChangesAsync(ct);
        return dist;
    }

    public async Task<CertificateDistribution> UpdateDistributionAsync(
        Guid id, Action<CertificateDistribution> mutate, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CertificateDistribution dist = await db.CertificateDistributions.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Certificate distribution {id} not found.");
        mutate(dist);
        dist.TargetSecretName = TrustBundleService.SanitizeName(dist.TargetSecretName, "entkube-cert");
        dist.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return dist;
    }

    /// <summary>Removes the distribution from the DB and best-effort deletes the mirrored Secrets.</summary>
    public async Task DeleteDistributionAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CertificateDistribution? dist = await db.CertificateDistributions.FindAsync([id], ct);
        if (dist is null) return;

        try
        {
            foreach ((KubernetesCluster cluster, IReadOnlyList<string> namespaces) in await ResolveTargetsAsync(db, dist, ct))
            {
                foreach (string ns in namespaces)
                {
                    await gate.AcknowledgeAsync(new PlannedClusterChange
                    {
                        Verb = ChangeVerb.Delete,
                        Kubeconfig = cluster.Kubeconfig!,
                        ClusterLabel = cluster.Name,
                        Kind = "secret",
                        Name = dist.TargetSecretName,
                        Namespace = ns,
                        Summary = $"Delete distributed cert secret {dist.TargetSecretName} in {ns}",
                    }, ct);
                    await RunKubectlAsync(
                        $"delete secret {dist.TargetSecretName} -n {ns} --ignore-not-found",
                        cluster.Kubeconfig!, ct);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete distributed secrets for {Dist}", dist.Name);
        }

        db.CertificateDistributions.Remove(dist);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lists the tenant's apps that have deployments (candidate targets for an app-scoped distribution).</summary>
    public async Task<List<TargetableApp>> ListTargetableAppsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Apps
            .Where(a => a.Customer.TenantId == tenantId)
            .OrderBy(a => a.Customer.Name).ThenBy(a => a.Name)
            .Select(a => new TargetableApp(a.Id, a.Name, a.Customer.Name))
            .ToListAsync(ct);
    }

    /// <summary>Lists the tenant's environments (for optionally narrowing an app-scoped distribution).</summary>
    public async Task<List<TargetEnvironment>> ListEnvironmentsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Environments
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Name)
            .Select(e => new TargetEnvironment(e.Id, e.Name))
            .ToListAsync(ct);
    }

    /// <summary>Lists the tenant's certificate secrets that can be chosen as a distribution source.</summary>
    public async Task<List<DistributableCertificate>> ListDistributableCertificatesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<VaultSecret> certs = await db.VaultSecrets
            .Include(s => s.Vault)
            .Where(s => s.Vault.TenantId == tenantId && s.SecretType == VaultSecretType.Certificate)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        List<DistributableCertificate> result = [];
        foreach (VaultSecret s in certs)
        {
            CertificateBundle? bundle = await vault.GetCertificateBundleByIdAsync(s.Id, ct);
            CertificateInfo? info = bundle is null ? null : CertificateParser.TryParse(bundle.Certificate);
            result.Add(new DistributableCertificate(s.Id, s.Name, bundle?.HasPrivateKey ?? false, info));
        }
        return result;
    }

    // ── Apply / reconcile ───────────────────────────────────────────────────────

    /// <summary>Mirrors one distribution to its cluster (gated). Returns per-run output.</summary>
    public async Task<CertificateDistributionResult> ApplyDistributionAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CertificateDistribution? dist = await db.CertificateDistributions.FindAsync([id], ct);
        if (dist is null) return new(false, 0, "Certificate distribution not found.");
        return await ApplyCoreAsync(db, dist, ct);
    }

    /// <summary>
    /// Re-applies every distribution across all tenants/clusters. Used by the background reconciler,
    /// where the cluster-change gate has no interactive sink and therefore does not block.
    /// </summary>
    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<CertificateDistribution> all = await db.CertificateDistributions.ToListAsync(ct);
        foreach (CertificateDistribution dist in all)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                CertificateDistributionResult r = await ApplyCoreAsync(db, dist, ct);
                if (!r.Success)
                    logger.LogWarning("Reconcile of cert distribution {Name} failed: {Output}", dist.Name, r.Output);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reconcile of cert distribution {Name} threw", dist.Name);
            }
        }
    }

    private async Task<CertificateDistributionResult> ApplyCoreAsync(
        ApplicationDbContext db, CertificateDistribution dist, CancellationToken ct)
    {
        CertificateBundle? bundle = await vault.GetCertificateBundleByIdAsync(dist.VaultSecretId, ct);
        if (bundle is null || !bundle.HasCertificate)
            return new(false, 0, "Source certificate could not be loaded from the vault.");

        if (dist.IncludeKey && !bundle.HasPrivateKey)
            return new(false, 0, "This distribution is configured to include the private key, but the selected certificate has no key. Add a key or switch to certificate-only.");

        List<(KubernetesCluster Cluster, IReadOnlyList<string> Namespaces)> groups =
            await ResolveTargetsAsync(db, dist, ct);
        if (groups.Count == 0)
            return new(false, 0, "No target namespaces resolved for this distribution (no matching apps/namespaces, or clusters have no kubeconfig).");

        int total = 0;
        bool allOk = true;
        List<string> outputs = [];
        foreach ((KubernetesCluster cluster, IReadOnlyList<string> namespaces) in groups)
        {
            string manifest = BuildSecretManifest(dist, bundle, namespaces);

            await gate.AcknowledgeAsync(new PlannedClusterChange
            {
                Verb = ChangeVerb.Apply,
                Kubeconfig = cluster.Kubeconfig!,
                ClusterLabel = cluster.Name,
                Summary = $"Distribute certificate {dist.TargetSecretName} to {namespaces.Count} namespace(s) on {cluster.Name}",
                Manifest = manifest,
            }, ct);

            (bool ok, string output) = await RunKubectlApplyAsync(manifest, cluster.Kubeconfig!, ct);
            allOk &= ok;
            total += namespaces.Count;
            if (!string.IsNullOrWhiteSpace(output))
                outputs.Add($"{cluster.Name}: {output}");

            if (ok)
                logger.LogInformation("Certificate {Secret} distributed to {Count} namespaces on {Cluster}",
                    dist.TargetSecretName, namespaces.Count, cluster.Name);
            else
                logger.LogWarning("Certificate distribution {Secret} failed on {Cluster}: {Output}",
                    dist.TargetSecretName, cluster.Name, output);
        }

        if (allOk)
        {
            dist.LastSyncedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new(allOk, total, string.Join("\n", outputs));
    }

    // ── Target resolution ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the (cluster, namespaces) groups a distribution reaches. App scope reads the app's
    /// deployments; the other scopes span the pinned cluster or, when ClusterId is null, every cluster
    /// the tenant owns (tenant-wide). Clusters without a kubeconfig are skipped.
    /// </summary>
    private async Task<List<(KubernetesCluster Cluster, IReadOnlyList<string> Namespaces)>> ResolveTargetsAsync(
        ApplicationDbContext db, CertificateDistribution dist, CancellationToken ct)
    {
        List<(KubernetesCluster, IReadOnlyList<string>)> groups = [];

        if (dist.Scope == TrustDistributionScope.App)
        {
            if (dist.AppId is null) return groups;

            List<AppDeployment> deployments = await db.AppDeployments
                .Where(d => d.AppId == dist.AppId
                    && (dist.EnvironmentId == null || d.EnvironmentId == dist.EnvironmentId)
                    && (dist.ClusterId == null || d.ClusterId == dist.ClusterId))
                .ToListAsync(ct);

            Dictionary<Guid, string?> lockedNs = (await db.AppEnvironments
                .Where(ae => ae.AppId == dist.AppId)
                .Select(ae => new { ae.EnvironmentId, ae.Namespace })
                .ToListAsync(ct))
                .GroupBy(x => x.EnvironmentId)
                .ToDictionary(g => g.Key, g => g.First().Namespace);

            foreach (IGrouping<Guid, AppDeployment> byCluster in deployments.GroupBy(d => d.ClusterId))
            {
                KubernetesCluster? cluster = await db.KubernetesClusters.FindAsync([byCluster.Key], ct);
                if (cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig)) continue;

                List<string> namespaces = byCluster
                    .Select(d => lockedNs.TryGetValue(d.EnvironmentId, out string? locked) && !string.IsNullOrWhiteSpace(locked)
                        ? locked! : d.Namespace)
                    .Where(ns => !string.IsNullOrWhiteSpace(ns))
                    .Distinct()
                    .ToList();
                if (namespaces.Count > 0) groups.Add((cluster, namespaces));
            }
            return groups;
        }

        // All / Tenant / Labels: a pinned cluster, or every cluster in the tenant when unset.
        List<KubernetesCluster> clusters = dist.ClusterId is not null
            ? await db.KubernetesClusters.Where(c => c.Id == dist.ClusterId).ToListAsync(ct)
            : await db.KubernetesClusters.Where(c => c.TenantId == dist.TenantId).ToListAsync(ct);

        foreach (KubernetesCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig)) continue;

            IReadOnlyList<string> namespaces = dist.Scope switch
            {
                TrustDistributionScope.TenantNamespaces =>
                    await TrustBundleService.ResolveTenantNamespacesAsync(db, dist.TenantId, cluster.Id, ct),
                TrustDistributionScope.AllNamespaces =>
                    (await ListClusterNamespacesAsync(cluster.Kubeconfig!, selector: null, ct))
                        .Where(ns => !SystemNamespaces.Contains(ns)).ToList(),
                TrustDistributionScope.MatchLabels =>
                    await ResolveLabelNamespacesAsync(dist, cluster.Kubeconfig!, ct),
                _ => [],
            };
            if (namespaces.Count > 0) groups.Add((cluster, namespaces));
        }
        return groups;
    }

    private static async Task<IReadOnlyList<string>> ResolveLabelNamespacesAsync(
        CertificateDistribution dist, string kubeconfig, CancellationToken ct)
    {
        Dictionary<string, string> labels = TrustBundleService.ParseSelector(dist.NamespaceSelectorJson);
        if (labels.Count == 0) return [];
        string selector = string.Join(",", labels.Select(kv => $"{kv.Key}={kv.Value}"));
        return await ListClusterNamespacesAsync(kubeconfig, selector, ct);
    }

    private static async Task<IReadOnlyList<string>> ListClusterNamespacesAsync(
        string kubeconfig, string? selector, CancellationToken ct)
    {
        string args = "get namespaces -o name" + (string.IsNullOrWhiteSpace(selector) ? "" : $" -l {selector}");
        (bool ok, string output) = await RunKubectlAsync(args, kubeconfig, ct);
        if (!ok) return [];
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.StartsWith("namespace/", StringComparison.Ordinal) ? l["namespace/".Length..] : l)
            .Where(l => l.Length > 0)
            .ToList();
    }

    // ── Secret manifest builder ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a multi-document manifest with one Secret per namespace. A cert+key distribution
    /// produces <c>kubernetes.io/tls</c> Secrets; a cert-only distribution produces Opaque Secrets
    /// carrying just the public certificate (no key ever leaves the app in cert-only mode).
    /// </summary>
    public static string BuildSecretManifest(
        CertificateDistribution dist, CertificateBundle bundle, IReadOnlyList<string> namespaces)
    {
        bool withKey = dist.IncludeKey && bundle.HasPrivateKey;
        string name = TrustBundleService.SanitizeName(dist.TargetSecretName, "entkube-cert");

        // Assemble the data keys once — they are identical across namespaces.
        List<(string Key, string Value)> data = [];
        data.Add(("tls.crt", bundle.CombinedCertificateChain));
        if (withKey) data.Add(("tls.key", bundle.PrivateKey!));
        if (bundle.HasCaCertificate) data.Add(("ca.crt", bundle.CaCertificate!));
        if (bundle.HasChain || bundle.HasCaCertificate) data.Add(("fullchain.crt", bundle.FullChain));

        string type = withKey ? "kubernetes.io/tls" : "Opaque";

        List<string> docs = [];
        foreach (string ns in namespaces)
        {
            StringBuilder sb = new();
            sb.AppendLine("apiVersion: v1");
            sb.AppendLine("kind: Secret");
            sb.AppendLine("metadata:");
            sb.AppendLine($"  name: {name}");
            sb.AppendLine($"  namespace: {ns}");
            sb.AppendLine("  labels:");
            sb.AppendLine($"    {VaultService.ManagedByLabelKey}: {VaultService.ManagedByLabelValue}");
            sb.AppendLine("    entkube.io/managed: \"true\"");
            sb.AppendLine($"type: {type}");
            sb.AppendLine("data:");
            foreach ((string key, string value) in data)
                sb.AppendLine($"  {key}: {Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}");
            docs.Add(sb.ToString().TrimEnd());
        }
        return string.Join("\n---\n", docs) + "\n";
    }

    // ── kubectl plumbing ─────────────────────────────────────────────────────────

    private static async Task<(bool, string)> RunKubectlApplyAsync(string manifest, string kubeconfig, CancellationToken ct)
    {
        string manifestPath = Path.Combine(Path.GetTempPath(), $"entkube-certdist-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(manifestPath, manifest, ct);
            return await RunKubectlAsync($"apply -f {manifestPath}", kubeconfig, ct, extraTemp: manifestPath);
        }
        finally
        {
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
    }

    private static async Task<(bool, string)> RunKubectlAsync(
        string arguments, string kubeconfig, CancellationToken ct, string? extraTemp = null)
    {
        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-certdist-{Guid.NewGuid():N}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            System.Diagnostics.ProcessStartInfo psi = new("kubectl", $"{arguments} --kubeconfig {kubeconfigPath}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["HOME"] = "/tmp";

            using System.Diagnostics.Process proc = new() { StartInfo = psi };
            StringBuilder output = new();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            return (proc.ExitCode == 0, output.ToString().TrimEnd());
        }
        finally
        {
            if (File.Exists(kubeconfigPath)) File.Delete(kubeconfigPath);
            if (extraTemp is not null && File.Exists(extraTemp)) File.Delete(extraTemp);
        }
    }
}
