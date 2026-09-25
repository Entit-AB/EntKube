using EntKube.Web.Data;
using EntKube.Web.Services.Support;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Support windows (§9), SLA clocks (§14.4) and time categories (§13).
///
/// <para>These are the numbers a customer can dispute and §14.6 can attach a penalty to, so
/// each case is named after the rule it defends. Instants are written in Swedish local time
/// through <see cref="Swedish"/> and converted with the framework's own time zone data, not
/// with the production helper, so a mistake in that helper cannot cancel itself out.</para>
///
/// <para>Reference dates: 2026-09-22 is a Tuesday, 2026-09-25 a Friday, 2026-09-26 a
/// Saturday, 2026-09-28 a Monday. Easter 2026 falls on 5 April, so 3 April is Good Friday
/// and 6 April Easter Monday.</para>
/// </summary>
public class BusinessCalendarTests
{
    private static DateTime Swedish(int year, int month, int day, int hour = 0, int minute = 0)
    {
        DateTime local = new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, BusinessCalendar.SwedishTime);
    }

    private static DateTime Tuesday(int hour, int minute = 0) => Swedish(2026, 9, 22, hour, minute);

    private static DateTime Saturday(int hour, int minute = 0) => Swedish(2026, 9, 26, hour, minute);

    // ---- §9: which hours each window covers -------------------------------------------

    [Theory]
    [InlineData(7, 59, false)]
    [InlineData(8, 0, true)]
    [InlineData(12, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]   // The window closes at 17:00; the instant itself is outside.
    [InlineData(21, 0, false)]
    public void S1_covers_office_hours_on_a_working_day(int hour, int minute, bool open) =>
        BusinessCalendar.IsOpen(Tuesday(hour, minute), SupportWindow.S1).Should().Be(open);

    [Theory]
    [InlineData(4, 59, false)]
    [InlineData(5, 0, true)]
    [InlineData(7, 0, true)]
    [InlineData(21, 59, true)]
    [InlineData(22, 0, false)]
    public void S2_adds_the_early_morning_and_the_evening(int hour, int minute, bool open) =>
        BusinessCalendar.IsOpen(Tuesday(hour, minute), SupportWindow.S2).Should().Be(open);

    [Fact]
    public void S1_and_S2_are_closed_at_the_weekend()
    {
        BusinessCalendar.IsOpen(Saturday(10), SupportWindow.S1).Should().BeFalse();
        BusinessCalendar.IsOpen(Saturday(10), SupportWindow.S2).Should().BeFalse();
    }

    /// <summary>
    /// Epiphany 2026 is a Tuesday. A weekday that is a public holiday is not a working
    /// day, so S1 and S2 are shut and no response clock runs.
    /// </summary>
    [Fact]
    public void S1_is_closed_on_a_red_day_that_falls_midweek() =>
        BusinessCalendar.IsOpen(Swedish(2026, 1, 6, 10), SupportWindow.S1).Should().BeFalse();

    /// <summary>
    /// Christmas Eve is an ordinary working day in Swedish law and a closed day under §9. If this
    /// test fails, the agreement's own definition of public holiday has been lost somewhere.
    /// </summary>
    [Fact]
    public void S1_is_closed_on_julafton() =>
        BusinessCalendar.IsOpen(Swedish(2026, 12, 24, 10), SupportWindow.S1).Should().BeFalse();

    [Fact]
    public void S3_is_open_at_the_weekend_and_on_red_days()
    {
        BusinessCalendar.IsOpen(Saturday(10), SupportWindow.S3).Should().BeTrue();
        BusinessCalendar.IsOpen(Swedish(2026, 12, 24, 10), SupportWindow.S3).Should().BeTrue();
    }

    [Fact]
    public void S3_still_closes_at_night() =>
        BusinessCalendar.IsOpen(Saturday(23), SupportWindow.S3).Should().BeFalse();

    [Theory]
    [InlineData(2026, 12, 24, 3)]    // Julafton, small hours
    [InlineData(2026, 9, 27, 23)]    // Sunday night
    [InlineData(2026, 1, 1, 0)]      // Nyårsdagen, midnight
    public void S4_is_always_open(int year, int month, int day, int hour) =>
        BusinessCalendar.IsOpen(Swedish(year, month, day, hour), SupportWindow.S4).Should().BeTrue();

    // ---- §9.1: a ticket arriving outside the window ------------------------------------

    [Fact]
    public void A_ticket_arriving_after_hours_starts_at_the_next_opening() =>
        BusinessCalendar.NextOpening(Swedish(2026, 9, 25, 18), SupportWindow.S1)
            .Should().Be(Swedish(2026, 9, 28, 8));

    /// <summary>
    /// Maundy Thursday evening under S1. The next opening is the Tuesday after Easter:
    /// Good Friday, the weekend and Easter Monday are all closed.
    /// </summary>
    [Fact]
    public void The_next_opening_steps_over_the_whole_Easter_weekend() =>
        BusinessCalendar.NextOpening(Swedish(2026, 4, 2, 18), SupportWindow.S1)
            .Should().Be(Swedish(2026, 4, 7, 8));

    [Fact]
    public void An_instant_inside_the_window_is_its_own_next_opening()
    {
        DateTime insideHours = Tuesday(10);

        BusinessCalendar.NextOpening(insideHours, SupportWindow.S1).Should().Be(insideHours);
    }

    // ---- §14.4: elapsed time is counted inside the window ------------------------------

    /// <summary>
    /// Friday 16:00 to Monday 09:00 is 65 hours of wall time and two hours of S1. This is the
    /// difference that decides whether a weekend ticket breached its response target.
    /// </summary>
    [Fact]
    public void Only_time_inside_the_window_counts() =>
        BusinessCalendar.OpenTimeBetween(
                Swedish(2026, 9, 25, 16), Swedish(2026, 9, 28, 9), SupportWindow.S1)
            .Should().Be(TimeSpan.FromHours(2));

    [Fact]
    public void Under_S4_window_time_is_wall_time() =>
        BusinessCalendar.OpenTimeBetween(
                Swedish(2026, 9, 25, 16), Swedish(2026, 9, 28, 9), SupportWindow.S4)
            .Should().Be(TimeSpan.FromHours(65));

    [Fact]
    public void A_full_working_day_of_S1_is_nine_hours() =>
        BusinessCalendar.OpenTimeBetween(Tuesday(0), Tuesday(23, 59), SupportWindow.S1)
            .Should().Be(TimeSpan.FromHours(9));

    [Fact]
    public void A_reversed_range_is_zero() =>
        BusinessCalendar.OpenTimeBetween(Tuesday(12), Tuesday(9), SupportWindow.S1)
            .Should().Be(TimeSpan.Zero);

    // ---- Daylight saving ----------------------------------------------------------------

    /// <summary>
    /// The clocks go forward on 29 March 2026, so midnight to noon is eleven hours, not
    /// twelve. Measured on the UTC instants, which is why the wall clock cannot mislead it.
    /// </summary>
    [Fact]
    public void Spring_forward_makes_the_day_an_hour_shorter() =>
        BusinessCalendar.OpenTimeBetween(
                Swedish(2026, 3, 29, 0), Swedish(2026, 3, 29, 12), SupportWindow.S4)
            .Should().Be(TimeSpan.FromHours(11));

    [Fact]
    public void Autumn_back_makes_the_day_an_hour_longer() =>
        BusinessCalendar.OpenTimeBetween(
                Swedish(2026, 10, 25, 0), Swedish(2026, 10, 25, 12), SupportWindow.S4)
            .Should().Be(TimeSpan.FromHours(13));

    /// <summary>
    /// Both Swedish transitions happen between 02:00 and 03:00, before any window opens, so
    /// an S3 Sunday is seventeen hours on either side of them.
    /// </summary>
    [Theory]
    [InlineData(2026, 3, 29)]
    [InlineData(2026, 10, 25)]
    public void A_transition_before_opening_does_not_change_the_window(int year, int month, int day) =>
        BusinessCalendar.OpenTimeBetween(
                Swedish(year, month, day, 0), Swedish(year, month, day + 1, 0), SupportWindow.S3)
            .Should().Be(TimeSpan.FromHours(17));

    // ---- §14.4: deadlines --------------------------------------------------------------

    /// <summary>
    /// A P1 raised at 16:00 on a Friday under S1 has a two-hour response target that expires
    /// at 09:00 on Monday. Measured in wall time it would have expired on Friday evening,
    /// and the report would show a breach that did not happen.
    /// </summary>
    [Fact]
    public void A_P1_response_target_rolls_over_the_weekend() =>
        BusinessCalendar.Deadline(Swedish(2026, 9, 25, 16), TimeSpan.FromHours(2), SupportWindow.S1)
            .Should().Be(Swedish(2026, 9, 28, 9));

    /// <summary>
    /// A P2 raised before Easter: one hour on the Thursday, then nine hours on each of the
    /// Tuesday and Wednesday after the holiday, leaving five on the Thursday.
    /// </summary>
    [Fact]
    public void A_P2_resolution_target_survives_a_four_day_closure() =>
        BusinessCalendar.Deadline(Swedish(2026, 4, 2, 16), TimeSpan.FromHours(24), SupportWindow.S1)
            .Should().Be(Swedish(2026, 4, 9, 13));

    [Fact]
    public void Under_S4_a_deadline_is_plain_addition() =>
        BusinessCalendar.Deadline(Saturday(23), TimeSpan.FromHours(2), SupportWindow.S4)
            .Should().Be(Swedish(2026, 9, 27, 1));

    [Fact]
    public void A_deadline_starting_outside_the_window_begins_at_the_opening() =>
        BusinessCalendar.Deadline(Swedish(2026, 9, 25, 18), TimeSpan.FromHours(4), SupportWindow.S1)
            .Should().Be(Swedish(2026, 9, 28, 12));

    [Fact]
    public void A_zero_budget_is_the_moment_the_clock_starts() =>
        BusinessCalendar.Deadline(Swedish(2026, 9, 25, 18), TimeSpan.Zero, SupportWindow.S1)
            .Should().Be(Swedish(2026, 9, 28, 8));

    [Fact]
    public void A_negative_budget_is_rejected()
    {
        Action act = () =>
            BusinessCalendar.Deadline(Tuesday(10), TimeSpan.FromHours(-1), SupportWindow.S1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- §14.4 / §14.6 / §23: targets counted in working days ---------------------------

    /// <summary>
    /// The five working days §14.6 allows for a P1 incident report, starting the Wednesday
    /// before Christmas. Christmas Eve, Christmas Day, Boxing Day, New Year's Eve and New Year's Day all fall
    /// out, so the report is due when the working day ends on 5 January.
    /// </summary>
    [Fact]
    public void A_five_working_day_deliverable_steps_over_the_holidays() =>
        BusinessCalendar.WorkingDaysDeadline(Swedish(2026, 12, 23, 14), 5)
            .Should().Be(Swedish(2027, 1, 5, 17));

    [Fact]
    public void A_one_working_day_target_expires_when_the_next_working_day_ends() =>
        BusinessCalendar.WorkingDaysDeadline(Swedish(2026, 9, 25, 9), 1)
            .Should().Be(Swedish(2026, 9, 28, 17));

    /// <summary>
    /// A Swedish working day ends at 17:00, not at midnight — a deliverable is due while
    /// there is still someone there to deliver it. 17:00 is also when S1 closes, so a target
    /// counted in working days and one counted in window hours agree about when the day is over.
    /// </summary>
    [Fact]
    public void A_working_day_target_expires_at_seventeen_hundred()
    {
        DateTime due = BusinessCalendar.WorkingDaysDeadline(Swedish(2026, 9, 22, 9), 3);

        TimeZoneInfo.ConvertTimeFromUtc(due, BusinessCalendar.SwedishTime).TimeOfDay
            .Should().Be(BusinessCalendar.WorkingDayEnd.ToTimeSpan());
        BusinessCalendar.IsOpen(due.AddMinutes(-1), SupportWindow.S1).Should().BeTrue();
        BusinessCalendar.IsOpen(due, SupportWindow.S1).Should().BeFalse();
    }

    // ---- §13: time categories -----------------------------------------------------------

    [Theory]
    [InlineData(8, SupportTimeCategory.Ordinary)]
    [InlineData(12, SupportTimeCategory.Ordinary)]
    [InlineData(16, SupportTimeCategory.Ordinary)]
    [InlineData(5, SupportTimeCategory.EveningMorning)]
    [InlineData(7, SupportTimeCategory.EveningMorning)]
    [InlineData(17, SupportTimeCategory.EveningMorning)]
    [InlineData(21, SupportTimeCategory.EveningMorning)]
    [InlineData(22, SupportTimeCategory.Night)]
    [InlineData(3, SupportTimeCategory.Night)]
    public void A_working_day_is_priced_by_the_clock(int hour, SupportTimeCategory expected) =>
        BusinessCalendar.CategoryAt(Tuesday(hour)).Should().Be(expected);

    [Fact]
    public void Weekend_daytime_is_the_weekend_rate() =>
        BusinessCalendar.CategoryAt(Saturday(10)).Should().Be(SupportTimeCategory.WeekendOrRedDay);

    [Fact]
    public void A_red_day_is_priced_like_a_weekend() =>
        BusinessCalendar.CategoryAt(Swedish(2026, 12, 24, 10))
            .Should().Be(SupportTimeCategory.WeekendOrRedDay);

    /// <summary>
    /// §13 puts night at "samtliga dagar", so 22:00–05:00 outranks the weekend rate rather
    /// than the two stacking.
    /// </summary>
    [Fact]
    public void Night_outranks_the_weekend() =>
        BusinessCalendar.CategoryAt(Saturday(23)).Should().Be(SupportTimeCategory.Night);

    /// <summary>
    /// §13 prices by when the work happened, not by which window was bought. The same
    /// Tuesday afternoon is ordinary time whether the application is on S1 or on S4.
    /// </summary>
    [Theory]
    [InlineData(SupportWindow.S1)]
    [InlineData(SupportWindow.S4)]
    public void The_chosen_window_does_not_change_the_rate(SupportWindow window) =>
        BusinessCalendar.CategoryAt(Tuesday(12), window, isP1Callout: false)
            .Should().Be(SupportTimeCategory.Ordinary);

    [Fact]
    public void A_P1_outside_the_bought_window_is_an_utryckning() =>
        BusinessCalendar.CategoryAt(Saturday(10), SupportWindow.S1, isP1Callout: true)
            .Should().Be(SupportTimeCategory.Callout);

    /// <summary>
    /// The same P1 at the same hour on S3 is inside the window, so it is ordinary weekend
    /// work rather than a callout. This is what the customer buys the wider window for.
    /// </summary>
    [Fact]
    public void The_same_P1_inside_a_wider_window_is_not_an_utryckning() =>
        BusinessCalendar.CategoryAt(Saturday(10), SupportWindow.S3, isP1Callout: true)
            .Should().Be(SupportTimeCategory.WeekendOrRedDay);

    [Fact]
    public void Work_that_is_not_a_P1_callout_is_priced_by_the_clock() =>
        BusinessCalendar.CategoryAt(Saturday(10), SupportWindow.S1, isP1Callout: false)
            .Should().Be(SupportTimeCategory.WeekendOrRedDay);

    // ---- Annex C: surcharges and hour bank factors ---------------------------------------

    [Theory]
    [InlineData(SupportTimeCategory.Ordinary, 0, 1.0)]
    [InlineData(SupportTimeCategory.EveningMorning, 25, 1.25)]
    [InlineData(SupportTimeCategory.WeekendOrRedDay, 50, 1.5)]
    [InlineData(SupportTimeCategory.Night, 100, 2.0)]
    [InlineData(SupportTimeCategory.Callout, 100, 2.0)]
    public void Surcharges_and_bank_factors_match_the_price_list(
        SupportTimeCategory category, int surcharge, double factor)
    {
        category.SurchargePercent().Should().Be(surcharge);
        category.BankFactor().Should().Be((decimal)factor);
    }

    /// <summary>§13 bills a call-out at a minimum of two hours per occasion.</summary>
    [Fact]
    public void An_utryckning_bills_at_least_two_hours()
    {
        SupportTimeCategory.Callout.MinimumBillableHours().Should().Be(2m);
        SupportTimeCategory.Ordinary.MinimumBillableHours().Should().Be(1m);
    }
}
