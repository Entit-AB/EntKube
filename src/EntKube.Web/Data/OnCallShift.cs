namespace EntKube.Web.Data;

public class OnCallShift
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public required string AssigneeName { get; set; }
    public string? AssigneeEmail { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string? Notes { get; set; }

    public OnCallSchedule Schedule { get; set; } = null!;
}
