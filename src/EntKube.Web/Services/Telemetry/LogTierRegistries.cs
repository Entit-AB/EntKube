using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Bundles the two per-tenant log segment registries — the important (<c>logs</c>, WARN+) tier and the
/// verbose (<c>logs_debug</c>, DEBUG/INFO) tier — and the policy for routing writes and queries between them.
/// Ingest splits a batch by severity so each tier's segments stay single-tier and can expire on their own
/// retention window; queries union the tiers, skipping the verbose one when the request's minimum level is
/// WARN or above (that tier can only hold sub-WARN rows). When tiering is off (or no verbose registry is
/// wired) everything lives in the important tier — the original single-stream behaviour.
/// </summary>
public sealed class LogTierRegistries(
    SegmentManagerRegistry<LogSegmentManager> important,
    SegmentManagerRegistry<VerboseLogSegmentManager>? verbose,
    SegmentEngineOptions options)
{
    /// <summary>The severity at/above which a log is "important" and goes to the long-retention tier.</summary>
    public const LogLevel ImportantFrom = LogLevel.Warn;

    /// <summary>True when log writes/queries are split across the two retention tiers.</summary>
    public bool Tiered => verbose is not null && options.TieredLogRetention;

    public SegmentManagerRegistry<LogSegmentManager> Important => important;
    public SegmentManagerRegistry<VerboseLogSegmentManager> Verbose =>
        verbose ?? throw new InvalidOperationException("Verbose log tier is not configured.");

    /// <summary>The manager(s) a query must search for the given minimum severity. The verbose tier is
    /// included only when tiering is on AND the query can match sub-WARN rows.</summary>
    public IReadOnlyList<LogSegmentManager> QueryManagers(Guid tenantId, LogLevel minLevel = LogLevel.None)
    {
        LogSegmentManager imp = important.For(tenantId);
        if (!Tiered || minLevel >= ImportantFrom) return [imp];
        return [imp, verbose!.For(tenantId)];
    }

    /// <summary>Splits a batch into (important, verbose) by severity. When tiering is off the verbose list is
    /// empty and everything is important — the caller then writes only the important tier.</summary>
    public (IReadOnlyList<LogIngestRecord> Important, IReadOnlyList<LogIngestRecord> Verbose) Split(
        IReadOnlyList<LogIngestRecord> records)
    {
        if (!Tiered) return (records, []);
        var imp = new List<LogIngestRecord>();
        var verb = new List<LogIngestRecord>();
        foreach (LogIngestRecord r in records)
            (r.Severity >= (short)ImportantFrom ? imp : verb).Add(r);
        return (imp, verb);
    }
}
