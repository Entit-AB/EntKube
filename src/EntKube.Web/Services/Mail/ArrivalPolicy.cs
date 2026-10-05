using EntKube.Web.Data;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// What is to happen to a message the moment it lands, and why.
/// </summary>
/// <param name="Action">
/// What is to be done without waiting for a person: nothing, open a ticket — which is what
/// sends the customer their number, since <c>TicketNotifier</c> announces every ticket it is
/// handed — or put the message on the history of the ticket it proves it belongs to.
/// </param>
/// <param name="Reason">
/// Said in full either way, and in the words an operator would use. "Nothing happened" is
/// the hardest state of this subsystem to debug: the mailbox looks healthy, the message is
/// in the queue, and there is no error anywhere. The reason is logged so that the question
/// "why did this one not get a receipt" has an answer that does not require reading this
/// file.
/// </param>
public readonly record struct ArrivalDecision(ArrivalAction Action, string Reason)
{
    /// <summary>Whether a ticket is to be opened for it. Reads as the question it is.</summary>
    public bool OpenNow => Action == ArrivalAction.OpenTicket;

    /// <summary>Whether it is to be put on the history of a ticket that already exists.</summary>
    public bool AppendNow => Action == ArrivalAction.AppendToTicket;

    /// <summary>Whether what it says is to be sent on to the customer.</summary>
    public bool ReplyNow => Action == ArrivalAction.ReplyToCustomer;

    /// <summary>Whether it is a refusal to be written onto the ticket it failed on.</summary>
    public bool RecordFailureNow => Action == ArrivalAction.RecordDeliveryFailure;
}

/// <summary>What is to be done with an arriving message without waiting for a person.</summary>
public enum ArrivalAction
{
    /// <summary>Nothing. It goes to the queue with its suggestions pending, as before.</summary>
    LeaveForAPerson = 0,

    /// <summary>Open a ticket for it, which is also what sends the customer their number.</summary>
    OpenTicket = 1,

    /// <summary>
    /// Add it to the history of the ticket its reference proves it belongs to. Sends nothing
    /// — <c>TicketService.AddEventAsync</c> writes an event and no mail — so this is the
    /// quieter of the two acts, and the one with no wording to get wrong.
    /// </summary>
    AppendToTicket = 2,

    /// <summary>
    /// Send what one of our own people wrote on to the customer. The loudest of the three:
    /// it puts words in the customer's inbox in our name, which is why it is the only one
    /// that insists the sender was actually verified.
    /// </summary>
    ReplyToCustomer = 3,

