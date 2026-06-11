namespace EntKube.Web.Data;

public enum NotificationChannelType { Slack, Teams, Email, Webhook }
public enum AlertSeverityFilter { All, WarningAndAbove, CriticalOnly }

public class NotificationChannel
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public NotificationChannelType Type { get; set; }
    public required string ConfigurationJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public AlertSeverityFilter SeverityFilter { get; set; } = AlertSeverityFilter.All;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public List<NotificationDelivery> Deliveries { get; set; } = [];
}
