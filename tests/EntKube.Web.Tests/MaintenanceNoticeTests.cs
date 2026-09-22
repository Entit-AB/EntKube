using EntKube.Web.Data;
using EntKube.Web.Services.Support;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The notice owed before planned maintenance, counted in working days.
///
/// <para>The figure decides nothing on its own — nothing here refuses a window. What it
/// decides is what the operator is told before they save it and what the monthly report
/// says afterwards, and both of those are only worth anything if the count is right over
/// a weekend, over Easter, and over the three eves §9 closes.</para>
/// </summary>
public class MaintenanceNoticeTests
{
    private static DateTime Swedish(int year, int month, int day, int hour = 9, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    // ---- Counting the days ----------------------------------------------------------------

    /// <summary>
    /// A plain week: announced on Monday, starting the Monday after. Five working days
    /// lie between them, which is exactly what is owed.
    /// </summary>
    [Fact]
    public void A_week_of_notice_over_a_plain_weekend_is_five_working_days()
    {
        NoticeGiven notice = MaintenanceNotice.Assess(
            Swedish(2026, 9, 7), Swedish(2026, 9, 14, 22));

        notice.Given.Should().Be(5);
        notice.IsSufficient.Should().BeTrue();
        notice.Shortfall.Should().Be(0);
    }

    /// <summary>
    /// The same seven days, but over Christmas. 24, 25 and 26 December are closed under
    /// §9, so a week's notice is two working days — a case nobody counts correctly by
    /// looking at a calendar in a hurry, which is the whole reason this is computed.
    /// </summary>
    [Fact]
    public void The_same_week_over_Christmas_is_not_enough()
    {
        // Monday 21 December 2026 to Monday 28 December. 24th (eve), 25th and 26th are
        // closed, leaving only the 22nd and 23rd.
        NoticeGiven notice = MaintenanceNotice.Assess(
            Swedish(2026, 12, 21), Swedish(2026, 12, 28, 22));

        notice.Given.Should().Be(3);
        notice.IsSufficient.Should().BeFalse();
        notice.Shortfall.Should().Be(2);
    }

    /// <summary>Told the same morning: no working days at all have passed.</summary>
    [Fact]
    public void Maintenance_announced_the_same_day_gives_no_notice()
    {
        NoticeGiven notice = MaintenanceNotice.Assess(
            Swedish(2026, 9, 22, 8), Swedish(2026, 9, 22, 22));

        notice.Given.Should().Be(0);
        notice.Shortfall.Should().Be(5);
    }

    /// <summary>
    /// The deadline and the count have to agree. Announcing exactly on the deadline gives
    /// the required days; a day later gives one fewer — otherwise the interface would tell
    /// an operator a date that the report then scored as short.
    /// </summary>
    [Fact]
    public void The_deadline_is_the_last_day_that_still_gives_full_notice()
    {
        DateTime start = Swedish(2026, 9, 30, 22);

        NoticeGiven notice = MaintenanceNotice.Assess(Swedish(2026, 9, 1), start);

        DateTime onDeadline = TimeZoneInfo.ConvertTimeToUtc(
            notice.Deadline.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

        MaintenanceNotice.Assess(onDeadline, start).IsSufficient.Should().BeTrue();
        MaintenanceNotice.Assess(onDeadline.AddDays(1), start).Given.Should().BeLessThan(5);
    }

    /// <summary>
    /// The count is Swedish calendar days, not UTC ones. An announcement at 23:30 UTC on a
    /// Sunday is Monday morning in Stockholm, and counting the UTC date would credit a
    /// working day that nobody had.
    /// </summary>
    [Fact]
    public void Notice_is_counted_in_Swedish_days_not_UTC_ones()
    {
        // 23:30 UTC on Sunday 13 September 2026 is 01:30 Monday the 14th in Stockholm.
        DateTime announced = new(2026, 9, 13, 23, 30, 0, DateTimeKind.Utc);

        NoticeGiven fromMonday = MaintenanceNotice.Assess(announced, Swedish(2026, 9, 18, 22));

        // Monday the 14th to Friday the 18th: four working days, not five.
        fromMonday.Given.Should().Be(4);
        fromMonday.IsSufficient.Should().BeFalse();
    }

    /// <summary>
    /// A window read back from the database arrives as <see cref="DateTimeKind.Unspecified"/>,
    /// and everything stored is UTC. Treating it as local would shift the instant by the
    /// server's offset — the fault that once turned worked time negative.
    /// </summary>
    [Fact]
    public void An_unspecified_kind_is_read_as_UTC()
    {
        DateTime utc = new(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc);
        DateTime asRead = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        MaintenanceNotice.Assess(asRead, Swedish(2026, 9, 14, 22))
            .Should().Be(MaintenanceNotice.Assess(utc, Swedish(2026, 9, 14, 22)));
    }

    // ---- Emergencies ----------------------------------------------------------------------

    /// <summary>
    /// Emergency maintenance owes no notice. It is still recorded, because the downtime is
    /// still excluded from availability and the customer is still owed the fact that it
    /// happened.
    /// </summary>
    [Fact]
    public void Emergency_maintenance_owes_no_notice()
    {
        NoticeGiven notice = MaintenanceNotice.Assess(
            Swedish(2026, 9, 22, 8), Swedish(2026, 9, 22, 9), MaintenanceKind.Emergency);

        notice.Exempt.Should().BeTrue();
        notice.IsSufficient.Should().BeTrue();
        notice.Shortfall.Should().Be(0);
        notice.Describe().Should().Be("Emergency — no notice owed");
    }

    /// <summary>
    /// A window recorded without saying which kind it is has not earned the exemption.
    /// This is the default the migration gives every window that predates the column.
    /// </summary>
    [Fact]
    public void A_window_that_says_nothing_is_planned_and_owes_notice() =>
        MaintenanceNotice.Assess(Swedish(2026, 9, 22, 8), Swedish(2026, 9, 22, 22))
            .Exempt.Should().BeFalse();

    // ---- Reading a stored window ------------------------------------------------------------

    /// <summary>
    /// With no separate announcement recorded, the notice runs from when the window was
    /// created — the only announcement the system can vouch for.
    /// </summary>
    [Fact]
    public void Without_an_announcement_the_notice_runs_from_when_it_was_created()
    {
        MaintenanceWindow window = new()
        {
            Title = "Database upgrade",
            CreatedBy = "nils",
            CreatedAt = Swedish(2026, 9, 7),
            StartsAt = Swedish(2026, 9, 14, 22),
            EndsAt = Swedish(2026, 9, 15, 2),
        };

        MaintenanceNotice.Assess(window).Given.Should().Be(5);
    }

    /// <summary>
    /// A window entered here after the mail already went out counts from the mail. Scoring
    /// it from the moment someone got round to recording it would be wrong in the
    /// direction that matters — it would report a breach that did not happen.
    /// </summary>
    [Fact]
    public void A_recorded_announcement_counts_from_when_the_customer_was_told()
    {
        MaintenanceWindow window = new()
        {
            Title = "Database upgrade",
            CreatedBy = "nils",
            CreatedAt = Swedish(2026, 9, 14, 8),
            AnnouncedAt = Swedish(2026, 9, 4),
            StartsAt = Swedish(2026, 9, 14, 22),
            EndsAt = Swedish(2026, 9, 15, 2),
        };

        MaintenanceNotice.Assess(window).IsSufficient.Should().BeTrue();
    }

    // ---- The calendar primitive underneath --------------------------------------------------

    /// <summary>
    /// Counting back and counting forward have to be inverses, or a deadline and the days
    /// it implies can disagree. The Sunday case is the one that caught a naive
    /// implementation: maintenance starting on a closed day landed the deadline a working
    /// day short of what it claimed.
    /// </summary>
    [Theory]
    [InlineData(2026, 9, 30, 5)]    // a plain Wednesday
    [InlineData(2026, 12, 28, 5)]   // across Christmas
    [InlineData(2026, 4, 7, 5)]     // across Easter
    [InlineData(2026, 1, 4, 10)]    // a Sunday start, across New Year's Eve
    [InlineData(2026, 6, 20, 5)]    // a Saturday start, across Midsummer
    public void The_deadline_inverts_counting_the_days(
        int year, int month, int day, int count)
    {
        DateOnly start = new(year, month, day);
        DateOnly back = SwedishHolidays.WorkingDaysBefore(start, count);

        SwedishHolidays.WorkingDaysBetween(back, start).Should().Be(count);
        SwedishHolidays.IsWorkingDay(back).Should().BeTrue(
            "the deadline has to be a day someone could actually have sent the mail");
    }

    /// <summary>No notice owed, no deadline to meet.</summary>
    [Fact]
    public void A_notice_period_of_nothing_has_no_deadline_before_the_day_itself() =>
        SwedishHolidays.WorkingDaysBefore(new DateOnly(2026, 9, 22), 0)
            .Should().Be(new DateOnly(2026, 9, 22));
}
