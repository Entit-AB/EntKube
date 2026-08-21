using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.SupplyChain;

/// <summary>What is known about the security posture of an image that is actually running.</summary>
public enum ImageScanState
{
    /// <summary>Scanned, and Critical or High findings were reported.</summary>
    Vulnerable = 0,
    /// <summary>In a managed registry but never successfully scanned — posture is unknown.</summary>
    Unscanned = 1,
    /// <summary>Pulled from a registry EntKube does not manage, so no scan data exists at all.</summary>
    Unmanaged = 2,
    /// <summary>Scanned, with no Critical or High findings.</summary>
    Clean = 3,
}

/// <summary>One distinct image running on a cluster, with whatever scan data backs it.</summary>
public sealed record RunningImage
{
    public required string Image { get; init; }
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }

    /// <summary>"namespace/Kind/name" for each workload using this image.</summary>
    public required IReadOnlyList<string> Workloads { get; init; }

    public required ImageScanState State { get; init; }

    /// <summary>Trivy summary from Harbor, when the image is in a managed registry and scanned.</summary>
    public HarborScanOverview? Scan { get; init; }

    /// <summary>Registry host the image was pulled from.</summary>
    public string? Registry { get; init; }

    /// <summary>Harbor project, when the image lives in a managed registry.</summary>
    public string? Project { get; init; }

    /// <summary>Harbor repository (below the project), when applicable.</summary>
    public string? Repository { get; init; }

    /// <summary>Tag or digest used as the registry reference — what a CVE drill-down needs.</summary>
    public string? Reference { get; init; }

    /// <summary>Why the state is Unscanned/Unmanaged, for the operator.</summary>
    public string? Note { get; init; }

    /// <summary>True when the image is pinned by digest rather than by a mutable tag.</summary>
    public bool IsDigestPinned { get; init; }

    public int CriticalCount => Scan?.Critical ?? 0;
    public int HighCount => Scan?.High ?? 0;
    public int FixableCount => Scan?.Fixable ?? 0;
}

/// <summary>A tenant-wide picture of what is running and what is known about it.</summary>
public sealed record SupplyChainReport
{
    public required IReadOnlyList<RunningImage> Images { get; init; }
    public required DateTime GeneratedAt { get; init; }

    /// <summary>
    /// Anything that limited coverage — an unreachable cluster, a registry that could not
    /// be queried, a cap that was hit. Surfaced so a partial sweep is never mistaken for
    /// a clean one.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int VulnerableCount => Images.Count(i => i.State == ImageScanState.Vulnerable);
    public int UnscannedCount => Images.Count(i => i.State == ImageScanState.Unscanned);
    public int UnmanagedCount => Images.Count(i => i.State == ImageScanState.Unmanaged);
    public int CleanCount => Images.Count(i => i.State == ImageScanState.Clean);

    public int TotalCritical => Images.Sum(i => i.CriticalCount);
    public int TotalHigh => Images.Sum(i => i.HighCount);

    /// <summary>Images running from a mutable tag rather than a digest — a provenance weakness.</summary>
    public int MutableTagCount => Images.Count(i => !i.IsDigestPinned && i.State != ImageScanState.Unmanaged);
}

