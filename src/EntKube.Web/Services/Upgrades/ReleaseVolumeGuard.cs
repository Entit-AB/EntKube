using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
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
/// Everything here goes through <see cref="IClusterClient"/>, so this service holds no
/// credential and every scale is recorded.
///
/// <para><b>This used to run its own kubectl, and the reason was sound.</b> The comment said:
/// "the upgrade dialog already names every workload that will be taken down, so routing each
/// scale through the change gate would ask the operator to acknowledge, mid-run, something they
/// just approved". That is true of a per-call verb — and the conclusion drawn from it was wrong,
/// because the alternative chosen was a service invisible to the gate, writing a kubeconfig to
/// disk, changing clusters with nothing recorded (§5.4). The scales are now declared for what they
/// are, through <see cref="EntKube.Web.Services.ClusterChanges.IClusterChangeGate.DeclareConsequence"/>: no second dialog, and an audit
/// row per scale naming the upgrade it followed from.</para>
/// </summary>
public class ReleaseVolumeGuard(
    IDbContextFactory<CatalogDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate,
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
    public async Task<VolumePreflight> InspectAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        ClusterComponent? component;
        await using (CatalogDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            component = await db.ClusterComponents
                .Include(c => c.Cluster)
                .FirstOrDefaultAsync(c => c.Id == componentId, ct);
        }

        if (component is null)
            return VolumePreflight.Failure("Component not found.");

        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusterAccess.ForAsync(tenantId, component.ClusterId, ct);

        if (cluster is null)
            return VolumePreflight.Failure(
                "That cluster is not this tenant's, or it has no kubeconfig configured.");

        // Only Helm releases are inspected here; a Manifest component is applied wholesale
        // by kubectl and carries no release identity to match resources against.
        if (!string.Equals(component.ComponentType, "HelmChart", StringComparison.OrdinalIgnoreCase))
            return new VolumePreflight();

        string? ns = component.Namespace?.Trim();
        if (string.IsNullOrWhiteSpace(ns))
            return VolumePreflight.Failure("Component has no namespace recorded.");

        string release = (component.ReleaseName ?? component.Name).Trim();

        List<string> warnings = [];

        string deploymentsJson;
        try
        {
            deploymentsJson = await cluster.GetJsonAsync("deployments", ns, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list deployments in {Namespace} for release {Release}", ns, release);
            return VolumePreflight.Failure($"Could not list workloads in namespace '{ns}': {ex.Message}");
        }

        Dictionary<string, string[]> claimModes;
        try
        {
            string pvcJson = await cluster.GetJsonAsync("persistentvolumeclaims", ns, ct: ct);
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
    /// <param name="release">
    /// What the operator approved, for the audit row behind each scale. The scales are consequences
    /// of that act, not separate decisions — see <see cref="EntKube.Web.Services.ClusterChanges.IClusterChangeGate.DeclareConsequence"/>.
    /// </param>
    public async Task<HelmExecutionResult> ScaleDownAsync(
        EntKube.Web.Services.Clusters.IClusterClient cluster, string release,
        IReadOnlyList<BlockedWorkload> blocked, CancellationToken ct = default)
    {
        if (blocked.Count == 0)
            return new HelmExecutionResult { Success = true, Output = "" };

        StringBuilder log = new();
        bool ok = true;

        using IDisposable consequence = gate.DeclareConsequence(
            $"workloads taken down for the '{release}' upgrade the operator acknowledged");

        foreach (BlockedWorkload workload in blocked)
        {
            log.AppendLine($"Scaling Deployment/{workload.Name} to 0 (was {workload.Replicas}) so "
                + $"{string.Join(", ", workload.Claims)} can be released.");

            HelmExecutionResult scale = await cluster.ScaleDeploymentAsync(
                workload.Name, workload.Namespace, 0,
                $"Scale Deployment/{workload.Name} to 0 for the '{release}' upgrade "
                + $"(releases {string.Join(", ", workload.Claims)})", ct);

            log.AppendLine(scale.Output);

            if (!scale.Success)
            {
                ok = false;
                continue;
            }

            (bool drained, string detail) = await WaitForDrainAsync(cluster, workload, ct);
            log.AppendLine(detail);
            if (!drained)
                ok = false;
        }

        return new HelmExecutionResult { Success = ok, Output = log.ToString().TrimEnd() };
    }

    /// <summary>
    /// Puts each workload back. A Helm upgrade whose rendered replica count did not change
    /// leaves our zero in place, so a workload still sitting at zero is restored to the
    /// count recorded before the upgrade; one Helm scaled itself is left alone.
    /// </summary>
    /// <param name="release">As <see cref="ScaleDownAsync"/>: the approved act these follow from.</param>
    public async Task<HelmExecutionResult> RestoreAsync(
        EntKube.Web.Services.Clusters.IClusterClient cluster, string release,
        IReadOnlyList<BlockedWorkload> blocked, CancellationToken ct = default)
    {
        if (blocked.Count == 0)
            return new HelmExecutionResult { Success = true, Output = "" };

        StringBuilder log = new();
        bool ok = true;

        using IDisposable consequence = gate.DeclareConsequence(
            $"workloads put back after the '{release}' upgrade the operator acknowledged");

        // Deliberately not honouring the caller's cancellation for the scale-up itself: a
        // cancelled upgrade must still leave the component running rather than at zero. That is
        // also why the declaration above matters — a cancelled upgrade has no dialog left to ask.
        foreach (BlockedWorkload workload in blocked)
        {
            int? live = await ReadReplicasAsync(cluster, workload);

            if (live is > 0)
            {
                log.AppendLine($"Deployment/{workload.Name} was set to {live} replicas by the upgrade — leaving it.");
                continue;
            }

            log.AppendLine($"Restoring Deployment/{workload.Name} to {workload.Replicas} replica(s).");

            HelmExecutionResult scale = await cluster.ScaleDeploymentAsync(
                workload.Name, workload.Namespace, workload.Replicas,
                $"Restore Deployment/{workload.Name} to {workload.Replicas} replica(s) after the "
                + $"'{release}' upgrade", CancellationToken.None);

            log.AppendLine(scale.Output);

            if (!scale.Success)
            {
                ok = false;
                log.AppendLine($"WARNING: {workload.Name} is still at zero replicas and needs to be scaled up by hand.");
                continue;
            }

            (bool ready, string detail) = await WaitForReadyAsync(cluster, workload);
            log.AppendLine(detail);
            if (!ready)
                ok = false;
        }

        return new HelmExecutionResult { Success = ok, Output = log.ToString().TrimEnd() };
    }

    // ── waiting ────────────────────────────────────────────────────────────

    private async Task<(bool Drained, string Detail)> WaitForDrainAsync(
        EntKube.Web.Services.Clusters.IClusterClient cluster, BlockedWorkload workload, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + DrainTimeout;

        while (DateTime.UtcNow < deadline)
        {
            // An absent field is how the API reports "no pods left" — the same answer as an
            // explicit 0, which is what the jsonpath read this replaces relied on too.
            (bool ok, int? replicas) = await ReadIntAsync(cluster, workload, "status", "replicas", ct);

            if (ok && (replicas is null || replicas == 0))
                return (true, $"Deployment/{workload.Name} has no pods left; its volumes are released.");

            await Task.Delay(PollInterval, ct);
        }

        return (false, $"Deployment/{workload.Name} still had pods after {DrainTimeout.TotalMinutes:0} minutes — "
            + "the upgrade may still hit a Multi-Attach error.");
    }

    private async Task<(bool Ready, string Detail)> WaitForReadyAsync(
        EntKube.Web.Services.Clusters.IClusterClient cluster, BlockedWorkload workload)
    {
        DateTime deadline = DateTime.UtcNow + ReadyTimeout;

        while (DateTime.UtcNow < deadline)
        {
            (bool ok, int? available) =
                await ReadIntAsync(cluster, workload, "status", "availableReplicas", CancellationToken.None);

            if (ok && available >= 1)
                return (true, $"Deployment/{workload.Name} is available again ({available} replica(s) ready).");

            await Task.Delay(PollInterval, CancellationToken.None);
        }

        return (false, $"Deployment/{workload.Name} was scaled back up but no replica became available within "
            + $"{ReadyTimeout.TotalMinutes:0} minutes — check its pods.");
    }

    private async Task<int?> ReadReplicasAsync(
        EntKube.Web.Services.Clusters.IClusterClient cluster, BlockedWorkload workload)
    {
        (bool ok, int? replicas) =
            await ReadIntAsync(cluster, workload, "spec", "replicas", CancellationToken.None);

        // Unreadable is null (the caller leaves the workload alone); readable-but-absent is 0,
        // which is what an empty jsonpath result meant before.
        return ok ? replicas ?? 0 : null;
    }

    /// <summary>
    /// One integer field of a Deployment, read as JSON through the seam rather than with
    /// <c>-o jsonpath</c>. The jsonpath form needed a kubeconfig on disk and an escaped expression
    /// in a command line; this parses the document the seam already knows how to fetch.
    /// </summary>
    private async Task<(bool Ok, int? Value)> ReadIntAsync(
        EntKube.Web.Services.Clusters.IClusterClient cluster, BlockedWorkload workload,
        string section, string field, CancellationToken ct)
    {
        HelmExecutionResult get;
        try
        {
            get = await cluster.GetResourceJsonAsync("deployment", workload.Name, workload.Namespace, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read Deployment/{Name} in {Namespace}", workload.Name, workload.Namespace);
            return (false, null);
        }

        if (!get.Success) return (false, null);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(get.Output);
            return doc.RootElement.TryGetProperty(section, out JsonElement s)
                   && s.TryGetProperty(field, out JsonElement v)
                   && v.TryGetInt32(out int parsed)
                ? (true, parsed)
                : (true, null);
        }
        catch (JsonException)
        {
            return (false, null);
        }
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

}
