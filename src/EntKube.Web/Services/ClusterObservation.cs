using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntKube.Web.Services;

/// <summary>How a cluster is doing, at the level someone deciding what to do about it needs.</summary>
public enum ClusterHealth
{
    /// <summary>Everything asked for exists and is Ready.</summary>
    Healthy,

    /// <summary>A rollout, a scale-up or a repair is in flight. Not a problem; not finished.</summary>
    Progressing,

    /// <summary>Something is wrong that will not fix itself.</summary>
    Degraded,

    /// <summary>The API server did not answer. Says nothing about the cluster — only about the path to it.</summary>
    Unreachable
}

/// <summary>Observed state of one worker pool.</summary>
public sealed record PoolObservation(
    string Name,
    int Desired,
    int Current,
    int Ready,
    int Updated,
    string? Version)
{
    /// <summary>True while machines are being added, removed or replaced.</summary>
    public bool IsSettled => Desired == Current && Current == Ready && Ready == Updated;
}

/// <summary>
/// What the cluster's own Cluster API objects say about it, projected into the handful of facts a
/// decision actually rests on.
///
/// <para>Deliberately a projection rather than a mirror. EntKube keeps no second copy of the CAPI
/// resources — this is read fresh, shown, and stored only so the UI has something to display
/// between passes. Nothing acts on the stored copy.</para>
/// </summary>
public sealed record ClusterObservation
{
    public required ClusterHealth Health { get; init; }

    /// <summary>Control plane replicas asked for, and how many are actually Ready.</summary>
    public int ControlPlaneDesired { get; init; }
    public int ControlPlaneReady { get; init; }

    /// <summary>The version the control plane reports, which is what an upgrade moves first.</summary>
    public string? ControlPlaneVersion { get; init; }

    /// <summary>True while the control plane has machines at more than one version.</summary>
    public bool ControlPlaneRollingOut { get; init; }

    public IReadOnlyList<PoolObservation> Pools { get; init; } = [];

    /// <summary>Machines CAPI reports as failed, by name. These are what a repair acts on.</summary>
    public IReadOnlyList<string> FailedMachines { get; init; } = [];

    /// <summary>Why, when <see cref="Health"/> is not Healthy. One line, for a list view.</summary>
    public string? Summary { get; init; }

    public DateTime ObservedAt { get; init; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ClusterObservation? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ClusterObservation>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>An unreachable cluster, which is a different thing from a broken one.</summary>
    public static ClusterObservation Unreachable(string why) => new()
    {
        Health = ClusterHealth.Unreachable,
        Summary = why
    };
}
