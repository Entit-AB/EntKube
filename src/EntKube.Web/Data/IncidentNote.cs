namespace EntKube.Web.Data;

public class IncidentNote
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public required string Author { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AlertIncident Incident { get; set; } = null!;
}
