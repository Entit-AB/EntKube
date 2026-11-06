namespace EntKube.Web.Data;

/// <summary>What a check's last run found.</summary>
public enum SyntheticCheckState
{
    /// <summary>It has not run yet.</summary>
    Unknown = 0,
    Passing = 1,
    Failing = 2,
}

/// <summary>
/// A URL EntKube requests on a schedule, from outside the application, to prove it still answers.
/// Unlike the route health probes this is user-defined: any public URL, with an expected status and
/// text.
///
/// <para><b>Why it names a cluster.</b> An incident belongs to a cluster, and alerts on a check go
/// through the same rules, maintenance windows and routing as everything else. The check is therefore
/// paired with a <see cref="TelemetryAlertRule"/> of kind <c>SyntheticCheck</c>, kept in step by
/// <c>SyntheticCheckService</c>. <see cref="ClusterId"/> is where the incident is filed, not where the
/// request comes from: the request always leaves from the management plane.</para>
///
/// <para>⚠️ Deliberately no custom headers, so there are no credentials to store. And the response
/// body is never kept or shown: this runs from EntKube's network position, so only a yes or no about
/// the expected text leaves the probe.</para>
/// </summary>
public class SyntheticCheck
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The cluster an incident is filed against. A soft reference, as on <see cref="RumSite"/>.</summary>
    public Guid ClusterId { get; set; }

    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>The exact status expected, or null for any 2xx or 3xx.</summary>
    public int? ExpectedStatus { get; set; }

    /// <summary>Text the response body must contain, or null to not read the body.</summary>
    public string? ExpectedText { get; set; }

    public int IntervalSeconds { get; set; } = 300;

    /// <summary>How many failures in a row raise an incident, so one blip pages nobody.</summary>
    public int FailuresBeforeAlert { get; set; } = 2;

    /// <summary>Incident severity: critical, warning or info.</summary>
    public string Severity { get; set; } = "warning";

    public bool IsEnabled { get; set; } = true;

    public SyntheticCheckState State { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime NextRunAt { get; set; }
    public int? LastStatusCode { get; set; }
    public int? LastDurationMs { get; set; }

    /// <summary>Why the last run failed, from a fixed set of phrases. Never exception text.</summary>
    public string? LastFailure { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One run of a check. Kept briefly, for uptime; a restored server simply starts again.</summary>
public class SyntheticCheckResult
{
    public long Id { get; set; }
    public Guid CheckId { get; set; }
    public DateTime At { get; set; }
    public bool Passed { get; set; }
    public int? StatusCode { get; set; }
    public int DurationMs { get; set; }
}
