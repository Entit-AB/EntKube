namespace EntKube.Web.Data;

/// <summary>How often a tenant receives the Operations Advisor digest.</summary>
public enum DigestFrequency
{
    Off,
    Daily,
    Weekly,
}

/// <summary>
/// Per-tenant Operations Advisor digest settings. Persisting <see cref="LastSentAt"/> makes
/// the cadence restart-safe (no double-send after a redeploy) and enables weekly delivery.
/// A tenant with no row falls back to the global <c>Advisor:Digest:*</c> configuration.
/// </summary>
public class AdvisorDigestConfig
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public DigestFrequency Frequency { get; set; } = DigestFrequency.Daily;

    /// <summary>Hour of day (UTC, 0–23) at or after which the digest is sent.</summary>
    public int HourUtc { get; set; } = 7;

    /// <summary>Day the weekly digest goes out (ignored unless <see cref="Frequency"/> is Weekly).</summary>
    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Monday;

    /// <summary>When the digest was last actually sent — the guard against re-sending within a period.</summary>
    public DateTime? LastSentAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
