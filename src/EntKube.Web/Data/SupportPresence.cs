namespace EntKube.Web.Data;

/// <summary>What a notification is about — the rows of Zammad's notification matrix.</summary>
public enum NotificationKind
{
    NewTicket = 0,
    Update = 1,
    Assigned = 2,
    Reminder = 3,
    Escalation = 4,
}

/// <summary>
/// The kinds of news a person also wants by mail, besides the bell (helpdesk phase 9) — the
/// e-mail column of Zammad's matrix. Which tickets is still the scope's to say: mail goes only
/// where the bell would ring.
/// </summary>
[Flags]
public enum NotifyByMail
{
    None = 0,
    NewTicket = 1,
    Update = 2,
    Assigned = 4,
    Reminder = 8,
    Escalation = 16,
}

/// <summary>
/// Which tickets a person hears about for one kind of event — the columns of Zammad's matrix.
/// Flags, so one column per kind holds the whole row.
/// </summary>
[Flags]
public enum NotifyScope
{
    None = 0,

    /// <summary>Tickets they hold — and, while they cover for an absent colleague, that colleague's.</summary>
    Mine = 1,

    /// <summary>Tickets nobody holds.</summary>
    Unassigned = 2,

    /// <summary>Tickets they follow.</summary>
    Followed = 4,

    /// <summary>Every ticket in the tenant.</summary>
    All = 8,
}

/// <summary>
/// One line in a person's notification list — the bell. Written by the follow-up sweep from the
/// ticket history, so it shows whatever reached the history however it got there.
/// </summary>
public class UserNotification
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string UserId { get; set; }

    public Guid? TicketId { get; set; }

    public int? TicketNumber { get; set; }

    public NotificationKind Kind { get; set; }

    public required string Text { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? SeenAt { get; set; }

    // Navigation
    public Ticket? Ticket { get; set; }
}

/// <summary>
/// A person's notification matrix for one tenant. No row means the defaults
/// (<c>NotificationPreferences.Defaults</c>).
/// </summary>
public class NotificationPreference
{
    public required string UserId { get; set; }

    public Guid TenantId { get; set; }

    public NotifyScope NewTicket { get; set; }

    public NotifyScope Update { get; set; }

    public NotifyScope Assigned { get; set; }

    public NotifyScope Reminder { get; set; }

    public NotifyScope Escalation { get; set; }

    /// <summary>Which of the above also come by mail. None unless chosen.</summary>
    public NotifyByMail Mail { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>How far a tenant's history has been turned into notifications. Advanced by compare-and-set.</summary>
public class NotificationCursor
{
    public Guid TenantId { get; set; }

    public DateTime Through { get; set; }
}

/// <summary>Which agreement clock an escalation notice was about.</summary>
public enum EscalationClock
{
    Response = 0,
    Resolution = 1,
    Update = 2,
}

/// <summary>
/// An escalation already announced: one notice per ticket, clock and deadline. The key is the
/// claim — a second instance inserting the same row fails, and does not notify again.
/// </summary>
public class NotifiedEscalation
{
    public Guid TicketId { get; set; }

    public EscalationClock Clock { get; set; }

    public DateTime Deadline { get; set; }

    public DateTime NotifiedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Somebody away, and who covers for them — Zammad's out of office. While it runs the substitute
/// hears about the absent person's tickets and sees them in an overview of their own, and the
/// rota passes over the absent person.
/// </summary>
public class OutOfOffice
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string UserId { get; set; }

    public DateOnly From { get; set; }

    /// <summary>The last day away, inclusive.</summary>
    public DateOnly To { get; set; }

    public string? SubstituteUserId { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Somebody with a ticket open right now, refreshed while they look at it. Rows older than the
/// heartbeat window are ignored and swept. In the database so every instance sees every viewer.
/// </summary>
public class TicketPresence
{
    public Guid TicketId { get; set; }

    public required string UserId { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    /// <summary>Writing a note or a mail on it, not only reading.</summary>
    public bool Editing { get; set; }

    public DateTime SeenAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Ticket Ticket { get; set; } = null!;
}

/// <summary>
/// A personal calendar feed of ticket deadlines. Only a hash of the token is kept; the address
/// holding the token is shown once, and resetting it makes the old address stop working.
/// </summary>
public class CalendarFeed
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string UserId { get; set; }

    public required string TokenHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
