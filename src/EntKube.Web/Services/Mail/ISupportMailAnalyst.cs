using EntKube.Web.Data;

namespace EntKube.Web.Services.Mail;

/// <summary>What the analyst is told about the world when it reads a message.</summary>
/// <param name="Message">The mail itself.</param>
/// <param name="Customer">The customer it was matched to, if the sender was recognised.</param>
/// <param name="Apps">That customer's applications, for matching a name in the text.</param>
/// <param name="OpenTickets">Their open tickets, for threading a reply onto one.</param>
/// <param name="TimebankExhausted">Whether §11.1's approval gate is already in force.</param>
public readonly record struct MailContext(
    InboundMailMessage Message,
    Customer? Customer,
    IReadOnlyList<App> Apps,
    IReadOnlyList<Ticket> OpenTickets,
    bool TimebankExhausted);

/// <summary>
/// Reads an inbound support message and proposes what to do with it.
///
/// <para><b>An interface, and on purpose.</b> The default implementation matches keywords
/// and reference numbers, which is unglamorous and entirely predictable. A language model
/// would read these messages far better — but routing a customer's support mail through a
/// third party is a personuppgiftsbiträde question before it is a technical one: §17
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
