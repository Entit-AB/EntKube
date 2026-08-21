using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Upgrades;

/// <summary>Where an installed component stands against what its repository publishes.</summary>
public enum UpgradeStatus
{
    /// <summary>A newer chart version is published.</summary>
    UpgradeAvailable = 0,
    /// <summary>The installed chart version is marked deprecated by its author.</summary>
    Deprecated = 1,
    /// <summary>Running the newest published version.</summary>
    UpToDate = 2,
    /// <summary>Could not be determined — repo unreachable, chart missing, or version unparseable.</summary>
    Unknown = 3,
}

/// <summary>One installed component measured against its repository.</summary>
public sealed record ComponentUpgrade
{
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }
    public required Guid ComponentId { get; init; }
    public required string ComponentName { get; init; }

    /// <summary>Catalog entry key when this component is a known catalog component; null when adopted ad hoc.</summary>
    public string? CatalogKey { get; init; }

    /// <summary>Catalog display name, falling back to the component's own name.</summary>
    public required string DisplayName { get; init; }

    public string? Category { get; init; }
    public string? ChartName { get; init; }
    public string? RepoUrl { get; init; }

    public string? InstalledVersion { get; init; }

    /// <summary>Newest version the operator should move to, honouring the pre-release rule below.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>When the newest version was published, when the repo records it.</summary>
    public DateTime? LatestReleasedAt { get; init; }

    public required UpgradeStatus Status { get; init; }
    public VersionLag Lag { get; init; } = VersionLag.UpToDate;

    /// <summary>How many published versions sit between installed and latest.</summary>
    public int VersionsBehind { get; init; }

    /// <summary>Version pinned by the EntKube catalog, when it pins one.</summary>
    public string? CatalogPinnedVersion { get; init; }

    /// <summary>
    /// True when the catalog's own pin is behind upstream. This is a signal for EntKube
    /// maintainers rather than for the operator — the operator cannot fix it by upgrading.
    /// </summary>
    public bool CatalogPinStale { get; init; }

    /// <summary>Operator-facing explanation when <see cref="Status"/> is Unknown.</summary>
    public string? Note { get; init; }

    /// <summary>True when an upgrade is both available and actionable from the UI.</summary>
    public bool IsActionable => Status is UpgradeStatus.UpgradeAvailable or UpgradeStatus.Deprecated;
}

/// <summary>A tenant-wide view of chart currency across every registered cluster.</summary>
public sealed record UpgradeReport
{
    public required IReadOnlyList<ComponentUpgrade> Components { get; init; }

    public int UpgradeCount => Components.Count(c => c.Status == UpgradeStatus.UpgradeAvailable);
    public int MajorCount => Components.Count(c => c.Status == UpgradeStatus.UpgradeAvailable && c.Lag == VersionLag.Major);
    public int DeprecatedCount => Components.Count(c => c.Status == UpgradeStatus.Deprecated);
    public int UnknownCount => Components.Count(c => c.Status == UpgradeStatus.Unknown);
}

