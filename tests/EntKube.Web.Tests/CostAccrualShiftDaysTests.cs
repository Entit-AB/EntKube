using EntKube.Web.Services.Cost;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>CostAccrual.ShiftDays</c>, asserted directly.
///
/// <para>⚠️ <b>Why directly, when it was added to fix three call sites that ARE covered.</b> A
/// mutation made the forward clamp return the FIRST day instead of the last, and the whole suite
/// stayed green. The reason is that no current caller can reach that branch: the only positive
/// shift is the per-day loop's offset, which never exceeds the window it is walking. So half of
/// this helper is defensive code, reachable only by a future caller — and defensive code nothing
/// asserts is how a wrong clamp ships later under a comment claiming it is safe.</para>
///
/// <para>The backward clamp IS reached today (by the handler's default <c>from</c> and by
/// <c>previousStart</c>), and the call-site facts cover it; it is asserted here too so both
/// directions read as one statement rather than one fact and one accident.</para>
/// </summary>
public class CostAccrualShiftDaysTests
{
    [Fact]
    public void An_ordinary_shift_moves_by_that_many_days_in_either_direction()
    {
        DateTime day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        CostAccrual.ShiftDays(day, 5).Should().Be(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));
        CostAccrual.ShiftDays(day, -9).Should().Be(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        CostAccrual.ShiftDays(day, 0).Should().Be(day);
    }

    /// <summary>
    /// ⚠️ The time of day is dropped, as <c>DayOf</c> does — a shift of whole days off a moment
    /// mid-afternoon is a DAY, not a moment 24 hours later. Three callers rely on comparing the
    /// result to ledger rows keyed by day.
    /// </summary>
    [Fact]
    public void The_result_is_a_day_in_utc_not_a_moment()
    {
        DateTime afternoon = new(2026, 3, 10, 15, 42, 7, DateTimeKind.Utc);

        DateTime shifted = CostAccrual.ShiftDays(afternoon, 1);

        shifted.Should().Be(new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc));
        shifted.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    /// ⚠️ <b>The branch no caller reaches, which is exactly why it is here.</b> Clamping forward
    /// must give the LAST representable day — a mutation returning the first instead passed every
    /// other test in the suite.
    /// </summary>
    [Fact]
    public void A_shift_past_the_last_representable_day_clamps_to_it()
    {
        CostAccrual.ShiftDays(CostAccrual.LastDay, 1).Should().Be(
            CostAccrual.LastDay, "and emphatically not the first day");

        CostAccrual.ShiftDays(new DateTime(9999, 12, 1, 0, 0, 0, DateTimeKind.Utc), 400)
            .Should().Be(CostAccrual.LastDay);

        CostAccrual.ShiftDays(CostAccrual.FirstDay, int.MaxValue).Should().Be(CostAccrual.LastDay);
    }

    [Fact]
    public void A_shift_before_the_first_representable_day_clamps_to_it()
    {
        CostAccrual.ShiftDays(CostAccrual.FirstDay, -1).Should().Be(
            CostAccrual.FirstDay, "and emphatically not the last day");

        CostAccrual.ShiftDays(new DateTime(1, 1, 20, 0, 0, 0, DateTimeKind.Utc), -400)
            .Should().Be(CostAccrual.FirstDay);
    }

    /// <summary>
    /// ⚠️ <c>int.MinValue</c> has no positive <c>int</c>, so negating it inside <c>int</c>
    /// arithmetic yields <c>int.MinValue</c> again — a NEGATIVE number, which compares as "less
    /// than the room available" and lets the unguarded <c>AddDays</c> run and throw. The
    /// implementation negates into a <c>long</c> for that reason, and this is the only thing that
    /// says so.
    /// </summary>
    [Fact]
    public void The_most_negative_shift_there_is_clamps_rather_than_throwing()
    {
        Func<DateTime> shifting = () =>
            CostAccrual.ShiftDays(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), int.MinValue);

        shifting.Should().NotThrow().Which.Should().Be(CostAccrual.FirstDay);
    }

    /// <summary>The two bounds are the days, not the moments — <c>DateTime.MaxValue</c> is 23:59:59.</summary>
    [Fact]
    public void The_bounds_are_whole_days()
    {
        CostAccrual.LastDay.Should().Be(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        CostAccrual.FirstDay.Should().Be(new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
