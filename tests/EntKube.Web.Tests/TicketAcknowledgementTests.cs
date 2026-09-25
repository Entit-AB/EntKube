using EntKube.Web.Data;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The receipt a reporter gets back.
///
/// <para>This is the only thing in the subsystem that reaches a customer without a person
/// pressing a button, so what it says is worth arguing over in a test rather than in a
/// mailbox. The rule it has to keep is §14.3's: until our first assessment, the priority
/// is the customer's, and a receipt announcing one as though it were settled either binds
/// us to something nobody assessed or reads as a downgrade of what they told us.</para>
/// </summary>
public class TicketAcknowledgementTests
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static Ticket Reported(
        TicketPriority priority = TicketPriority.P2, int number = 1042) => new()
    {
        Id = Guid.NewGuid(),
        Number = number,
        TenantId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        Title = "Journalen svarar inte",
        Description = "Ingen på avdelning 4 kommer in.",
        Priority = priority,
        ReportedAt = Swedish(2026, 9, 22, 9, 15),
        ClockStartsAt = Swedish(2026, 9, 22, 9, 15),
        PriorityEffectiveFrom = Swedish(2026, 9, 22, 9, 15),
        Channel = TicketChannel.Email,
    };

    private static Acknowledgement Receipt(
        TicketPriority priority = TicketPriority.P2,
        SupportWindow window = SupportWindow.S1,
        string? appName = "Journalportalen",
        DateTime? due = null) =>
        TicketAcknowledgement.For(
            Reported(priority), window, appName, due ?? Swedish(2026, 9, 22, 13, 15));

    // ---- The reference ----------------------------------------------------------------------

    /// <summary>
    /// The number goes in the subject, not only the body. A reply threads on In-Reply-To
    /// when the client sends one and on the subject when it does not — and the person
    /// quoting it on the phone reads the subject.
    /// </summary>
    [Fact]
    public void The_subject_carries_the_number_and_what_they_reported()
    {
        Acknowledgement receipt = Receipt();

        receipt.Subject.Should().StartWith("[#1042]");
        receipt.Subject.Should().Contain("Journalen svarar inte");
    }

    [Fact]
    public void The_body_states_the_reference_they_should_quote() =>
        Receipt().Body.Should().Contain("[#1042]");

    // ---- What it must not claim ---------------------------------------------------------------

    /// <summary>
    /// <b>The rule.</b> §14.3 gives the customer's assessment precedence until our first
    /// one, so the receipt says the priority is as they reported it and that confirmation
    /// is still to come. Saying "Priority: P2" flat would be us deciding.
    /// </summary>
    [Fact]
    public void The_priority_is_given_as_reported_and_not_as_decided()
    {
        string body = Receipt().Body;

        body.Should().Contain("as reported, not yet confirmed");
        body.Should().Contain("confirm the priority in writing",
            "the customer is told the assessment is a separate act that is still coming");
    }

    /// <summary>
    /// And that they may disagree with it. §14.3's precedence is worth nothing to somebody
    /// who does not know they have it.
    /// </summary>
    [Fact]
    public void The_customer_is_told_they_can_disagree_with_the_assessment() =>
        Receipt().Body.Should().Contain("If you disagree");

    // ---- The response target --------------------------------------------------------------------

    /// <summary>
    /// The useful sentence. A deadline computed inside the support window is the one thing
    /// the customer cannot work out for themselves, and it is what stops the next mail
    /// being "has anybody seen this".
    /// </summary>
    [Fact]
    public void A_response_target_is_given_as_a_time_they_can_hold_us_to()
    {
        string body = Receipt(due: Swedish(2026, 9, 22, 13, 15)).Body;

        body.Should().Contain("We will come back to you by");
        body.Should().Contain("13:15");
        body.Should().Contain("Tuesday 22 September");
    }

    /// <summary>
    /// Stated, because a customer who reports a fault at 16:55 under S1 and hears "by
    /// 09:55" would otherwise reasonably think we had lost a night.
    /// </summary>
    [Fact]
    public void The_receipt_explains_that_the_clock_runs_inside_the_support_hours() =>
        Receipt().Body.Should().Contain("time outside them does not count against it");

    /// <summary>P4 has no response target, and the receipt says so rather than inventing one.</summary>
    [Fact]
    public void A_priority_with_no_target_promises_an_assessment_instead()
    {
        string body = TicketAcknowledgement.For(
            Reported(TicketPriority.P4), SupportWindow.S1, null, responseDue: null).Body;

        body.Should().Contain("no fixed response time");
        body.Should().NotContain("We will come back to you by");
    }

    // ---- Written for the person reading it ------------------------------------------------------

    /// <summary>
    /// "P2" and "S1" are our vocabulary. The person who reported a fault from a ward has
    /// no reason to know either, and a receipt is for them.
    /// </summary>
    [Fact]
    public void The_codes_are_spelled_out_in_words()
    {
        string body = Receipt(TicketPriority.P2, SupportWindow.S1).Body;

        body.Should().Contain("a serious fault");
        body.Should().Contain("working days 08:00–17:00");
    }

    [Theory]
    [InlineData(SupportWindow.S3, "every day 05:00–22:00")]
    [InlineData(SupportWindow.S4, "around the clock")]
    public void Each_support_window_is_described_rather_than_named(
        SupportWindow window, string expected) =>
        TicketAcknowledgement.Describe(window).Should().Be(expected);

    /// <summary>
    /// Times are shown in Stockholm. Everything is stored in UTC, and a receipt quoting it
    /// would have the reader doing arithmetic on their own deadline.
    /// </summary>
    [Fact]
    public void Times_are_shown_on_the_readers_clock()
    {
        // 09:15 Stockholm is 07:15 UTC in September.
        string body = Receipt().Body;

        body.Should().Contain("09:15");
        body.Should().NotContain("07:15");
    }

    /// <summary>An application is named when one was recognised, and not invented when not.</summary>
    [Fact]
    public void The_application_appears_only_when_it_is_known()
    {
        Receipt(appName: "Journalportalen").Body.Should().Contain("Journalportalen");

        TicketAcknowledgement.For(Reported(), SupportWindow.S1, null, null)
            .Body.Should().NotContain("Application:");
    }

    /// <summary>
    /// The instruction that keeps a conversation on one ticket instead of opening a second.
    /// </summary>
    [Fact]
    public void The_reader_is_told_that_replying_adds_to_the_same_ticket() =>
        Receipt().Body.Should().Contain("Replying to this message adds your reply to the same ticket");
}
