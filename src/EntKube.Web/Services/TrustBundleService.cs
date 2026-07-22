using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
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
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IClusterChangeGate gate,
    ILogger<TrustBundleService> logger)
{
    /// <summary>The trust-manager API group/version and label we stamp on managed Bundles.</summary>
    public const string BundleApiVersion = "trust.cert-manager.io/v1alpha1";

    /// <summary>
    /// True when trust-manager is recorded as installed on the cluster. Used to warn in the UI
    /// that trust bundles won't reconcile until the component is present. A DB check against the
    /// component inventory — cheap and does not touch the cluster.
    /// </summary>
    public async Task<bool> IsTrustManagerAvailableAsync(Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents
            .AnyAsync(c => c.ClusterId == clusterId
                        && c.Status == ComponentStatus.Installed
                        && (c.Name == "trust-manager"
                            || c.HelmChartName == "trust-manager"
                            || c.ReleaseName == "trust-manager"), ct);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<List<CaTrustBundle>> GetBundlesAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.CaTrustBundles
            .Include(b => b.Sources)
            .Where(b => b.TenantId == tenantId && b.ClusterId == clusterId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
    }

    public async Task<CaTrustBundle?> GetBundleAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.CaTrustBundles
            .Include(b => b.Sources)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<CaTrustBundle> CreateBundleAsync(CaTrustBundle bundle, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        bundle.Id = bundle.Id == Guid.Empty ? Guid.NewGuid() : bundle.Id;
        bundle.TargetName = SanitizeName(bundle.TargetName, "entkube-trust-bundle");
        bundle.CreatedAt = bundle.UpdatedAt = DateTime.UtcNow;
        db.CaTrustBundles.Add(bundle);
        await db.SaveChangesAsync(ct);
        return bundle;
    }

    public async Task<CaTrustBundle> UpdateBundleAsync(
        Guid id, Action<CaTrustBundle> mutate, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CaTrustBundle bundle = await db.CaTrustBundles.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Trust bundle {id} not found.");
        mutate(bundle);
        bundle.TargetName = SanitizeName(bundle.TargetName, "entkube-trust-bundle");
        bundle.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return bundle;
    }

    /// <summary>Removes the bundle from the DB and best-effort deletes the live Bundle CR from the cluster.</summary>
    public async Task DeleteBundleAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CaTrustBundle? bundle = await db.CaTrustBundles.FindAsync([id], ct);
        if (bundle is null) return;

        KubernetesCluster? cluster = await db.KubernetesClusters.FindAsync([bundle.ClusterId], ct);
        if (cluster is not null && !string.IsNullOrWhiteSpace(cluster.Kubeconfig))
        {
            try
            {
                await gate.AcknowledgeAsync(new PlannedClusterChange
                {
                    Verb = ChangeVerb.Delete,
                    Kubeconfig = cluster.Kubeconfig!,
                    ClusterLabel = cluster.Name,
                    Kind = "bundles.trust.cert-manager.io",
                    Name = bundle.TargetName,
                    Summary = $"Delete trust Bundle {bundle.TargetName}",
                }, ct);
                await RunKubectlAsync(
                    $"delete bundles.trust.cert-manager.io {bundle.TargetName} --ignore-not-found",
                    cluster.Kubeconfig!, null, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Operator cancelled the CR removal — leave the DB row so they can retry.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete Bundle CR {Name} from {Cluster}", bundle.TargetName, cluster.Name);
            }
        }

        db.CaTrustBundles.Remove(bundle);
        await db.SaveChangesAsync(ct);
    }

    public async Task<CaTrustBundleSource> AddSourceAsync(
        Guid bundleId, string name, string pem, CancellationToken ct = default)
    {
        if (CertificateParser.TryParse(pem) is null)
            throw new InvalidOperationException("The CA certificate is not valid PEM. Paste the full -----BEGIN CERTIFICATE----- block.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();
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

    public async Task RemoveSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CaTrustBundleSource? source = await db.CaTrustBundleSources.FindAsync([sourceId], ct);
        if (source is null) return;
        Guid bundleId = source.BundleId;
        db.CaTrustBundleSources.Remove(source);
        await TouchBundleAsync(db, bundleId, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task TouchBundleAsync(ApplicationDbContext db, Guid bundleId, CancellationToken ct)
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
    public async Task<(bool Success, string Output)> ApplyBundleAsync(Guid bundleId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        CaTrustBundle? bundle = await db.CaTrustBundles
            .Include(b => b.Sources)
            .FirstOrDefaultAsync(b => b.Id == bundleId, ct);
        if (bundle is null) return (false, "Trust bundle not found.");

        KubernetesCluster? cluster = await db.KubernetesClusters.FindAsync([bundle.ClusterId], ct);
        if (cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (false, "Target cluster has no kubeconfig configured.");

        if (bundle.Sources.Count == 0 && !bundle.IncludeDefaultCAs)
            return (false, "The bundle has no CA sources and default CAs are disabled — nothing to distribute.");

        IReadOnlyList<string> tenantNamespaces = [];
        if (bundle.Scope == TrustDistributionScope.TenantNamespaces)
        {
            tenantNamespaces = await ResolveTenantNamespacesAsync(db, bundle.TenantId, bundle.ClusterId, ct);
            if (tenantNamespaces.Count == 0)
                return (false, "No namespaces found for this tenant on the target cluster. Deploy an app first, or choose a different scope.");
        }

        string manifest = BuildBundleManifest(bundle, tenantNamespaces);

        await gate.AcknowledgeAsync(new PlannedClusterChange
        {
            Verb = ChangeVerb.Apply,
            Kubeconfig = cluster.Kubeconfig!,
            ClusterLabel = cluster.Name,
            Summary = $"Apply trust Bundle {bundle.TargetName}",
            Manifest = manifest,
        }, ct);

        (bool ok, string output) = await RunKubectlApplyAsync(manifest, cluster.Kubeconfig!, ct);
        if (ok)
            logger.LogInformation("Trust bundle {Name} applied to {Cluster}", bundle.TargetName, cluster.Name);
        else
            logger.LogWarning("Trust bundle apply failed for {Cluster}: {Output}", cluster.Name, output);
        return (ok, output);
    }

    /// <summary>
    /// Resolves the distinct namespace names this tenant's apps occupy on a given cluster,
    /// preferring the governance-locked AppEnvironment namespace when present.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ResolveTenantNamespacesAsync(
        ApplicationDbContext db, Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        List<Guid> appIds = await db.Apps
            .Where(a => a.Customer.TenantId == tenantId)
            .Select(a => a.Id)
            .ToListAsync(ct);

        Dictionary<(Guid, Guid), string?> lockedNs = (await db.AppEnvironments
            .Where(ae => appIds.Contains(ae.AppId))
            .Select(ae => new { ae.AppId, ae.EnvironmentId, ae.Namespace })
            .ToListAsync(ct))
            .ToDictionary(x => (x.AppId, x.EnvironmentId), x => x.Namespace);

        List<AppDeployment> deployments = await db.AppDeployments
            .Where(d => appIds.Contains(d.AppId) && d.ClusterId == clusterId)
            .ToListAsync(ct);

        return deployments
            .Select(d => lockedNs.TryGetValue((d.AppId, d.EnvironmentId), out string? locked) && !string.IsNullOrWhiteSpace(locked)
                ? locked!
                : d.Namespace)
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Select(ns => ns!)
            .Distinct()
            .OrderBy(ns => ns)
            .ToList();
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

    // ── kubectl plumbing ───────────────────────────────────────────────────────

    private static async Task<(bool, string)> RunKubectlApplyAsync(string manifest, string kubeconfig, CancellationToken ct)
    {
        string manifestPath = Path.Combine(Path.GetTempPath(), $"entkube-trust-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(manifestPath, manifest, ct);
            return await RunKubectlAsync($"apply -f {manifestPath}", kubeconfig, manifestPath, ct);
        }
        finally
        {
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
    }

    private static async Task<(bool, string)> RunKubectlAsync(
        string arguments, string kubeconfig, string? extraTempToDelete, CancellationToken ct)
    {
        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-trust-{Guid.NewGuid():N}.kubeconfig");
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
            if (extraTempToDelete is not null && File.Exists(extraTempToDelete)) File.Delete(extraTempToDelete);
        }
    }

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
}
