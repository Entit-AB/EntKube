namespace EntKube.Web.Data;

/// <summary>
/// A record of an expiring-secret notification that was sent (or attempted). Serves
/// three purposes: a dedupe key so the background scanner notifies once per
/// (secret, threshold, expiry) cycle, an audit trail of what operations were told,
/// and the data behind the "recent notifications" history in the UI.
///
/// <see cref="SecretId"/> is intentionally not a foreign key: history rows survive
/// deletion of the underlying vault secret. <see cref="SecretName"/> is denormalized
/// for the same reason.
/// </summary>
public class SecretExpiryNotification
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The customer scope this notice belongs to (null = tenant/ops-level).</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>The vault secret this notice was about (not an FK — survives secret deletion).</summary>
    public Guid SecretId { get; set; }

    /// <summary>Denormalized secret name for display in history.</summary>
    public string SecretName { get; set; } = "";

    /// <summary>Which lead-time threshold (in days) triggered this notice. 0 means expired.</summary>
    public int ThresholdDays { get; set; }

    /// <summary>
    /// The secret's expiry instant at the time of sending. Part of the dedupe key:
    /// when a secret is renewed its expiry changes, so a new cycle of notices fires.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Days until expiry at send time (negative when already expired).</summary>
    public int DaysUntilExpiry { get; set; }

    /// <summary>True when this was a manual "send now" from the UI rather than the scanner.</summary>
    public bool Manual { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    /// <summary>Number of channels the notice was successfully delivered to.</summary>
    public int ChannelsNotified { get; set; }

    /// <summary>True when at least one channel accepted the notification.</summary>
    public bool Success { get; set; }

    /// <summary>Error detail when delivery failed (truncated). Null on success.</summary>
    public string? Error { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
