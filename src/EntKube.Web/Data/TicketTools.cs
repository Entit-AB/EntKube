namespace EntKube.Web.Data;

/// <summary>
/// A reusable piece of text an agent drops into a note or reply — Zammad's text module.
///
/// <para>The content may carry placeholders such as <c>#{ticket.number}</c> or
/// <c>#{requester.name}</c>, filled in when it is inserted (<c>TextModuleRenderer</c>). A module
/// can be narrowed to one customer; one with no customer is offered on every ticket. There are no
/// groups here (docs/helpdesk.md, decision 1), so "per group" became "per customer".</para>
/// </summary>
public class TextModule
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Offered only on this customer's tickets, or on every ticket when null.</summary>
    public Guid? CustomerId { get; set; }

    public required string Name { get; set; }

    /// <summary>Words it is found by besides its name, space-separated.</summary>
    public string Keywords { get; set; } = "";

    public string Content { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
/// Prefilled answers for the agent's new-ticket form — Zammad's ticket template. Applying one
/// fills only the fields still empty, so it can never overwrite what the agent already typed.
/// No placeholders (Zammad's have none either): a template describes a kind of ticket, not one.
/// </summary>
public class TicketTemplate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    /// <summary>The priority to propose. Still a proposal: confirming it is the §14.3 act.</summary>
    public TicketPriority? Priority { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? AppId { get; set; }

    /// <summary>Tags to add, space- or comma-separated.</summary>
    public string Tags { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}

/// <summary>Who a macro hands the ticket to.</summary>
public enum MacroAssign
{
    /// <summary>Leaves the assignee alone.</summary>
    Unchanged = 0,

    /// <summary>The person running the macro.</summary>
    Me = 1,

    /// <summary>Back to the queue.</summary>
    Nobody = 2,
}

/// <summary>Where the screen goes after a macro has run on one ticket.</summary>
public enum MacroAfterRun
{
    StayOnTicket = 0,

    BackToQueue = 1,
}

/// <summary>
/// A named bundle of changes run on a ticket in one click — Zammad's macro.
///
/// <para><b>Typed columns, not a list of free-form actions</b>, because each thing a macro may do
/// here has rules of its own: a priority needs the written reason §14.3 requires (so the macro
/// carries one), a resolution needs its text, and only a resolved ticket may be closed. A JSON
/// action list would have to re-implement those checks for every shape it could hold.</para>
///
/// <para><b>What a macro cannot do</b>, by design: send anything to the customer (Zammad's cannot
/// either), pause a clock (the agreement wants the party and the reason written down for each
/// pause), or reject a ticket (§14.4 wants the system the fault lay in). A note the customer sees
/// in the portal is allowed, and says so in the editor.</para>
///
/// <para>Run by a person, recorded as that person — with the macro's name beside each change.</para>
/// </summary>
public class Macro
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public bool IsActive { get; set; } = true;

    public MacroAssign Assign { get; set; }

    /// <summary>Tags to add, space- or comma-separated.</summary>
    public string AddTags { get; set; } = "";

    /// <summary>Tags to remove, space- or comma-separated.</summary>
    public string RemoveTags { get; set; } = "";

    /// <summary>Set a reminder this many hours from when the macro runs. Null leaves it alone.</summary>
    public int? ReminderAfterHours { get; set; }

    public string? Note { get; set; }

    public bool NoteCustomerVisible { get; set; }

    public TicketPriority? Priority { get; set; }

    /// <summary>The written reason recorded with <see cref="Priority"/>. Required when it is set.</summary>
    public string? PriorityReason { get; set; }

    /// <summary>Resolve the ticket with this text. Null leaves the status alone.</summary>
    public string? Resolution { get; set; }

    /// <summary>Close the ticket when it is resolved (by this macro or before it).</summary>
    public bool Close { get; set; }

    public MacroAfterRun AfterRun { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}

/// <summary>
/// A ticket's checklist: the steps of the work, ticked off as they are done. At most one per
/// ticket, as in Zammad, keyed by the ticket.
/// </summary>
public class TicketChecklist
{
    public Guid TicketId { get; set; }

    public string Title { get; set; } = "Checklist";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    // Navigation
    public Ticket Ticket { get; set; } = null!;
    public List<TicketChecklistItem> Items { get; set; } = [];
}

/// <summary>
/// One step on a checklist. An item whose text names another ticket (<c>#1042</c>) is not ticked
/// by hand: it is done when that ticket is — see <c>TicketChecklists</c>.
/// </summary>
public class TicketChecklistItem
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    public required string Text { get; set; }

    public bool IsChecked { get; set; }

    public int SortOrder { get; set; }

    public string? CheckedBy { get; set; }

    public DateTime? CheckedAt { get; set; }

    // Navigation
    public TicketChecklist Checklist { get; set; } = null!;
}

/// <summary>A reusable list of steps to start a ticket's checklist from.</summary>
public class ChecklistTemplate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    /// <summary>One item per line.</summary>
    public string Items { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
