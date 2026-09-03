using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using k8s;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Services;

/// <summary>
/// Manages autoscalers at app+environment scope: KEDA ScaledObjects/ScaledJobs and native
/// autoscaling/v2 HorizontalPodAutoscalers. Provides CRUD operations, renders the manifests,
/// and applies them to the app's namespace on every cluster the app is deployed to in an
/// environment.
/// </summary>
public class KedaScalerService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate,
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

        await EnsureNoTargetConflictAsync(db, appId, environmentId, scaler, ct);

        if (id is null) db.KedaScalers.Add(scaler);
        await db.SaveChangesAsync(ct);
        return scaler;
    }

    /// <summary>
    /// Creates or updates a native autoscaling/v2 HorizontalPodAutoscaler on CPU and/or
    /// memory utilization. Unlike a ScaledObject this needs no KEDA controller — only
    /// metrics-server, which supplies the resource metrics.
    /// </summary>
    public async Task<KedaScaler> SaveHpaAsync(
        Guid tenantId, Guid appId, Guid environmentId, Guid? id,
        string name, string scaleTargetKind, string scaleTargetName,
        int? minReplicas, int? maxReplicas,
        int? targetCpuUtilization, int? targetMemoryUtilization,
        string? behaviorYaml,
        CancellationToken ct = default)
    {
        name = NormalizeName(name);

        if (string.IsNullOrWhiteSpace(scaleTargetName))
            throw new InvalidOperationException("Target workload name is required.");

        // An HPA without metrics never scales — the v1 default of 80% CPU does not apply to
        // an autoscaling/v2 object with an empty metrics list, so reject it here instead of
        // letting the operator wonder why nothing happens.
        if (targetCpuUtilization is null && targetMemoryUtilization is null)
            throw new InvalidOperationException("Set a target CPU and/or memory utilization — an HPA with no metrics never scales.");

        if (targetCpuUtilization is { } cpu && cpu < 1)
            throw new InvalidOperationException("Target CPU utilization must be at least 1%.");
        if (targetMemoryUtilization is { } mem && mem < 1)
            throw new InvalidOperationException("Target memory utilization must be at least 1%.");

        // Scale-to-zero is a KEDA capability (or an alpha feature gate); a plain HPA rejects
        // minReplicas: 0 at admission, so catch it while the operator is still in the form.
        if (minReplicas is { } min && min < 1)
            throw new InvalidOperationException("An HPA's minimum replicas must be at least 1. Use a KEDA ScaledObject to scale to zero.");
        if (maxReplicas is { } max)
        {
            if (max < 1)
                throw new InvalidOperationException("Maximum replicas must be at least 1.");
            if (minReplicas is { } min2 && max < min2)
                throw new InvalidOperationException("Maximum replicas must be greater than or equal to minimum replicas.");
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        await EnsureNameAvailableAsync(db, appId, environmentId, name, id, ct);

        KedaScaler scaler = id is { } existingId
            ? await db.KedaScalers.FirstOrDefaultAsync(s => s.Id == existingId, ct)
                ?? throw new InvalidOperationException($"Autoscaler {existingId} not found.")
            : new KedaScaler { Id = Guid.NewGuid(), TenantId = tenantId, AppId = appId, EnvironmentId = environmentId, Name = name };

        scaler.Name = name;
        scaler.Kind = KedaScalerKind.Hpa;
        scaler.ScaleTargetKind = string.IsNullOrWhiteSpace(scaleTargetKind) ? "Deployment" : scaleTargetKind.Trim();
        scaler.ScaleTargetName = scaleTargetName.Trim();
        scaler.MinReplicaCount = minReplicas;
        scaler.MaxReplicaCount = maxReplicas;
        scaler.TargetCpuUtilization = targetCpuUtilization;
        scaler.TargetMemoryUtilization = targetMemoryUtilization;
        scaler.BehaviorYaml = string.IsNullOrWhiteSpace(behaviorYaml) ? null : behaviorYaml;
        scaler.TriggersYaml = null;
        scaler.CustomYaml = null;
        scaler.PollingInterval = null;
        scaler.CooldownPeriod = null;
        scaler.UpdatedAt = DateTime.UtcNow;

        await EnsureNoTargetConflictAsync(db, appId, environmentId, scaler, ct);

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

    /// <summary>Copies all autoscalers for an app from one environment to another, replacing the target.</summary>
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
                TargetCpuUtilization = src.TargetCpuUtilization,
                TargetMemoryUtilization = src.TargetMemoryUtilization,
                BehaviorYaml = src.BehaviorYaml,
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

    // ── Conflicting autoscalers ───────────────────────────────────────────────

    private static async Task EnsureNoTargetConflictAsync(
        ApplicationDbContext db, Guid appId, Guid environmentId, KedaScaler candidate, CancellationToken ct)
    {
        List<KedaScaler> existing = await db.KedaScalers
            .AsNoTracking()
            .Where(s => s.AppId == appId && s.EnvironmentId == environmentId)
            .ToListAsync(ct);

        if (FindTargetConflict(existing, candidate) is { } conflict)
            throw new InvalidOperationException(conflict);
    }

    /// <summary>
    /// Returns a human-readable reason when <paramref name="candidate"/> would scale a workload
    /// that another autoscaler in the same app+environment already scales, or null when it is clear.
    ///
    /// Both families end up driving the same Scale subresource — a KEDA ScaledObject creates its own
    /// HPA behind the scenes — so two of them on one workload fight each other: each keeps computing
    /// a replica count from its own metrics and overwriting the other's. Kubernetes does not reject
    /// that, it just flaps, which is why this is caught at save time.
    ///
    /// Custom-YAML scalers are not inspected: their target is inside free-form YAML, so a conflict
    /// there cannot be detected reliably.
    /// </summary>
    public static string? FindTargetConflict(IEnumerable<KedaScaler> existing, KedaScaler candidate)
    {
        if (!IsStructured(candidate) || string.IsNullOrWhiteSpace(candidate.ScaleTargetName))
            return null;

        KedaScaler? clash = existing.FirstOrDefault(s =>
            s.Id != candidate.Id
            && IsStructured(s)
            && string.Equals(s.ScaleTargetName, candidate.ScaleTargetName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.ScaleTargetKind, candidate.ScaleTargetKind, StringComparison.OrdinalIgnoreCase));

        if (clash is null) return null;

        string clashKind = clash.Kind == KedaScalerKind.Hpa ? "HorizontalPodAutoscaler" : "KEDA ScaledObject";
        string ownKind   = candidate.Kind == KedaScalerKind.Hpa ? "HorizontalPodAutoscaler" : "KEDA ScaledObject";

        return $"The {clashKind} '{clash.Name}' already scales {candidate.ScaleTargetKind}/{candidate.ScaleTargetName}. " +
               $"Two autoscalers on one workload overwrite each other's replica count — " +
               $"edit '{clash.Name}' instead, or point this {ownKind} at a different workload.";
    }

    private static bool IsStructured(KedaScaler s) =>
        s.Kind is KedaScalerKind.ScaledObject or KedaScalerKind.Hpa;

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
    /// environment, then applies the autoscaler manifests to each namespace via kubectl.
    /// Returns one result per target, keyed by "{cluster}/{namespace}".
    /// </summary>
    public async Task<List<(string Target, bool Success, string Output)>> ApplyToEnvironmentAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KedaScaler> scalers = await GetScalersAsync(appId, environmentId, ct);
        if (scalers.Count == 0)
            return [("(no scalers)", false, "No autoscalers configured — nothing to apply.")];

        List<(KubernetesCluster Cluster, string Namespace)> targets =
            await ResolveTargetsAsync(db, appId, environmentId, ct);

        if (targets.Count == 0)
            return [("(no deployments)", false, "No deployments found for this app in this environment. Create a deployment first.")];

        var results = new List<(string Target, bool Success, string Output)>();
        foreach (var (cluster, ns) in targets)
        {
            (bool ok, string output) = await ApplyToNamespaceAsync(scalers, cluster, ns, ct);
            results.Add(($"{cluster.Name}/{ns}", ok, output));
        }
        return results;
    }

    /// <summary>
    /// Every (cluster, namespace) pair this app occupies in an environment — one per deployment,
    /// deduplicated. The governance namespace lock wins over the deployment's own namespace, which
    /// is what apply writes to, so scanning and applying always look at the same places.
    /// </summary>
    private static async Task<List<(KubernetesCluster Cluster, string Namespace)>> ResolveTargetsAsync(
        ApplicationDbContext db, Guid appId, Guid environmentId, CancellationToken ct)
    {
        string? lockedNs = (await db.AppEnvironments
            .FirstOrDefaultAsync(ae => ae.AppId == appId && ae.EnvironmentId == environmentId, ct))?.Namespace;

        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .ToListAsync(ct);

        return deployments
            .Where(d => d.Cluster is not null)
            .Select(d => (Cluster: d.Cluster!, Namespace: string.IsNullOrWhiteSpace(lockedNs) ? d.Namespace : lockedNs))
            .Where(t => !string.IsNullOrWhiteSpace(t.Namespace))
            .DistinctBy(t => (t.Cluster.Id, t.Namespace))
            .ToList();
    }

    /// <summary>Applies all autoscaler manifests to a single namespace via kubectl.</summary>
    public async Task<(bool Success, string Output)> ApplyToNamespaceAsync(
        List<KedaScaler> scalers, KubernetesCluster cluster, string ns, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (false, "Cluster has no kubeconfig configured.");

        string yaml = BuildManifest(scalers, ns);
        if (string.IsNullOrWhiteSpace(yaml))
            return (false, "No manifests generated (check configuration).");

        await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
        {
            Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
            Kubeconfig = cluster.Kubeconfig,
            ClusterLabel = cluster.Name,
            Namespace = ns,
            Summary = $"Apply autoscalers to {ns}",
            Manifest = yaml,
        }, ct);

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
                logger.LogInformation("Autoscalers applied to {Cluster}/{Namespace}", cluster.Name, ns);
            else
                logger.LogWarning("Autoscaler apply failed for {Cluster}/{Namespace}: {Output}", cluster.Name, ns, output);

            return (ok, output.ToString().TrimEnd());
        }
        finally
        {
            if (File.Exists(kubeconfigPath)) File.Delete(kubeconfigPath);
            if (File.Exists(manifestPath))   File.Delete(manifestPath);
        }
    }

    // ── Cluster scan ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the autoscalers that actually exist in this app's namespaces — HPAs and, when the
    /// KEDA CRDs are installed, ScaledObjects and ScaledJobs — and reconciles them against what
    /// EntKube has configured.
    ///
    /// This is what catches the autoscalers EntKube did not write: one shipped in a deployment's
    /// raw manifests, or one somebody applied by hand. Those are invisible to the save-time
    /// conflict check (<see cref="FindTargetConflict"/>), which only sees stored rows, yet they
    /// fight an EntKube-managed autoscaler on the same workload just as hard.
    ///
    /// Read-only: nothing is created, changed, or deleted.
    /// </summary>
    public async Task<AutoscalerScanResult> ScanEnvironmentAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        List<KedaScaler> configured = await GetScalersAsync(appId, environmentId, ct);

        List<(KubernetesCluster Cluster, string Namespace)> targets;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            targets = await ResolveTargetsAsync(db, appId, environmentId, ct);
        }

        AutoscalerScanResult result = new();

        foreach ((KubernetesCluster cluster, string ns) in targets)
        {
            result.ScannedTargets.Add($"{cluster.Name}/{ns}");

            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                result.Errors.Add($"{cluster.Name}: no kubeconfig configured.");
                continue;
            }

            Kubernetes client;
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(cluster.Kubeconfig));
                client = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{cluster.Name}/{ns}: {DescribeApiError(ex)}");
                continue;
            }

            // The two resource families are read independently: one of them failing (an
            // apiserver still warming up its storage, a missing permission) must not blank out
            // what the other found.
            using (client)
            {
                try
                {
                    result.Live.AddRange(await ScanHpasAsync(client, cluster.Name, ns, ct));
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{cluster.Name}/{ns}: HorizontalPodAutoscalers — {DescribeApiError(ex)}");
                }

                try
                {
                    result.Live.AddRange(await ScanKedaAsync(client, cluster.Name, ns, ct));
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{cluster.Name}/{ns}: KEDA scalers — {DescribeApiError(ex)}");
                }
            }
        }

        ReconcileScan(result, configured);
        return result;
    }

    /// <summary>
    /// Runs an API call, retrying the failures that mean "ask again shortly" rather than "no".
    /// A managed control plane answers 429 <c>storage is (re)initializing</c> while an apiserver
    /// (or a freshly served API group) warms up, and hands back its own retry hint in the Status
    /// body; honouring it turns a scan that used to fail outright into one that just takes a
    /// moment. Genuine answers — 404, 403 — are not retried.
    /// </summary>
    private static async Task<T> WithApiRetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        // Five attempts with the backoff below is ~12s of patience — enough for an apiserver
        // that is scaling up, short enough that an operator watching the tab does not give up.
        const int maxAttempts = 5;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (k8s.Autorest.HttpOperationException ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                await Task.Delay(RetryDelayFor(ex.Response?.Content, attempt), ct);
            }
        }
    }

    private static bool IsTransient(k8s.Autorest.HttpOperationException ex) =>
        ex.Response?.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                                or System.Net.HttpStatusCode.ServiceUnavailable
                                or System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// How long to wait before retrying. The floor is the server's own
    /// <c>details.retryAfterSeconds</c> when it sent one — waiting less than it asked for is
    /// pointless — but never shorter than a progressive backoff, because an apiserver reporting
    /// "storage is (re)initializing" keeps saying "1 second" for as long as it takes to warm up.
    /// Clamped so a confused hint cannot stall the scan for minutes.
    /// </summary>
    public static TimeSpan RetryDelayFor(string? statusBody, int attempt)
    {
        double backoff = Math.Pow(2, attempt - 1);   // 1s, 2s, 4s, 8s
        double hinted = ReadStatusField(statusBody, "details", "retryAfterSeconds") is { } hint
                        && double.TryParse(hint, out double parsed)
            ? parsed
            : 0;

        return TimeSpan.FromSeconds(Math.Clamp(Math.Max(backoff, hinted), 1, 5));
    }

    /// <summary>
    /// Turns an API failure into something an operator can act on. Kubernetes errors arrive as a
    /// JSON Status object; the raw body in the UI buries the one useful sentence, so the message
    /// and code are lifted out.
    /// </summary>
    public static string DescribeApiError(Exception ex)
    {
        if (ex is not k8s.Autorest.HttpOperationException http) return ex.Message;

        string? message = ReadStatusField(http.Response?.Content, "message");
        int? code = http.Response?.StatusCode is { } status ? (int)status : null;

        if (string.IsNullOrWhiteSpace(message)) return http.Message;

        string suffix = code is null ? "" : $" (HTTP {code})";
        return IsTransient(http)
            ? $"{message}{suffix} — still failing after retries, try again shortly."
            : $"{message}{suffix}";
    }

    /// <summary>Reads a field out of a Kubernetes Status JSON body, optionally nested one level.</summary>
    private static string? ReadStatusField(string? body, string field, string? nestedField = null)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            JsonNode? node = nestedField is null ? root?[field] : root?[field]?[nestedField];
            return node?.ToString();
        }
        catch
        {
            return null;   // not JSON — the caller falls back to the raw exception message
        }
    }

    private static async Task<List<LiveAutoscaler>> ScanHpasAsync(
        Kubernetes client, string clusterName, string ns, CancellationToken ct)
    {
        List<LiveAutoscaler> found = [];

        k8s.Models.V2HorizontalPodAutoscalerList list = await WithApiRetryAsync(
            () => client.AutoscalingV2.ListNamespacedHorizontalPodAutoscalerAsync(ns, cancellationToken: ct), ct);

        foreach (k8s.Models.V2HorizontalPodAutoscaler hpa in list.Items)
        {
            // KEDA creates (and owns) an HPA per ScaledObject/ScaledJob. Reporting those as
            // stray HPAs would flag every healthy KEDA scaler as a conflict, so they are tied
            // back to their owner instead.
            k8s.Models.V1OwnerReference? kedaOwner = hpa.Metadata?.OwnerReferences?
                .FirstOrDefault(o => o.Kind is "ScaledObject" or "ScaledJob");

            found.Add(new LiveAutoscaler
            {
                ClusterName = clusterName,
                Namespace   = ns,
                Kind        = LiveAutoscalerKind.Hpa,
                Name        = hpa.Metadata?.Name ?? "(unnamed)",
                TargetKind  = hpa.Spec?.ScaleTargetRef?.Kind ?? "",
                TargetName  = hpa.Spec?.ScaleTargetRef?.Name ?? "",
                MinReplicas = hpa.Spec?.MinReplicas,
                MaxReplicas = hpa.Spec?.MaxReplicas,
                Summary     = DescribeHpaMetrics(hpa),
                CurrentReplicas = hpa.Status?.CurrentReplicas,
                DesiredReplicas = hpa.Status?.DesiredReplicas,
                StatusNote  = DescribeHpaStatus(hpa),
                Owner       = kedaOwner is null ? AutoscalerOwner.External : AutoscalerOwner.Keda,
                OwnerName   = kedaOwner?.Name,
            });
        }

        return found;
    }

    /// <summary>
    /// Lists KEDA ScaledObjects and ScaledJobs. A cluster without the KEDA CRDs answers 404 —
    /// that is "KEDA is not installed here", not a scan failure, so it yields no rows quietly.
    /// </summary>
    private static async Task<List<LiveAutoscaler>> ScanKedaAsync(
        Kubernetes client, string clusterName, string ns, CancellationToken ct)
    {
        List<LiveAutoscaler> found = [];

        foreach ((string plural, LiveAutoscalerKind kind) in
                 new[] { ("scaledobjects", LiveAutoscalerKind.ScaledObject), ("scaledjobs", LiveAutoscalerKind.ScaledJob) })
        {
            JsonNode? root;
            try
            {
                object raw = await WithApiRetryAsync(
                    () => client.CustomObjects.ListNamespacedCustomObjectAsync(
                        "keda.sh", "v1alpha1", ns, plural, cancellationToken: ct), ct);
                root = JsonNode.Parse(JsonSerializer.Serialize(raw));
            }
            catch (k8s.Autorest.HttpOperationException ex) when (
                ex.Response?.StatusCode is System.Net.HttpStatusCode.NotFound
                                        or System.Net.HttpStatusCode.Forbidden)
            {
                continue;
            }

            if (root?["items"] is not JsonArray items) continue;

            foreach (JsonNode? item in items)
            {
                if (item is null) continue;

                JsonNode? spec = item["spec"];
                JsonNode? targetRef = spec?["scaleTargetRef"];

                found.Add(new LiveAutoscaler
                {
                    ClusterName = clusterName,
                    Namespace   = ns,
                    Kind        = kind,
                    Name        = item["metadata"]?["name"]?.GetValue<string>() ?? "(unnamed)",
                    // A ScaledJob has no scale target — it creates Jobs — so the target stays blank.
                    TargetKind  = targetRef?["kind"]?.GetValue<string>() ?? (targetRef is null ? "" : "Deployment"),
                    TargetName  = targetRef?["name"]?.GetValue<string>() ?? "",
                    MinReplicas = TryInt(spec?["minReplicaCount"]),
                    MaxReplicas = TryInt(spec?["maxReplicaCount"]),
                    Summary     = DescribeTriggers(spec?["triggers"]),
                    StatusNote  = DescribeKedaStatus(item["status"]),
                    Owner       = AutoscalerOwner.External,
                });
            }
        }

        return found;
    }

    /// <summary>
    /// Marks which live autoscalers EntKube wrote, flags the ones that fight over a workload,
    /// and reports configured scalers that are not on the cluster yet. Pure: takes the scan as
    /// read from the clusters and the configured rows, and annotates the former.
    /// </summary>
    public static void ReconcileScan(AutoscalerScanResult result, List<KedaScaler> configured)
    {
        // Resource names EntKube owns. For Custom YAML the resource name lives inside the YAML,
        // not in the scaler's own name, so it is read back out of the document.
        HashSet<string> managedNames = new(
            configured.SelectMany(ManagedResourceNames), StringComparer.OrdinalIgnoreCase);

        foreach (LiveAutoscaler live in result.Live)
        {
            if (live.Owner == AutoscalerOwner.Keda) continue;   // owned by its ScaledObject/ScaledJob
            if (managedNames.Contains(live.Name))
                live.Owner = AutoscalerOwner.EntKube;
        }

        // Everything that drives a workload's replica count: the live objects (minus KEDA's own
        // HPAs, which are represented by their ScaledObject) plus configured scalers not yet applied.
        var byWorkload = result.Live
            .Where(l => l.Owner != AutoscalerOwner.Keda && !string.IsNullOrWhiteSpace(l.TargetName))
            .GroupBy(l => $"{l.ClusterName}|{l.Namespace}|{l.TargetKind}|{l.TargetName}".ToLowerInvariant());

        foreach (var group in byWorkload)
        {
            List<LiveAutoscaler> rivals = [.. group];
            if (rivals.Count < 2) continue;

            foreach (LiveAutoscaler live in rivals)
            {
                string others = string.Join(", ", rivals.Where(r => r != live).Select(r => $"{r.Kind} {r.Name}"));
                live.Conflict =
                    $"Also scaled by {others}. Autoscalers on one workload overwrite each other's replica count — remove all but one.";
            }
        }

        // Configured-but-absent. Custom YAML is skipped: its resource name is only as reliable as
        // the parse, and a false "not applied" would be worse than staying quiet.
        if (result.Errors.Count == 0 && result.ScannedTargets.Count > 0)
        {
            foreach (KedaScaler s in configured.Where(s => s.Kind != KedaScalerKind.Custom))
            {
                bool present = result.Live.Any(l => string.Equals(l.Name, s.Name, StringComparison.OrdinalIgnoreCase));
                if (!present)
                    result.NotApplied.Add(s.Name);
            }
        }
    }

    /// <summary>
    /// The Kubernetes resource names a configured scaler owns. Structured kinds use the scaler
    /// name; Custom YAML uses whatever metadata.name each of its documents declares.
    /// </summary>
    private static IEnumerable<string> ManagedResourceNames(KedaScaler scaler)
    {
        if (scaler.Kind != KedaScalerKind.Custom)
        {
            yield return scaler.Name;
            yield break;
        }

        if (string.IsNullOrWhiteSpace(scaler.CustomYaml)) yield break;

        List<string> names = [];
        try
        {
            YamlStream yaml = [];
            yaml.Load(new StringReader(scaler.CustomYaml));
            foreach (YamlDocument doc in yaml.Documents)
            {
                if (doc.RootNode is not YamlMappingNode root) continue;
                if (!root.Children.TryGetValue(new YamlScalarNode("metadata"), out YamlNode? meta)) continue;
                if (meta is not YamlMappingNode metaMap) continue;
                if (metaMap.Children.TryGetValue(new YamlScalarNode("name"), out YamlNode? name)
                    && name is YamlScalarNode { Value: { Length: > 0 } value })
                {
                    names.Add(value);
                }
            }
        }
        catch
        {
            // Unparseable YAML is the user's problem at apply time; for the scan it just means
            // we cannot claim any live object, so those show up as "outside EntKube".
        }

        foreach (string n in names) yield return n;
    }

    private static int? TryInt(JsonNode? node)
    {
        try { return node?.GetValue<int>(); }
        catch { return null; }
    }

    private static string DescribeHpaMetrics(k8s.Models.V2HorizontalPodAutoscaler hpa)
    {
        if (hpa.Spec?.Metrics is not { Count: > 0 } metrics) return "no metrics";

        List<string> parts = [];
        foreach (k8s.Models.V2MetricSpec m in metrics)
        {
            if (m.Resource is { } r)
            {
                string target = r.Target?.AverageUtilization is { } util
                    ? $"{util}%"
                    : (r.Target?.AverageValue ?? r.Target?.Value)?.ToString() ?? "?";
                parts.Add($"{r.Name} {target}");
            }
            else
            {
                string? name = m.Pods?.Metric?.Name ?? m.External?.Metric?.Name
                               ?? m.ObjectProperty?.Metric?.Name ?? m.ContainerResource?.Name;
                parts.Add(string.IsNullOrEmpty(name) ? m.Type ?? "metric" : $"{m.Type}/{name}");
            }
        }
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Surfaces why an HPA is not doing its job — a False ScalingActive/AbleToScale condition
    /// carries messages like "missing request for cpu", which is the usual reason an HPA sits idle.
    /// </summary>
    private static string? DescribeHpaStatus(k8s.Models.V2HorizontalPodAutoscaler hpa)
    {
        k8s.Models.V2HorizontalPodAutoscalerCondition? bad = hpa.Status?.Conditions?
            .FirstOrDefault(c => c.Type is "ScalingActive" or "AbleToScale"
                              && string.Equals(c.Status, "False", StringComparison.OrdinalIgnoreCase));
        return bad is null ? null : bad.Message ?? bad.Reason;
    }

    private static string DescribeTriggers(JsonNode? triggers)
    {
        if (triggers is not JsonArray arr || arr.Count == 0) return "no triggers";
        List<string> types = [];
        foreach (JsonNode? t in arr)
        {
            string? type = t?["type"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(type)) types.Add(type);
        }
        return types.Count == 0 ? "no triggers" : string.Join(" + ", types);
    }

    private static string? DescribeKedaStatus(JsonNode? status)
    {
        if (status?["conditions"] is not JsonArray conditions) return null;

        foreach (JsonNode? c in conditions)
        {
            string? type = c?["type"]?.GetValue<string>();
            string? state = c?["status"]?.GetValue<string>();
            if (type == "Ready" && !string.Equals(state, "True", StringComparison.OrdinalIgnoreCase))
                return c?["message"]?.GetValue<string>() ?? c?["reason"]?.GetValue<string>() ?? "not ready";
        }
        return null;
    }

    // ── Manifest builder ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates the manifest YAML for all scalers in the given namespace, separated by
    /// "---". Returns an empty string when nothing is generated.
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

        if (scaler.Kind == KedaScalerKind.Hpa)
            return BuildHpaYaml(scaler);

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

    /// <summary>
    /// Renders an autoscaling/v2 HorizontalPodAutoscaler. Returns null when the scaler has no
    /// target workload or no metric — either would be rejected by the API server or, worse,
    /// accepted and never scale.
    /// </summary>
    private static string? BuildHpaYaml(KedaScaler scaler)
    {
        if (string.IsNullOrWhiteSpace(scaler.ScaleTargetName)) return null;
        if (scaler.TargetCpuUtilization is null && scaler.TargetMemoryUtilization is null) return null;

        string targetKind = string.IsNullOrWhiteSpace(scaler.ScaleTargetKind) ? "Deployment" : scaler.ScaleTargetKind;

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: autoscaling/v2");
        sb.AppendLine("kind: HorizontalPodAutoscaler");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {scaler.Name}");
        sb.AppendLine("spec:");
        sb.AppendLine("  scaleTargetRef:");
        sb.AppendLine($"    apiVersion: {ScaleTargetApiVersion(targetKind)}");
        sb.AppendLine($"    kind: {targetKind}");
        sb.AppendLine($"    name: {scaler.ScaleTargetName}");
        // minReplicas defaults to 1 server-side; maxReplicas is required, so fall back to the
        // minimum rather than emitting an object the API server rejects.
        sb.AppendLine($"  minReplicas: {scaler.MinReplicaCount ?? 1}");
        sb.AppendLine($"  maxReplicas: {scaler.MaxReplicaCount ?? Math.Max(scaler.MinReplicaCount ?? 1, 10)}");
        sb.AppendLine("  metrics:");
        if (scaler.TargetCpuUtilization is { } cpu)
            sb.Append(ResourceMetric("cpu", cpu));
        if (scaler.TargetMemoryUtilization is { } mem)
            sb.Append(ResourceMetric("memory", mem));

        if (!string.IsNullOrWhiteSpace(scaler.BehaviorYaml))
        {
            sb.AppendLine("  behavior:");
            sb.Append(IndentBlock(scaler.BehaviorYaml.TrimEnd(), 4));
        }

        return sb.ToString();
    }

    private static string ResourceMetric(string resource, int utilization) =>
        "    - type: Resource\n" +
        "      resource:\n" +
        $"        name: {resource}\n" +
        "        target:\n" +
        "          type: Utilization\n" +
        $"          averageUtilization: {utilization}\n";

    /// <summary>
    /// apiVersion for an HPA's scaleTargetRef. The built-in workloads all live in apps/v1;
    /// anything else is assumed to be a scalable custom resource and is left to the caller's
    /// naming (e.g. "argoproj.io/v1alpha1/Rollout" is not offered by the UI).
    /// </summary>
    private static string ScaleTargetApiVersion(string kind) => kind switch
    {
        "Deployment" or "StatefulSet" or "ReplicaSet" => "apps/v1",
        "ReplicationController" => "v1",
        _ => "apps/v1"
    };

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

/// <summary>What kind of autoscaler object was found on the cluster.</summary>
public enum LiveAutoscalerKind
{
    Hpa,
    ScaledObject,
    ScaledJob
}

/// <summary>Who put a live autoscaler on the cluster.</summary>
public enum AutoscalerOwner
{
    /// <summary>Not written by EntKube — a raw manifest, a Helm chart, or someone's kubectl.</summary>
    External,

    /// <summary>Matches an autoscaler configured here for this app+environment.</summary>
    EntKube,

    /// <summary>An HPA the KEDA operator created for its own ScaledObject/ScaledJob.</summary>
    Keda
}

/// <summary>One autoscaler as it currently exists in a namespace on a cluster.</summary>
public class LiveAutoscaler
{
    public required string ClusterName { get; init; }
    public required string Namespace { get; init; }
    public required LiveAutoscalerKind Kind { get; init; }
    public required string Name { get; init; }

    public string TargetKind { get; init; } = "";
    public string TargetName { get; init; } = "";
    public int? MinReplicas { get; init; }
    public int? MaxReplicas { get; init; }

    /// <summary>Metrics (HPA) or trigger types (KEDA), condensed for display.</summary>
    public string Summary { get; init; } = "";

    public int? CurrentReplicas { get; init; }
    public int? DesiredReplicas { get; init; }

    /// <summary>Why the autoscaler is unhealthy, from its conditions. Null when it looks fine.</summary>
    public string? StatusNote { get; init; }

    public AutoscalerOwner Owner { get; set; } = AutoscalerOwner.External;

    /// <summary>Name of the KEDA resource that owns this HPA, when <see cref="Owner"/> is Keda.</summary>
    public string? OwnerName { get; init; }

    /// <summary>Set when another autoscaler scales the same workload.</summary>
    public string? Conflict { get; set; }
}

/// <summary>Result of reading an app's namespaces and reconciling them against configured autoscalers.</summary>
public class AutoscalerScanResult
{
    /// <summary>"cluster/namespace" for every place that was looked at.</summary>
    public List<string> ScannedTargets { get; init; } = [];

    public List<LiveAutoscaler> Live { get; init; } = [];

    /// <summary>Clusters/namespaces that could not be read. Their contents are unknown, not empty.</summary>
    public List<string> Errors { get; init; } = [];

    /// <summary>Configured autoscalers with no matching object on any scanned cluster.</summary>
    public List<string> NotApplied { get; init; } = [];

    public List<LiveAutoscaler> Conflicting => Live.Where(l => l.Conflict is not null).ToList();
    public List<LiveAutoscaler> Unmanaged => Live.Where(l => l.Owner == AutoscalerOwner.External).ToList();
}
