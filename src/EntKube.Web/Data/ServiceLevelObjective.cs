namespace EntKube.Web.Data;

/// <summary>What an objective counts as a good event.</summary>
public enum SloKind
{
    /// <summary>Runs of a synthetic check that passed.</summary>
    SyntheticUptime = 0,

    /// <summary>A service's inbound requests that did not error.</summary>
    ServiceAvailability = 1,

    /// <summary>
    /// Five-minute slices with traffic in which the service's p95 latency was at or under a threshold.
    /// Time slices rather than requests, because the store reports a p95 per bucket and not how many
    /// requests were under a given latency.
    /// </summary>
    ServiceLatency = 2,
}

/// <summary>
/// A service level objective: the share of good events promised over a rolling window, and the error
/// budget that leaves.
///
/// <para>Separate from <see cref="SlaTarget"/>, which is the support agreement's availability figure and
/// is invoiced against. This is an engineering target, set and moved by the team that owns a service.</para>
///
/// <para><see cref="ClusterId"/> is where an incident is filed. For a synthetic objective it is the
/// check's; for a service it is where the service runs. Soft references throughout.</para>
/// </summary>
public class ServiceLevelObjective
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public SloKind Kind { get; set; }

    /// <summary>The share of good events promised, e.g. 99.9.</summary>
    public double TargetPercent { get; set; } = 99.9;

    public int WindowDays { get; set; } = 30;

    public Guid? CheckId { get; set; }
    public Guid ClusterId { get; set; }
    public string? Service { get; set; }

    /// <summary>For <see cref="SloKind.ServiceLatency"/>: the p95 a slice must be at or under.</summary>
    public double? LatencyThresholdMs { get; set; }

    /// <summary>Raise an incident once this much of the budget is spent, or null for no alert.</summary>
    public double? AlertAtBudgetSpentPercent { get; set; }

    public string Severity { get; set; } = "warning";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One objective's good and total events for one UTC day. Kept because the raw data is not: synthetic
/// runs last two weeks and spans a month, while a window can be ninety days.
/// </summary>
public class SloDailyTally
{
    public Guid SloId { get; set; }
    public DateOnly Day { get; set; }
    public long Good { get; set; }
    public long Total { get; set; }
    public DateTime ComputedAt { get; set; }
}
