namespace EntKube.Web.Data;

public class OnCallSchedule
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The support window this rotation exists to staff. §10.1.1 makes S3 and S4 a promise
    /// of a jourrotation being there; knowing which window a schedule covers is what lets
    /// the system answer "is next weekend actually staffed" before the customer finds out
    /// it was not. Null for a rotation that is not tied to a window.
    /// </summary>
    public SupportWindow? Covers { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public List<OnCallShift> Shifts { get; set; } = [];
}
