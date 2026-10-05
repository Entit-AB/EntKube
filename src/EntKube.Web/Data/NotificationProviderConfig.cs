namespace EntKube.Web.Data;

public enum NotificationProviderType { Smtp, MsTeamsGraph }

/// <summary>
/// How one tenant reaches the outside world to deliver a notification — the SMTP server
/// it sends through, or the Microsoft Graph app registration it posts to Teams with.
///
/// <para><b>Per tenant, not per installation.</b> These rows hold credentials for someone
/// else's mail server and someone else's Entra directory. One tenant's Graph client secret
/// is not a thing another tenant may send with, and a single shared SMTP relay cannot be
/// authorised to send as every tenant's domain — SPF and DKIM are published per domain, so
/// a shared relay is precisely the configuration that gets the mail junked.</para>
/// </summary>
public class NotificationProviderConfig
{
    public Guid Id { get; set; }

    /// <summary>
    /// Owning tenant. Unique together with <see cref="ProviderType"/>: a tenant configures
    /// each provider at most once.
    /// </summary>
    public Guid TenantId { get; set; }

    public NotificationProviderType ProviderType { get; set; }
    public required string ConfigurationJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
