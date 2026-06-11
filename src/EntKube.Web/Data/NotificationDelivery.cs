namespace EntKube.Web.Data;

public class NotificationDelivery
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public Guid ChannelId { get; set; }
    public bool IsFiring { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string? Error { get; set; }

    public AlertIncident Incident { get; set; } = null!;
    public NotificationChannel Channel { get; set; } = null!;
}
