using EntKube.Web.Data;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The §14.4 clocks. These decide whether a response time was missed, and §14.6 pays the
/// customer 10% of the window fee for each P1 or P2 response breach — so an error here
/// costs money in one direction and credibility in the other.
///
/// <para>Instants are written in Swedish local time. Reference dates: 2026-09-22 is a
/// Tuesday, 2026-09-25 a Friday, 2026-09-26 a Saturday, 2026-09-28 a Monday.</para>
/// </summary>
public class TicketClockTests
{
    private static DateTime Swedish(int year, int month, int day, int hour = 0, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static DateTime Tue(int hour, int minute = 0) => Swedish(2026, 9, 22, hour, minute);

    private static DateTime Fri(int hour, int minute = 0) => Swedish(2026, 9, 25, hour, minute);

    private static DateTime Mon(int hour, int minute = 0) => Swedish(2026, 9, 28, hour, minute);

    // ---- Response ----------------------------------------------------------------------

    /// <summary>
    /// The case the whole subsystem exists for. A P1 raised at 16:00 on a Friday under S1
    /// has until 09:00 on Monday — two hours of open window, not two hours of wall clock.
    /// </summary>
    [Fact]
    public void A_P1_response_target_spans_the_weekend_under_S1()
    {
        ClockStatus status = TicketClock.Response(
            Fri(16), Fri(16), TicketPriority.P1, SupportWindow.S1,
            firstResponseAt: null, now: Fri(16, 30));

        status.Deadline.Should().Be(Mon(9));
        status.Breached.Should().BeFalse();
    }

    [Fact]
    public void The_same_P1_under_S4_is_due_two_hours_later()
    {
        ClockStatus status = TicketClock.Response(
            Fri(16), Fri(16), TicketPriority.P1, SupportWindow.S4,
            firstResponseAt: null, now: Fri(16, 30));

        status.Deadline.Should().Be(Fri(18));
    }

    [Fact]
    public void A_response_after_the_deadline_is_a_breach()
    {
        ClockStatus status = TicketClock.Response(
            Tue(9), Tue(9), TicketPriority.P1, SupportWindow.S1,
            firstResponseAt: Tue(12), now: Tue(13));

        status.Deadline.Should().Be(Tue(11));
        status.MetAt.Should().Be(Tue(12));
        status.Breached.Should().BeTrue();
    }

    [Fact]
    public void A_response_inside_the_deadline_is_not()
    {
        ClockStatus status = TicketClock.Response(
            Tue(9), Tue(9), TicketPriority.P1, SupportWindow.S1,
            firstResponseAt: Tue(10, 30), now: Tue(13));

        status.Breached.Should().BeFalse();
        status.Elapsed.Should().Be(TimeSpan.FromMinutes(90));
    }

    /// <summary>
    /// §14.3: after a reprioritisation the new priority's targets run from that moment. A
    /// P3 upgraded to P1 at 14:00 is due a response at 16:00, not two hours after it was
    /// first registered that morning.
    /// </summary>
    [Fact]
    public void Reprioritising_restarts_the_target_from_the_moment_it_happened()
    {
        ClockStatus status = TicketClock.Response(
            clockStartsAt: Tue(9), priorityEffectiveFrom: Tue(14),
            TicketPriority.P1, SupportWindow.S1, firstResponseAt: null, now: Tue(15));

        status.Deadline.Should().Be(Tue(16));
        status.Breached.Should().BeFalse();
    }

    /// <summary>
    /// §14.4 pauses the resolution time by name and says nothing about response time. Since a missed
    /// response is the only thing that carries a penalty, the response clock keeps running —
    /// the reading that cannot be accused of excusing our own lateness.
    /// </summary>
    [Fact]
    public void A_pause_does_not_stop_the_response_clock()
    {
        ClockStatus status = TicketClock.Response(
            Tue(9), Tue(9), TicketPriority.P1, SupportWindow.S1,
            firstResponseAt: null, now: Tue(12));

        status.Deadline.Should().Be(Tue(11));
        status.Breached.Should().BeTrue();
    }

    // ---- Resolution and pauses ----------------------------------------------------------

    [Fact]
    public void A_P2_resolution_target_of_24_hours_spans_three_working_days_under_S1()
    {
        ClockStatus status = TicketClock.Resolution(
            Tue(9), Tue(9), TicketPriority.P2, SupportWindow.S1,
            pauses: [], resolvedAt: null, now: Tue(10));

        // Tue 09–17 is 8h, Wed 9h, leaving 7h of the Thursday.
        status.Deadline.Should().Be(Swedish(2026, 9, 24, 15));
    }

    /// <summary>
    /// §14.4: the clock stops while we wait on the customer or a third party. Two hours of
    /// waiting moves the deadline two hours of window time further out.
    /// </summary>
    [Fact]
    public void A_closed_pause_pushes_the_resolution_deadline_out_by_its_window_time()
    {
        ClockPause waiting = new(Tue(10), Tue(12));

        ClockStatus status = TicketClock.Resolution(
            Tue(9), Tue(9), TicketPriority.P2, SupportWindow.S1,
            pauses: [waiting], resolvedAt: null, now: Tue(13));

        status.Deadline.Should().Be(Swedish(2026, 9, 24, 17));
        status.Elapsed.Should().Be(TimeSpan.FromHours(2));
    }

    /// <summary>
    /// Time spent waiting outside the support window was never counted in the first place,
    /// so a pause over a weekend under S1 changes nothing.
    /// </summary>
    [Fact]
    public void A_pause_outside_the_window_costs_nothing()
    {
        ClockPause overTheWeekend = new(Fri(18), Mon(7));

        ClockStatus withPause = TicketClock.Resolution(
            Fri(9), Fri(9), TicketPriority.P2, SupportWindow.S1,
            pauses: [overTheWeekend], resolvedAt: null, now: Mon(9));

        ClockStatus without = TicketClock.Resolution(
            Fri(9), Fri(9), TicketPriority.P2, SupportWindow.S1,
            pauses: [], resolvedAt: null, now: Mon(9));

        withPause.Deadline.Should().Be(without.Deadline);
    }

    /// <summary>
    /// While a pause is open the clock is not running, so there is no deadline to miss. A
    /// ticket waiting on the customer's hosting provider is not late.
    /// </summary>
    [Fact]
    public void An_open_pause_leaves_the_ticket_with_no_deadline()
    {
        ClockPause stillWaiting = new(Tue(10), null);

        ClockStatus status = TicketClock.Resolution(
            Tue(9), Tue(9), TicketPriority.P2, SupportWindow.S1,
            pauses: [stillWaiting], resolvedAt: null, now: Swedish(2026, 10, 9, 12));

        status.Deadline.Should().BeNull();
        status.Paused.Should().BeTrue();
        status.Breached.Should().BeFalse();
        status.Elapsed.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Several_pauses_all_count()
    {
        ClockPause first = new(Tue(10), Tue(11));
        ClockPause second = new(Tue(13), Tue(15));

        ClockStatus status = TicketClock.Resolution(
            Tue(9), Tue(9), TicketPriority.P1, SupportWindow.S1,
            pauses: [first, second], resolvedAt: null, now: Tue(16));

        // Seven hours elapsed, three of them paused.
        status.Elapsed.Should().Be(TimeSpan.FromHours(4));
    }

    // ---- Working-day targets -------------------------------------------------------------

    [Fact]
    public void A_P3_response_is_due_at_the_end_of_the_next_working_day()
    {
        ClockStatus status = TicketClock.Response(
            Tue(9), Tue(9), TicketPriority.P3, SupportWindow.S1,
            firstResponseAt: null, now: Tue(10));

        status.Deadline.Should().Be(Swedish(2026, 9, 23, 17));
    }

    [Fact]
    public void A_P3_resolution_is_due_five_working_days_out()
    {
        ClockStatus status = TicketClock.Resolution(
            Tue(9), Tue(9), TicketPriority.P3, SupportWindow.S1,
            pauses: [], resolvedAt: null, now: Tue(10));

        status.Deadline.Should().Be(Swedish(2026, 9, 29, 17));
    }

    /// <summary>
    /// §14.4 gives P4 "nästa release eller enligt överenskommelse", which is not a deadline
    /// anything can be measured against — so it has none, and can never be breached.
    /// </summary>
    [Fact]
    public void A_P4_has_no_resolution_deadline()
    {
        ClockStatus status = TicketClock.Resolution(
            Tue(9), Tue(9), TicketPriority.P4, SupportWindow.S1,
            pauses: [], resolvedAt: null, now: Swedish(2027, 1, 1));

        status.Deadline.Should().BeNull();
        status.Breached.Should().BeFalse();
    }

    // ---- Escalation -----------------------------------------------------------------------

    [Fact]
    public void A_P1_escalates_to_level_two_after_four_hours()
    {
        DateTime? due = TicketClock.EscalationDue(
            Tue(9), Tue(9), TicketPriority.P1, SupportWindow.S1, [], resolvedAt: null);

        due.Should().Be(Tue(13));
    }

    [Fact]
    public void A_P2_escalates_after_sixteen()
    {
        DateTime? due = TicketClock.EscalationDue(
            Tue(9), Tue(9), TicketPriority.P2, SupportWindow.S1, [], resolvedAt: null);

        // Eight hours of the Tuesday, then eight of the Wednesday.
        due.Should().Be(Swedish(2026, 9, 23, 16));
    }

    [Fact]
    public void A_resolved_ticket_does_not_escalate()
    {
        DateTime? due = TicketClock.EscalationDue(
            Tue(9), Tue(9), TicketPriority.P1, SupportWindow.S1, [], resolvedAt: Tue(10));

        due.Should().BeNull();
    }

    [Fact]
    public void A_P3_does_not_escalate_on_a_timer() =>
        TicketClock.EscalationDue(Tue(9), Tue(9), TicketPriority.P3, SupportWindow.S1, [], null)
            .Should().BeNull();

    // ---- The §14.4 table itself ------------------------------------------------------------

    [Theory]
    [InlineData(TicketPriority.P1, 2, 8)]
    [InlineData(TicketPriority.P2, 4, 24)]
    public void The_hour_based_targets_match_the_agreement(
        TicketPriority priority, int responseHours, int resolutionHours)
    {
        TicketSla.Response(priority).WindowTime.Should().Be(TimeSpan.FromHours(responseHours));
        TicketSla.Resolution(priority).WindowTime.Should().Be(TimeSpan.FromHours(resolutionHours));
    }

    [Fact]
    public void The_working_day_targets_match_the_agreement()
    {
        TicketSla.Response(TicketPriority.P3).WorkingDays.Should().Be(1);
        TicketSla.Response(TicketPriority.P4).WorkingDays.Should().Be(2);
        TicketSla.Resolution(TicketPriority.P3).WorkingDays.Should().Be(5);
        TicketSla.Resolution(TicketPriority.P4).Exists.Should().BeFalse();
    }

    [Fact]
    public void The_update_intervals_match_the_agreement()
    {
        TicketSla.UpdateInterval(TicketPriority.P1).Should().Be(TimeSpan.FromHours(1));
        TicketSla.UpdateInterval(TicketPriority.P2).Should().Be(TimeSpan.FromHours(4));
        TicketSla.UpdateInterval(TicketPriority.P3).Should().BeNull();
    }

    /// <summary>
    /// §14.6 pays a penalty for a missed response on P1 and P2 only, and explicitly not for
    /// an overrun resolution time, which it calls a goal.
    /// </summary>
    [Theory]
    [InlineData(TicketPriority.P1, true)]
    [InlineData(TicketPriority.P2, true)]
    [InlineData(TicketPriority.P3, false)]
    [InlineData(TicketPriority.P4, false)]
    public void Only_P1_and_P2_response_breaches_carry_a_penalty(TicketPriority priority, bool carries) =>
        TicketSla.ResponseBreachCarriesPenalty(priority).Should().Be(carries);
}
