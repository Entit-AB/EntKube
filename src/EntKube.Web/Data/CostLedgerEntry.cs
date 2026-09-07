namespace EntKube.Web.Data;

/// <summary>
/// One day of accrued cost for one namespace on one cluster — the ledger the cost
/// history is read from.
///
/// <para><b>This is an accrual, not a sample of the run rate.</b> The run rate answers
/// "what would a month at today's consumption cost"; it is a projection, and reading it
/// once a month would bill a workload that ran for three days as though it ran for
/// thirty — or miss it entirely if it was gone by the time anyone looked. Each sweep
/// therefore books the hours that have actually elapsed since the previous one, priced
/// at the rate measured for that period. Summing <see cref="TotalCost"/> over a month
/// gives what was really consumed.</para>
///
/// <para><b>Daily grain, accrued hourly.</b> The hourly sweep adds into the row for the
/// current UTC day rather than writing a row of its own, which is the difference between
/// a table of a few thousand rows a month and one of a few hundred thousand. Intra-day
/// resolution would tell nobody anything the live run rate does not already show.</para>
///
/// <para><b>Ownership is denormalised on purpose.</b> The customer, app and environment
/// are copied in as they were when the cost was incurred, and the row keeps bare ids
/// rather than foreign keys. A statement for June must not change its mind in July
/// because an app was renamed, moved to another customer, or deleted — a ledger that
/// re-derives the past from present relationships is not a record of anything. The only
/// relationship kept is the tenant, because a purged tenant is meant to leave nothing
/// behind.</para>
/// </summary>
public class CostLedgerEntry
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The cluster the cost was incurred on. A plain id, not a foreign key: removing a
    /// cluster from EntKube must not delete the record of what it cost while it ran.
    /// </summary>
    public Guid ClusterId { get; set; }

    /// <summary>The cluster's name when the cost was incurred.</summary>
    public string ClusterName { get; set; } = "";

    public string Namespace { get; set; } = "";

    /// <summary>
    /// The UTC day this accrual belongs to, at midnight. A span crossing midnight is
    /// split across two rows in proportion to the time either side of it, so a day's
    /// total is genuinely that day's.
    /// </summary>
    public DateTime Day { get; set; }

    // ── Ownership, as it stood when the cost was incurred ──

    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }

    /// <summary>The app this namespace's cost rolls up to, or null when several share it.</summary>
    public Guid? AppId { get; set; }
    public string? AppName { get; set; }

    public string? EnvironmentName { get; set; }

    /// <summary>
    /// True when several apps shared this namespace, so the cost is attributable to the
    /// customer but not to one app. Recorded rather than inferred from a null
    /// <see cref="AppId"/>, which would be indistinguishable from a platform namespace.
    /// </summary>
    public bool IsMultiApp { get; set; }

    /// <summary>
    /// True when this namespace's own cost was pooled and charged out to the billable
    /// namespaces on its cluster. Kept for auditability — the pool has to be explicable —
    /// but <b>every total must exclude these rows</b> or the same money is counted twice.
    /// </summary>
    public bool IsRedistributed { get; set; }

    // ── Coverage ──

    /// <summary>
    /// Hours of consumption this row accounts for. Not necessarily 24 even for a complete
    /// day: a namespace that appeared at noon accrues twelve, and hours nothing measured
    /// are not billed. <see cref="CostLedgerCoverage"/> is what separates those two cases.
    /// </summary>
    public decimal Hours { get; set; }

    // ── Accrued consumption, as resource-hours ──
    //
    // Stored as hours rather than as an average so that partial days and gaps add up
    // correctly: a mean over a period that was only half measured is not a figure that
    // can be summed with its neighbours. The average is recovered by dividing by Hours.

    public double CpuCoreHours { get; set; }
    public double MemoryGiBHours { get; set; }
    public double StorageGiBHours { get; set; }
    public double LoadBalancerHours { get; set; }
    public double PublicIpHours { get; set; }

    // ── Accrued money ──

    public decimal CpuCost { get; set; }
    public decimal MemoryCost { get; set; }
    public decimal StorageCost { get; set; }

    /// <summary>Load balancers and public addresses this namespace provisioned.</summary>
    public decimal NetworkCost { get; set; }

    /// <summary>Its share of the platform namespaces and fixed cluster fees.</summary>
    public decimal SharedCost { get; set; }

    /// <summary>What this namespace consumed in its own right.</summary>
    public decimal DirectCost => CpuCost + MemoryCost + StorageCost + NetworkCost;

    public decimal TotalCost => DirectCost + SharedCost;

    /// <summary>
    /// The currency the amounts are in, recorded per row: a tenant can reprice a cluster
    /// or run clusters in different currencies, and a historical amount means nothing
    /// without the unit it was booked in.
    /// </summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Whether the compute figures were charged on requests or on actual usage. The
    /// basis is a per-cluster setting that can be changed, and a past amount is not
    /// explicable without knowing which one produced it.
    /// </summary>
    public bool ChargedOnRequests { get; set; }

    /// <summary>When this row was last accrued into. Diagnostic.</summary>
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
/// How much of a day the ledger actually measured, per cluster.
///
/// <para>Without this a chart lies. An hour the management plane was down accrues
/// nothing, and a day missing six hours of accrual looks exactly like a day the
/// workloads were smaller — so "the bill went down" and "we stopped watching" become
/// the same picture. Recording coverage separately is what lets the history say which
/// one happened.</para>
///
/// <para>One row per cluster per day: a few hundred rows a year, against which the
/// alternative — inferring coverage from the maximum <see cref="CostLedgerEntry.Hours"/>
/// on the day — would be wrong for any namespace that legitimately came and went.</para>
/// </summary>
public class CostLedgerCoverage
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>A plain id for the same reason as on the entry: the record outlives the cluster.</summary>
    public Guid ClusterId { get; set; }

    public string ClusterName { get; set; } = "";

    /// <summary>The UTC day, at midnight.</summary>
    public DateTime Day { get; set; }

    /// <summary>Hours of this day that were measured and billed.</summary>
    public decimal CoveredHours { get; set; }

    /// <summary>
    /// Hours of this day that elapsed without a measurement close enough to bill for
    /// them — the management plane was down, or the cluster could not be read. Billed to
    /// nobody, and stated rather than quietly rolled into the covered figure.
    /// </summary>
    public decimal GapHours { get; set; }

    /// <summary>Number of sweeps that contributed to this day. Diagnostic.</summary>
    public int SampleCount { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
/// The high-water mark of the ledger for one cluster: the instant up to which cost has
/// already been accrued.
///
/// <para>This is what makes the accrual safe to run from more than one place. Each sweep
/// claims the span from <see cref="LastSampleAt"/> to now with a conditional update, and
/// a writer that loses the race sees no rows affected and books nothing — so a second
/// management-plane instance produces no cost at all rather than a second copy of it.
/// Double-billing is the failure this table exists to prevent; the claim is taken
/// <em>before</em> the rows are written, so the surviving failure mode is an hour lost to
/// a crash mid-write, which shows up in <see cref="CostLedgerCoverage"/> instead of
/// silently inflating someone's invoice.</para>
/// </summary>
public class CostLedgerCursor
{
    public Guid Id { get; set; }

    public Guid ClusterId { get; set; }

    /// <summary>
    /// The end of the last period accrued. The first sweep for a cluster sets this and
    /// bills nothing: there is no measurement covering the time before it, and inventing
    /// one would put a guess in the ledger.
    /// </summary>
    public DateTime LastSampleAt { get; set; }

    // Navigation
    public KubernetesCluster Cluster { get; set; } = null!;
}
