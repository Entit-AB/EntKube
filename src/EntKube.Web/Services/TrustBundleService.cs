using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Services;

/// <summary>
/// A CA source with its parsed validity metadata, for display in the CA &amp; Trust UI.
/// </summary>
public sealed record TrustSourceView(CaTrustBundleSource Source, CertificateInfo? Info);

/// <summary>
/// Manages CA trust bundles at tenant+cluster scope. Each bundle renders to a trust-manager
/// <c>Bundle</c> custom resource that distributes its assembled CA certificates (public certs
/// only) into a ConfigMap/Secret in every selected namespace. Provides CRUD, validity parsing,
/// Bundle manifest generation, and gated apply/removal against the cluster.
/// </summary>
public class TrustBundleService(
    IDbContextFactory<CatalogDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    EntKube.Contracts.Delivery.IDeliveryApi delivery,
    ILogger<TrustBundleService> logger)
{
    /// <summary>The trust-manager API group/version and label we stamp on managed Bundles.</summary>
    public const string BundleApiVersion = "trust.cert-manager.io/v1alpha1";

    /// <summary>
    /// True when trust-manager is recorded as installed on the cluster. Used to warn in the UI
    /// that trust bundles won't reconcile until the component is present. A DB check against the
    /// component inventory — cheap and does not touch the cluster.
    /// </summary>
    /// <param name="tenantId">Whose cluster.</param>
    public async Task<bool> IsTrustManagerAvailableAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents
            .AnyAsync(c => c.ClusterId == clusterId
                        && c.Cluster.TenantId == tenantId
                        && c.Status == ComponentStatus.Installed
                        && (c.Name == "trust-manager"
                            || c.HelmChartName == "trust-manager"
                            || c.ReleaseName == "trust-manager"), ct);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<List<CaTrustBundle>> GetBundlesAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();
        return await db.CaTrustBundles
            .Include(b => b.Sources)
            .Where(b => b.TenantId == tenantId && b.ClusterId == clusterId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose bundle. ⚠️ A CA trust bundle is distributed into namespaces as a Secret that
    /// workloads trust for outbound TLS. Its sources come back with it here.
    /// <c>DeleteBundleAsync</c> beside it was already scoped; the rest of this service was not.
    /// </param>
    public async Task<CaTrustBundle?> GetBundleAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();
        return await db.CaTrustBundles
            .Include(b => b.Sources)
            .FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct);
    }

    /// <param name="tenantId">
    /// Whose bundle. ⚠️ This takes a whole entity from the caller, so its <c>TenantId</c> and
    /// <c>ClusterId</c> are whatever was posted. Both are checked and the tenant is stamped from
    /// the parameter rather than trusted from the object.
    /// </param>
    public async Task<CaTrustBundle> CreateBundleAsync(
        Guid tenantId, CaTrustBundle bundle, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();

        if (!await db.KubernetesClusters.AnyAsync(
                c => c.Id == bundle.ClusterId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That cluster does not belong to this tenant.");
        }

        bundle.TenantId = tenantId;
        bundle.Id = bundle.Id == Guid.Empty ? Guid.NewGuid() : bundle.Id;
        bundle.TargetName = SanitizeName(bundle.TargetName, "entkube-trust-bundle");
        bundle.CreatedAt = bundle.UpdatedAt = DateTime.UtcNow;
        db.CaTrustBundles.Add(bundle);
        await db.SaveChangesAsync(ct);
        return bundle;
    }

    /// <param name="tenantId">
    /// Whose bundle. ⚠️ <paramref name="mutate"/> is an arbitrary action over the loaded entity,
    /// so it can rewrite any column — including <c>TenantId</c> and <c>ClusterId</c>. The row is
    /// loaded scoped, and both are pinned back afterwards: a caller may edit a bundle, not move
    /// it to another tenant or point it at another cluster.
    /// </param>
    public async Task<CaTrustBundle> UpdateBundleAsync(
        Guid tenantId, Guid id, Action<CaTrustBundle> mutate, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();
        CaTrustBundle bundle = await db.CaTrustBundles
            .FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException($"Trust bundle {id} not found.");

        Guid cluster = bundle.ClusterId;
        mutate(bundle);
        bundle.TenantId = tenantId;
        bundle.ClusterId = cluster;
        bundle.TargetName = SanitizeName(bundle.TargetName, "entkube-trust-bundle");
        bundle.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return bundle;
    }

    /// <summary>Removes the bundle from the DB and best-effort deletes the live Bundle CR from the cluster.</summary>
    public async Task DeleteBundleAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();

        // Scoped: the bundle must be this tenant's before anything is removed from a cluster.
        CaTrustBundle? bundle = await db.CaTrustBundles
            .FirstOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId, ct);
        if (bundle is null) return;

        Clusters.IClusterClient? target = await clusterAccess.ForAsync(tenantId, bundle.ClusterId, ct);
        if (target is not null)
        {
            try
            {
                // The Bundle CR is CLUSTER-scoped, so the namespace is empty — which the seam's
                // delete now reads as "omit the flag" rather than sending `-n `. That gap is why
                // this method used to run its own kubectl with its own copy of the credential.
                await target.DeleteManifestAsync(
                    "bundles.trust.cert-manager.io", bundle.TargetName, ns: "", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Operator cancelled the CR removal — leave the DB row so they can retry.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete Bundle CR {Name} from {Cluster}",
                    bundle.TargetName, target.ClusterName);
            }
        }

        db.CaTrustBundles.Remove(bundle);
        await db.SaveChangesAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose bundle. ⚠️ The outbound twin of <c>MtlsService.AddCertificateAsync</c> and just as
    /// sharp: a source added here is a CA that every workload the bundle is distributed to will
    /// trust for outbound TLS. With a bare bundle id, one tenant could add their own CA to
    /// another's trust bundle and then present a certificate those workloads accept.
    /// </param>
    public async Task<CaTrustBundleSource> AddSourceAsync(
        Guid tenantId, Guid bundleId, string name, string pem, CancellationToken ct = default)
    {
        if (CertificateParser.TryParse(pem) is null)
            throw new InvalidOperationException("The CA certificate is not valid PEM. Paste the full -----BEGIN CERTIFICATE----- block.");

        using CatalogDbContext db = dbFactory.CreateDbContext();

        if (!await db.CaTrustBundles.AnyAsync(b => b.Id == bundleId && b.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That trust bundle does not belong to this tenant.");
        }

        CaTrustBundleSource source = new()
        {
            Id = Guid.NewGuid(),
            BundleId = bundleId,
            Name = string.IsNullOrWhiteSpace(name) ? "CA certificate" : name.Trim(),
            Pem = pem.Trim(),
        };
        db.CaTrustBundleSources.Add(source);
        await TouchBundleAsync(db, bundleId, ct);
        await db.SaveChangesAsync(ct);
        return source;
    }

    /// <param name="tenantId">
    /// Whose source. <c>CaTrustBundleSource</c> has no <c>TenantId</c> — it hangs off a bundle,
    /// which has one — so the scope runs source → bundle. Removing a CA from a stranger's bundle
    /// breaks the outbound TLS their workloads depend on.
    /// </param>
    public async Task RemoveSourceAsync(
        Guid tenantId, Guid sourceId, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();
        CaTrustBundleSource? source = await db.CaTrustBundleSources
            .FirstOrDefaultAsync(s => s.Id == sourceId && s.Bundle.TenantId == tenantId, ct);
        if (source is null) return;
        Guid bundleId = source.BundleId;
        db.CaTrustBundleSources.Remove(source);
        await TouchBundleAsync(db, bundleId, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task TouchBundleAsync(CatalogDbContext db, Guid bundleId, CancellationToken ct)
    {
        CaTrustBundle? bundle = await db.CaTrustBundles.FindAsync([bundleId], ct);
        if (bundle is not null) bundle.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Parsed validity of each CA source for display (subject/expiry).</summary>
    public static IReadOnlyList<TrustSourceView> ViewSources(CaTrustBundle bundle) =>
        bundle.Sources
            .OrderBy(s => s.Name)
            .Select(s => new TrustSourceView(s, CertificateParser.TryParse(s.Pem)))
            .ToList();

    // ── Apply ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the bundle to a trust-manager Bundle CR and applies it to the cluster.
    /// trust-manager then continuously syncs the trust store into every selected namespace.
    /// </summary>
    public async Task<(bool Success, string Output)> ApplyBundleAsync(
        Guid tenantId, Guid bundleId, CancellationToken ct = default)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();
        CaTrustBundle? bundle = await db.CaTrustBundles
            .Include(b => b.Sources)
            .FirstOrDefaultAsync(b => b.Id == bundleId && b.TenantId == tenantId, ct);
        if (bundle is null) return (false, "Trust bundle not found.");

        Clusters.IClusterClient? target = await clusterAccess.ForAsync(tenantId, bundle.ClusterId, ct);
        if (target is null)
            return (false, "The target cluster is not this tenant's, or has no kubeconfig configured.");

        if (bundle.Sources.Count == 0 && !bundle.IncludeDefaultCAs)
            return (false, "The bundle has no CA sources and default CAs are disabled — nothing to distribute.");

        IReadOnlyList<string> tenantNamespaces = [];
        if (bundle.Scope == TrustDistributionScope.TenantNamespaces)
        {
            // Delivery's question, asked of Delivery. The predicate that used to live here —
            // the tenant's apps, their governance-locked namespaces, their deployments on this
            // cluster — is three of Delivery's tables and none of Catalog's.
            tenantNamespaces = await delivery.GetTenantNamespacesOnClusterAsync(
                tenantId, bundle.ClusterId, ct);
            if (tenantNamespaces.Count == 0)
                return (false, "No namespaces found for this tenant on the target cluster. Deploy an app first, or choose a different scope.");
        }

        string manifest = BuildBundleManifest(bundle, tenantNamespaces);

        // Asked through the seam, which supplies the credential and the cluster's label — this
        // caller mutates through kubectl rather than through a seam operation, so it raises the
        // acknowledgment itself, but it no longer needs a kubeconfig in order to.
        await target.AcknowledgeAsync(new PlannedChange
        {
            Verb = ChangeVerb.Apply,
            Summary = $"Apply trust Bundle {bundle.TargetName}",
            Manifest = manifest,
        }, ct);

        (bool ok, string output) = await ApplyThroughClusterAsync(target, manifest, ct);
        if (ok)
            logger.LogInformation(
                "Trust bundle {Name} applied to {Cluster}", bundle.TargetName, target.ClusterName);
        else
            logger.LogWarning(
                "Trust bundle apply failed for {Cluster}: {Output}", target.ClusterName, output);
        return (ok, output);
    }

    // ── Bundle manifest builder ────────────────────────────────────────────────

    /// <summary>
    /// Builds the trust-manager <c>Bundle</c> YAML. Sources become <c>inLine</c> PEM blocks
    /// (plus <c>useDefaultCAs</c> when enabled); the target writes into a ConfigMap or Secret in
    /// namespaces resolved by the bundle's scope.
    /// </summary>
    public static string BuildBundleManifest(CaTrustBundle bundle, IReadOnlyList<string> tenantNamespaces)
    {
        StringBuilder sb = new();
        sb.AppendLine($"apiVersion: {BundleApiVersion}");
        sb.AppendLine("kind: Bundle");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {SanitizeName(bundle.TargetName, "entkube-trust-bundle")}");
        sb.AppendLine("  labels:");
        sb.AppendLine($"    {VaultService.ManagedByLabelKey}: {VaultService.ManagedByLabelValue}");
        sb.AppendLine("spec:");
        sb.AppendLine("  sources:");

        foreach (CaTrustBundleSource source in bundle.Sources.OrderBy(s => s.Name))
        {
            sb.AppendLine("    - inLine: |");
            foreach (string line in NormalizePem(source.Pem))
                sb.AppendLine($"        {line}");
        }
        if (bundle.IncludeDefaultCAs)
            sb.AppendLine("    - useDefaultCAs: true");

        sb.AppendLine("  target:");
        string targetBlock = bundle.TargetKind == TrustBundleTargetKind.Secret ? "secret" : "configMap";
        sb.AppendLine($"    {targetBlock}:");
        sb.AppendLine($"      key: \"{EscapeKey(bundle.TargetKey)}\"");
        AppendNamespaceSelector(sb, bundle, tenantNamespaces, indent: "    ");

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendNamespaceSelector(
        StringBuilder sb, CaTrustBundle bundle, IReadOnlyList<string> tenantNamespaces, string indent)
    {
        switch (bundle.Scope)
        {
            case TrustDistributionScope.AllNamespaces:
                // An empty selector matches every namespace.
                sb.AppendLine($"{indent}namespaceSelector: {{}}");
                break;

            case TrustDistributionScope.TenantNamespaces:
                sb.AppendLine($"{indent}namespaceSelector:");
                sb.AppendLine($"{indent}  matchExpressions:");
                sb.AppendLine($"{indent}    - key: kubernetes.io/metadata.name");
                sb.AppendLine($"{indent}      operator: In");
                sb.AppendLine($"{indent}      values:");
                foreach (string ns in tenantNamespaces)
                    sb.AppendLine($"{indent}        - {ns}");
                break;

            case TrustDistributionScope.MatchLabels:
                Dictionary<string, string> labels = ParseSelector(bundle.NamespaceSelectorJson);
                if (labels.Count == 0)
                {
                    sb.AppendLine($"{indent}namespaceSelector: {{}}");
                }
                else
                {
                    sb.AppendLine($"{indent}namespaceSelector:");
                    sb.AppendLine($"{indent}  matchLabels:");
                    foreach ((string k, string v) in labels)
                        sb.AppendLine($"{indent}    {k}: \"{EscapeKey(v)}\"");
                }
                break;
        }
    }

    public static Dictionary<string, string> ParseSelector(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string SerializeSelector(IReadOnlyDictionary<string, string> labels) =>
        JsonSerializer.Serialize(labels);

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>Splits PEM into trimmed non-empty lines for block-scalar embedding.</summary>
    private static IEnumerable<string> NormalizePem(string pem) =>
        pem.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0);

    private static string EscapeKey(string value) => (value ?? "").Replace("\"", "\\\"");

    /// <summary>Coerces a name to a valid RFC 1123 DNS label (lowercase alnum + '-', ≤63 chars).</summary>
    public static string SanitizeName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        StringBuilder sb = new();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);
            else if (c is '-' or '.' or ' ' or '_') sb.Append('-');
        }
        string result = sb.ToString().Trim('-');
        while (result.Contains("--")) result = result.Replace("--", "-");
        if (result.Length > 63) result = result[..63].Trim('-');
        return result.Length == 0 ? fallback : result;
    }

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
