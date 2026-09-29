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
/// <para><b>And why it counts.</b> The four conditions all judge a message on its own, which
/// leaves one thing unbounded: a correspondent writing in a loop while carrying none of the
/// headers that would have marked it a program passes all four, every time, for as long as
/// it goes on. <see cref="AutomaticRepliesPerSender"/> is the ceiling on that, and going
/// over it costs nothing but the automatic reply.</para>
///
/// <para>Pure, so the rule can be argued over in a test rather than in a mailbox.</para>
/// </summary>
public static class ArrivalPolicy
{
    /// <summary>The name that goes on a ticket opened this way, and on its events.</summary>
    public const string Actor = "EntKube support mailbox";

    /// <summary>
    /// How many times one address may be answered automatically within
    /// <see cref="RepeatWindow"/> before the rest of its mail is left for a person.
    ///
    /// <para><b>What this is for, and what it is not.</b> It is not spam control — an
    /// unplaced sender never gets this far. It is the bound on a correspondent that writes
    /// in a loop while carrying none of the headers that would have given it away: a broken
    /// integration, a forwarding rule pointed at us, a responder somebody wrote by hand.
    /// Each of those produces a ticket and a receipt per message, and the person who used to
    /// absorb that by not pressing Accept forty times is no longer in the path.</para>
    ///
    /// <para><b>Why six.</b> It has to sit above a real person having a bad morning and well
    /// below anything automatic. Six distinct reports from one address inside an hour is
    /// already unusual enough to be worth a person's eye; a loop passes six in seconds.</para>
    ///
    /// <para><b>Why being wrong about it is cheap.</b> Going over the cap is not a refusal
    /// and loses nothing: the message is taken in, analysed and queued exactly as every
    /// message was before any of this existed. The only thing withheld is the automatic
    /// reply, and a person can still open the ticket from the queue in one click.</para>
    /// </summary>
    public const int AutomaticRepliesPerSender = 6;

    /// <summary>The window <see cref="AutomaticRepliesPerSender"/> is counted over.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(1);

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
    /// <param name="answeredRecently">
    /// How many messages from this same address have already been answered automatically
    /// inside <see cref="RepeatWindow"/>. Counted by the caller, which is the only party
    /// with a database; defaulted so that a caller with nothing to count — a test, a message
    /// handed in by hand — is not made to say zero.
    /// </param>
    public static ArrivalDecision Decide(
        InboundMailMessage message,
        IReadOnlyList<MailSuggestion> suggestions,
        bool mailboxAcknowledges,
        int answeredRecently = 0)
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

        // Last, deliberately. Everything above describes the message itself, and a message
        // that would have been declined for what it is should say so rather than blaming
        // the address it came from.
        if (answeredRecently >= AutomaticRepliesPerSender)
        {
            return new(
                false,
                $"{message.FromAddress} has already been answered automatically "
                + $"{answeredRecently} times in the last hour, which is more than a person "
                + "reports. The rest of its mail is being left for one.");
        }

        return new(true, "a person at a known contact reported something we have no ticket for.");
    }
}
