namespace EntKube.Web.Data;

/// <summary>Who is covering a shift — our own staff, or somebody under §18.</summary>
public enum OnCallAffiliation
{
    /// <summary>Our own employee.</summary>
    Employee = 0,

    /// <summary>A subconsultant engaged under §18, bound by §17's terms and approved.</summary>
    Subconsultant = 1,

    /// <summary>A partner company covering under the same terms.</summary>
    Partner = 2,
}

public class OnCallShift
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public required string AssigneeName { get; set; }
    public string? AssigneeEmail { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    /// <summary>
    /// A phone number. At 03:00 an e-mail address is not a way to reach a person, and §14.3
    /// requires us to make contact over Teams for a P1 or P2 — so the roster has to carry
    /// more than a mailto.
    /// </summary>
    public string? AssigneePhone { get; set; }

    public string? AssigneeTeamsHandle { get; set; }

    /// <summary>
    /// Whether this shift is covered by our own staff or by a subconsultant. §18 allows
    /// subconsultants for on-call duty and requires a current register of them, so which
    /// shifts they cover is worth knowing at a glance rather than by recognising names.
    /// </summary>
    public OnCallAffiliation Affiliation { get; set; } = OnCallAffiliation.Employee;

    /// <summary>The subconsultant covering this shift, when one is.</summary>
    public Guid? SubconsultantId { get; set; }

    /// <summary>What is running hot as this shift ends.</summary>
    public string? HandoverNotes { get; set; }

    public string? Notes { get; set; }

    public OnCallSchedule Schedule { get; set; } = null!;
    public Subconsultant? Subconsultant { get; set; }
}
