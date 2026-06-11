namespace EntKube.Web.Data;

public enum IncidentStatus { Active, Acknowledged, Resolved }

public class AlertIncident
{
    public Guid Id { get; set; }
    public Guid ClusterId { get; set; }
    public required string Fingerprint { get; set; }
    public required string AlertName { get; set; }
    public required string Severity { get; set; }
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string RunbookUrl { get; set; } = "";
    public string LabelsJson { get; set; } = "{}";
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Active;
    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? AssignedTo { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? EscalatedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public KubernetesCluster Cluster { get; set; } = null!;
    public List<IncidentNote> Notes { get; set; } = [];
    public List<NotificationDelivery> Deliveries { get; set; } = [];
}
