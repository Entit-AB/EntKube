using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Reads a cluster's own Cluster API objects and projects them into
/// <see cref="ClusterObservation"/> — what exists, as opposed to what was asked for.
///
/// <para>Parsing is defensive throughout, and that is not politeness: these objects come from a
/// cluster that may be mid-rollout, running a CAPI version whose status fields differ slightly, or
/// answering with a partially-populated resource because a controller has not caught up. A missing
/// field means "not known yet", never zero — the difference matters, because zero ready replicas
/// reads as an outage and would have a reconciler acting on it.</para>
/// </summary>
public class ClusterStateReader(IKubernetesClientFactory k8s, ILogger<ClusterStateReader> logger)
{
    public async Task<ClusterObservation> ObserveAsync(
        string clusterName, string kubeconfig, CancellationToken ct = default)
    {
        string kcpJson;
        string mdJson;
        string machineJson;

        try
        {
            kcpJson = await k8s.GetJsonAsync("kubeadmcontrolplanes.controlplane.cluster.x-k8s.io",
                CapiManifestBuilder.Namespace, kubeconfig, ct: ct);
            mdJson = await k8s.GetJsonAsync("machinedeployments.cluster.x-k8s.io",
                CapiManifestBuilder.Namespace, kubeconfig, ct: ct);
            machineJson = await k8s.GetJsonAsync("machines.cluster.x-k8s.io",
                CapiManifestBuilder.Namespace, kubeconfig, ct: ct);
        }
        catch (Exception ex)
        {
            // The cluster did not answer. That is a statement about the path to it, not about the
            // cluster — and treating it as "nothing exists" is how a reconciler deletes a working
            // cluster it merely could not reach.
            logger.LogWarning(ex, "Could not read CAPI state from cluster {Cluster}", clusterName);
            return ClusterObservation.Unreachable($"The cluster's API server did not answer: {ex.Message}");
        }

        (int cpDesired, int cpReady, string? cpVersion, bool cpRolling) = ReadControlPlane(kcpJson, clusterName);
        IReadOnlyList<PoolObservation> pools = ReadPools(mdJson, clusterName);
        IReadOnlyList<string> failed = ReadFailedMachines(machineJson, clusterName);

        return Judge(cpDesired, cpReady, cpVersion, cpRolling, pools, failed);
    }

    /// <summary>
    /// Turns the numbers into a verdict. Kept separate and pure so the judgement — which decides
    /// whether an operator is woken up — can be tested without a cluster.
    /// </summary>
    public static ClusterObservation Judge(
        int controlPlaneDesired,
        int controlPlaneReady,
        string? controlPlaneVersion,
        bool controlPlaneRollingOut,
        IReadOnlyList<PoolObservation> pools,
        IReadOnlyList<string> failedMachines)
    {
        ClusterHealth health;
        string? summary = null;

        if (controlPlaneDesired > 0 && controlPlaneReady == 0)
        {
            // No control plane is Ready. Whatever else is true, nothing about this cluster works.
            health = ClusterHealth.Degraded;
            summary = "No control-plane node is ready.";
        }
        else if (failedMachines.Count > 0)
        {
            health = ClusterHealth.Degraded;
            summary = failedMachines.Count == 1
                ? $"Machine {failedMachines[0]} has failed."
                : $"{failedMachines.Count} machines have failed.";
        }
        else if (controlPlaneReady < controlPlaneDesired)
        {
            // Short of the asked-for count but serving: a node is being replaced or added.
            health = ClusterHealth.Progressing;
            summary = $"Control plane at {controlPlaneReady} of {controlPlaneDesired} nodes.";
        }
        else if (controlPlaneRollingOut)
        {
            health = ClusterHealth.Progressing;
            summary = "Control-plane rollout in progress.";
        }
        else if (pools.Any(p => !p.IsSettled))
        {
            PoolObservation pool = pools.First(p => !p.IsSettled);
            health = ClusterHealth.Progressing;
            summary = $"Pool {pool.Name} at {pool.Ready} of {pool.Desired} nodes.";
        }
        else
        {
            health = ClusterHealth.Healthy;
        }

        return new ClusterObservation
        {
            Health = health,
            ControlPlaneDesired = controlPlaneDesired,
            ControlPlaneReady = controlPlaneReady,
            ControlPlaneVersion = controlPlaneVersion,
            ControlPlaneRollingOut = controlPlaneRollingOut,
            Pools = pools,
            FailedMachines = failedMachines,
            Summary = summary
        };
    }