    /// <summary>
    /// Write a refused message onto the ticket it failed on, where somebody will see that
    /// the customer never heard from us. Sends nothing and is not shown to the customer, so
    /// it is the safest of the four — and the one whose absence is most expensive, because
    /// a bounce nobody records is a customer who simply never replied.
    /// </summary>
    RecordDeliveryFailure = 4,
}

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
/// <para><b>The other act, and why it is allowed too.</b> A reply to a ticket that already
/// has a number used to stop here: the duplicate ticket was prevented, which was the point,
/// but the reply itself then sat in the queue until somebody pressed a button to put it where
/// everybody already knew it went. So it is now put there — on one condition, that the
/// reference is <em>proven</em>. A reference we minted carries a check token
/// (<see cref="Tickets.TicketReference"/>), so a subject either came from something we sent or
/// it did not. A bare number somebody typed still waits for a person, because a typed number
/// can be the wrong one and the cost of being wrong is a customer's message on a stranger's
/// history. Appending sends no mail at all, which is why it needs less justification than the
/// receipt does and not more.</para>
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
    /// <param name="referenceIsProven">
    /// Whether what placed this message on an existing ticket was something only somebody we
    /// wrote to could be holding: a thread on one of our own Message-Ids, or a subject
    /// reference whose check token validates. Read by the append branch and nothing else.
    /// Defaulted to false, which is the safe answer — a caller that does not know cannot
    /// cause anything to be appended.
    /// </param>
    public static ArrivalDecision Decide(
        InboundMailMessage message,
        IReadOnlyList<MailSuggestion> suggestions,
        bool mailboxAcknowledges,
        int answeredRecently = 0,
        bool referenceIsProven = false)
    {
        if (!mailboxAcknowledges)
        {
            return Leave("the mailbox is set to leave new mail for a person to open.");
        }

        // A bounce, and deliberately before the machine-generated test below — which it
        // would otherwise fail, being the most machine-generated thing there is. That
        // ordering is the whole point: "do not answer this" and "do not record this" are
        // different instructions, and the old code only had the first. Recording sends
        // nothing, so there is nothing here for the loop guard to protect against.
        if (suggestions.Any(s => s.Kind == MailSuggestionKind.FlagDeliveryFailure
                                 && s.TicketId is not null))
        {
            return new(
                ArrivalAction.RecordDeliveryFailure,
                "it is a delivery report for one of our own messages on a ticket that is "
                + "still open, so the ticket says the customer never got it.");
        }

        if (message.IsMachineGenerated)
        {
            // Also the guard that stops a technician's own out-of-office, bounced back off
            // the mail we sent them about their new ticket, being forwarded to the customer
            // as our answer.
            return Leave(
                "the message says it was sent by a program — an automatic reply, a bounce or a "
                + "list. Answering it could be two machines writing to each other.");
        }

        // One of our own people answering the customer. Taken before the reply-to-a-ticket
        // branch below, because the two are mutually exclusive by construction and the
        // consequence of confusing them is a colleague's words filed as the customer's.
        if (suggestions.FirstOrDefault(s => s.Kind == MailSuggestionKind.ReplyToCustomer)
            is { } reply)
        {
            if (!referenceIsProven)
            {
                return Leave(
                    "it reads as one of ours answering the customer, but the reference it "
                    + "quotes is not one we minted — so which ticket it answers is for a "
                    + "person to say.");
            }

            // The one place a verified sender is insisted on rather than merely preferred.
            // Everything else here either files a message or writes to the person who sent
            // it; this writes to the customer, in our name, saying whatever the From address
            // asked us to say. Unknown is not a lesser failure than Failed for that purpose:
            // it means nobody told us which server's verdicts to believe, so a forged From on
            // our own domain cannot be told from a real one.
            if (message.SenderAuthenticity != SenderAuthenticity.Verified)
            {
                return Leave(
                    message.SenderAuthenticity == SenderAuthenticity.Failed
                        ? "it would be sent on to the customer in our name, and our own mail "
                          + "server could not verify that the sender is who they say."
                        : "it would be sent on to the customer in our name, and this mailbox "
                          + "has no trusted server name configured — so a forged From on our "
                          + "own domain cannot be told from a real one. Set the mailbox's "
                          + "authentication-results server to let this happen by itself.");
            }

            if (string.IsNullOrWhiteSpace(reply.DraftText))
            {
                // Belt and braces: the analyst only raises this suggestion with the text on
                // it, and an empty mail to a customer in our name is not a thing to risk on
                // that remaining true.
                return Leave("there is nothing above the cut line to send.");
            }

            return new(
                ArrivalAction.ReplyToCustomer,
                "one of our own people answered the customer above the cut line, on a ticket "
                + "their reference proves.");
        }

        // A reply already has a number: the one in the subject or the thread that placed it
        // here. Opening a second ticket for it is the bug the reference exists to prevent,
        // and sending a second receipt would teach the customer to quote the newer one. So
        // this branch never opens anything — the only question left is whether the message
        // goes onto that ticket now or waits for somebody to agree that it belongs there.
        if (suggestions.Any(s => s.Kind == MailSuggestionKind.AppendToTicket))
        {
            if (!referenceIsProven)
            {
                return Leave(
                    "it quotes a ticket number, but not one of our references — a typed number "
                    + "can be the wrong number, so where it belongs is for a person to say.");
            }

            // Who sent it still matters, even holding a reference we minted. The message goes
            // onto a history §14.6 makes evidence between the parties, and it is shown to the
            // customer. A forged From would put words in a named person's mouth there.
            if (suggestions.Any(s => s.Kind == MailSuggestionKind.FlagForgedSender))
            {
                return Leave(
                    "it quotes one of our references, but our own mail server could not verify "
                    + "that the sender is who they say.");
            }

            if (suggestions.Any(s => s.Kind == MailSuggestionKind.FlagUnknownSender))
            {
                return Leave(
                    "it quotes one of our references, but who it is from needs deciding before "
                    + "their words go on a ticket in their name.");
            }

            // Not subject to AutomaticRepliesPerSender, which counts mail we send. An append
            // sends none, so a correspondent in a loop costs a long ticket history and a
            // person's attention — not a pile of letters in a customer's name.
            return new(
                ArrivalAction.AppendToTicket,
                "it carries one of our own references to a ticket that is still open.");
        }

        if (!suggestions.Any(s => s.Kind == MailSuggestionKind.OpenTicket))
        {
            return Leave("nothing about it proposed opening a ticket.");
        }

        if (message.CustomerId is null)
        {
            return Leave("we do not know whose message this is.");
        }

        // Both of the analyst's doubts about who sent it. Each says the same thing in its
        // own way: a ticket is about to be opened in somebody's name, and the name rests on
        // something a stranger could have written. A receipt would then go out to that
        // stranger, in the customer's matter, quoting a real ticket number.
        if (suggestions.Any(s => s.Kind == MailSuggestionKind.FlagUnknownSender))
        {
            return Leave(
                "who it is from needs deciding before a ticket is opened in their name.");
        }

        if (suggestions.Any(s => s.Kind == MailSuggestionKind.FlagForgedSender))
        {
            return Leave(
                "our own mail server could not verify that the sender is who they say.");
        }

        // Last, deliberately. Everything above describes the message itself, and a message
        // that would have been declined for what it is should say so rather than blaming
        // the address it came from.
        if (answeredRecently >= AutomaticRepliesPerSender)
        {
            return Leave(
                $"{message.FromAddress} has already been answered automatically "
                + $"{answeredRecently} times in the last hour, which is more than a person "
                + "reports. The rest of its mail is being left for one.");
        }

        return new(
            ArrivalAction.OpenTicket,
            "a person at a known contact reported something we have no ticket for.");
    }

    /// <summary>Leave it for a person, saying why in full. See the remarks on the reason.</summary>
    private static ArrivalDecision Leave(string reason) =>
        new(ArrivalAction.LeaveForAPerson, reason);
}
