using EntKube.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the bound on the telemetry indexer's VOLUME — as opposed to the bounds on each of its
/// consumers, all of which were already enforced while the disk filled up anyway.
///
/// The property these defend is the order of the remedies. Evicting local copies is free and must be
/// tried first; deleting sealed segments destroys telemetry and is only ever right when the alternative
/// is a full volume, which fails writes, makes the collector upstream buffer until it is OOM-killed, and
/// loses the newest data instead of the oldest.
/// </summary>
public class VolumeBudgetTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static VolumeState At(double usedPercent, long total = 20 * Gb) =>
        new(total, total - (long)(total * usedPercent / 100d));

    // ── When to act ──

    [Fact]
    public void A_comfortable_volume_is_left_alone()
    {
        VolumeBudget.Decide(At(50), highWaterPercent: 85, canDropOldest: true, warmTierIsExhausted: false)
            .Should().Be(VolumeAction.None);
    }

    [Fact]
    public void Past_the_high_water_mark_the_warm_tier_goes_first()
    {
        // Evicting a warm copy costs a download on a later query and loses nothing, so it is always the
        // first thing tried and never needs anyone's permission.
        VolumeBudget.Decide(At(90), highWaterPercent: 85, canDropOldest: true, warmTierIsExhausted: false)
            .Should().Be(VolumeAction.TrimWarmTier);
    }

    [Fact]
    public void Only_once_nothing_is_left_to_evict_is_data_dropped()
    {
        VolumeBudget.Decide(At(90), highWaterPercent: 85, canDropOldest: true, warmTierIsExhausted: true)
            .Should().Be(VolumeAction.DropOldest);
    }

    [Fact]
    public void A_full_volume_drops_nothing_when_dropping_is_not_allowed()
    {
        // Either the operator forbade it, or the archives are in object storage — where deleting them
        // frees nothing on this disk, so it would be loss with no benefit whatsoever.
        VolumeBudget.Decide(At(99), highWaterPercent: 85, canDropOldest: false, warmTierIsExhausted: true)
            .Should().Be(VolumeAction.None);
    }

    [Fact]
    public void An_unmeasurable_volume_is_not_treated_as_a_full_one()
    {
        // A failed measurement reports zero bytes. Reading that as 100% used would delete data because a
        // stat call failed, which is a far worse mistake than doing nothing for one cycle.
        VolumeBudget.Decide(new VolumeState(0, 0), 85, canDropOldest: true, warmTierIsExhausted: true)
            .Should().Be(VolumeAction.None);
    }

    // ── How much to reclaim ──

    [Fact]
    public void Reclaiming_aims_below_the_trigger_not_at_it()
    {
        // 90% of 20 GiB used, target 70% → 4 GiB has to go. Stopping at the high-water mark instead would
        // leave the next cycle over the line again, taking a little more each time, forever.
        long bytes = VolumeBudget.BytesToReclaim(At(90), targetPercent: 70);

        bytes.Should().BeCloseTo(4 * Gb, 64L * 1024 * 1024);
    }

    [Fact]
    public void Nothing_is_reclaimed_when_already_under_the_target()
    {
        VolumeBudget.BytesToReclaim(At(60), targetPercent: 70).Should().Be(0);
    }

    [Fact]
    public void An_unmeasurable_volume_reclaims_nothing()
    {
        VolumeBudget.BytesToReclaim(new VolumeState(0, 0), 70).Should().Be(0);
    }

    // ── Thresholds that were typed wrong ──

    [Fact]
    public void A_zero_threshold_does_not_mean_reclaim_constantly()
    {
        // A mistyped 0 read literally would put an indexer into a permanent reclaim loop on an empty
        // disk. Clamped to a floor instead, so a volume with plenty of room is still left alone.
        VolumeBudget.Decide(At(20), highWaterPercent: 0, canDropOldest: true, warmTierIsExhausted: false)
            .Should().Be(VolumeAction.None);

        // The floor is not "never", either — past it the guard still does the free thing.
        VolumeBudget.Decide(At(60), highWaterPercent: 0, canDropOldest: true, warmTierIsExhausted: false)
            .Should().Be(VolumeAction.TrimWarmTier);
    }

    [Fact]
    public void A_threshold_above_a_hundred_does_not_mean_never_reclaim()
    {
        // The opposite mistake, and the more dangerous one: it would silently disable the guard entirely
        // and reintroduce exactly the failure it exists to prevent.
        VolumeBudget.Decide(At(99.5), highWaterPercent: 1000, canDropOldest: true, warmTierIsExhausted: false)
            .Should().Be(VolumeAction.TrimWarmTier);
    }
}
