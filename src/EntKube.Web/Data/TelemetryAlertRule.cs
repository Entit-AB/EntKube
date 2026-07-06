namespace EntKube.Web.Data;

/// <summary>What a telemetry alert rule evaluates over the native store.</summary>
public enum TelemetryAlertKind
{
    /// <summary>% of a service's inbound spans (SERVER/CONSUMER or trace-entry) that error, over the window.</summary>
    TraceErrorRate,
    /// <summary>A service's p95 span latency (ms) over the window.</summary>
    TraceLatencyP95,
    /// <summary>Rate of Error+Fatal logs (optionally namespace/text-scoped) per minute over the window.</summary>
    LogErrorRate,
    /// <summary>Rate of browser JS errors per minute for a RUM site over the window.</summary>
    RumErrorRate,
    /// <summary>A RUM site's p75 Largest Contentful Paint (ms) over the window.</summary>
    RumLcpP75
}

/// <summary>
/// A user-defined alert rule evaluated on a schedule against the native telemetry store (logs/spans).
/// Firing raises an <see cref="AlertIncident"/> (deduped per rule+cluster) through the existing
/// incident/notification pipeline; clearing resolves it. Complements the read-only Prometheus rules.
/// </summary>
public class TelemetryAlertRule
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Target cluster, or null to evaluate against every cluster of the tenant. For the RUM kinds
    /// this is the cluster the incident is attributed to (derived from the site's cluster link).</summary>
    public Guid? ClusterId { get; set; }

    /// <summary>The RUM site to evaluate, for the RUM kinds (required for those).</summary>
    public Guid? SiteId { get; set; }

    public required string Name { get; set; }
    public TelemetryAlertKind Kind { get; set; }

    /// <summary>Service name for the trace-based kinds (required for those).</summary>
    public string? Service { get; set; }
    /// <summary>Optional namespace scope for LogErrorRate.</summary>
    public string? Namespace { get; set; }
    /// <summary>Optional case-sensitive substring the log body must contain (LogErrorRate).</summary>
    public string? MatchText { get; set; }

    /// <summary>Threshold whose unit depends on Kind: ms (p95), percent (error rate), or per-minute (log rate).</summary>
    public double Threshold { get; set; }

    /// <summary>Evaluation/look-back window in minutes.</summary>
    public int WindowMinutes { get; set; } = 5;

    /// <summary>Incident severity string (e.g. "critical", "warning", "info").</summary>
    public string Severity { get; set; } = "warning";
    public string? RunbookUrl { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
