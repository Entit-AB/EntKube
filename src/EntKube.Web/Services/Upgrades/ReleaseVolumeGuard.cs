using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Upgrades;

/// <summary>
/// A Deployment in a Helm release that cannot be rolled in place because it holds a
/// volume only one node can mount at a time. Upgrading it needs the old pod gone
/// before the new one is scheduled.
/// </summary>
public sealed record BlockedWorkload
{
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>Desired replicas at inspection time — what the workload is restored to afterwards.</summary>
    public required int Replicas { get; init; }

    /// <summary>The non-shareable claims this workload mounts, in spec order.</summary>
    public required IReadOnlyList<string> Claims { get; init; }

    /// <summary>Operator-facing explanation of why a rolling update would deadlock.</summary>
    public required string Reason { get; init; }
}

/// <summary>What an upgrade of one component would run into before it is started.</summary>
public sealed record VolumePreflight
{
    /// <summary>Workloads that must be taken to zero replicas first. Empty means a plain rolling upgrade is safe.</summary>
    public IReadOnlyList<BlockedWorkload> Blocked { get; init; } = [];

    /// <summary>Non-fatal problems — an unreadable PVC, a namespace we could not list.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Set when nothing could be inspected at all; the upgrade can still be forced.</summary>
    public string? Error { get; init; }

    public bool RequiresScaleDown => Blocked.Count > 0;

    /// <summary>Total pods that go away during the upgrade — the size of the outage.</summary>
    public int ReplicasAffected => Blocked.Sum(b => b.Replicas);

    public static VolumePreflight Failure(string error) => new() { Error = error };
}

/// <summary>
/// Answers "will a helm upgrade of this release actually be able to roll?" and, when it
/// cannot, takes the offending workloads out of the way and puts them back.
///
/// The failure this exists for: a Deployment with the default RollingUpdate strategy and
/// a ReadWriteOnce PersistentVolumeClaim. Kubernetes creates the new pod before deleting
/// the old one, the new pod cannot attach a volume the old pod still holds, and the
/// upgrade sits in Multi-Attach error until Helm's --wait times out. Clusters without an
/// RWX storage class (the OpenStack/Cinder case) hit this on every such component.
///
/// The fix is the same one an operator would do by hand: scale to zero, wait for the pods
/// to actually go away so the volume detaches, upgrade, then scale back up.
///
/// Why scaling back up is not optional: `helm upgrade` computes a three-way merge patch
/// from the old manifest to the new one. When the rendered replica count is unchanged
/// between chart versions, replicas is simply absent from the patch and the live value —
/// our zero — is preserved. Left alone, a successful upgrade would leave the component
/// down. So the restore reads the live value back and only re-applies the recorded count
/// when Helm did not set one of its own.
///
/// Reads go through <see cref="IKubernetesClientFactory"/>. The scale itself is a direct
/// kubectl call rather than the gated patch path on purpose: the upgrade dialog already
/// names every workload that will be taken down, so routing each scale through the change
/// gate would ask the operator to acknowledge, mid-run, something they just approved.
/// </summary>
public class ReleaseVolumeGuard(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    ILogger<ReleaseVolumeGuard> logger)
{
    /// <summary>How long to wait for the last pod of a scaled-down workload to disappear.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for a restored workload to report an available replica.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Inspects the Deployments of a component's Helm release and reports the ones a
    /// rolling update could not get past. Read-only.
    /// </summary>
    public async Task<VolumePreflight> InspectAsync(Guid componentId, CancellationToken ct = default)
    {
        ClusterComponent? component;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            component = await db.ClusterComponents
                .Include(c => c.Cluster)
                .FirstOrDefaultAsync(c => c.Id == componentId, ct);
        }

        if (component is null)
            return VolumePreflight.Failure("Component not found.");

        if (string.IsNullOrWhiteSpace(component.Cluster.Kubeconfig))
            return VolumePreflight.Failure("Cluster has no kubeconfig configured.");

        // Only Helm releases are inspected here; a Manifest component is applied wholesale
        // by kubectl and carries no release identity to match resources against.
        if (!string.Equals(component.ComponentType, "HelmChart", StringComparison.OrdinalIgnoreCase))
            return new VolumePreflight();

        string? ns = component.Namespace?.Trim();
        if (string.IsNullOrWhiteSpace(ns))
            return VolumePreflight.Failure("Component has no namespace recorded.");

        string release = (component.ReleaseName ?? component.Name).Trim();
        string kubeconfig = component.Cluster.Kubeconfig;

        List<string> warnings = [];

