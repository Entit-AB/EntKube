using EntKube.Web.Data;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// What is to happen to a message the moment it lands, and why.
/// </summary>
/// <param name="OpenNow">
/// Whether a ticket is to be opened for it there and then — which is what sends the
/// customer their number, since <c>TicketNotifier</c> announces every ticket it is handed.
/// </param>
/// <param name="Reason">
/// Said in full either way, and in the words an operator would use. "Nothing happened" is
/// the hardest state of this subsystem to debug: the mailbox looks healthy, the message is
/// in the queue, and there is no error anywhere. The reason is logged so that the question
/// "why did this one not get a receipt" has an answer that does not require reading this
/// file.
/// </param>
public readonly record struct ArrivalDecision(bool OpenNow, string Reason);

/// <summary>
/// Whether an arriving message may be answered by the machine, with a ticket number, before
/// any person has read it.
///
/// <para><b>Why a receipt may be automatic at all</b>, when nothing else here is. Everything
/// else the machine could say to a customer is a judgement — what priority this is, whether
/// it is resolved, whether it is billable — and §14.3 and §14.4 make those written acts by a
/// person. A receipt is not a judgement: it is a fact about the past, that a message arrived
/// at a time and now has a number. §14.6 makes those timestamps the record between the
/// parties, which argues for telling the customer what we recorded rather than for sitting
/// on it until somebody opens the queue. Nothing downstream changes: the priority still
/// arrives at the customer marked as theirs and unconfirmed, and a person still confirms it
/// in writing.</para>
///
/// <para><b>Why it is nonetheless narrow.</b> Opening a ticket starts §14.4's clocks and
/// sends mail in a customer's name, so it is only done where all four of these hold — the
/// message is from a person, it is not already a reply to a ticket that has a number, we
/// know whose it is without having guessed, and our own server did not say the sender is
/// forged. Anything else goes to the queue exactly as it did before, with its suggestions
/// pending and a person to read them. The test is deliberately one a person can apply by
/// eye: every reason it can give names something visible on the message.</para>
///
/// <para>Pure, so the rule can be argued over in a test rather than in a mailbox.</para>
/// </summary>
public static class ArrivalPolicy
{
    /// <summary>The name that goes on a ticket opened this way, and on its events.</summary>
    public const string Actor = "EntKube support mailbox";

    /// <summary>
    /// Decides, from the message and what the analyst made of it.
    /// </summary>
    /// <param name="message">The message as it was taken in.</param>
    /// <param name="suggestions">What the analyst proposed for it.</param>
    /// <param name="mailboxAcknowledges">
    /// Whether the mailbox it arrived in is set to answer on arrival —
    /// <see cref="SupportMailbox.AcknowledgeOnArrival"/>. False where no mailbox is
    /// configured at all: a message handed in by some other route was not sent to an
    /// address whose owner agreed to answer from it.
    /// </param>
    public static ArrivalDecision Decide(
        InboundMailMessage message,
        IReadOnlyList<MailSuggestion> suggestions,
        bool mailboxAcknowledges)
    {
        if (!mailboxAcknowledges)
        {
            return new(false, "the mailbox is set to leave new mail for a person to open.");
        }

        if (message.IsMachineGenerated)
        {
            return new(
                false,
                "the message says it was sent by a program — an automatic reply, a bounce or a "
                + "list. Answering it could be two machines writing to each other.");
        }

        // A reply already has a number: the one in the subject or the thread that placed it
        // here. Opening a second ticket for it is the bug the reference exists to prevent,
        // and sending a second receipt would teach the customer to quote the newer one.
        if (suggestions.Any(s => s.Kind == MailSuggestionKind.AppendToTicket))
        {
            return new(false, "it belongs to a ticket that already has a number.");
        }

        if (!suggestions.Any(s => s.Kind == MailSuggestionKind.OpenTicket))
        {
            return new(false, "nothing about it proposed opening a ticket.");
        }

        if (message.CustomerId is null)
        {
            return new(false, "we do not know whose message this is.");
        }

        // Both of the analyst's doubts about who sent it. Each says the same thing in its
        // own way: a ticket is about to be opened in somebody's name, and the name rests on
        // something a stranger could have written. A receipt would then go out to that
        // stranger, in the customer's matter, quoting a real ticket number.
        if (suggestions.Any(s => s.Kind == MailSuggestionKind.FlagUnknownSender))
        {
            return new(
                false,
                "who it is from needs deciding before a ticket is opened in their name.");
        }

        if (suggestions.Any(s => s.Kind == MailSuggestionKind.FlagForgedSender))
        {
            return new(
                false, "our own mail server could not verify that the sender is who they say.");
        }

        return new(true, "a person at a known contact reported something we have no ticket for.");
    }
}
