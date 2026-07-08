namespace EntKube.Web.Data;

/// <summary>
/// User-driven lifecycle state for an advisor finding. Findings themselves are
/// computed on read (never persisted); this row persists only the human decision
/// about one — acknowledged, snoozed, dismissed, assigned — plus first/last-seen
/// timestamps used for aging and escalation. Keyed by the finding's stable
/// synthetic <see cref="FindingKey"/> (e.g. "secret:{id}", "slo-breach:{id}").
/// </summary>
public enum AdvisorFindingStatus
{
    /// <summary>Default — open and unhandled.</summary>
    Active,
    /// <summary>Someone is on it; still counts, but flagged as handled.</summary>
    Acknowledged,
    /// <summary>Hidden until <see cref="AdvisorFindingState.SnoozedUntil"/> passes.</summary>
    Snoozed,
    /// <summary>Accepted/ignored; hidden unless explicitly shown.</summary>
    Dismissed,
}

public class AdvisorFindingState
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The finding's stable synthetic id (<c>OperationsFinding.Id</c>).</summary>
    public required string FindingKey { get; set; }

    public AdvisorFindingStatus Status { get; set; } = AdvisorFindingStatus.Active;

    /// <summary>When a snooze expires and the finding should resurface. Null unless snoozed.</summary>
    public DateTime? SnoozedUntil { get; set; }

    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Free-text owner (user name/email) currently handling this finding.</summary>
    public string? AssignedTo { get; set; }

    public string? Note { get; set; }

    /// <summary>First time the finding was observed — drives aging/escalation.</summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Most recent scan/observation. Stale rows are pruned so recurrences start fresh.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