    // ──────── Parsing ────────

    private (int Desired, int Ready, string? Version, bool RollingOut) ReadControlPlane(string json, string clusterName)
    {
        foreach (JsonElement item in Items(json))
        {
            if (!BelongsTo(item, clusterName)) continue;

            JsonElement spec = Child(item, "spec");
            JsonElement status = Child(item, "status");

            int desired = Int(spec, "replicas") ?? 0;
            int ready = Int(status, "readyReplicas") ?? 0;
            int updated = Int(status, "updatedReplicas") ?? 0;
            string? version = Str(spec, "version");

            // Machines at more than one version means a rollout is in flight. updatedReplicas is
            // the count already at the target, so a shortfall is the rollout's remaining work.
            bool rolling = desired > 0 && updated < desired;

            return (desired, ready, version, rolling);
        }

        return (0, 0, null, false);
    }

    private IReadOnlyList<PoolObservation> ReadPools(string json, string clusterName)
    {
        List<PoolObservation> pools = [];

        foreach (JsonElement item in Items(json))
        {
            if (!BelongsTo(item, clusterName)) continue;

            JsonElement metadata = Child(item, "metadata");
            JsonElement spec = Child(item, "spec");
            JsonElement status = Child(item, "status");

            string name = Str(metadata, "name") ?? "";
            // The pool's own name, not the prefixed resource name — that is what the spec calls it.
            string poolName = name.StartsWith(clusterName + "-", StringComparison.Ordinal)
                ? name[(clusterName.Length + 1)..]
                : name;

            pools.Add(new PoolObservation(
                Name: poolName,
                Desired: Int(spec, "replicas") ?? 0,
                Current: Int(status, "replicas") ?? 0,
                Ready: Int(status, "readyReplicas") ?? 0,
                Updated: Int(status, "updatedReplicas") ?? 0,
                Version: VersionOfTemplate(spec)));
        }

        return pools.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private IReadOnlyList<string> ReadFailedMachines(string json, string clusterName)
    {
        List<string> failed = [];

        foreach (JsonElement item in Items(json))
        {
            if (!BelongsTo(item, clusterName)) continue;

            JsonElement status = Child(item, "status");
            string? phase = Str(status, "phase");

            // CAPI's own words. "Failed" and "Deleting" are different: a machine on its way out
            // during a rollout is expected and must not read as a fault.
            if (string.Equals(phase, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                failed.Add(Str(Child(item, "metadata"), "name") ?? "(unnamed)");
            }
        }

        return failed;
    }

    private static string? VersionOfTemplate(JsonElement spec)
    {
        JsonElement template = Child(spec, "template");
        JsonElement templateSpec = Child(template, "spec");
        return Str(templateSpec, "version");
    }

    /// <summary>
    /// Whether a CAPI object belongs to this cluster. Both the label and the spec field are checked
    /// because the label is set by the controllers, and an object applied but not yet reconciled
    /// has only the spec — which is exactly the window a newly-created cluster is observed in.
    /// </summary>
    private static bool BelongsTo(JsonElement item, string clusterName)
    {
        JsonElement labels = Child(Child(item, "metadata"), "labels");
        if (Str(labels, "cluster.x-k8s.io/cluster-name") == clusterName)
        {
            return true;
        }

        string? specCluster = Str(Child(item, "spec"), "clusterName");
        if (specCluster == clusterName)
        {
            return true;
        }

        // The control plane is named for its cluster and carries neither, until it is reconciled.
        string? name = Str(Child(item, "metadata"), "name");
        return name is not null && name.StartsWith(clusterName + "-", StringComparison.Ordinal);
    }

    private IEnumerable<JsonElement> Items(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "CAPI resource list was not valid JSON");
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
                yield return item.Clone();
            }
        }
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement child)
            ? child
            : default;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
