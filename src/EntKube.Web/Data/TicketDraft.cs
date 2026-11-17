namespace EntKube.Web.Data;

/// <summary>What a draft is a draft of, and who can see it.</summary>
public enum TicketDraftKind
{
    /// <summary>The note one person is typing on one ticket, saved as they type. Only theirs.</summary>
    PersonalNote = 0,

    /// <summary>A note left on a ticket for a colleague to pick up. One per ticket.</summary>
    SharedNote = 1,

    /// <summary>A named, half-filled new ticket the whole tenant can pick up.</summary>
    SharedNewTicket = 2,
}

/// <summary>
/// Unfinished work kept on the server — Zammad's autosave and shared drafts.
///
/// <para><b>On the server, not in the browser</b>, because the point is that it outlives the tab:
/// a reload, a dropped connection, another instance behind the load balancer, or a colleague
/// taking over. Nothing in a draft is on the ticket's history until it is actually added there;
/// a draft is not a record between the parties, and so it can be overwritten and deleted.</para>
///
/// <para><b>The tenant is a column, not a foreign key.</b> A note draft hangs off its ticket and
/// cascades with it; a second cascade path from the tenant is one SQL Server refuses, so the
/// tenant purge deletes drafts itself (as it does ticket links).</para>
/// </summary>
public class TicketDraft
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public TicketDraftKind Kind { get; set; }

    /// <summary>The ticket, for a note draft. Null for a new-ticket draft.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>Whose it is, for a personal draft. Null for a shared one.</summary>
    public string? OwnerUserId { get; set; }

    /// <summary>A new-ticket draft's name, so it can be found in the list.</summary>
    public string? Name { get; set; }

    /// <summary>The note's text, or the new ticket's description.</summary>
    public string Text { get; set; } = "";

    public bool CustomerVisible { get; set; }

    // A new ticket's other fields.
    public string? Title { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? AppId { get; set; }
    public TicketPriority? Priority { get; set; }
    public string Tags { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public Ticket? Ticket { get; set; }
}