        string deploymentsJson;
        try
        {
            deploymentsJson = await k8s.GetJsonAsync("deployments", ns, kubeconfig, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list deployments in {Namespace} for release {Release}", ns, release);
            return VolumePreflight.Failure($"Could not list workloads in namespace '{ns}': {ex.Message}");
        }

        Dictionary<string, string[]> claimModes;
        try
        {
            string pvcJson = await k8s.GetJsonAsync("persistentvolumeclaims", ns, kubeconfig, ct: ct);
            claimModes = ParseClaimAccessModes(pvcJson);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list PVCs in {Namespace}", ns);
            warnings.Add($"Could not read persistent volume claims in '{ns}' ({ex.Message}); "
                + "volume safety could not be determined.");
            claimModes = [];
        }

        return Evaluate(deploymentsJson, claimModes, release, ns, warnings);
    }

    /// <summary>
    /// Pure evaluation of a namespace's Deployments against its claims. Separated from the
    /// cluster calls so the rules can be tested against recorded kubectl output.
    /// </summary>
    public static VolumePreflight Evaluate(
        string deploymentsJson,
        IReadOnlyDictionary<string, string[]> claimAccessModes,
        string releaseName,
        string ns,
        List<string>? warnings = null)
    {
        warnings ??= [];
        List<BlockedWorkload> blocked = [];

        foreach (JsonElement item in EnumerateItems(deploymentsJson))
        {
            if (!BelongsToRelease(item, releaseName, ns))
                continue;

            string? name = GetString(item, "metadata", "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            JsonElement spec = Child(item, "spec");
            if (spec.ValueKind != JsonValueKind.Object)
                continue;

            // An absent replicas field means 1 (the API default), not 0.
            int replicas = spec.TryGetProperty("replicas", out JsonElement r) && r.ValueKind == JsonValueKind.Number
                ? r.GetInt32()
                : 1;

            // Already at zero: nothing holds the volume, so the new pod attaches cleanly.
            if (replicas <= 0)
                continue;

            if (!RollingUpdateOverlaps(spec))
                continue;

            List<string> claims = MountedClaims(spec);
            if (claims.Count == 0)
                continue;

            List<string> exclusive = [];
            foreach (string claim in claims)
            {
                if (!claimAccessModes.TryGetValue(claim, out string[]? modes))
                {
                    // A claim we cannot see (deleted, or the list failed) is not evidence of
                    // safety — say so rather than silently treating it as shareable.
                    warnings.Add($"Deployment/{name}: claim '{claim}' was not found in '{ns}', so its access mode is unknown.");
                    continue;
                }

                if (!modes.Contains("ReadWriteMany", StringComparer.Ordinal))
                    exclusive.Add(claim);
            }

            if (exclusive.Count == 0)
                continue;

            string claimList = string.Join(", ", exclusive);
            blocked.Add(new BlockedWorkload
            {
                Name = name,
                Namespace = ns,
                Replicas = replicas,
                Claims = exclusive,
                Reason = $"Rolling update would start a replacement pod while the current one still holds "
                    + $"{claimList} ({(exclusive.Count == 1 ? "a volume" : "volumes")} no second node can mount).",
            });
        }

        return new VolumePreflight
        {
            Blocked = [.. blocked.OrderBy(b => b.Name, StringComparer.Ordinal)],
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Takes each blocked workload to zero replicas and waits for its pods to actually
    /// terminate — the volume is not detached until the last one is gone.
    /// </summary>
    public async Task<HelmExecutionResult> ScaleDownAsync(
        string kubeconfig, IReadOnlyList<BlockedWorkload> blocked, CancellationToken ct = default)
    {
        if (blocked.Count == 0)
            return new HelmExecutionResult { Success = true, Output = "" };

        StringBuilder log = new();
        bool ok = true;

        string kubeconfigPath = await WriteKubeconfigAsync(kubeconfig, ct);
        try
        {
            foreach (BlockedWorkload workload in blocked)
            {
                log.AppendLine($"Scaling Deployment/{workload.Name} to 0 (was {workload.Replicas}) so "
                    + $"{string.Join(", ", workload.Claims)} can be released.");

                HelmExecutionResult scale = await RunKubectlAsync(
                    $"scale deployment/{workload.Name} -n {workload.Namespace} --replicas=0 --kubeconfig={kubeconfigPath}", ct);

                log.AppendLine(scale.Output);

                if (!scale.Success)
                {
                    ok = false;
                    continue;
                }

                (bool drained, string detail) = await WaitForDrainAsync(workload, kubeconfigPath, ct);
                log.AppendLine(detail);
                if (!drained)
                    ok = false;
            }
        }
        finally
        {
            DeleteQuietly(kubeconfigPath);
        }

        return new HelmExecutionResult { Success = ok, Output = log.ToString().TrimEnd() };
    }

    /// <summary>
    /// Puts each workload back. A Helm upgrade whose rendered replica count did not change
    /// leaves our zero in place, so a workload still sitting at zero is restored to the
    /// count recorded before the upgrade; one Helm scaled itself is left alone.
    /// </summary>
    public async Task<HelmExecutionResult> RestoreAsync(
        string kubeconfig, IReadOnlyList<BlockedWorkload> blocked, CancellationToken ct = default)
    {
        if (blocked.Count == 0)
            return new HelmExecutionResult { Success = true, Output = "" };

        StringBuilder log = new();
        bool ok = true;

        // Deliberately not honouring the caller's cancellation for the scale-up itself: a
        // cancelled upgrade must still leave the component running rather than at zero.
        string kubeconfigPath = await WriteKubeconfigAsync(kubeconfig, CancellationToken.None);
        try
        {
            foreach (BlockedWorkload workload in blocked)
            {
                int? live = await ReadReplicasAsync(workload, kubeconfigPath);

                if (live is > 0)
                {
                    log.AppendLine($"Deployment/{workload.Name} was set to {live} replicas by the upgrade — leaving it.");
                    continue;
                }

                log.AppendLine($"Restoring Deployment/{workload.Name} to {workload.Replicas} replica(s).");

                HelmExecutionResult scale = await RunKubectlAsync(
                    $"scale deployment/{workload.Name} -n {workload.Namespace} --replicas={workload.Replicas} --kubeconfig={kubeconfigPath}",
                    CancellationToken.None);

                log.AppendLine(scale.Output);

                if (!scale.Success)
                {
                    ok = false;
                    log.AppendLine($"WARNING: {workload.Name} is still at zero replicas and needs to be scaled up by hand.");
                    continue;
                }

                (bool ready, string detail) = await WaitForReadyAsync(workload, kubeconfigPath);
                log.AppendLine(detail);
                if (!ready)
                    ok = false;
            }
        }
        finally
        {
            DeleteQuietly(kubeconfigPath);
        }

        return new HelmExecutionResult { Success = ok, Output = log.ToString().TrimEnd() };
    }

    // ── waiting ────────────────────────────────────────────────────────────

    private async Task<(bool Drained, string Detail)> WaitForDrainAsync(
        BlockedWorkload workload, string kubeconfigPath, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + DrainTimeout;

        while (DateTime.UtcNow < deadline)
        {
            HelmExecutionResult get = await RunKubectlAsync(
                $"get deployment {workload.Name} -n {workload.Namespace} "
                + $"-o jsonpath={{.status.replicas}} --kubeconfig={kubeconfigPath}", ct);

            // An empty jsonpath result is the field being absent, which is how the API
            // reports "no pods left" — the same answer as an explicit 0.
            string value = get.Output.Trim();
            if (get.Success && (value.Length == 0 || value == "0"))
                return (true, $"Deployment/{workload.Name} has no pods left; its volumes are released.");

            await Task.Delay(PollInterval, ct);
        }

        return (false, $"Deployment/{workload.Name} still had pods after {DrainTimeout.TotalMinutes:0} minutes — "
            + "the upgrade may still hit a Multi-Attach error.");
    }

    private async Task<(bool Ready, string Detail)> WaitForReadyAsync(BlockedWorkload workload, string kubeconfigPath)
    {
        DateTime deadline = DateTime.UtcNow + ReadyTimeout;

        while (DateTime.UtcNow < deadline)
        {
            HelmExecutionResult get = await RunKubectlAsync(
                $"get deployment {workload.Name} -n {workload.Namespace} "
                + $"-o jsonpath={{.status.availableReplicas}} --kubeconfig={kubeconfigPath}", CancellationToken.None);

            if (get.Success && int.TryParse(get.Output.Trim(), out int available) && available >= 1)
                return (true, $"Deployment/{workload.Name} is available again ({available} replica(s) ready).");

            await Task.Delay(PollInterval, CancellationToken.None);
        }

        return (false, $"Deployment/{workload.Name} was scaled back up but no replica became available within "
            + $"{ReadyTimeout.TotalMinutes:0} minutes — check its pods.");
    }

    private static async Task<int?> ReadReplicasAsync(BlockedWorkload workload, string kubeconfigPath)
    {
        HelmExecutionResult get = await RunKubectlAsync(
            $"get deployment {workload.Name} -n {workload.Namespace} "
            + $"-o jsonpath={{.spec.replicas}} --kubeconfig={kubeconfigPath}", CancellationToken.None);

        if (!get.Success)
            return null;

        string value = get.Output.Trim();
        return value.Length == 0 ? 0 : int.TryParse(value, out int parsed) ? parsed : null;
    }

    // ── JSON helpers ───────────────────────────────────────────────────────

    /// <summary>Maps claim name → access modes for every PVC in a namespace list.</summary>
    public static Dictionary<string, string[]> ParseClaimAccessModes(string pvcJson)
    {
        Dictionary<string, string[]> modes = new(StringComparer.Ordinal);

        foreach (JsonElement item in EnumerateItems(pvcJson))
        {
            string? name = GetString(item, "metadata", "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Prefer what the bound volume actually supports; fall back to the request.
            JsonElement status = Child(item, "status");
            JsonElement spec = Child(item, "spec");

            string[] accessModes = ReadStringArray(status, "accessModes");
            if (accessModes.Length == 0)
                accessModes = ReadStringArray(spec, "accessModes");

            modes[name!] = accessModes;
        }

        return modes;
    }

    /// <summary>
    /// True when the Deployment's update strategy can run old and new pods at once.
    /// Recreate never overlaps, and RollingUpdate with maxSurge 0 deletes before it creates.
    /// </summary>
    private static bool RollingUpdateOverlaps(JsonElement spec)
    {
        JsonElement strategy = Child(spec, "strategy");
        string type = (strategy.ValueKind == JsonValueKind.Object
            ? GetString(strategy, "type")
            : null) ?? "RollingUpdate";

        if (string.Equals(type, "Recreate", StringComparison.Ordinal))
            return false;

        JsonElement rolling = Child(strategy, "rollingUpdate");
        if (rolling.ValueKind == JsonValueKind.Object && rolling.TryGetProperty("maxSurge", out JsonElement surge))
        {
            // maxSurge is an IntOrString: 0 or "0" or "0%" all mean "never exceed the desired count".
            string raw = surge.ValueKind == JsonValueKind.Number
                ? surge.GetRawText()
                : surge.GetString() ?? "";

            if (raw.Trim().TrimEnd('%') == "0")
                return false;
        }

        return true;
    }

    /// <summary>Claim names mounted by the pod template, in spec order and deduplicated.</summary>
    private static List<string> MountedClaims(JsonElement spec)
    {
        List<string> claims = [];

        JsonElement volumes = Child(Child(Child(spec, "template"), "spec"), "volumes");
        if (volumes.ValueKind != JsonValueKind.Array)
            return claims;

        foreach (JsonElement volume in volumes.EnumerateArray())
        {
            string? claim = GetString(volume, "persistentVolumeClaim", "claimName");
            if (!string.IsNullOrWhiteSpace(claim) && !claims.Contains(claim))
                claims.Add(claim!);
        }

        return claims;
    }

    /// <summary>
    /// Whether a resource belongs to the named release. Helm 3 stamps every resource it
    /// owns with meta.helm.sh/release-name; the app.kubernetes.io/instance label is the
    /// fallback for a resource created before that or by a chart that overrode it.
    /// </summary>
    private static bool BelongsToRelease(JsonElement item, string releaseName, string ns)
    {
        string? annotated = GetString(item, "metadata", "annotations", "meta.helm.sh/release-name");
        if (annotated is not null)
        {
            string? annotatedNs = GetString(item, "metadata", "annotations", "meta.helm.sh/release-namespace");
            return string.Equals(annotated, releaseName, StringComparison.Ordinal)
                && (annotatedNs is null || string.Equals(annotatedNs, ns, StringComparison.Ordinal));
        }

        return string.Equals(
            GetString(item, "metadata", "labels", "app.kubernetes.io/instance"),
            releaseName, StringComparison.Ordinal);
    }

    private static IEnumerable<JsonElement> EnumerateItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                // Clone so the element outlives the document this method disposes.
                yield return item.Clone();
            }
        }
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child)
            ? child
            : default;

    private static string? GetString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string[] ReadStringArray(JsonElement element, string name)
    {
        JsonElement array = Child(element, name);
        if (array.ValueKind != JsonValueKind.Array)
            return [];

        return [.. array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)];
    }

    // ── process ────────────────────────────────────────────────────────────

    private static async Task<string> WriteKubeconfigAsync(string kubeconfig, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"entkube-upgrade-{Guid.NewGuid()}.kubeconfig");
        await File.WriteAllTextAsync(path, kubeconfig, ct);
        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp kubeconfig is not worth failing an upgrade over.
        }
    }

    private static async Task<HelmExecutionResult> RunKubectlAsync(string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "kubectl",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["HOME"] = "/tmp";

        using Process process = new() { StartInfo = psi };

        try
        {
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string stdout = (await stdoutTask).Trim();
            string stderr = (await stderrTask).Trim();

            return new HelmExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = (stdout + (stderr.Length == 0 ? "" : "\n" + stderr)).Trim(),
            };
        }
        catch (Exception ex)
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = HelmExecutionResult.DescribeLaunchFailure("kubectl", ex),
            };
        }
    }
}
