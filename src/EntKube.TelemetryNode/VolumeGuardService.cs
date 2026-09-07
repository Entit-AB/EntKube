using EntKube.Telemetry;

namespace EntKube.TelemetryNode;

/// <summary>
/// Keeps the indexer's PersistentVolume from filling up.
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
public sealed class VolumeGuardService(
    IReadOnlyList<ISegmentManagerRegistry> registries,
    ISegmentBlobStore blobs,
    SegmentEngineOptions options,
    ILogger<VolumeGuardService> logger) : BackgroundService
{
    /// <summary>
    /// Often enough to catch a fill before it lands, rarely enough to be free. Sealing runs every 30s and
    /// retention hourly; a volume does not go from comfortable to full inside two minutes unless something
    /// is very wrong, and if it does, the next tick still beats the disk.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether the sealed archives sit on the volume being measured. When they are in object storage,
    /// deleting them reclaims nothing here — so dropping data would be pure loss with no benefit, and the
    /// guard must not do it however full the disk gets.
    /// </summary>
    private bool ArchivesAreLocal => blobs is LocalSegmentBlobStore;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunAsync(stoppingToken);
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

    private async Task RunAsync(CancellationToken ct)
    {
        VolumeState state = Measure();

        VolumeAction action = VolumeBudget.Decide(
            state, options.VolumeHighWaterPercent, CanDropOldest, warmTierIsExhausted: false);

        if (action == VolumeAction.None) return;

        logger.LogWarning(
            "Telemetry volume at {Used:F1}% ({Free} bytes free of {Total}); reclaiming down to {Target}%.",
            state.UsedPercent, state.FreeBytes, state.TotalBytes, options.VolumeTargetPercent);

        // Step one: give up local copies. Costs a download on a later query and nothing else.
        int evicted = 0;
        foreach (SegmentManagerBase manager in AllManagers())
        {
            ct.ThrowIfCancellationRequested();
            evicted += await manager.TrimWarmTierAsync(ct);
        }

        state = Measure();
        if (VolumeBudget.Decide(state, options.VolumeHighWaterPercent, CanDropOldest, warmTierIsExhausted: true)
            != VolumeAction.DropOldest)
        {
            if (state.UsedPercent < options.VolumeHighWaterPercent)
            {
                logger.LogInformation(
                    "Evicting {Count} warm segment(s) brought the volume to {Used:F1}%.",
                    evicted, state.UsedPercent);
            }
            else if (!ArchivesAreLocal)
            {
                // Nothing here is reclaimable: the archives are in the bucket, the warm tier is already
                // empty, and what is left is the active index and staging. Saying which is the difference
                // between an operator enlarging the right thing and guessing.
                logger.LogError(
                    "Telemetry volume still at {Used:F1}% after evicting the warm tier, and the sealed "
                    + "archives are in object storage — so the space is held by the active index or the "
                    + "staging directory, not by anything this can reclaim. Enlarge the volume, or lower "
                    + "the segment roll thresholds so the active index is sealed sooner.",
                    state.UsedPercent);
            }
            else
            {
                logger.LogError(
                    "Telemetry volume still at {Used:F1}% after evicting the warm tier, and dropping the "
                    + "oldest segments is disabled. Ingest will fail when the volume is full. Configure "
                    + "object storage, enlarge the volume, or shorten retention.",
                    state.UsedPercent);
            }

            return;
        }

        // Step two: the archives are here, and there is nothing else left to give.
        long target = VolumeBudget.BytesToReclaim(state, options.VolumeTargetPercent);
        long freed = 0;
        int dropped = 0;

        // Spread the reclaim across signals rather than draining one: taking it all from whichever is
        // enumerated first would delete a whole tier's history while another kept its full window, and
        // which tier that was would depend on registration order.
        List<SegmentManagerBase> managers = AllManagers();
        long share = Math.Max(1, target / Math.Max(1, managers.Count));

        foreach (SegmentManagerBase manager in managers)
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

    private bool CanDropOldest => options.DropOldestWhenVolumeFull && ArchivesAreLocal;

    private List<SegmentManagerBase> AllManagers() =>
        [.. registries.SelectMany(r => r.ActiveManagers)];

    /// <summary>
    /// Measures the filesystem holding the data path.
    ///
    /// A failure here yields a zero total, which <see cref="VolumeBudget"/> reads as "unknown" and acts on
    /// by doing nothing. Guessing in either direction would be worse: a false full deletes data that did
    /// not need deleting, and a false empty is the state this whole class exists to prevent.
    /// </summary>
    private VolumeState Measure()
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
