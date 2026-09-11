using EntKube.Telemetry;

namespace EntKube.TelemetryNode;

/// <summary>
/// Keeps the cluster's telemetry bucket inside a size the operator chose.
///
/// <para>Retention bounds telemetry in TIME. That bounds the bucket only if you already know the cluster's
/// log rate, and nothing tells you when it changes — a chatty deploy, a crash loop printing a stack trace
/// per request, a debug level left on over a weekend. Every other ceiling in the engine measures the local
/// volume, and object storage is not the local volume: it just accepts what it is given. So the bucket had
/// no bound at all, and the first signal was the bill.</para>
///
/// <para><see cref="VolumeGuardService"/> is the same idea for the disk, and this deliberately mirrors it:
/// measure the resource, act before it becomes someone else's problem, and shed the OLDEST data because
/// past the ceiling the choice is not "keep or drop" but "drop the far end of the window, or keep paying
/// for it". Nothing is dropped while the bucket is inside its budget, and nothing at all when no budget is
/// set — the default is unbounded, so this changes nothing for an existing install until it is configured.</para>
///
/// <para>Runs on the indexer only. The querier reads the same bucket and must never delete from it.</para>
/// </summary>
public sealed class ObjectStorageBudgetService(
    IReadOnlyList<ISegmentManagerRegistry> registries,
    ISegmentBlobStore blobs,
    SegmentEngineOptions options,
    ILogger<ObjectStorageBudgetService> logger) : BackgroundService
{
    /// <summary>
    /// Slower than the volume guard's two minutes: a bucket cannot fail the way a disk can — an over-budget
    /// bucket costs money, a full disk stops ingest — so this trades reaction time for staying cheap.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.ObjectStorageMaxBytes <= 0) return;   // unbounded: nothing to enforce

        if (blobs is LocalSegmentBlobStore)
        {
            // The archives are on the volume, which the volume guard already measures against a real
            // free-space figure. Running both would have two policies deleting the same segments on
            // different triggers, and the disk's is the one that must win.
            logger.LogInformation(
                "An object-storage budget is configured but this node has no bucket — sealed archives are "
                + "on the volume, where the volume guard bounds them. The budget is not enforced.");
            return;
        }

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
                    logger.LogError(ex, "Object-storage budget cycle failed; will retry next tick.");
                }
            }
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        long budget = options.ObjectStorageMaxBytes;

        // One catalog read per (tenant, signal). The sizes were recorded at seal time, so measuring the
        // bucket never lists it — which matters, because listing a bucket with a hundred thousand objects
        // in it is itself a cost.
        List<(SegmentManagerBase Manager, long Bytes)> usage = [];
        long total = 0;
        foreach (SegmentManagerBase manager in AllManagers())
        {
            long bytes = await manager.SealedBytesAsync(ct);
            if (bytes > 0) usage.Add((manager, bytes));
            total += bytes;
        }

        // The policy itself lives in ObjectStorageBudget, next to VolumeBudget and testable the same way:
        // each signal sheds its share of the overage, oldest first.
        IReadOnlyList<long> plan = ObjectStorageBudget.Plan(
            [.. usage.Select(u => u.Bytes)], budget, options.ObjectStorageTargetPercent);
        if (plan.Count == 0) return;

        logger.LogWarning(
            "Telemetry object storage holds {Total} bytes against a {Budget} byte budget; dropping the "
            + "oldest sealed segments to get back under it.", total, budget);

        int dropped = 0;
        long freed = 0;
        for (int i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (plan[i] <= 0) continue;

            (int count, long bytesFreed) = await usage[i].Manager.DropOldestAsync(plan[i], ct);
            dropped += count;
            freed += bytesFreed;
        }

        logger.LogWarning(
            "Object-storage budget: dropped {Count} sealed segment(s), {Freed} bytes. Raise the budget, "
            + "shorten retention so less is written, or lower the log volume reaching the indexer.",
            dropped, freed);
    }

    private IEnumerable<SegmentManagerBase> AllManagers() =>
        registries.SelectMany(r => r.ActiveManagers);
}