/// <summary>
/// Answers "what is running, where did it come from, and what is wrong with it?" by
/// joining live cluster workloads to the Trivy scan data Harbor already produces.
///
/// The join runs from the workloads outward, not from the registry inward: it starts
/// with the images actually running and looks up only those repositories. Enumerating
/// every project and repository in Harbor would cost hundreds of API calls to describe
/// images nobody is running — the interesting question is always about what is live.
///
/// Read-only. It never triggers a scan and never mutates a cluster or registry.
/// </summary>
public class SupplyChainService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    WorkloadService workloads,
    HarborService harbor,
    ILogger<SupplyChainService> logger)
{
    /// <summary>
    /// Distinct repositories queried per sweep. A tenant running images from thousands of
    /// distinct repositories would otherwise turn one page load into a registry crawl.
    /// Hitting this is reported as a warning, never silently truncated.
    /// </summary>
    private const int MaxRepositoryLookups = 200;

    public async Task<SupplyChainReport> GetTenantReportAsync(
        Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        List<string> warnings = [];

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var clusters = await db.KubernetesClusters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.Name })
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        List<RunningImage> results = [];
        // Cached per (config, project, repository) so several running tags of the same
        // repository cost one Harbor call, not one each.
        Dictionary<string, List<HarborArtifactInfo>> artifactCache = [];
        int lookups = 0;

        foreach (var cluster in clusters)
        {
            WorkloadSnapshot snapshot;
            try
            {
                snapshot = await workloads.LoadAsync(cluster.Id, ns: null, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read workloads on “{cluster.Name}”: {ex.Message}");
                continue;
            }

            if (snapshot.Error is not null)
            {
                warnings.Add($"Could not read workloads on “{cluster.Name}”: {snapshot.Error}");
                continue;
            }

            List<HarborComponentConfig> harborConfigs;
            try
            {
                harborConfigs = await harbor.GetConfigsForClusterAsync(tenantId, cluster.Id, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not load Harbor configs for cluster {ClusterId}", cluster.Id);
                harborConfigs = [];
            }

            // Pods duplicate their controller's images, so group by image and keep the
            // controllers as the owning workloads — one row per image, not per pod.
            var byImage = snapshot.Workloads
                .SelectMany(w => w.Images.Select(image => (image, workload: w)))
                .Where(x => !string.IsNullOrWhiteSpace(x.image))
                .GroupBy(x => x.image, StringComparer.Ordinal);

            foreach (var group in byImage)
            {
                ImageReference? parsed = ImageReference.Parse(group.Key);
                List<string> owners = [.. group
                    .Select(x => $"{x.workload.Namespace}/{x.workload.Kind}/{x.workload.Name}")
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)];

                RunningImage Base(ImageScanState state, string? note = null) => new()
                {
                    Image = group.Key,
                    ClusterId = cluster.Id,
                    ClusterName = cluster.Name,
                    Workloads = owners,
                    State = state,
                    Registry = parsed?.Registry,
                    Project = parsed?.Project,
                    Repository = parsed?.HarborRepository,
                    Reference = parsed?.Reference,
                    IsDigestPinned = parsed?.Digest is not null,
                    Note = note,
                };

                HarborComponentConfig? managing = parsed is null
                    ? null
                    : harborConfigs.FirstOrDefault(c => parsed.IsFromRegistry(c.RegistryUrl));

                if (parsed is null || managing is null)
                {
                    results.Add(Base(ImageScanState.Unmanaged,
                        "Pulled from a registry EntKube does not manage — no scan data available."));
                    continue;
                }

                if (parsed.Project is null || parsed.HarborRepository is null)
                {
                    results.Add(Base(ImageScanState.Unscanned,
                        "Image path has no project component, so it cannot be located in Harbor."));
                    continue;
                }

                string cacheKey = $"{managing.Id}|{parsed.Project}|{parsed.HarborRepository}";
                if (!artifactCache.TryGetValue(cacheKey, out List<HarborArtifactInfo>? artifacts))
                {
                    if (lookups >= MaxRepositoryLookups)
                    {
                        results.Add(Base(ImageScanState.Unscanned,
                            "Repository lookup limit reached for this sweep."));
                        continue;
                    }

                    lookups++;
                    try
                    {
                        artifacts = await harbor.GetArtifactsAsync(
                            tenantId, managing, parsed.Project, parsed.HarborRepository, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Harbor lookup failed for {Repository}", parsed.Repository);
                        artifacts = [];
                        warnings.Add($"Could not read {parsed.Repository} from Harbor: {ex.Message}");
                    }

                    artifactCache[cacheKey] = artifacts;
                }

                HarborArtifactInfo? artifact = FindArtifact(artifacts, parsed);
                if (artifact is null)
                {
                    results.Add(Base(ImageScanState.Unscanned,
                        "Running image was not found in the registry — it may have been deleted or retagged."));
                    continue;
                }

                HarborScanOverview? scan = artifact.ScanOverview;
                if (scan is null || !scan.IsScanned)
                {
                    // An unscanned image is NOT a clean one. Reporting it as clean would be
                    // the single most dangerous thing this feature could do.
                    results.Add(Base(ImageScanState.Unscanned, scan is null
                        ? "Harbor has never scanned this image."
                        : $"Harbor scan status is “{scan.ScanStatus}”.") with { Scan = scan });
                    continue;
                }

                ImageScanState state = scan.Critical > 0 || scan.High > 0
                    ? ImageScanState.Vulnerable
                    : ImageScanState.Clean;

                results.Add(Base(state) with { Scan = scan });
            }
        }

        if (lookups >= MaxRepositoryLookups)
        {
            warnings.Add(
                $"Stopped after {MaxRepositoryLookups} repository lookups; some images were not checked.");
        }

        List<RunningImage> ordered = [.. results
            .OrderBy(r => r.State)
            .ThenByDescending(r => r.CriticalCount)
            .ThenByDescending(r => r.HighCount)
            .ThenBy(r => r.Image, StringComparer.OrdinalIgnoreCase)];

        return new SupplyChainReport
        {
            Images = ordered,
            GeneratedAt = now,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Locates the artifact backing a running image — by digest when the image is pinned,
    /// otherwise by tag. Exposed for tests so the matching rules can be checked without
    /// a live registry.
    /// </summary>
    public static HarborArtifactInfo? FindArtifact(
        IReadOnlyList<HarborArtifactInfo> artifacts, ImageReference image)
    {
        if (image.Digest is not null)
        {
            return artifacts.FirstOrDefault(a =>
                string.Equals(a.Digest, image.Digest, StringComparison.OrdinalIgnoreCase));
        }

        string tag = image.Tag ?? "latest";
        return artifacts.FirstOrDefault(a =>
            a.Tags.Any(t => string.Equals(t, tag, StringComparison.Ordinal)));
    }
}
