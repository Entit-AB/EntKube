using System.Text.RegularExpressions;
using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Answers <see cref="IMetricsQuery"/> from the central rollup store, for clusters whose metrics
/// EntKube holds itself (docs/telemetry-central-again.md, phase 3).
///
/// <para><b>Nothing resolves this yet — not even DI.</b> It is constructed directly by its tests.
/// Registering it would be dead configuration until something chooses between it and
/// <see cref="PromMetricsService"/>, and that choice is the next change, deliberately separate:
/// routing is the moment a dashboard's numbers change source, and the semantics it would change
/// them to are worth a second opinion first.</para>
///
/// <para><b>Every method takes the asking tenant</b>, and every query filters on it, because the
/// cluster id used to be the whole question and a page that got one wrong read another tenant's
/// metrics. Unlike <see cref="PromMetricsService"/>, which reaches a cluster's own Prometheus and
/// so is bounded by which cluster it found, this store holds every tenant's rows in one table —
/// the predicate is the only boundary there is.</para>
/// </summary>
public class RollupMetricsQuery(
    IDbContextFactory<TelemetryDbContext> dbFactory,
    IConfiguration configuration) : IMetricsQuery
{
    /// <summary>
    /// How far back discovery looks for names and services. The same 24 hours
    /// <see cref="PromMetricsService"/> uses, so the explorer's dropdowns offer the same
    /// candidates from either backend — a list that changed with the backend would look like
    /// metrics appearing and disappearing.
    /// </summary>
    private static readonly TimeSpan DiscoveryLookback = TimeSpan.FromHours(24);

    /// <summary>
    /// The resolution rows were written at. Read from configuration rather than from the rows,
    /// and used as a query predicate: a store whose dial was changed holds rows at both widths,
    /// and mixing them would difference a counter against a bucket of a different size.
    /// </summary>
    private int Resolution =>
        MetricRollupFold.Clamp(configuration.GetValue<int?>("Telemetry:MetricsResolutionSeconds") ?? 30);

    public async Task<bool> HasDataAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DateTime since = DateTime.UtcNow - DiscoveryLookback;

        return await db.MetricRollups
            .AnyAsync(r => r.TenantId == tenantId && r.ClusterId == clusterId
                && r.BucketStart >= since, ct);
    }

    public async Task<KubernetesOperationResult<List<string>>> GetMetricNamesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DateTime since = DateTime.UtcNow - DiscoveryLookback;

        List<string> names = await Scoped(db, tenantId, clusterId, namespaces, podPattern)
            .Where(r => r.BucketStart >= since)
            .Select(r => r.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

        return KubernetesOperationResult<List<string>>.Success(names);
    }

    public async Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DateTime since = DateTime.UtcNow - DiscoveryLookback;

        List<string> services = await Scoped(db, tenantId, clusterId, namespaces, podPattern)
            .Where(r => r.BucketStart >= since && r.Service != null)
            .Select(r => r.Service!)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);

        return KubernetesOperationResult<List<string>>.Success(services);
    }

    public async Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
        Guid tenantId, Guid clusterId, string name, string? service, DateTime from, DateTime to,
        int buckets = 60, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        Window w = WindowFor(name, from, to, buckets);

        List<MetricBucket> rows = await LoadAsync(
            tenantId, clusterId, name, service, w, namespaces, podPattern,
            // Scoped by POD even though the answer is one line: a counter has to be differenced
            // within one series, and a pod is the finest series the store keeps. Grouping any
            // coarser here would difference across pods and turn one pod's restart into a spike
            // in the total.
            r => r.Pod ?? "", ct);

        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse(
            name, MetricSeriesBuilder.ToStep(rows, Resolution, w.Step), w.Step);

        return KubernetesOperationResult<List<TimeSeriesDataPoint>>.Success(
            [.. series.Select(p => new TimeSeriesDataPoint { Timestamp = p.Timestamp, Value = p.Value })]);
    }

    public async Task<KubernetesOperationResult<List<MetricSeriesGroup>>> GetSeriesByAsync(
        Guid tenantId, Guid clusterId, string name, string? service, string dimension,
        DateTime from, DateTime to, int buckets = 60, int maxSeries = 24,
        CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        // The three dimensions the explorer can ask for, defaulting to pod — the same mapping
        // PromMetricsQueryBuilder.BreakdownLabel applies, including the default, so a breakdown
        // means the same thing whichever backend answers it.
        Func<MetricRollup, string> scope = dimension switch
        {
            "service" => r => r.Service ?? "",
            "namespace" => r => r.Namespace ?? "",
            _ => r => r.Pod ?? "",
        };

        Window w = WindowFor(name, from, to, buckets);

        List<MetricBucket> rows = await LoadAsync(
            tenantId, clusterId, name, service, w, namespaces, podPattern, scope, ct);

        List<(string Scope, List<MetricSeriesPoint> Points)> grouped = MetricSeriesBuilder.CollapseByScope(
            name, MetricSeriesBuilder.ToStep(rows, Resolution, w.Step), w.Step, maxSeries);

        return KubernetesOperationResult<List<MetricSeriesGroup>>.Success(
            [.. grouped.Select(g => new MetricSeriesGroup(
                g.Scope,
                [.. g.Points.Select(p => new TimeSeriesDataPoint { Timestamp = p.Timestamp, Value = p.Value })]))]);
    }

    /// <summary>
    /// The plotted window: the step the caller's bucket count implies, the first bucket that
    /// belongs to the answer, and the first bucket that has to be READ to produce it.
    /// </summary>
    /// <param name="Step">
    /// Never finer than the stored resolution — the store cannot answer finer, and
    /// <see cref="MetricSeriesBuilder.ToStep"/> would hand back stored-width buckets while the
    /// rate arithmetic believed they were narrower.
    /// </param>
    /// <param name="First">The step bucket that <c>from</c> falls in: the start of the answer.</param>
    /// <param name="Lead">
    /// The first bucket that has to be READ. One step before <see cref="First"/> for a counter, and
    /// <see cref="First"/> itself for a gauge.
    ///
    /// <para>A counter's first bucket has no rate without its predecessor, so without the lead the
    /// series begins one step late — and since every chart's left edge is a <c>from</c> someone
    /// chose, the symptom is a blank first interval on every counter chart at every range, which
    /// reads as missing data rather than as arithmetic.</para>
    ///
    /// <para><b>A gauge is deliberately NOT widened, and that is the whole protection against
    /// plotting outside the range</b> — there is no filter after the fact. Two reasons it is done
    /// this way round. A gauge needs no predecessor, so reading one would only be a point before
    /// the operator's own left edge; and in the broken-down view <c>topk</c> orders series by their
    /// peak BEFORE anything could be filtered, so a pod whose only large reading sat in a lead
    /// bucket could displace a pod that was larger inside the range. Not reading it cannot do
    /// either. For a counter the question does not arise: the lead bucket yields no point, which
    /// <c>MetricSeriesBuilderTests</c> pins.</para>
    /// </param>
    /// <param name="End">The caller's exclusive end; the window is [Lead, End).</param>
    private readonly record struct Window(int Step, DateTime First, DateTime Lead, DateTime End);

    private Window WindowFor(string name, DateTime from, DateTime to, int buckets)
    {
        int step = Math.Max(MetricSeriesBuilder.StepFor(from, to, buckets), Resolution);
        DateTime first = MetricRollupFold.BucketFor(from, step);

        return new Window(
            step,
            first,
            MetricSeriesBuilder.IsCounter(name) ? first.AddSeconds(-step) : first,
            to);
    }

    /// <summary>
    /// The rows for one metric over one window, projected to the scope the caller groups by.
    /// </summary>
    private async Task<List<MetricBucket>> LoadAsync(
        Guid tenantId, Guid clusterId, string name, string? service, Window w,
        IReadOnlyList<string>? namespaces, string? podPattern,
        Func<MetricRollup, string> scope, CancellationToken ct)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        int resolution = Resolution;
        (DateTime lead, DateTime end) = (w.Lead, w.End);

        IQueryable<MetricRollup> q = Scoped(db, tenantId, clusterId, namespaces, podPattern)
            .Where(r => r.Name == name
                && r.ResolutionSeconds == resolution
                && r.BucketStart >= lead
                && r.BucketStart < end);

        if (!string.IsNullOrWhiteSpace(service))
        {
            q = q.Where(r => r.Service == service);
        }

        List<MetricRollup> rows = await q
            .OrderBy(r => r.BucketStart)
            .ToListAsync(ct);

        // Returned at the STORED width. Re-bucketing to the caller's step happens above, in
        // memory: the aggregate a coarser bucket needs (Last = the newest fine bucket's reading,
        // not the largest) is not something every provider can express, and the row count is
        // already bounded by the window.
        return [.. rows.Select(r => new MetricBucket(
            scope(r),
            // Stamped UTC on the way out. Rows are written UTC, but what a provider hands back
            // depends on the provider: Npgsql returns Utc for a timestamptz, SQLite returns
            // Unspecified because it stored TEXT.
            //
            // ⚠️ This OVERLAPS with MetricRollupFold.BucketFor, which also had to stop treating an
            // Unspecified timestamp as local — measured by removal, either one alone repairs the
            // coarse-step read that found the bug, so neither is "the" fix. They are kept because
            // they protect different things: BucketFor protects the WRITE path, where a
            // wrongly-bucketed row is permanent, and this protects what leaves the query, where an
            // Unspecified instant slides a chart's x-axis by the host's offset. The Kind stamped
            // here is asserted by
            // RollupMetricsQueryTests.Every_timestamp_handed_back_is_utc_whatever_the_provider_stored,
            // which is the only test that fails if this line goes — DateTime.Equals ignores Kind,
            // so every value assertion in that file passes without it.
            DateTime.SpecifyKind(r.BucketStart, DateTimeKind.Utc),
            r.Count, r.Sum, r.Min, r.Max, r.Last))];
    }

    /// <summary>
    /// The tenant and cluster filter, plus the explorer's namespace and pod scope.
    /// </summary>
    private static IQueryable<MetricRollup> Scoped(
        TelemetryDbContext db, Guid tenantId, Guid clusterId,
        IReadOnlyList<string>? namespaces, string? podPattern)
    {
        IQueryable<MetricRollup> q = db.MetricRollups
            .Where(r => r.TenantId == tenantId && r.ClusterId == clusterId);

        if (namespaces is { Count: > 0 })
        {
            List<string> wanted = [.. namespaces];
            q = q.Where(r => r.Namespace != null && wanted.Contains(r.Namespace));
        }

        if (PodPrefixes(podPattern) is { Count: > 0 } prefixes)
        {
            q = q.Where(r => r.Pod != null && prefixes.Any(p => r.Pod!.StartsWith(p)));
        }

        return q;
    }

    /// <summary>
    /// The workload prefixes inside <c>^(a|b|c)-</c>, or empty when the pattern is not that shape.
    ///
    /// <para><b>The pod scope arrives as a POSIX regex, and is applied as a prefix match
    /// instead.</b> <c>DeploymentPodScope</c> builds <c>^(workload-a|workload-b)-</c> for
    /// Prometheus, which no provider can translate to SQL — and evaluating it in memory would mean
    /// loading every row the cluster has before filtering, which is the shape this store exists to
    /// avoid. For every pattern the product generates, a prefix match means the same thing.</para>
    ///
    /// <para>Anything else returns empty, which means UNSCOPED — deliberately, and this is the
    /// one judgement call here. A pattern this cannot read is better answered broadly than
    /// narrowly: too many pods in a chart is visible and the operator can narrow it, whereas too
    /// few is indistinguishable from the workload having no metrics at all.</para>
    /// </summary>
    public static List<string> PodPrefixes(string? podPattern)
    {
        if (string.IsNullOrWhiteSpace(podPattern)) return [];

        Match m = Regex.Match(podPattern, @"^\^\((?<alts>[^)]*)\)-$");
        if (!m.Success) return [];

        return [.. m.Groups["alts"].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // The workload names were Regex.Escape'd on the way in, so a literal prefix match
            // needs them unescaped again.
            .Select(Regex.Unescape)
            .Where(s => s.Length > 0)];
    }
}
