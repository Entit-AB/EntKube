using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Which arriving message may be answered by the machine.
///
/// <para>The rule decides two things at once, because opening the ticket is what sends the
/// receipt: whether a ticket exists before anybody has read the message, and whether mail
/// goes out in a customer's matter with nobody's name on it. Both of those are worth being
/// wrong about in only one direction — leaving a message in the queue costs a delay that a
/// person fixes by reading it, and answering the wrong thing cannot be taken back.</para>
/// </summary>
public class ArrivalPolicyTests
{
    private static MailSuggestion Replies(string said = "Vi tittar på det.") => new()
    {
        Id = Guid.NewGuid(),
        Kind = MailSuggestionKind.ReplyToCustomer,
        Summary = "Send this to the customer",
        DraftText = said,
        TicketId = Guid.NewGuid(),
    };

    private static InboundMailMessage Message(
        Guid? customerId = null, bool machine = false,
        SenderAuthenticity authenticity = SenderAuthenticity.Verified) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = "karin@kund.example",
            Subject = "Journalen svarar inte",
            Body = "Ingen kommer in.",
            CustomerId = customerId ?? Guid.NewGuid(),
            IsMachineGenerated = machine,
            SenderAuthenticity = authenticity,
        };

    private static MailSuggestion Suggestion(MailSuggestionKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Summary = kind.ToString(),
    };

    private static MailSuggestion[] ProposesATicket() =>
        [Suggestion(MailSuggestionKind.OpenTicket), Suggestion(MailSuggestionKind.ProposePriority)];

    /// <summary>
    /// The case the whole thing exists for: a person at a recorded contact reports a fault
    /// nobody has a ticket for. They get a number without waiting for the queue to be read.
    /// </summary>
    [Fact]
    public void A_fault_report_from_a_known_contact_is_answered_at_once()
    {
        ArrivalDecision decision =
            ArrivalPolicy.Decide(Message(), ProposesATicket(), mailboxAcknowledges: true);

        decision.OpenNow.Should().BeTrue();
        decision.Reason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The tenant's switch is the outer one. It is the only thing in this subsystem that
    /// sends mail with no person behind it, so whoever answers for what leaves the building
    /// has to be able to stop it without stopping the mailbox.
    /// </summary>
    [Fact]
    public void The_mailbox_switch_settles_it_before_anything_else_is_asked()
    {
        ArrivalDecision decision =
            ArrivalPolicy.Decide(Message(), ProposesATicket(), mailboxAcknowledges: false);

        decision.OpenNow.Should().BeFalse();
        decision.Reason.Should().Contain("person");
    }

    /// <summary>
    /// <b>The loop.</b> An out-of-office reply, a bounce or a list posting is a program
    /// writing, and a receipt sent back to one can be answered again. Two of them left
    /// alone overnight is a mailbox nobody can use in the morning.
    /// </summary>
    [Fact]
    public void A_message_that_says_it_came_from_a_program_is_not_answered()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(machine: true), ProposesATicket(), mailboxAcknowledges: true);

        decision.OpenNow.Should().BeFalse();
        decision.Reason.Should().Contain("program");
    }

    /// <summary>
    /// A reply already has a number — the one in its subject or its thread. Opening a second
    /// ticket is the bug the reference exists to prevent, and a second receipt would teach
    /// the customer to quote the wrong number from then on.
    /// </summary>
    [Fact]
    public void A_reply_to_an_open_ticket_gets_no_second_number()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(),
            [Suggestion(MailSuggestionKind.AppendToTicket)],
            mailboxAcknowledges: true);

        decision.OpenNow.Should().BeFalse();
    }

    /// <summary>
    /// <b>The second thing that happens without a person.</b> A reply holding a reference we
    /// minted goes onto its ticket now rather than waiting in the queue for somebody to agree
    /// with what the reference already says. Nothing is sent: an append writes an event, which
    /// is why it needs less justifying than the receipt and not more.
    /// </summary>
    [Fact]
    public void A_reply_carrying_one_of_our_references_is_put_on_its_ticket()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(),
            [Suggestion(MailSuggestionKind.AppendToTicket)],
            mailboxAcknowledges: true,
            referenceIsProven: true);

        decision.AppendNow.Should().BeTrue();
        decision.OpenNow.Should().BeFalse();
    }

    /// <summary>
    /// <b>And the line it stops at.</b> A number somebody typed is not a reference we minted:
    /// a digit can be wrong, and the cost of being wrong is a customer's words on the history
    /// of a ticket that is not theirs — a history §14.6 makes evidence between the parties.
    /// So an unproven number does exactly what it did before, which is wait for a person.
    /// </summary>
    [Fact]
    public void A_reply_quoting_a_bare_number_still_waits_for_a_person()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(),
            [Suggestion(MailSuggestionKind.AppendToTicket)],
            mailboxAcknowledges: true,
            referenceIsProven: false);

        decision.Action.Should().Be(ArrivalAction.LeaveForAPerson);
        decision.Reason.Should().Contain("typed");
    }

    /// <summary>
    /// A bounce or an out-of-office threaded onto a ticket is a program writing, and its
    /// text does not belong on a history shown to the customer.
    /// </summary>
    [Fact]
    public void A_reference_does_not_let_a_program_write_on_a_ticket()
    {
        ArrivalPolicy.Decide(
                Message(machine: true),
                [Suggestion(MailSuggestionKind.AppendToTicket)],
                mailboxAcknowledges: true,
                referenceIsProven: true)
            .AppendNow.Should().BeFalse();
    }

    /// <summary>
    /// Holding a reference says the sender was written to. It does not say the From address
    /// is theirs, and the append is attributed to that address on a history the customer
    /// reads — so the analyst's two doubts about who sent it still stop it.
    /// </summary>
    [Theory]
    [InlineData(MailSuggestionKind.FlagForgedSender)]
    [InlineData(MailSuggestionKind.FlagUnknownSender)]
    public void A_reference_does_not_settle_who_the_reply_is_from(MailSuggestionKind doubt)
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(),
            [Suggestion(MailSuggestionKind.AppendToTicket), Suggestion(doubt)],
            mailboxAcknowledges: true,
            referenceIsProven: true);

        decision.AppendNow.Should().BeFalse();
        decision.Reason.Should().Contain("reference");
    }

    /// <summary>
    /// The tenant's switch is the outer one for both acts. An operator who turned it off
    /// asked for nothing to happen to arriving mail on its own, and an append is something
    /// happening — even though it sends nothing.
    /// </summary>
    [Fact]
    public void The_mailbox_switch_also_settles_whether_a_reply_is_appended()
    {
        ArrivalPolicy.Decide(
                Message(),
                [Suggestion(MailSuggestionKind.AppendToTicket)],
                mailboxAcknowledges: false,
                referenceIsProven: true)
            .AppendNow.Should().BeFalse();
    }

    /// <summary>
    /// The cap counts mail we send, and an append sends none. A correspondent replying into
    /// one ticket all morning costs a long history and a person's attention; it must not also
    /// use up the budget that stops us writing letters to somebody in a loop.
    /// </summary>
    [Fact]
    public void The_reply_cap_does_not_apply_to_an_append()
    {
        ArrivalPolicy.Decide(
                Message(),
                [Suggestion(MailSuggestionKind.AppendToTicket)],
                mailboxAcknowledges: true,
                answeredRecently: ArrivalPolicy.AutomaticRepliesPerSender * 10,
                referenceIsProven: true)
            .AppendNow.Should().BeTrue();
    }

    /// <summary>
    /// Nothing proposed a ticket — the analyst had nothing to say, or the message is one of
    /// the kinds it only flags. There is nothing to open and so nothing to acknowledge.
    /// </summary>
    [Fact]
    public void A_message_nothing_proposed_a_ticket_for_is_left_alone()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(), [Suggestion(MailSuggestionKind.FlagTimebank)], mailboxAcknowledges: true);

        decision.OpenNow.Should().BeFalse();
    }

    /// <summary>
    /// An unplaced message has no customer to open a ticket against, no agreement to take a
    /// support window from, and a sender we have never heard of.
    /// </summary>
    [Fact]
    public void A_message_we_cannot_place_is_left_for_a_person()
    {
        InboundMailMessage stranger = Message();
        stranger.CustomerId = null;

        ArrivalPolicy.Decide(stranger, ProposesATicket(), mailboxAcknowledges: true)
            .OpenNow.Should().BeFalse();
    }

    /// <summary>
    /// <b>Both of the analyst's doubts about who sent it stop the receipt.</b> Each says the
    /// same thing in its own way: the only thing connecting this message to the customer is
    /// something a stranger could have written. Answering it automatically would send a real
    /// ticket number, in that customer's matter, to whoever asked for it.
    /// </summary>
    [Theory]
    [InlineData(MailSuggestionKind.FlagUnknownSender)]
    [InlineData(MailSuggestionKind.FlagForgedSender)]
    public void A_sender_in_doubt_is_not_answered_automatically(MailSuggestionKind flag)
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(), [.. ProposesATicket(), Suggestion(flag)], mailboxAcknowledges: true);

        decision.OpenNow.Should().BeFalse();
    }

    // ---- The one thing the four conditions cannot bound ---------------------------------

    /// <summary>
    /// <b>A loop that carries none of the headers.</b> A broken integration, a forwarding
    /// rule pointed at us, a responder somebody wrote by hand — each passes every test above,
    /// every time, for as long as it goes on, and the person who used to absorb that by not
    /// pressing Accept is no longer in the path.
    /// </summary>
    [Fact]
    public void An_address_answered_too_often_stops_being_answered()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(), ProposesATicket(), mailboxAcknowledges: true,
            answeredRecently: ArrivalPolicy.AutomaticRepliesPerSender);

        decision.OpenNow.Should().BeFalse();
        decision.Reason.Should().Contain("karin@kund.example");
    }

    /// <summary>
    /// The cap has to sit above a real person having a bad morning. One short of it is still
    /// answered — being wrong in the other direction means a genuine report goes unanswered
    /// because the same person wrote earlier.
    /// </summary>
    [Fact]
    public void One_short_of_the_cap_is_still_answered() =>
        ArrivalPolicy.Decide(
            Message(), ProposesATicket(), mailboxAcknowledges: true,
            answeredRecently: ArrivalPolicy.AutomaticRepliesPerSender - 1)
            .OpenNow.Should().BeTrue();

    /// <summary>
    /// The cap is asked last. A message declined for what it is should say so, rather than
    /// blaming the address it came from — the operator reading the reason is trying to work
    /// out what to fix.
    /// </summary>
    [Fact]
    public void A_message_that_fails_on_its_own_terms_is_not_blamed_on_its_sender()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(machine: true), ProposesATicket(), mailboxAcknowledges: true,
            answeredRecently: ArrivalPolicy.AutomaticRepliesPerSender * 10);

        decision.Reason.Should().Contain("program");
        decision.Reason.Should().NotContain("times in the last hour");
    }

    /// <summary>
    /// Every answer says why, in words an operator can act on. A message that was left
    /// alone leaves no other trace — the mailbox is healthy, the queue has the message, and
    /// nothing anywhere is an error.
    /// </summary>
    [Fact]
    public void Every_decision_explains_itself()
    {
        foreach (bool acknowledges in (bool[])[true, false])
        {
            ArrivalPolicy.Decide(Message(machine: true), ProposesATicket(), acknowledges)
                .Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    // ---- One of ours answering the customer --------------------------------------------------

    /// <summary>
    /// <b>The third thing that happens without a person, and the loudest.</b> A technician
    /// replies to the mail we sent them about their ticket; what they wrote above the cut
    /// line goes to the customer. This is the whole point of assigning by mail — a ticket
    /// answered without opening the application.
    /// </summary>
    [Fact]
    public void What_one_of_ours_wrote_above_the_line_is_sent_to_the_customer()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(), [Replies()], mailboxAcknowledges: true, referenceIsProven: true);

        decision.ReplyNow.Should().BeTrue();
        decision.OpenNow.Should().BeFalse();
        decision.AppendNow.Should().BeFalse();
    }

    /// <summary>
    /// <b>The guard that matters most.</b> This is the only decision here that puts words in
    /// a customer's inbox in our name, saying whatever the From address asked us to say — so
    /// it is the only one that insists the sender was actually verified.
    ///
    /// <para>Unknown is not a lesser failure than Failed for this purpose. It means nobody
    /// told us whose verdicts to believe, so a forged From on our own domain reads exactly
    /// like a real one — and the forger only needs a reference we already sent them.</para>
    /// </summary>
    [Theory]
    [InlineData(SenderAuthenticity.Unknown)]
    [InlineData(SenderAuthenticity.Failed)]
    public void An_unverified_sender_cannot_make_us_write_to_the_customer(
        SenderAuthenticity authenticity)
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(authenticity: authenticity), [Replies()],
            mailboxAcknowledges: true, referenceIsProven: true);

        decision.ReplyNow.Should().BeFalse();
        decision.Action.Should().Be(ArrivalAction.LeaveForAPerson);
        decision.Reason.Should().Contain("customer");
    }

    /// <summary>
    /// Unknown says what to configure rather than only that it refused. "Nothing happened"
    /// with no reason is this subsystem's worst failure mode, and a feature that silently
    /// does nothing on every installation that has not set one field is exactly that.
    /// </summary>
    [Fact]
    public void An_unconfigured_mailbox_is_told_what_is_missing()
    {
        ArrivalPolicy.Decide(
                Message(authenticity: SenderAuthenticity.Unknown), [Replies()],
                mailboxAcknowledges: true, referenceIsProven: true)
            .Reason.Should().Contain("trusted server name");
    }

    /// <summary>
    /// A typed number is not a reference we minted, and sending a colleague's words to
    /// whichever customer that number belongs to is not a mistake that can be taken back.
    /// </summary>
    [Fact]
    public void A_reply_on_an_unproven_number_is_not_sent_anywhere()
    {
        ArrivalDecision decision = ArrivalPolicy.Decide(
            Message(), [Replies()], mailboxAcknowledges: true, referenceIsProven: false);

        decision.ReplyNow.Should().BeFalse();
        decision.Reason.Should().Contain("not one we minted");
    }

    /// <summary>
    /// <b>The loop, in its most expensive form.</b> A technician's own out-of-office, sent
    /// back at the mail announcing their new ticket, carries a real reference from a real
    /// colleague's address — and relaying it would send "I am on holiday until the 14th" to
    /// a customer waiting on a P1.
    /// </summary>
    [Fact]
    public void A_technicians_own_out_of_office_is_not_relayed()
    {
        ArrivalPolicy.Decide(
                Message(machine: true), [Replies()],
                mailboxAcknowledges: true, referenceIsProven: true)
            .ReplyNow.Should().BeFalse();
    }

    /// <summary>
    /// Nothing above the line is nothing to send. The analyst does not raise this suggestion
    /// without the text, and an empty mail to a customer in our name is not a thing to risk
    /// on that staying true.
    /// </summary>
    [Fact]
    public void An_empty_reply_is_not_sent()
    {
        MailSuggestion nothing = Replies();
        nothing.DraftText = "   ";

        ArrivalPolicy.Decide(
                Message(), [nothing], mailboxAcknowledges: true, referenceIsProven: true)
            .ReplyNow.Should().BeFalse();
    }

    /// <summary>The tenant's switch is the outer one for all three acts.</summary>
    [Fact]
    public void The_mailbox_switch_also_settles_whether_a_reply_is_relayed()
    {
        ArrivalPolicy.Decide(
                Message(), [Replies()], mailboxAcknowledges: false, referenceIsProven: true)
            .ReplyNow.Should().BeFalse();
    }
}
