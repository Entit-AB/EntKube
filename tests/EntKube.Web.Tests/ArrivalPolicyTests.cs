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
    private static InboundMailMessage Message(
        Guid? customerId = null, bool machine = false) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = "karin@kund.example",
            Subject = "Journalen svarar inte",
            Body = "Ingen kommer in.",
            CustomerId = customerId ?? Guid.NewGuid(),
            IsMachineGenerated = machine,
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
}
