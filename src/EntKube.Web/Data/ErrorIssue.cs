namespace EntKube.Web.Data;

/// <summary>Where an error was seen.</summary>
public enum ErrorIssueSource
{
    /// <summary>An error or fatal log line from a cluster.</summary>
    Log = 0,

    /// <summary>An uncaught JavaScript error from a RUM site.</summary>
    Browser = 1,
}

/// <summary>What a person has decided about an issue.</summary>
public enum ErrorIssueStatus
{
    Open = 0,

    /// <summary>Believed fixed. Seen again after this, it reopens as a regression.</summary>
    Resolved = 1,

    /// <summary>Known and accepted. It goes on counting, and never reopens on its own.</summary>
    Ignored = 2,
}

/// <summary>
/// One fault, however many times it happened. Lines become an issue by their
/// <see cref="Fingerprint"/>, which leaves out whatever varies from one occurrence to the next.
///
/// <para><see cref="ScopeId"/> is a cluster for <see cref="ErrorIssueSource.Log"/> and a RUM site for
/// <see cref="ErrorIssueSource.Browser"/>, with no foreign key to either: an issue records what happened,
/// and a resolved one still means something after its cluster is gone.</para>
///
/// <para>⚠️ <see cref="Title"/> and <see cref="SampleMessage"/> are log text. A browser error's message
/// can be set by anybody who can run script on the page, so treat both as untrusted.</para>
/// </summary>
public class ErrorIssue
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public ErrorIssueSource Source { get; set; }
    public Guid ScopeId { get; set; }

    /// <summary>The cluster or site's name when this was first seen.</summary>
    public string ScopeName { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    /// <summary>The normalized message, which reads the same for every occurrence.</summary>
    public string Title { get; set; } = "";

    public string? Namespace { get; set; }
    public string? Workload { get; set; }

    /// <summary>One real occurrence, the most recent seen.</summary>
    public string SampleMessage { get; set; } = "";

    public string? SampleTraceId { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public long Occurrences { get; set; }

    /// <summary>
    /// True when some scan hit its limit, so <see cref="Occurrences"/> is a lower bound. Sticky: one
    /// undercount makes the running total a lower bound for good.
    /// </summary>
    public bool CountIsPartial { get; set; }

    public ErrorIssueStatus Status { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    /// <summary>When it last came back after being resolved, if it ever has.</summary>
    public DateTime? RegressedAt { get; set; }

    public int Regressions { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>How far a scope has been scanned. The row is what stops two instances counting one window twice.</summary>
public class ErrorScanCursor
{
    public Guid ScopeId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime ScannedThrough { get; set; }
}
