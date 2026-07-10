namespace EntKube.Web.Data;

public enum NotificationProviderType { Smtp, MsTeamsGraph }

public class NotificationProviderConfig
{
    public Guid Id { get; set; }

    /// <summary>Owning tenant. Each tenant configures its own SMTP server and
    /// Microsoft Graph app registration, so credentials are scoped per tenant.</summary>
    public Guid TenantId { get; set; }

    public NotificationProviderType ProviderType { get; set; }
    public required string ConfigurationJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
