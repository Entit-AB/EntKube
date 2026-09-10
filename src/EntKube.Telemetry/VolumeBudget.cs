namespace EntKube.Telemetry;

/// <summary>What the volume guard should do about the disk it just measured.</summary>
public enum VolumeAction
{
    /// <summary>Enough room. Nothing to do.</summary>
    None,

    /// <summary>Over the high-water mark: evict local copies of sealed segments, which loses nothing.</summary>
    TrimWarmTier,

    /// <summary>
    /// Still over after evicting everything evictable, and the archives are on this same volume —
    /// so the only remaining lever is to drop the oldest sealed data.
    /// </summary>
    DropOldest,
}

/// <summary>How full the volume is, and what that means.</summary>
/// <param name="TotalBytes">Size of the volume.</param>
/// <param name="FreeBytes">Bytes available.</param>
public readonly record struct VolumeState(long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0d;

    public double UsedPercent => UsedFraction * 100d;
}

/// <summary>
/// Decides what to do as the indexer's volume fills.
///
/// <para><b>The rule this exists to enforce: when the volume cannot hold the retention window, drop the
/// OLDEST data rather than stall on the newest.</b> A full volume is not a graceful degradation. Writes
/// start failing, the ingest endpoint answers 500, the collector upstream retries and buffers — so it
/// grows until the kubelet OOM-kills it — and what is lost is the telemetry from exactly the period
/// somebody is trying to watch. Ageing out last month's logs to keep recording this minute's is the
/// trade every log store makes, and making it deliberately is far better than arriving at it by
/// filling up.</para>
///
/// <para>The order matters. Evicting the warm tier is free — those are local copies of segments whose
/// archives are durable elsewhere, so a query pays a download and nothing is lost. Only when that is
/// exhausted, and the archives themselves are on this volume (no object storage configured), is there
/// anything left to reclaim, and reclaiming it destroys data. So it is the last resort, it is loud, and
/// it can be switched off by an operator who would genuinely rather the indexer stop.</para>
///
/// <para>Kept pure so the thresholds can be exercised without filling a disk.</para>
/// </summary>
public static class VolumeBudget
{
    /// <summary>
    /// Chooses an action for the measured volume.
    /// </summary>
    /// <param name="state">What the volume looks like now.</param>
    /// <param name="highWaterPercent">Used-percentage at which reclaiming starts.</param>
    /// <param name="canDropOldest">
    /// Whether dropping sealed data is permitted — false when the operator has forbidden it, and
    /// pointless when archives live in object storage rather than on this volume.
    /// </param>
    /// <param name="warmTierIsExhausted">
    /// True when a trim has already run and left nothing further to evict, so trimming again would be a
    /// no-op and the guard would spin doing nothing while the disk stayed full.
    /// </param>
    public static VolumeAction Decide(
        VolumeState state, int highWaterPercent, bool canDropOldest, bool warmTierIsExhausted)
    {
        // An unmeasurable volume is not a full one. Reporting a total of zero as 0% used would be just as
        // wrong as reporting it as 100% — the honest answer is to do nothing and leave the logs to say so.
        if (state.TotalBytes <= 0) return VolumeAction.None;

        if (state.UsedPercent < Clamp(highWaterPercent)) return VolumeAction.None;

        if (!warmTierIsExhausted) return VolumeAction.TrimWarmTier;

        return canDropOldest ? VolumeAction.DropOldest : VolumeAction.None;
    }

    /// <summary>
    /// How many bytes must go to bring the volume back to <paramref name="targetPercent"/>.
    ///
    /// Reclaiming down to the high-water mark itself would leave the guard triggering again on the very
    /// next tick, so it aims at a lower target and does the work in one pass.
    /// </summary>
    public static long BytesToReclaim(VolumeState state, int targetPercent)
    {
        if (state.TotalBytes <= 0) return 0;

        long allowed = (long)(state.TotalBytes * (Clamp(targetPercent) / 100d));
        return Math.Max(0, state.UsedBytes - allowed);
    }

    /// <summary>
    /// Thresholds are a percentage of a real disk, so nonsense values are clamped rather than trusted:
    /// a mistyped 0 would otherwise mean "reclaim constantly" and a mistyped 1000 "never reclaim", and
    /// both are worse than the value the operator obviously meant.
    /// </summary>
    private static double Clamp(int percent) => Math.Clamp(percent, 50, 99);
}

/// <summary>
/// Decides how much each signal must give up when the telemetry BUCKET is over its size budget.
///
/// <para>Separated from the service that acts on it for the same reason as <see cref="VolumeBudget"/>:
/// the part with the rules in it should be readable and testable without a bucket, a catalog or a clock.</para>
/// </summary>
public static class ObjectStorageBudget
{
    /// <summary>
    /// Bytes each signal should shed, in the same order as <paramref name="bytesPerSignal"/>. Empty when
    /// the total is inside the budget, when no budget is set, or when there is nothing stored.
    ///
    /// <para>The overage is split in proportion to what each signal actually holds. Draining one signal
    /// before touching the next would trade all of a cluster's log history for a week of span waterfalls
    /// — or the reverse — decided by nothing more than iteration order. Proportional shedding keeps every
    /// signal's window the same shape, so what an operator loses is uniformly "the far end", which is the
    /// only kind of loss that can be reasoned about after the fact.</para>
    /// </summary>
    public static IReadOnlyList<long> Plan(
        IReadOnlyList<long> bytesPerSignal, long budgetBytes, int targetPercent)
    {
        if (budgetBytes <= 0 || bytesPerSignal.Count == 0) return [];

        long total = 0;
        foreach (long b in bytesPerSignal) total += Math.Max(0, b);
        if (total <= budgetBytes || total == 0) return [];

        // Integer division first, so the target can never exceed the budget through rounding.
        long target = budgetBytes / 100 * Math.Clamp(targetPercent, 10, 99);
        long excess = total - target;

        long[] plan = new long[bytesPerSignal.Count];
        for (int i = 0; i < bytesPerSignal.Count; i++)
        {
            long held = Math.Max(0, bytesPerSignal[i]);
            plan[i] = held == 0 ? 0 : (long)((double)excess * held / total);
        }
        return plan;
    }
}
