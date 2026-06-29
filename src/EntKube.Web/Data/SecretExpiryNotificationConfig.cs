namespace EntKube.Web.Data;

/// <summary>
/// Per-tenant configuration for expiring-secret notifications. Controls whether the
/// background scanner notifies operations about vault secrets (certificates and
/// OAuth/OIDC client credentials) approaching expiry, and at which lead times.
///
/// Lead times are stored as a comma-separated list of "days before expiry" values
/// (e.g. "30,14,7,1"). Each threshold fires once per secret per expiry cycle; a
/// renewed secret (new expiry date) starts a fresh cycle. Notifications are sent
/// through the tenant's configured notification channels, honoring alert routing rules.
/// </summary>
public class SecretExpiryNotificationConfig
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// When set, this is a customer-owned config (managed in the portal) covering only
    /// that customer's secrets. When null, it's the tenant/ops-level config covering all
    /// of the tenant's secrets. One config per (TenantId, CustomerId).
    /// </summary>
    public Guid? CustomerId { get; set; }

    /// <summary>When false, the background scanner skips this scope. Defaults to off.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Comma-separated lead times in days before expiry at which to notify
    /// (e.g. "30,14,7,1"). An expired secret always notifies (the "0" threshold).
    /// </summary>
    public string ThresholdDaysCsv { get; set; } = "30,14,7,1";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Parses <see cref="ThresholdDaysCsv"/> into a distinct, descending list of
    /// non-negative day thresholds. Invalid entries are ignored. Always includes 0
    /// (notify on/after expiry).
    /// </summary>
    public IReadOnlyList<int> Thresholds()
    {
        HashSet<int> days = [0];
        foreach (string part in ThresholdDaysCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int d) && d >= 0)
            {
                days.Add(d);
            }
        }
        return days.OrderByDescending(d => d).ToList();
    }
}
