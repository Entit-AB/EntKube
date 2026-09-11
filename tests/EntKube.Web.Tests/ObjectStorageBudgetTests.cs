using EntKube.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The bound on the BUCKET. Retention bounds telemetry in time, which bounds object storage only if you
/// already know a cluster's log rate — so a busy month was simply a larger invoice, discovered afterwards.
/// These pin the policy: nothing is dropped while the bucket is inside its budget, nothing at all without
/// a budget, and past the ceiling every signal sheds its share rather than one being drained first.
/// </summary>
public class ObjectStorageBudgetTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void No_budget_means_nothing_is_ever_dropped()
    {
        // The default, and it must stay the default: turning this on for an existing install without
        // being asked would delete telemetry somebody is still paying to keep.
        ObjectStorageBudget.Plan([100 * Gib, 400 * Gib], budgetBytes: 0, targetPercent: 90)
            .Should().BeEmpty();
    }

    [Fact]
    public void Inside_the_budget_nothing_is_dropped()
    {
        ObjectStorageBudget.Plan([10 * Gib, 20 * Gib], budgetBytes: 50 * Gib, targetPercent: 90)
            .Should().BeEmpty();
    }

    [Fact]
    public void Over_the_budget_it_reclaims_down_to_the_target_not_to_the_ceiling()
    {
        // Reclaiming back to exactly the trigger fires again on the next pass and takes a little more
        // each time; the target is below the ceiling so one pass settles it.
        IReadOnlyList<long> plan = ObjectStorageBudget.Plan(
            [60 * Gib, 40 * Gib], budgetBytes: 50 * Gib, targetPercent: 90);

        plan.Sum().Should().BeCloseTo(100 * Gib - 45 * Gib, (ulong)(1 * Gib));
    }

    [Fact]
    public void Every_signal_sheds_in_proportion_to_what_it_holds()
    {
        // 80/20 split of the data means an 80/20 split of the loss. The alternative — draining the
        // first signal iterated — would trade all of a cluster's log history for span waterfalls, or
        // the reverse, decided by nothing but ordering.
        IReadOnlyList<long> plan = ObjectStorageBudget.Plan(
            [80 * Gib, 20 * Gib], budgetBytes: 50 * Gib, targetPercent: 90);

        plan.Should().HaveCount(2);
        ((double)plan[0] / plan[1]).Should().BeApproximately(4d, 0.01);
    }

    [Fact]
    public void A_signal_holding_nothing_is_asked_for_nothing()
    {
        IReadOnlyList<long> plan = ObjectStorageBudget.Plan(
            [100 * Gib, 0], budgetBytes: 50 * Gib, targetPercent: 90);

        plan[1].Should().Be(0);
        plan[0].Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_target_can_never_exceed_the_ceiling_through_rounding()
    {
        // targetPercent is clamped to 10..99, so a plan always frees something once over budget —
        // a target at or above 100% would compute a zero (or negative) overage and never converge.
        ObjectStorageBudget.Plan([100 * Gib], budgetBytes: 50 * Gib, targetPercent: 300)
            .Single().Should().BeGreaterThan(0);
    }
}
