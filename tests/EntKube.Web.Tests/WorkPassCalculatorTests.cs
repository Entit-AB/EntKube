using EntKube.Web.Data;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Time;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The §13 billing arithmetic. These numbers go on an invoice the customer is entitled to
/// validate, so each case is named after the rule it defends.
///
/// <para>2026-09-22 is a Tuesday; 2026-09-26 a Saturday.</para>
/// </summary>
public class WorkPassCalculatorTests
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static DateTime Tue(int hour, int minute = 0) => Swedish(2026, 9, 22, hour, minute);

    private static DateTime Sat(int hour, int minute = 0) => Swedish(2026, 9, 26, hour, minute);

    private static readonly Guid TicketA = Guid.NewGuid();
    private static readonly Guid TicketB = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();

    private static TimeEntry Entry(
        DateTime from, DateTime to, Guid? ticketId = null, WorkKind kind = WorkKind.Management) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            AppId = AppId,
            TicketId = ticketId ?? TicketA,
            StartedAt = from,
            EndedAt = to,
            Kind = kind,
            Description = "Felsökning",
        };

    // ---- Splitting by category -----------------------------------------------------------

    /// <summary>
    /// Work that crosses 17:00 is part ordinary and part kväll. Pricing all of it at
    /// whichever category it started in is the easy mistake, and it is worth 25%.
    /// </summary>
    [Fact]
    public void Work_across_a_boundary_is_split_between_the_categories()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([Entry(Tue(16, 30), Tue(18, 0))]);

        WorkPass pass = passes.Should().ContainSingle().Subject;

        pass.Parts.Should().HaveCount(2);
        pass.Parts[0].Category.Should().Be(SupportTimeCategory.Ordinary);
        pass.Parts[0].WorkedHours.Should().Be(0.5m);
        pass.Parts[1].Category.Should().Be(SupportTimeCategory.EveningMorning);
        pass.Parts[1].WorkedHours.Should().Be(1.0m);
    }

    [Fact]
    public void Weekend_work_is_all_at_the_weekend_rate()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([Entry(Sat(10), Sat(12))]);

        passes[0].Parts.Should().ContainSingle()
            .Which.Category.Should().Be(SupportTimeCategory.WeekendOrRedDay);
    }

    /// <summary>Night runs 22:00–05:00 across midnight, and midnight is not a rate change.</summary>
    [Fact]
    public void A_night_shift_across_midnight_is_one_category()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses(
            [Entry(Tue(23, 0), Swedish(2026, 9, 23, 2, 0))]);

        passes[0].Parts.Should().ContainSingle();
        passes[0].Parts[0].Category.Should().Be(SupportTimeCategory.Night);
        passes[0].Parts[0].WorkedHours.Should().Be(3m);
    }

    // ---- Per påbörjad timme, per pass -------------------------------------------------------

    /// <summary>
    /// §13: contiguous work on one ärende is a single arbetspass and short bursts inside it
    /// are not rounded separately. Three ten-minute touches are one hour, not three.
    /// </summary>
    [Fact]
    public void Short_bursts_within_one_pass_round_once()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([
            Entry(Tue(9, 0), Tue(9, 10)),
            Entry(Tue(9, 30), Tue(9, 40)),
            Entry(Tue(10, 0), Tue(10, 10)),
        ]);

        WorkPass pass = passes.Should().ContainSingle().Subject;

        pass.WorkedHours.Should().Be(0.5m);
        pass.BilledHours.Should().Be(1m);
    }

    /// <summary>
    /// A gap longer than an hour is a separate visit to the ticket, and so its own started
    /// hour. Without this, a morning and an afternoon session would bill as one.
    /// </summary>
    [Fact]
    public void A_long_gap_starts_a_new_pass()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([
            Entry(Tue(9, 0), Tue(9, 20)),
            Entry(Tue(14, 0), Tue(14, 20)),
        ]);

        passes.Should().HaveCount(2);
        passes.Sum(p => p.BilledHours).Should().Be(2m);
    }

    /// <summary>Work on two tickets is two passes however close together it was.</summary>
    [Fact]
    public void Different_tickets_are_different_passes()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([
            Entry(Tue(9, 0), Tue(9, 20), TicketA),
            Entry(Tue(9, 20), Tue(9, 40), TicketB),
        ]);

        passes.Should().HaveCount(2);
        passes.Sum(p => p.BilledHours).Should().Be(2m);
    }

    [Fact]
    public void Ninety_minutes_bills_as_two_hours()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([Entry(Tue(9), Tue(10, 30))]);

        passes[0].BilledHours.Should().Be(2m);
    }

    [Fact]
    public void A_whole_number_of_hours_is_not_rounded_up()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([Entry(Tue(9), Tue(11))]);

        passes[0].BilledHours.Should().Be(2m);
    }

    /// <summary>
    /// The rounding goes to the category the pass ended in: the work ran into that started
    /// hour, so that is the hour it ran into.
    /// </summary>
    [Fact]
    public void The_rounding_lands_in_the_category_the_pass_ended_in()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([Entry(Tue(16, 30), Tue(17, 30))]);

        WorkPass pass = passes[0];

        pass.WorkedHours.Should().Be(1m);
        pass.BilledHours.Should().Be(1m);
        pass.Parts[0].BilledHours.Should().Be(0.5m);   // ordinary, unrounded
        pass.Parts[1].BilledHours.Should().Be(0.5m);   // evening, also unrounded
    }

    [Fact]
    public void A_rounded_pass_adds_the_remainder_to_the_last_category()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([Entry(Tue(16, 30), Tue(17, 45))]);

        WorkPass pass = passes[0];

        pass.WorkedHours.Should().Be(1.25m);
        pass.BilledHours.Should().Be(2m);
        pass.Parts[0].BilledHours.Should().Be(0.5m);
        pass.Parts[1].BilledHours.Should().Be(1.5m);
    }

    // ---- Timbank factors ---------------------------------------------------------------------

    /// <summary>§13: one hour of weekend work takes 1.5 hours out of the bank.</summary>
    [Fact]
    public void The_bank_is_drawn_at_the_category_factor()
    {
        IReadOnlyList<WorkPass> weekend = WorkPassCalculator.BuildPasses([Entry(Sat(10), Sat(12))]);
        IReadOnlyList<WorkPass> ordinary = WorkPassCalculator.BuildPasses([Entry(Tue(10), Tue(12))]);

        weekend[0].BankHours.Should().Be(3m);
        ordinary[0].BankHours.Should().Be(2m);
    }

    [Fact]
    public void Night_work_draws_double()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([Entry(Tue(23), Swedish(2026, 9, 23, 1))]);

        passes[0].BankHours.Should().Be(4m);
    }

    /// <summary>
    /// §11.1 keeps utvecklingsuppdrag and on-boarding out of the timbank; they are billed
    /// separately.
    /// </summary>
    [Theory]
    [InlineData(WorkKind.Management, true)]
    [InlineData(WorkKind.IncidentAssessment, true)]
    [InlineData(WorkKind.Development, false)]
    [InlineData(WorkKind.Onboarding, false)]
    [InlineData(WorkKind.Travel, false)]
    public void Only_förvaltning_draws_on_the_timbank(WorkKind kind, bool draws) =>
        WorkPassCalculator.DrawsOnTimebank(kind).Should().Be(draws);

    // ---- Utryckning ----------------------------------------------------------------------------

    /// <summary>
    /// §13 bills an utryckning at a minimum of two hours per occasion, at the callout rate,
    /// however short the work was.
    /// </summary>
    [Fact]
    public void An_utryckning_bills_at_least_two_hours()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses(
            [Entry(Sat(23, 0), Sat(23, 20))], calloutTickets: new HashSet<Guid> { TicketA });

        WorkPass pass = passes[0];

        pass.WorkedHours.Should().BeApproximately(0.333m, 0.01m);
        pass.BilledHours.Should().Be(2m);
        pass.Parts[0].Category.Should().Be(SupportTimeCategory.Callout);
        pass.BankHours.Should().Be(4m);
    }

    [Fact]
    public void A_callout_overrides_the_clock_derived_category()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses(
            [Entry(Sat(10), Sat(13))], calloutTickets: new HashSet<Guid> { TicketA });

        passes[0].Parts.Should().ContainSingle()
            .Which.Category.Should().Be(SupportTimeCategory.Callout);
    }

    // ---- The free half hour (§10.3) ---------------------------------------------------------------

    /// <summary>
    /// §10.3 includes the first thirty minutes of assessing an incident in the grundavgift.
    /// Twenty minutes of assessment is therefore free, and bills nothing at all.
    /// </summary>
    [Fact]
    public void The_first_half_hour_of_an_assessment_is_free()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses(
            [Entry(Tue(9, 0), Tue(9, 20), kind: WorkKind.IncidentAssessment)]);

        WorkPass pass = passes[0];

        pass.FreeHours.Should().BeApproximately(0.333m, 0.01m);
        pass.BilledHours.Should().Be(0m);
    }

    [Fact]
    public void Assessment_beyond_the_free_half_hour_is_billed()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses(
            [Entry(Tue(9, 0), Tue(10, 0), kind: WorkKind.IncidentAssessment)]);

        WorkPass pass = passes[0];

        pass.FreeHours.Should().Be(0.5m);
        pass.WorkedHours.Should().Be(0.5m);
        pass.BilledHours.Should().Be(1m);
    }

    /// <summary>The allowance is per incident, so a second ticket gets its own.</summary>
    [Fact]
    public void Each_incident_gets_its_own_free_half_hour()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([
            Entry(Tue(9, 0), Tue(9, 20), TicketA, WorkKind.IncidentAssessment),
            Entry(Tue(11, 0), Tue(11, 20), TicketB, WorkKind.IncidentAssessment),
        ]);

        passes.Should().HaveCount(2);
        passes.Sum(p => p.BilledHours).Should().Be(0m);
    }

    /// <summary>
    /// The allowance is spent once, not once per visit — otherwise a ticket touched six
    /// times would get three free hours.
    /// </summary>
    [Fact]
    public void The_free_half_hour_is_not_renewed_by_coming_back()
    {
        IReadOnlyList<WorkPass> passes = WorkPassCalculator.BuildPasses([
            Entry(Tue(9, 0), Tue(9, 20), TicketA, WorkKind.IncidentAssessment),
            Entry(Tue(14, 0), Tue(14, 20), TicketA, WorkKind.IncidentAssessment),
        ]);

        // Twenty minutes free, then ten of the second visit free, leaving ten billable —
        // rounded to the started hour.
        passes.Sum(p => p.FreeHours).Should().Be(0.5m);
        passes.Sum(p => p.BilledHours).Should().Be(1m);
    }

    // ---- Edges ------------------------------------------------------------------------------------

    [Fact]
    public void An_entry_that_ends_before_it_starts_is_ignored() =>
        WorkPassCalculator.BuildPasses([Entry(Tue(11), Tue(9))]).Should().BeEmpty();

    [Fact]
    public void No_entries_means_no_passes() =>
        WorkPassCalculator.BuildPasses([]).Should().BeEmpty();
}
