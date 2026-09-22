namespace EntKube.Web.Data;

/// <summary>Where an inbound message got to in triage.</summary>
public enum MailTriageState
{
    /// <summary>Received, not yet looked at by anyone or anything.</summary>
    Received = 0,

    /// <summary>The analyst has proposed what to do; a person has not decided yet.</summary>
    Proposed = 1,

    /// <summary>A person acted on it.</summary>
    Handled = 2,

    /// <summary>A person decided it needed nothing — spam, a bounce, an autoreply.</summary>
    Dismissed = 3,
}

/// <summary>
/// A message that arrived in the support mailbox.
///
/// <para>§14.3 accepts e-mail as a reporting channel for P3 and P4, and in practice
/// everything arrives there first. Keeping the message itself, rather than only the ticket
/// it became, matters because §14.6 makes the portal's timestamps the record between the
/// parties — and "when did you actually receive this" is the first thing asked when a
/// response time is disputed.</para>
/// </summary>
public class InboundMailMessage
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The provider's own identifier, so the same message is not ingested twice.</summary>
    public required string MessageId { get; set; }

    /// <summary>The thread this belongs to, from the mail headers, when there is one.</summary>
    public string? InReplyTo { get; set; }

    public required string FromAddress { get; set; }

    public string? FromName { get; set; }

    public required string Subject { get; set; }

    public string Body { get; set; } = "";

    /// <summary>
    /// When the message was sent, from the mail itself — not when we happened to fetch it.
    /// This is what a response time is counted from.
    /// </summary>
    public DateTime SentAt { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public MailTriageState State { get; set; } = MailTriageState.Received;

    /// <summary>The customer the sender was matched to, when the address was recognised.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>The ticket this message ended up on.</summary>
    public Guid? TicketId { get; set; }

    public string? HandledBy { get; set; }

    public DateTime? HandledAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer? Customer { get; set; }
    public Ticket? Ticket { get; set; }
    public List<MailSuggestion> Suggestions { get; set; } = [];
}

/// <summary>What the analyst thinks should happen to a message.</summary>
public enum MailSuggestionKind
{
    /// <summary>Open a new ticket for it.</summary>
    OpenTicket = 0,

    /// <summary>Add it to a ticket that already exists.</summary>
    AppendToTicket = 1,

    /// <summary>A priority, with the §14.2 criteria it matched.</summary>
    ProposePriority = 2,

    /// <summary>A drafted reply, for a person to read before it goes anywhere.</summary>
    DraftReply = 3,

    /// <summary>The message names a third party we are waiting on; §14.4 may allow a pause.</summary>
    SuggestPause = 4,

    /// <summary>This reads like §15 new development rather than management.</summary>
    FlagDevelopment = 5,

    /// <summary>The customer's hour bank is spent, so §11.1's approval gate applies.</summary>
    FlagTimebank = 6,

    /// <summary>The sender was not recognised.</summary>
    FlagUnknownSender = 7,
}

/// <summary>Whether a suggestion has been acted on.</summary>
public enum MailSuggestionState
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2,
}

/// <summary>
/// One thing the analyst proposes, waiting for a person to agree.
///
/// <para><b>Everything here is a proposal.</b> §14.3 makes confirming a priority a written,
/// reasoned act, §14.4 makes a resolution something the customer accepts, and §14.6 attaches
/// 10% of the window fee to a mis-clocked P1. None of those are judgements to hand to a
/// keyword match or a language model. What the analyst is good at is reading the queue
/// quickly and having the paperwork ready; the deciding stays with a person, and the
/// decision is recorded with their name on it.</para>
/// </summary>
public class MailSuggestion
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public MailSuggestionKind Kind { get; set; }

    /// <summary>What is being proposed, in a line an operator can act on.</summary>
    public required string Summary { get; set; }

    /// <summary>
    /// Why. For a priority this is the §14.2 wording it matched — the reasoning has to be
    /// visible, because a person is about to put their name to it.
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>The drafted text, for a suggestion that carries one.</summary>
    public string? DraftText { get; set; }

    /// <summary>The priority proposed, for <see cref="MailSuggestionKind.ProposePriority"/>.</summary>
    public TicketPriority? Priority { get; set; }

    /// <summary>The ticket involved, for the suggestions that name one.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>The application the message appears to be about.</summary>
    public Guid? AppId { get; set; }

    public MailSuggestionState State { get; set; } = MailSuggestionState.Pending;

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public InboundMailMessage Message { get; set; } = null!;
}
