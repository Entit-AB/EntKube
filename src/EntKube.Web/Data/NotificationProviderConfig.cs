namespace EntKube.Web.Data;

public enum NotificationProviderType { Smtp, MsTeamsGraph }

public class NotificationProviderConfig
{
    public Guid Id { get; set; }
    public NotificationProviderType ProviderType { get; set; }
    public required string ConfigurationJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
}
