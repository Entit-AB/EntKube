namespace EntKube.Telemetry;

/// <summary>
/// Which tier of the index a query should read. The tiers are physically separable: the hot active index
/// exists only in the process writing it, while sealed segments live in object storage and can be read by
/// any pod that can reach the bucket.
///
/// That asymmetry is what the in-cluster split is built on — see docs/telemetry-in-cluster.md §3.2.
/// </summary>
public enum SegmentScope
{
    /// <summary>Active index plus every overlapping sealed segment. The default, and correct for any single process.</summary>
    All,

    /// <summary>Only the unsealed active index — events too recent to have been sealed. Answerable only by the indexer.</summary>
    Hot,

    /// <summary>Only sealed segments (warm on local disk, cold in object storage). Answerable by any pod with bucket access.</summary>
    Sealed,
}
