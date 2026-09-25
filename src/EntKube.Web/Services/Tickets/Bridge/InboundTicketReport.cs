using EntKube.Web.Data;

namespace EntKube.Web.Services.Tickets.Bridge;

/// <summary>What a delivery is telling us.</summary>
public enum InboundTicketKind
{
    /// <summary>A ticket we have not seen before.</summary>
    Raised = 0,

    /// <summary>Something added to one we have — a comment, a field change, an update.</summary>
    Updated = 1,

    /// <summary>
    /// Their side considers it done.
    ///
    /// <para>Recorded, and <b>nothing more</b>. §14.4 makes resolution something the
    /// customer accepts and we record; a service desk closing its own copy is not that act
    /// and must not perform it here. See <see cref="TicketBridgeService"/>.</para>
    /// </summary>
    Closed = 2,
}

/// <summary>
/// One delivery from a customer's ticketing system, in our terms rather than theirs.
///
/// <para><b>The whole point of the shape.</b> Every adapter reduces to this, so the rules
/// that matter — what a priority is worth, what may move a clock — are applied once, in
/// <see cref="TicketBridgeService"/>, instead of four times in four vendors' vocabularies
/// where the fourth one will get it subtly wrong.</para>
/// </summary>
public sealed record InboundTicketReport
{
    /// <summary>
    /// Their identifier for it, opaque and stable. This is what dedupes a resend, so an
    /// adapter that cannot find one should fail the delivery rather than invent one: a
    /// synthesised id that differs between two deliveries makes duplicates of everything.
    /// </summary>
    public required string ExternalId { get; init; }

    /// <summary>What a person there would quote — <c>INC0012345</c>, <c>SUP-481</c>.</summary>
    public string? ExternalKey { get; init; }

    public string? Url { get; init; }

    public required InboundTicketKind Kind { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = "";

    /// <summary>
    /// Their priority as it arrived, before any mapping — kept verbatim so the ticket can
    /// say what was actually sent, not only what we made of it.
    /// </summary>
    public string? RawPriority { get; init; }

    /// <summary>
    /// When <em>they</em> recorded it. §14.3 counts from when a fault was reported, and for
    /// a ticket raised in the customer's own system that moment happened there.
    ///
    /// <para>Null when the payload did not say, and then the delivery time stands in —
    /// which is later, and therefore never invents SLA time we did not have.</para>
    /// </summary>
    public DateTime? ReportedAt { get; init; }

    public string? RequestedBy { get; init; }

    public string? RequestedByEmail { get; init; }

    /// <summary>
    /// What the payload said the ticket is about, if anything — a component, a service, a
    /// configuration item. Matched against the customer's applications by name, and falling
    /// back to the connection's default.
    /// </summary>
    public string? AppHint { get; init; }

    /// <summary>
    /// The text of an update, for <see cref="InboundTicketKind.Updated"/>. Appended to the
    /// ticket's history as a note, under the name of whoever wrote it there.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>
/// Reads one vendor's delivery into an <see cref="InboundTicketReport"/>.
///
/// <para><b>An adapter may not decide anything.</b> It renames fields and nothing else: no
/// priority mapping (that is the connection's, and configured), no clock, no status. Its
/// whole job is to stop the rest of the bridge from knowing that ServiceNow calls it
/// <c>short_description</c> and Jira calls it <c>summary</c>.</para>
///
/// <para>Adding a system is therefore writing one of these and nothing else, which is the
/// difference between "and others" being a sentence and being a project.</para>
/// </summary>
public interface IInboundTicketAdapter
{
    /// <summary>The system this reads. One adapter per member of <see cref="TicketSystem"/>.</summary>
    TicketSystem System { get; }

    /// <summary>
    /// Reads a delivery, or explains why it cannot be read.
    ///
    /// <para>Never throws for a payload it does not understand — a malformed delivery is an
    /// answer to give the sender, not an exception to log.</para>
    /// </summary>
    InboundTicketRead Read(string payload, TicketBridgeConnection connection);
}

/// <summary>An adapter's answer: the report, or why there isn't one.</summary>
/// <param name="Report">What was read, when it could be.</param>
/// <param name="Error">What was wrong with it, for the sender and for the connection's log.</param>
public readonly record struct InboundTicketRead(InboundTicketReport? Report, string? Error)
{
    public static InboundTicketRead Ok(InboundTicketReport report) => new(report, null);

    public static InboundTicketRead Failed(string error) => new(null, error);

    public bool IsOk => Report is not null;
}
