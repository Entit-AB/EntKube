namespace EntKube.Web.Data;

/// <summary>What a <see cref="ResourceUsageSnapshot"/> measures.</summary>
public enum ResourceUsageKind
{
    /// <summary>PersistentVolumeClaim used/capacity ratio.</summary>
    PvcFill,
    /// <summary>Pod memory working-set ÷ its memory limit.</summary>
    MemoryVsLimit,
}

/// <summary>
/// A periodically-collected point sample of a saturation ratio (0..1) for one PVC or
/// workload on a cluster. The advisor reads a recent window of these to project when a
/// resource will hit its ceiling — the "this will break" signal — because a live
/// Prometheus query is far too slow to run on the compute-on-read path.
/// Written by <see cref="EntKube.Web.Services.ResourceUsageCollectorService"/>.
/// </summary>
public class ResourceUsageSnapshot
{
    public Guid Id { get; set; }
    public Guid ClusterId { get; set; }
    public ResourceUsageKind Kind { get; set; }

    public required string Namespace { get; set; }

    /// <summary>PVC name (PvcFill) or pod name (MemoryVsLimit).</summary>
    public required string Name { get; set; }

    /// <summary>Saturation ratio 0..1 (used ÷ capacity, or working-set ÷ limit).</summary>
    public double Fraction { get; set; }

    public DateTime SnapshotAt { get; set; } = DateTime.UtcNow;
}
