using EntKube.Web.Data;

namespace EntKube.Web.Services.Mail;

/// <summary>What the analyst is told about the world when it reads a message.</summary>
/// <param name="Message">The mail itself.</param>
/// <param name="Customer">The customer it was matched to, if the sender was recognised.</param>
/// <param name="Apps">That customer's applications, for matching a name in the text.</param>
/// <param name="OpenTickets">Their open tickets, for threading a reply onto one.</param>
/// <param name="TimebankExhausted">Whether §11.1's approval gate is already in force.</param>
/// <param name="Rules">The phrases this tenant watches for, configured or built-in.</param>
/// <param name="PlacedOnTheSendersWord">
/// Whether the customer was decided only by an address the sender put in To or Cc.
///
/// <para>Those headers are written by whoever sent the message, so naming a customer's
/// support alias in them is a claim and not evidence — and a placement resting on one
/// must not silence the prompt that would have had somebody look twice.</para>
/// </param>
/// <param name="PlacedOnTheFromAddress">
/// Whether the customer was decided by the From address — a §23 contact or a registered
/// domain — rather than by where the message was actually delivered.
///
/// <para>Both of those registers are exact and were written by somebody who knew what they
/// were saying, which is why a match on one carries no caveat. They match on a header the
/// sender wrote, though, so what they are worth depends entirely on whether that header was
/// ever checked. See <see cref="InboundMailMessage.SenderAuthenticity"/>.</para>
/// </param>
public readonly record struct MailContext(
    InboundMailMessage Message,
    Customer? Customer,
    IReadOnlyList<App> Apps,
    IReadOnlyList<Ticket> OpenTickets,
    bool TimebankExhausted,
    MailTriageRuleSet Rules,
    bool PlacedOnTheSendersWord = false,
    bool PlacedOnTheFromAddress = false);

/// <summary>
/// Reads an inbound support message and proposes what to do with it.
///
/// <para><b>An interface, and on purpose.</b> The default implementation matches keywords
/// and reference numbers, which is unglamorous and entirely predictable. A language model
/// would read these messages far better — but routing a customer's support mail through a
/// third party is a data processor question before it is a technical one: §17
/// requires a DPA before personal data is processed, §18 requires the customer to approve
/// a new processor, and these messages will contain patient data. That is a decision for
/// the people who sign the agreement, not one to make by adding a dependency.</para>
///
/// <para>So the seam exists, the pipeline around it is real, and what plugs into it is
/// somebody's call.</para>
/// </summary>
public interface ISupportMailAnalyst
{
    /// <summary>
    /// What should happen to this message. Every returned suggestion is a proposal for a
    /// person to accept or reject — nothing here decides anything.
    /// </summary>
    Task<IReadOnlyList<MailSuggestion>> AnalyseAsync(MailContext context, CancellationToken ct = default);
}
