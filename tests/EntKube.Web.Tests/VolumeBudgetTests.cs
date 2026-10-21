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
        VolumeBudget.Decide(At(50), highWaterPercent: 85, canDropOldest: true, warmTierIsExhausted: false, canSealActive: false)
            .Should().Be(VolumeAction.None);
    }

    [Fact]
    public void Past_the_high_water_mark_the_warm_tier_goes_first()
    {
        // Evicting a warm copy costs a download on a later query and loses nothing, so it is always the
        // first thing tried and never needs anyone's permission.
        VolumeBudget.Decide(At(90), highWaterPercent: 85, canDropOldest: true, warmTierIsExhausted: false, canSealActive: false)
            .Should().Be(VolumeAction.TrimWarmTier);
    }

    [Fact]
    public void Only_once_nothing_is_left_to_evict_is_data_dropped()
    {
        VolumeBudget.Decide(At(90), highWaterPercent: 85, canDropOldest: true, warmTierIsExhausted: true, canSealActive: false)
            .Should().Be(VolumeAction.DropOldest);
    }

    [Fact]
    public void A_full_volume_drops_nothing_when_dropping_is_not_allowed()
    {
        // Either the operator forbade it, or the archives are in object storage — where deleting them
        // frees nothing on this disk, so it would be loss with no benefit whatsoever.
        VolumeBudget.Decide(At(99), highWaterPercent: 85, canDropOldest: false, warmTierIsExhausted: true, canSealActive: false)
            .Should().Be(VolumeAction.None);
    }

    [Fact]
    public void An_unmeasurable_volume_is_not_treated_as_a_full_one()
    {
        // A failed measurement reports zero bytes. Reading that as 100% used would delete data because a
        // stat call failed, which is a far worse mistake than doing nothing for one cycle.
        VolumeBudget.Decide(new VolumeState(0, 0), 85, canDropOldest: true, warmTierIsExhausted: true, canSealActive: false)
            .Should().Be(VolumeAction.None);
    }

    // ── Sealing under pressure ──

    [Fact]
    public void Unsealed_data_is_moved_to_object_storage_before_anything_is_deleted()
    {
        // The watermark's whole purpose: "offload the oldest records to the assigned S3 storage". An
        // active index is uncompressed Lucene on this disk; sealing it puts the bytes in the bucket and
        // leaves an evictable copy behind. Dropping ahead of that would delete telemetry while a remedy
        // that loses nothing was still available.
        VolumeBudget.Decide(At(90), highWaterPercent: 85, canDropOldest: true,
                warmTierIsExhausted: true, canSealActive: true)
            .Should().Be(VolumeAction.SealActive);
    }

    [Fact]
    public void Evicting_still_comes_before_sealing()
    {
        // Eviction is free and instant; sealing compresses and uploads. Order by cost, so a volume that
        // a trim alone would rescue never pays for a zstd pass.
        VolumeBudget.Decide(At(90), highWaterPercent: 85, canDropOldest: true,
                warmTierIsExhausted: false, canSealActive: true)
            .Should().Be(VolumeAction.TrimWarmTier);
    }

    [Fact]
    public void With_nothing_left_to_seal_the_last_resort_is_reached()
    {
        // The failure this guards against is a guard that never stops choosing the free remedy: if
        // "seal" were returned on an idle store, the disk would stay full while the log said it was
        // working. Sealing is only offered while there is something to seal.
        VolumeBudget.Decide(At(90), highWaterPercent: 85, canDropOldest: true,
                warmTierIsExhausted: true, canSealActive: false)
            .Should().Be(VolumeAction.DropOldest);
    }

    [Fact]
    public void Sealing_is_offered_even_when_dropping_is_forbidden()
    {
        // These are independent permissions. Dropping is off whenever the archives are in a bucket —
        // which is exactly the case where sealing into that bucket is the most useful thing to do.
        VolumeBudget.Decide(At(95), highWaterPercent: 85, canDropOldest: false,
                warmTierIsExhausted: true, canSealActive: true)
            .Should().Be(VolumeAction.SealActive);
    }

    [Fact]
    public void A_comfortable_volume_seals_nothing_early()
    {
        // Sealing under the mark would pre-empt the roll thresholds, turning the size/age policy into
        // "whenever the guard happens to look" and filling object storage with small segments.
        VolumeBudget.Decide(At(50), highWaterPercent: 70, canDropOldest: true,
                warmTierIsExhausted: true, canSealActive: true)
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
        VolumeBudget.Decide(At(20), highWaterPercent: 0, canDropOldest: true, warmTierIsExhausted: false, canSealActive: false)
            .Should().Be(VolumeAction.None);

        // The floor is not "never", either — past it the guard still does the free thing.
        VolumeBudget.Decide(At(60), highWaterPercent: 0, canDropOldest: true, warmTierIsExhausted: false, canSealActive: false)
            .Should().Be(VolumeAction.TrimWarmTier);
    }

    [Fact]
    public void A_threshold_above_a_hundred_does_not_mean_never_reclaim()
    {
        // The opposite mistake, and the more dangerous one: it would silently disable the guard entirely
        // and reintroduce exactly the failure it exists to prevent.
        VolumeBudget.Decide(At(99.5), highWaterPercent: 1000, canDropOldest: true, warmTierIsExhausted: false, canSealActive: false)
            .Should().Be(VolumeAction.TrimWarmTier);
    }
}
