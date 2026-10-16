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
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
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
    public async Task DeleteDistributionAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Scoped: the distribution must be this tenant's before any Secret is removed from a
        // cluster. It used to be found by id alone.
        CertificateDistribution? dist = await db.CertificateDistributions
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (dist is null) return;

        try
        {
            foreach ((Clusters.IClusterClient cluster, IReadOnlyList<string> namespaces) in
                     await ResolveTargetsAsync(db, tenantId, dist, ct))
            {
                foreach (string ns in namespaces)
                {
                    // The seam raises the acknowledgment for its own delete, so this no longer
                    // builds a PlannedClusterChange — which is what it needed a kubeconfig for.
                    await cluster.DeleteManifestAsync("secret", dist.TargetSecretName, ns, ct);
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
            CertificateBundle? bundle = await vault.GetCertificateBundleByIdAsync(tenantId, s.Id, ct);
            CertificateInfo? info = bundle is null ? null : CertificateParser.TryParse(bundle.Certificate);
            result.Add(new DistributableCertificate(s.Id, s.Name, bundle?.HasPrivateKey ?? false, info));
        }
        return result;
    }

    // ── Apply / reconcile ───────────────────────────────────────────────────────

    /// <summary>Mirrors one distribution to its cluster (gated). Returns per-run output.</summary>
    public async Task<CertificateDistributionResult> ApplyDistributionAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CertificateDistribution? dist = await db.CertificateDistributions
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (dist is null) return new(false, 0, "Certificate distribution not found.");
        return await ApplyCoreAsync(db, tenantId, dist, ct);
    }

    /// <summary>
    /// Re-applies every distribution across all tenants/clusters. Used by the background reconciler,
    /// where the cluster-change gate has no interactive sink and therefore does not block.
    /// </summary>
    /// <summary>
    /// One tenant's certificate distributions. Public and static so the scoping is assertable
    /// without a cluster; see <c>ReconcileSweepTenancyTests</c>.
    /// </summary>
    public static IQueryable<CertificateDistribution> DistributionsFor(ApplicationDbContext db, Guid tenantId)
        => db.CertificateDistributions.Where(d => d.TenantId == tenantId);

    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // One tenant at a time, outermost (docs/decomposition.md §4.0.1.2). This writes a Secret —
        // a certificate and often its private key — into namespaces on a cluster, so which tenant
        // it acts as is the whole question.
        //
        // ⚠️ The previous version of this loop claimed to be that and was not. It selected every
        // distribution in the installation, took the tenant off each row, and then re-read the row
        // with `d.Id == distributionId && d.TenantId == tenantId` — a predicate comparing a row to
        // itself, which can never fail. It preserved the (tenant, distribution) pair, which is why
        // it looked converted; the tenant was still the row's own answer. The tenant is an input
        // now, so a distribution can only be applied as its owner.
        foreach (Guid tenantId in await db.Tenants.Select(x => x.Id).ToListAsync(ct))
        {
            if (ct.IsCancellationRequested) break;

            List<CertificateDistribution> distributions = await DistributionsFor(db, tenantId).ToListAsync(ct);

            foreach (CertificateDistribution dist in distributions)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    CertificateDistributionResult r = await ApplyCoreAsync(db, tenantId, dist, ct);
                    if (!r.Success)
                        logger.LogWarning("Reconcile of cert distribution {Name} failed: {Output}", dist.Name, r.Output);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Reconcile of cert distribution {Name} threw", dist.Name);
                }
            }
        }
    }

    private async Task<CertificateDistributionResult> ApplyCoreAsync(
        ApplicationDbContext db, Guid tenantId, CertificateDistribution dist, CancellationToken ct)
    {
        CertificateBundle? bundle = await vault.GetCertificateBundleByIdAsync(tenantId, dist.VaultSecretId, ct);
        if (bundle is null || !bundle.HasCertificate)
            return new(false, 0, "Source certificate could not be loaded from the vault.");

        if (dist.IncludeKey && !bundle.HasPrivateKey)
            return new(false, 0, "This distribution is configured to include the private key, but the selected certificate has no key. Add a key or switch to certificate-only.");

        List<(Clusters.IClusterClient Cluster, IReadOnlyList<string> Namespaces)> groups =
            await ResolveTargetsAsync(db, tenantId, dist, ct);
        if (groups.Count == 0)
            return new(false, 0, "No target namespaces resolved for this distribution (no matching apps/namespaces, or clusters have no kubeconfig).");

        int total = 0;
        bool allOk = true;
        List<string> outputs = [];
        foreach ((Clusters.IClusterClient cluster, IReadOnlyList<string> namespaces) in groups)
        {
            string manifest = BuildSecretManifest(dist, bundle, namespaces);

            // The seam supplies the credential and the cluster's label; this caller still raises
            // the acknowledgment itself, because it applies through kubectl and wants one dialog
            // for the whole namespace set rather than one per Secret.
            await cluster.AcknowledgeAsync(new PlannedChange
            {
                Verb = ChangeVerb.Apply,
                Summary = $"Distribute certificate {dist.TargetSecretName} to {namespaces.Count} namespace(s) on {cluster.ClusterName}",
                Manifest = manifest,
            }, ct);

            (bool ok, string output) = await ApplyThroughClusterAsync(cluster, manifest, ct);
            allOk &= ok;
            total += namespaces.Count;
            if (!string.IsNullOrWhiteSpace(output))
                outputs.Add($"{cluster.ClusterName}: {output}");

            if (ok)
                logger.LogInformation("Certificate {Secret} distributed to {Count} namespaces on {Cluster}",
                    dist.TargetSecretName, namespaces.Count, cluster.ClusterName);
            else
                logger.LogWarning("Certificate distribution {Secret} failed on {Cluster}: {Output}",
                    dist.TargetSecretName, cluster.ClusterName, output);
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
    private async Task<List<(Clusters.IClusterClient Cluster, IReadOnlyList<string> Namespaces)>> ResolveTargetsAsync(
        ApplicationDbContext db, Guid tenantId, CertificateDistribution dist, CancellationToken ct)
    {
        // Returns seam clients rather than cluster ROWS. The rows carried a decrypted kubeconfig
        // into every caller — including the namespace listing, which only needed to talk to the
        // cluster — and the tenant each was then checked against came off the row itself.
        List<(Clusters.IClusterClient, IReadOnlyList<string>)> groups = [];

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
                Clusters.IClusterClient? cluster = await clusterAccess.ForAsync(tenantId, byCluster.Key, ct);
                if (cluster is null) continue;

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
        // Scoped by the ASKING tenant, not by dist.TenantId — a pinned cluster belonging to
        // someone else is now refused by the seam rather than written to.
        List<Guid> clusterIds = dist.ClusterId is not null
            ? [dist.ClusterId.Value]
            : await db.KubernetesClusters.Where(c => c.TenantId == tenantId).Select(c => c.Id).ToListAsync(ct);

        foreach (Guid clusterId in clusterIds)
        {
            Clusters.IClusterClient? cluster = await clusterAccess.ForAsync(tenantId, clusterId, ct);
            if (cluster is null) continue;

            IReadOnlyList<string> namespaces = dist.Scope switch
            {
                TrustDistributionScope.TenantNamespaces =>
                    await TrustBundleService.ResolveTenantNamespacesAsync(db, tenantId, clusterId, ct),
                TrustDistributionScope.AllNamespaces =>
                    (await ListClusterNamespacesAsync(cluster, selector: null, ct))
                        .Where(ns => !SystemNamespaces.Contains(ns)).ToList(),
                TrustDistributionScope.MatchLabels =>
                    await ResolveLabelNamespacesAsync(dist, cluster, ct),
                _ => [],
            };
            if (namespaces.Count > 0) groups.Add((cluster, namespaces));
        }
        return groups;
    }

    private static async Task<IReadOnlyList<string>> ResolveLabelNamespacesAsync(
        CertificateDistribution dist, Clusters.IClusterClient cluster, CancellationToken ct)
    {
        Dictionary<string, string> labels = TrustBundleService.ParseSelector(dist.NamespaceSelectorJson);
        if (labels.Count == 0) return [];
        string selector = string.Join(",", labels.Select(kv => $"{kv.Key}={kv.Value}"));
        return await ListClusterNamespacesAsync(cluster, selector, ct);
    }

    /// <summary>
    /// The cluster's namespace names, optionally by label.
    ///
    /// <para>Through the seam, which needed an empty namespace to mean "cluster-scoped" before
    /// this was possible — Namespaces are not in a namespace. The names come out of JSON now
    /// rather than <c>-o name</c> lines, which is the same list without a prefix to strip.</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> ListClusterNamespacesAsync(
        Clusters.IClusterClient cluster, string? selector, CancellationToken ct)
    {
        try
        {
            string json = await cluster.GetJsonAsync("namespaces", ns: "", selector ?? "", ct);

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items)
                || items.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }

            return [.. items
                .EnumerateArray()
                .Select(i => i.TryGetProperty("metadata", out System.Text.Json.JsonElement md)
                    && md.TryGetProperty("name", out System.Text.Json.JsonElement n)
                        ? n.GetString() : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)];
        }
        catch (Exception)
        {
            // A cluster that cannot be listed contributes no namespaces, which is what the
            // kubectl version did by returning (ok: false) and an empty list.
            return [];
        }
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

    /// <summary>
    /// Applies a manifest through the cluster client, keeping the <c>(ok, output)</c> shape this
    /// service reports with.
    ///
    /// <para>This replaced a local copy of kubectl plumbing — write the manifest to a temp file,
    /// write the kubeconfig next to it, spawn a process, read both streams, delete both files.
    /// The only reason it existed is that <c>ApplyManifestAsync</c> used to discard kubectl's
    /// output, and this service logs it. Now that the factory returns it, the copy is redundant
    /// and the credential stops passing through here.</para>
    /// </summary>
    private async Task<(bool Ok, string Output)> ApplyThroughClusterAsync(
        Clusters.IClusterClient target, string manifest, CancellationToken ct)
    {
        try
        {
            return (true, await target.ApplyManifestAsync(manifest, ct));
        }
        catch (Exception ex)
        {
            // kubectl failure arrives as an exception from the factory; this service has always
            // reported it as a value, and its callers aggregate across clusters rather than
            // abandoning the sweep at the first bad one.
            return (false, ex.Message);
        }
    }
}
