using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Services;

/// <summary>
/// Manages KEDA autoscalers (ScaledObject / ScaledJob) at app+environment scope.
/// Provides CRUD operations, renders keda.sh/v1alpha1 manifests, and applies them
/// to the app's namespace on every cluster the app is deployed to in an environment.
/// </summary>
public class KedaScalerService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<KedaScalerService> logger)
{
    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<List<KedaScaler>> GetScalersAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KedaScalers
            .Where(s => s.AppId == appId && s.EnvironmentId == environmentId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates or updates a structured ScaledObject autoscaler. When <paramref name="id"/>
    /// is null a new record is created; otherwise the existing record is updated in place.
    /// </summary>
    public async Task<KedaScaler> SaveScaledObjectAsync(
        Guid tenantId, Guid appId, Guid environmentId, Guid? id,
        string name, string scaleTargetKind, string scaleTargetName,
        int? minReplicaCount, int? maxReplicaCount,
        int? pollingInterval, int? cooldownPeriod,
        string triggersYaml,
        CancellationToken ct = default)
    {
        name = NormalizeName(name);
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        await EnsureNameAvailableAsync(db, appId, environmentId, name, id, ct);

        KedaScaler scaler = id is { } existingId
            ? await db.KedaScalers.FirstOrDefaultAsync(s => s.Id == existingId, ct)
                ?? throw new InvalidOperationException($"KEDA scaler {existingId} not found.")
            : new KedaScaler { Id = Guid.NewGuid(), TenantId = tenantId, AppId = appId, EnvironmentId = environmentId, Name = name };

        scaler.Name = name;
        scaler.Kind = KedaScalerKind.ScaledObject;
        scaler.ScaleTargetKind = string.IsNullOrWhiteSpace(scaleTargetKind) ? "Deployment" : scaleTargetKind.Trim();
        scaler.ScaleTargetName = scaleTargetName.Trim();
        scaler.MinReplicaCount = minReplicaCount;
        scaler.MaxReplicaCount = maxReplicaCount;
        scaler.PollingInterval = pollingInterval;
        scaler.CooldownPeriod = cooldownPeriod;
        scaler.TriggersYaml = triggersYaml;
        scaler.CustomYaml = null;
        scaler.UpdatedAt = DateTime.UtcNow;

        if (id is null) db.KedaScalers.Add(scaler);
        await db.SaveChangesAsync(ct);
        return scaler;
    }

    /// <summary>Creates or updates a raw-YAML autoscaler (ScaledObject or ScaledJob).</summary>
    public async Task<KedaScaler> SaveCustomAsync(
        Guid tenantId, Guid appId, Guid environmentId, Guid? id,
        string name, string customYaml,
        CancellationToken ct = default)
    {
        name = NormalizeName(name);
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        await EnsureNameAvailableAsync(db, appId, environmentId, name, id, ct);

        KedaScaler scaler = id is { } existingId
            ? await db.KedaScalers.FirstOrDefaultAsync(s => s.Id == existingId, ct)
                ?? throw new InvalidOperationException($"KEDA scaler {existingId} not found.")
            : new KedaScaler { Id = Guid.NewGuid(), TenantId = tenantId, AppId = appId, EnvironmentId = environmentId, Name = name };

        scaler.Name = name;
        scaler.Kind = KedaScalerKind.Custom;
        scaler.CustomYaml = customYaml;
        scaler.UpdatedAt = DateTime.UtcNow;

        if (id is null) db.KedaScalers.Add(scaler);
        await db.SaveChangesAsync(ct);
        return scaler;
    }

    public async Task<bool> DeleteScalerAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KedaScaler? scaler = await db.KedaScalers.FindAsync([id], ct);
        if (scaler is null) return false;
        db.KedaScalers.Remove(scaler);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Copies all KEDA scalers for an app from one environment to another, replacing the target.</summary>
    public async Task CopyFromEnvironmentAsync(
        Guid appId, Guid sourceEnvId, Guid targetEnvId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KedaScaler> source = await db.KedaScalers
            .Where(s => s.AppId == appId && s.EnvironmentId == sourceEnvId)
            .ToListAsync(ct);

        List<KedaScaler> target = await db.KedaScalers
            .Where(s => s.AppId == appId && s.EnvironmentId == targetEnvId)
            .ToListAsync(ct);

        db.KedaScalers.RemoveRange(target);

        foreach (KedaScaler src in source)
        {
            db.KedaScalers.Add(new KedaScaler
            {
                Id = Guid.NewGuid(),
                TenantId = src.TenantId,
                AppId = appId,
                EnvironmentId = targetEnvId,
                Name = src.Name,
                Kind = src.Kind,
                ScaleTargetName = src.ScaleTargetName,
                ScaleTargetKind = src.ScaleTargetKind,
                MinReplicaCount = src.MinReplicaCount,
                MaxReplicaCount = src.MaxReplicaCount,
                PollingInterval = src.PollingInterval,
                CooldownPeriod = src.CooldownPeriod,
                TriggersYaml = src.TriggersYaml,
                CustomYaml = src.CustomYaml
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name is required.");
        return name;
    }

    private static async Task EnsureNameAvailableAsync(
        ApplicationDbContext db, Guid appId, Guid environmentId, string name, Guid? selfId, CancellationToken ct)
    {
        bool taken = await db.KedaScalers.AnyAsync(
            s => s.AppId == appId && s.EnvironmentId == environmentId && s.Name == name
              && (selfId == null || s.Id != selfId), ct);
        if (taken)
            throw new InvalidOperationException($"A KEDA scaler named '{name}' already exists in this environment.");
    }

    // ── Cluster availability check ────────────────────────────────────────────

    /// <summary>
    /// Returns true if at least one cluster registered for this tenant+environment
    /// has KEDA installed (ComponentStatus.Installed).
    /// </summary>
    public async Task<bool> IsKedaAvailableAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<Guid> clusterIds = await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId && c.EnvironmentId == environmentId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (clusterIds.Count == 0) return false;

        return await db.ClusterComponents
            .AnyAsync(c => clusterIds.Contains(c.ClusterId)
                        && c.Status == ComponentStatus.Installed
                        && (c.Name == "keda"
                            || c.HelmChartName == "keda"
                            || c.ReleaseName == "keda"), ct);
    }

    // ── Apply to environment ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves every (cluster, namespace) pair the app is deployed to in this
    /// environment, then applies the KEDA manifests to each namespace via kubectl.
    /// Returns one result per target, keyed by "{cluster}/{namespace}".
    /// </summary>
    public async Task<List<(string Target, bool Success, string Output)>> ApplyToEnvironmentAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KedaScaler> scalers = await GetScalersAsync(appId, environmentId, ct);
        if (scalers.Count == 0)
            return [("(no scalers)", false, "No autoscalers configured — nothing to apply.")];

        // Locked namespace for this app in this environment (governance namespace lock).
        string? lockedNs = (await db.AppEnvironments
            .FirstOrDefaultAsync(ae => ae.AppId == appId && ae.EnvironmentId == environmentId, ct))?.Namespace;

        // All deployments for this app in this environment, with cluster kubeconfig.
        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .ToListAsync(ct);

        if (deployments.Count == 0)
            return [("(no deployments)", false, "No deployments found for this app in this environment. Create a deployment first.")];

        // Build unique (cluster, namespace) pairs, preferring the locked namespace.
        var targets = deployments
            .Select(d => (Cluster: d.Cluster!, Namespace: string.IsNullOrWhiteSpace(lockedNs) ? d.Namespace : lockedNs))
            .Where(t => t.Cluster is not null && !string.IsNullOrWhiteSpace(t.Namespace))
            .DistinctBy(t => (t.Cluster.Id, t.Namespace))
            .ToList();

        var results = new List<(string Target, bool Success, string Output)>();
        foreach (var (cluster, ns) in targets)
        {
            (bool ok, string output) = await ApplyToNamespaceAsync(scalers, cluster, ns, ct);
            results.Add(($"{cluster.Name}/{ns}", ok, output));
        }
        return results;
    }

    /// <summary>Applies all KEDA scaler manifests to a single namespace via kubectl.</summary>
    public async Task<(bool Success, string Output)> ApplyToNamespaceAsync(
        List<KedaScaler> scalers, KubernetesCluster cluster, string ns, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (false, "Cluster has no kubeconfig configured.");

        string yaml = BuildManifest(scalers, ns);
        if (string.IsNullOrWhiteSpace(yaml))
            return (false, "No manifests generated (check configuration).");

        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-keda-{Guid.NewGuid():N}.kubeconfig");
        string manifestPath   = Path.Combine(Path.GetTempPath(), $"entkube-keda-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, cluster.Kubeconfig, ct);
            await File.WriteAllTextAsync(manifestPath, yaml, ct);

            // -n {ns} targets the app's namespace; manifests omit an explicit namespace so
            // both structured ScaledObjects and user-authored Custom YAML land there.
            System.Diagnostics.ProcessStartInfo psi = new("kubectl",
                $"apply -n {ns} -f {manifestPath} --kubeconfig {kubeconfigPath}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using System.Diagnostics.Process proc = new() { StartInfo = psi };
            StringBuilder output = new();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            bool ok = proc.ExitCode == 0;
            if (ok)
                logger.LogInformation("KEDA scalers applied to {Cluster}/{Namespace}", cluster.Name, ns);
            else
                logger.LogWarning("KEDA apply failed for {Cluster}/{Namespace}: {Output}", cluster.Name, ns, output);

            return (ok, output.ToString().TrimEnd());
        }
        finally
        {
            if (File.Exists(kubeconfigPath)) File.Delete(kubeconfigPath);
            if (File.Exists(manifestPath))   File.Delete(manifestPath);
        }
    }

    // ── Manifest builder ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates KEDA manifest YAML for all scalers in the given namespace,
    /// separated by "---". Returns an empty string when nothing is generated.
    /// </summary>
    public static string BuildManifest(List<KedaScaler> scalers, string ns)
    {
        List<string> docs = [];
        foreach (KedaScaler scaler in scalers)
        {
            string? yaml = BuildScalerYaml(scaler, ns);
            if (!string.IsNullOrWhiteSpace(yaml))
                docs.Add(yaml.Trim());
        }
        return string.Join("\n---\n", docs);
    }

    /// <summary>Renders a single scaler. Returns null when the scaler is incomplete.</summary>
    public static string? BuildScalerYaml(KedaScaler scaler, string ns)
    {
        if (scaler.Kind == KedaScalerKind.Custom)
            return string.IsNullOrWhiteSpace(scaler.CustomYaml) ? null : scaler.CustomYaml;

        // ScaledObject — requires a target workload and at least one trigger.
        if (string.IsNullOrWhiteSpace(scaler.ScaleTargetName) || string.IsNullOrWhiteSpace(scaler.TriggersYaml))
            return null;

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: keda.sh/v1alpha1");
        sb.AppendLine("kind: ScaledObject");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {scaler.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine("  scaleTargetRef:");
        sb.AppendLine($"    kind: {(string.IsNullOrWhiteSpace(scaler.ScaleTargetKind) ? "Deployment" : scaler.ScaleTargetKind)}");
        sb.AppendLine($"    name: {scaler.ScaleTargetName}");
        if (scaler.MinReplicaCount is { } min) sb.AppendLine($"  minReplicaCount: {min}");
        if (scaler.MaxReplicaCount is { } max) sb.AppendLine($"  maxReplicaCount: {max}");
        if (scaler.PollingInterval is { } poll) sb.AppendLine($"  pollingInterval: {poll}");
        if (scaler.CooldownPeriod is { } cool) sb.AppendLine($"  cooldownPeriod: {cool}");
        sb.AppendLine("  triggers:");
        sb.Append(IndentBlock(scaler.TriggersYaml!.TrimEnd(), 4));

        return sb.ToString();
    }

    /// <summary>Indents every non-empty line of a block by the given number of spaces.</summary>
    private static string IndentBlock(string text, int spaces)
    {
        string pad = new(' ', spaces);
        StringBuilder sb = new();
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine(line.Length == 0 ? line : pad + line);
        return sb.ToString();
    }
}
