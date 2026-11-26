using System.Text.Json.Nodes;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>Who scrapes a target, or what stops it. Decides what happens to a monitor of ours.</summary>
public enum MonitorState
{
    /// <summary>EntKube's monitor scrapes it, or will as soon as it is applied.</summary>
    Managed,

    /// <summary>Something else scrapes the same endpoint already. Ours, if there is one, is removed.</summary>
    CoveredElsewhere,

    /// <summary>
    /// Its pods ask to be scraped by annotation, which the stack's own job honours. Ours, if there is
    /// one, is removed.
    /// </summary>
    Annotated,

    /// <summary>No pods of it are running. Nothing is created; ours, if there is one, stays.</summary>
    NotRunning,

    /// <summary>It runs, and exposes no metrics port. Nothing is created; ours, if there is one, stays.</summary>
    NotExposed,

    /// <summary>A NetworkPolicy keeps Prometheus out. Nothing is created; ours, if there is one, stays.</summary>
    BlockedByNetworkPolicy,

    /// <summary>
    /// Whether Prometheus would pick the monitor up cannot be told, or it certainly would not.
    /// Nothing is created; ours, if there is one, stays. One exception: where two Services lead a
    /// ServiceMonitor to the same pod port, ours would scrape it twice itself, and ours goes.
    /// </summary>
    NotSelectable,

    /// <summary>There is nothing for EntKube to create.</summary>
    Info,
}

/// <summary>What the next apply does about a target's monitor.</summary>
public enum MonitorChange
{
    None,
    Create,
    Update,

    /// <summary>Deleted and created again, because an apply cannot undo what was done to it.</summary>
    Replace,
    Remove,
}

/// <summary>One endpoint a monitor scrapes, as it runs now.</summary>
public sealed record PlannedEndpoint(string Pod, string Address, string Path);

/// <summary>One target and what is true of its monitor, for a person to read.</summary>
/// <param name="Key">The target's key.</param>
/// <param name="Title">Its name.</param>
/// <param name="Kind">What sort of thing it is.</param>
/// <param name="Namespace">Where it runs, and where its monitor is.</param>
/// <param name="Monitor">The monitor it has or should have; null where there is none to have.</param>
/// <param name="State">Who scrapes it, or what stops it.</param>
/// <param name="Change">What the next apply does.</param>
/// <param name="Detail">A full sentence: what is scraped by whom, or what blocks it and what to do.</param>
/// <param name="Endpoints">Where it is scraped, for the states where that is known.</param>
/// <param name="TargetsUp">How many of Prometheus's targets for our monitor are up. Managed rows only.</param>
/// <param name="TargetsTotal">How many targets Prometheus has for our monitor. Managed rows only.</param>
public sealed record MonitorPlanRow(
    string Key,
    string Title,
    string Kind,
    string Namespace,
    MonitorIdentity? Monitor,
    MonitorState State,
    MonitorChange Change,
    string Detail,
    IReadOnlyList<PlannedEndpoint> Endpoints,
    int? TargetsUp = null,
    int? TargetsTotal = null)
{
    /// <summary>The monitor's name, or null where there is none to have.</summary>
    public string? MonitorName => Monitor?.Name;
}

/// <summary>A monitor to write.</summary>
/// <param name="Identity">Which one.</param>
/// <param name="Manifest">What it should say.</param>
/// <param name="Change">Whether it is new, different, or has to be deleted first.</param>
public sealed record PlannedMonitor(MonitorIdentity Identity, JsonObject Manifest, MonitorChange Change);

/// <summary>Whether there was anything to plan.</summary>
public enum MonitorPlanScope
{
    /// <summary>Prometheus and its monitor kinds are there.</summary>
    Planned,

    /// <summary>This cluster has no Prometheus EntKube installed. Nothing is created or removed.</summary>
    NoPrometheus,

    /// <summary>The cluster does not know the monitor kinds (yet). Nothing is created or removed.</summary>
    NoMonitorKinds,
}

/// <summary>What the monitors on one cluster are, and what would change.</summary>
/// <param name="Rows">One row per target, in the order they are known.</param>
/// <param name="Apply">Only the monitors that differ from what they should be.</param>
/// <param name="Prune">
/// Monitors of ours to remove: their owner is gone, or something else scrapes the same endpoint.
/// </param>
public sealed record ScrapeMonitorPlan(
    IReadOnlyList<MonitorPlanRow> Rows,
    IReadOnlyList<PlannedMonitor> Apply,
    IReadOnlyList<MonitorIdentity> Prune)
{
    public MonitorPlanScope Scope { get; init; } = MonitorPlanScope.Planned;

    public bool HasChanges => Apply.Count > 0 || Prune.Count > 0;

    /// <summary>
    /// What is deleted, replaced ones included, as one input for kubectl. Empty when nothing is.
    /// </summary>
    public IReadOnlyList<MonitorIdentity> Delete =>
        [.. Prune, .. Apply.Where(m => m.Change == MonitorChange.Replace).Select(m => m.Identity)];

    /// <summary>A plan for a cluster that has nothing to plan against.</summary>
    public static ScrapeMonitorPlan Nothing(MonitorPlanScope scope) => new([], [], []) { Scope = scope };
}
