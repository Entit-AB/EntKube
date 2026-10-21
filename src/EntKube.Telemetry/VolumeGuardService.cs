namespace EntKube.Telemetry;

/// <summary>
/// Keeps the volume holding the segment engine's data from filling up — the indexer's PersistentVolume
/// in a cluster, and the management plane's own disk now that telemetry is held centrally again
/// (docs/telemetry-central-again.md). It lives beside the engine rather than in either host because
/// the question it answers — "how full is the disk these segments are on?" — is the same in both.
///
/// <para>Everything else in the engine bounds one <em>part</em> of the volume: the active index is bounded
/// by the roll thresholds, the warm tier by its day window and byte ceiling, retention by its day window.
/// Nothing measured the volume itself — so a node whose sealed archives live on that same volume (which is
/// what happens when no object storage is configured) had no bound at all on its largest consumer. It kept
/// the full retention window of compressed archives next to the warm tier, on a disk sized for the warm
/// tier, and filled up.</para>
///
/// <para>A full volume is the worst failure this component has. Writes fail, the ingest endpoint answers
/// 500, and the collector upstream — which is configured to retry 5xx and buffer while it does — grows
/// until the kubelet OOM-kills it. Two alerts, one cause, and the data lost is the newest.</para>
///
/// <para>So this measures the disk and acts before that happens, in the order that costs least:
/// evict local copies of sealed segments (free — the archives are elsewhere), and only if the archives are
/// on this very volume and there is nothing left to evict, drop the oldest sealed segments. See
/// <see cref="VolumeBudget"/> for why that is the right last resort rather than stalling.</para>
/// </summary>
/// <param name="measureVolume">
/// How to measure the disk, for tests. Filling a real volume to its high-water mark is the one thing a
/// test cannot do, and it is also the only part of this class that touches the machine — so it is the
/// seam, and everything that decides and acts on the measurement is exercised for real through it.
/// Null in production, where <see cref="MeasureDrive"/> stats the filesystem holding the data path.
/// </param>
public sealed class VolumeGuardService(
    IReadOnlyList<ISegmentManagerRegistry> registries,
    SegmentEngineOptions options,
    ILogger<VolumeGuardService> logger,
    Func<VolumeState>? measureVolume = null) : BackgroundService
{
    /// <summary>
    /// Often enough to catch a fill before it lands, rarely enough to be free. Sealing runs every 30s and
    /// retention hourly; a volume does not go from comfortable to full inside two minutes unless something
    /// is very wrong, and if it does, the next tick still beats the disk.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ReclaimOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Volume guard cycle failed; will retry next tick.");
                }
            }
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }

    /// <summary>One measure-and-reclaim pass. Public so a test can drive it without a timer.</summary>
    public async Task ReclaimOnceAsync(CancellationToken ct = default)
    {
        VolumeState state = Measure();

        // One snapshot of the live managers for the whole pass. Taking it again between steps would let
        // a tenant appearing mid-pass be sealed but not evicted, or counted in a share it never got.
        List<SegmentManagerBase> managers = AllManagers();
        List<SegmentManagerBase> droppable = DroppableManagers();

        if (VolumeBudget.Decide(state, options.VolumeHighWaterPercent, CanDropOldest(droppable),
                warmTierIsExhausted: false, canSealActive: AnySealable(managers)) == VolumeAction.None)
        {
            return;
        }

        logger.LogWarning(
            "Telemetry volume at {Used:F1}% ({Free} bytes free of {Total}); reclaiming down to {Target}%.",
            state.UsedPercent, state.FreeBytes, state.TotalBytes, options.VolumeTargetPercent);

        // Step one: give up local copies. Costs a download on a later query and nothing else.
        int evicted = 0;
        foreach (SegmentManagerBase manager in managers)
        {
            ct.ThrowIfCancellationRequested();
            evicted += await manager.TrimWarmTierAsync(ct);
        }

        // Step two: seal what has not been sealed yet, which is the whole point of the watermark —
        // "offload the oldest records to the assigned object storage as the disk fills". An active index
        // is uncompressed Lucene; sealing turns it into a zstd archive in the bucket, and the copy it
        // leaves behind is then evictable like any other sealed segment, so the trim below is what
        // actually frees the space. Nothing is lost: this only brings forward work the roll thresholds
        // would have done on their own schedule.
        state = Measure();
        int sealedCount = 0;

        if (VolumeBudget.Decide(state, options.VolumeHighWaterPercent, CanDropOldest(droppable),
                warmTierIsExhausted: true, canSealActive: AnySealable(managers)) == VolumeAction.SealActive)
        {
            foreach (SegmentManagerBase manager in managers)
            {
                ct.ThrowIfCancellationRequested();
                if (manager.ActiveDocCount <= 0) continue;

                try
                {
                    if (await manager.RollAndSealAsync(ct) is not null) sealedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Packing stages the archive on this same volume before the index goes away, so on a
                    // disk this full a seal can fail for want of the headroom it needs to create room.
                    // Reported and stepped over: the remaining managers may still have space to seal into,
                    // and the drop below is the remedy that needs no headroom at all.
                    logger.LogWarning(ex,
                        "Could not seal {Manager} under disk pressure; continuing with the other signals.",
                        manager.GetType().Name);
                }
            }

            foreach (SegmentManagerBase manager in managers)
            {
                ct.ThrowIfCancellationRequested();
                evicted += await manager.TrimWarmTierAsync(ct);
            }

            state = Measure();
            logger.LogInformation(
                "Sealed {Sealed} active index(es) under disk pressure; volume now at {Used:F1}%.",
                sealedCount, state.UsedPercent);
        }

        // Step three: everything that could be moved has been moved. What is left is deletion.
        //
        // canSealActive is false here deliberately rather than re-measured: this pass has already sealed
        // everything it could, and anything ingested since is next tick's business. Asking again would
        // let a trickle of arriving data stand between a full disk and the only remedy left.
        if (VolumeBudget.Decide(state, options.VolumeHighWaterPercent, CanDropOldest(droppable),
                warmTierIsExhausted: true, canSealActive: false) != VolumeAction.DropOldest)
        {
            if (state.UsedPercent < options.VolumeHighWaterPercent)
            {
                logger.LogInformation(
                    "Reclaiming brought the volume to {Used:F1}% ({Evicted} warm segment(s) evicted, "
                    + "{Sealed} index(es) sealed).",
                    state.UsedPercent, evicted, sealedCount);
            }
            else if (droppable.Count == 0)
            {
                // Nothing here is reclaimable: the archives are in the bucket, the warm tier is already
                // empty, everything sealable is sealed, and what is left is the active index and staging.
                // Saying which is the difference between an operator enlarging the right thing and guessing.
                logger.LogError(
                    "Telemetry volume still at {Used:F1}% after evicting the warm tier and sealing every "
                    + "active index, and the sealed archives are in object storage — so the space is held "
                    + "by what has arrived since, or by the staging directory, not by anything this can "
                    + "reclaim. Enlarge the volume, or lower the segment roll thresholds.",
                    state.UsedPercent);
            }
            else
            {
                logger.LogError(
                    "Telemetry volume still at {Used:F1}% after evicting the warm tier and sealing, and "
                    + "dropping the oldest segments is disabled. Ingest will fail when the volume is full. "
                    + "Configure object storage, enlarge the volume, or shorten retention.",
                    state.UsedPercent);
            }

            return;
        }

        long target = VolumeBudget.BytesToReclaim(state, options.VolumeTargetPercent);
        long freed = 0;
        int dropped = 0;

        // Spread the reclaim across signals rather than draining one: taking it all from whichever is
        // enumerated first would delete a whole tier's history while another kept its full window, and
        // which tier that was would depend on registration order.
        //
        // Only across the DROPPABLE ones: deleting a segment whose archive lives in a bucket frees
        // nothing on this disk, so including it in the share would spend a signal's history on no relief.
        long share = Math.Max(1, target / Math.Max(1, droppable.Count));

        foreach (SegmentManagerBase manager in droppable)
        {
            if (freed >= target) break;
            ct.ThrowIfCancellationRequested();

            (int count, long bytes) = await manager.DropOldestAsync(share, ct);
            dropped += count;
            freed += bytes;
        }

        state = Measure();
        logger.LogWarning(
            "Reclaimed {Freed} bytes by dropping {Dropped} of the oldest segment(s); volume now at {Used:F1}%.",
            freed, dropped, state.UsedPercent);
    }

    /// <summary>Whether anything is waiting in an active index, so that sealing would move real bytes.</summary>
    private static bool AnySealable(List<SegmentManagerBase> managers) =>
        managers.Any(m => m.ActiveDocCount > 0);

    /// <summary>
    /// Managers whose archives share this volume, so deleting one of their segments actually frees
    /// space here. Asked per manager because the management plane resolves a blob store per tenant —
    /// one tenant's archives can be in a bucket while the next tenant's are on this disk.
    /// </summary>
    private List<SegmentManagerBase> DroppableManagers() =>
        [.. AllManagers().Where(m => m.ArchivesAreLocal)];

    private bool CanDropOldest(List<SegmentManagerBase> droppable) =>
        options.DropOldestWhenVolumeFull && droppable.Count > 0;

    private List<SegmentManagerBase> AllManagers() =>
        [.. registries.SelectMany(r => r.ActiveManagers)];

    /// <summary>
    /// Measures the filesystem holding the data path.
    ///
    /// A failure here yields a zero total, which <see cref="VolumeBudget"/> reads as "unknown" and acts on
    /// by doing nothing. Guessing in either direction would be worse: a false full deletes data that did
    /// not need deleting, and a false empty is the state this whole class exists to prevent.
    /// </summary>
    private VolumeState Measure() => measureVolume?.Invoke() ?? MeasureDrive();

    private VolumeState MeasureDrive()
    {
        try
        {
            // The data path itself, NOT its path root. On Linux the root of /data/telemetry is "/", which
            // is the container's own filesystem — measuring that would report the image's free space
            // while the mounted PersistentVolume, the only disk that matters here, filled up unwatched.
            // DriveInfo resolves a path to the filesystem containing it, which is exactly the question.
            var drive = new DriveInfo(Path.GetFullPath(options.DataPath));
            return new VolumeState(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not measure the telemetry volume at {Path}", options.DataPath);
            return new VolumeState(0, 0);
        }
    }
}