/// <summary>
/// Answers "what is out of date across the fleet?" by joining three sources that
/// already exist: the tracked <see cref="ClusterComponent"/> rows (installed chart
/// version), <see cref="ComponentCatalog"/> (the version EntKube recommends), and
/// each chart repository's index (what upstream actually publishes).
///
/// Read-only and side-effect free — it never touches a cluster. Acting on a finding
/// goes through the existing <c>ComponentLifecycleService</c> upgrade path behind the
/// change gate, so there is exactly one route that mutates a cluster.
/// </summary>
public class ComponentUpgradeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    HelmRepoIndexClient indexClient,
    ILogger<ComponentUpgradeService> logger)
{
    /// <summary>
    /// Builds the upgrade report for every installed Helm component across the tenant's clusters.
    /// </summary>
    public async Task<UpgradeReport> GetTenantReportAsync(
        Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var tracked = await db.ClusterComponents
            .AsNoTracking()
            .Where(c => c.Cluster.TenantId == tenantId && c.Status == ComponentStatus.Installed)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.ComponentType,
                c.HelmRepoUrl,
                c.HelmChartName,
                c.HelmChartVersion,
                c.ClusterId,
                ClusterName = c.Cluster.Name,
            })
            .ToListAsync(ct);

        List<ComponentUpgrade> results = [];

        foreach (var component in tracked)
        {
            // Manifest components have no chart to compare against; excluding them here keeps
            // the report free of rows that could never show an upgrade.
            if (!string.Equals(component.ComponentType, "HelmChart", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CatalogEntry? catalog = ComponentCatalog.ResolveForComponent(component.Name, component.HelmChartName);

            string? repoUrl = component.HelmRepoUrl ?? catalog?.HelmRepoUrl;
            string? chartName = component.HelmChartName ?? catalog?.HelmChartName;

            ChartIndexResult index = await indexClient.GetChartVersionsAsync(repoUrl, chartName, now, ct);

            results.Add(Evaluate(
                clusterId: component.ClusterId,
                clusterName: component.ClusterName,
                componentId: component.Id,
                componentName: component.Name,
                installedVersion: component.HelmChartVersion,
                repoUrl: repoUrl,
                chartName: chartName,
                catalog: catalog,
                index: index));
        }

        // Most-urgent first: actionable rows before informational ones, biggest lag first,
        // so the top of the list is always the next thing worth doing.
        results.Sort((a, b) =>
        {
            int byStatus = a.Status.CompareTo(b.Status);
            if (byStatus != 0) return byStatus;

            int byLag = a.Lag.CompareTo(b.Lag);
            if (byLag != 0) return byLag;

            int byBehind = b.VersionsBehind.CompareTo(a.VersionsBehind);
            if (byBehind != 0) return byBehind;

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        logger.LogDebug(
            "Upgrade report for tenant {TenantId}: {Total} components, {Upgrades} upgradeable",
            tenantId, results.Count, results.Count(r => r.Status == UpgradeStatus.UpgradeAvailable));

        return new UpgradeReport { Components = results };
    }

    /// <summary>
    /// Compares one installed component against its published versions. Pure and static so
    /// the ranking rules can be tested without a database or a repository.
    /// </summary>
    public static ComponentUpgrade Evaluate(
        Guid clusterId,
        string clusterName,
        Guid componentId,
        string componentName,
        string? installedVersion,
        string? repoUrl,
        string? chartName,
        CatalogEntry? catalog,
        ChartIndexResult index)
    {
        ComponentUpgrade Base(UpgradeStatus status, string? note = null) => new()
        {
            ClusterId = clusterId,
            ClusterName = clusterName,
            ComponentId = componentId,
            ComponentName = componentName,
            CatalogKey = catalog?.Key,
            DisplayName = catalog?.DisplayName ?? componentName,
            Category = catalog?.Category,
            ChartName = chartName,
            RepoUrl = repoUrl,
            InstalledVersion = installedVersion,
            CatalogPinnedVersion = catalog?.HelmChartVersion,
            Status = status,
            Note = note,
        };

        if (!index.Success)
        {
            return Base(UpgradeStatus.Unknown, index.Error);
        }

        SemVer? installed = SemVer.Parse(installedVersion);
        if (installed is null)
        {
            // A release installed without a pinned version tracks whatever was latest at
            // install time. We can't say whether that is current, and claiming "up to date"
            // would be a false all-clear.
            return Base(UpgradeStatus.Unknown, string.IsNullOrWhiteSpace(installedVersion)
                ? "No chart version recorded for this release."
                : $"Installed version '{installedVersion}' is not valid SemVer.");
        }

        // Only offer a pre-release upgrade to someone already running a pre-release.
        // Pushing an operator from a stable chart onto an rc is never the right default.
        List<ChartRelease> candidates = index.Releases
            .Where(r => r.Parsed is not null)
            .Where(r => installed.IsPreRelease || !r.Parsed!.IsPreRelease)
            .ToList();

        if (candidates.Count == 0)
        {
            return Base(UpgradeStatus.Unknown, "Repository lists no comparable versions for this chart.");
        }

        ChartRelease latest = candidates[0];
        bool installedIsDeprecated = index.Releases
            .Any(r => r.Deprecated && r.Parsed is not null && r.Parsed.Equals(installed));

        VersionLag lag = SemVer.Compare(installed, latest.Parsed!);
        int versionsBehind = candidates.Count(r => r.Parsed! > installed);

        // A stale catalog pin is our bug, not the operator's, so it is reported alongside
        // rather than as the component's status.
        SemVer? pinned = SemVer.Parse(catalog?.HelmChartVersion);
        bool pinStale = pinned is not null
            && !pinned.IsPreRelease
            && SemVer.Compare(pinned, latest.Parsed!) != VersionLag.UpToDate;

        UpgradeStatus status = lag == VersionLag.UpToDate
            ? (installedIsDeprecated ? UpgradeStatus.Deprecated : UpgradeStatus.UpToDate)
            : UpgradeStatus.UpgradeAvailable;

        return Base(status) with
        {
            LatestVersion = latest.Version,
            LatestReleasedAt = latest.Created,
            Lag = lag,
            VersionsBehind = versionsBehind,
            CatalogPinStale = pinStale,
            Note = installedIsDeprecated ? "The installed chart version is marked deprecated upstream." : null,
        };
    }
}
