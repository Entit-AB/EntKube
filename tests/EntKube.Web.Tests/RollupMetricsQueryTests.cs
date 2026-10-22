using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading metrics back out of the central rollup store.
///
/// <para><b>Two different things are at stake here, and only one of them is arithmetic.</b>
/// <see cref="MetricSeriesBuilderTests"/> already pins the series semantics against known numbers;
/// what these tests add is everything between a caller's question and those numbers — which rows
/// are fetched, and in particular <b>whose</b>. Unlike <c>PromMetricsService</c>, which reaches a
/// cluster's own Prometheus and is therefore bounded by the cluster it found, this store holds
/// every tenant's rows in one table. The tenant predicate is the only boundary there is, so each
/// of the five methods is tested against a populated neighbouring tenant rather than against an
/// empty database — a missing predicate returns a plausible answer, not an error.</para>
/// </summary>
public sealed class RollupMetricsQueryTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid otherTenant = Guid.NewGuid();
    private readonly Guid otherCluster = Guid.NewGuid();

    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    public RollupMetricsQueryTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Database.EnsureCreated();
        }
        factory = new TestDbContextFactory(connection);
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private RollupMetricsQuery Query(int resolutionSeconds = 30)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:MetricsResolutionSeconds"] = resolutionSeconds.ToString(),
            })
            .Build();

        return new RollupMetricsQuery(factory, config);
    }

    /// <summary>
    /// Stores one bucket. <paramref name="value"/> is written to every aggregate, which is the
    /// common single-scrape case; <c>Last</c> is what both rules actually read.
    /// </summary>
    private async Task SeedAsync(
        DateTime bucketStart, double value, string name = "cpu", string? pod = "pod-a",
        string? service = "svc", string? ns = "ns", Guid? tenant = null, Guid? cluster = null,
        int resolutionSeconds = 30, long count = 1)
    {
        await using TelemetryDbContext db = ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();

        db.MetricRollups.Add(new MetricRollup
        {
            Id = Guid.NewGuid(),
            TenantId = tenant ?? tenantId,
            ClusterId = cluster ?? clusterId,
            Name = name,
            Service = service,
            Namespace = ns,
            Pod = pod,
            BucketStart = bucketStart,
            ResolutionSeconds = resolutionSeconds,
            Count = count,
            Sum = value * count,
            Min = value,
            Max = value,
            Last = value,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A five-minute window at the stored resolution: ten 30-second steps.</summary>
    private static (DateTime From, DateTime To, int Buckets) FiveMinutes => (T0, T0.AddMinutes(5), 10);

    // ── Whose rows: the predicate that is the only boundary ──

    [Fact]
    public async Task A_stranger_asking_about_this_cluster_is_told_there_is_no_data()
    {
        await SeedAsync(DateTime.UtcNow.AddMinutes(-1), 10);

        // The cluster id is right. Only the tenant is wrong — which is exactly the shape of the
        // bug this predicate exists for: a page that got a cluster id from somewhere it should
        // not have, and a store with no cluster table to bound it.
        (await Query().HasDataAsync(otherTenant, clusterId)).Should().BeFalse();
        (await Query().HasDataAsync(tenantId, clusterId)).Should().BeTrue("the owner can see it");
    }

    [Fact]
    public async Task A_stranger_cannot_discover_this_clusters_metric_names_or_services()
    {
        await SeedAsync(DateTime.UtcNow.AddMinutes(-1), 10, name: "secret_metric", service: "secret-svc");

        RollupMetricsQuery q = Query();

        (await q.GetMetricNamesAsync(otherTenant, clusterId)).Data.Should().BeEmpty();
        (await q.GetServicesAsync(otherTenant, clusterId)).Data.Should().BeEmpty();

        // Not a refusal: an empty success is what a cluster with no central metrics looks like,
        // and the caller must not be able to tell the two apart.
        (await q.GetMetricNamesAsync(otherTenant, clusterId)).IsSuccess.Should().BeTrue();

        (await q.GetMetricNamesAsync(tenantId, clusterId)).Data.Should().Equal(["secret_metric"]);
        (await q.GetServicesAsync(tenantId, clusterId)).Data.Should().Equal(["secret-svc"]);
    }

    [Fact]
    public async Task A_stranger_cannot_read_this_clusters_series()
    {
        await SeedAsync(T0, 42);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;
        RollupMetricsQuery q = Query();

        (await q.GetSeriesAsync(otherTenant, clusterId, "cpu", null, from, to, buckets))
            .Data.Should().BeEmpty();
        (await q.GetSeriesByAsync(otherTenant, clusterId, "cpu", null, "pod", from, to, buckets))
            .Data.Should().BeEmpty();

        (await q.GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(42);
    }

    [Fact]
    public async Task Another_cluster_of_the_same_tenant_is_a_different_answer()
    {
        await SeedAsync(T0, 7);
        await SeedAsync(T0, 1000, cluster: otherCluster);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(7);
    }

    // ── Discovery ──

    [Fact]
    public async Task Discovery_offers_what_was_reported_in_the_last_day_and_not_what_was_not()
    {
        // The same 24 hours PromMetricsService asks Prometheus for. A list that changed with the
        // backend would look like metrics appearing and disappearing when routing flipped.
        await SeedAsync(DateTime.UtcNow.AddHours(-1), 1, name: "recent", service: "recent-svc");
        await SeedAsync(DateTime.UtcNow.AddHours(-25), 1, name: "ancient", service: "ancient-svc");

        RollupMetricsQuery q = Query();

        (await q.GetMetricNamesAsync(tenantId, clusterId)).Data.Should().Equal(["recent"]);
        (await q.GetServicesAsync(tenantId, clusterId)).Data.Should().Equal(["recent-svc"]);
    }

    [Fact]
    public async Task Discovery_lists_each_name_once_and_in_order()
    {
        DateTime now = DateTime.UtcNow.AddMinutes(-1);
        await SeedAsync(now, 1, name: "zeta");
        await SeedAsync(now.AddSeconds(-30), 1, name: "zeta");
        await SeedAsync(now, 1, name: "alpha");

        (await Query().GetMetricNamesAsync(tenantId, clusterId)).Data
            .Should().Equal(["alpha", "zeta"]);
    }

    [Fact]
    public async Task A_scope_without_a_service_does_not_appear_as_a_blank_one()
    {
        // A null Service is a real row — a metric reported without one — and offering it as an
        // empty dropdown entry would be a service the operator could select and never match.
        await SeedAsync(DateTime.UtcNow.AddMinutes(-1), 1, service: null);

        (await Query().GetServicesAsync(tenantId, clusterId)).Data.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_store_answers_successfully_with_nothing()
    {
        (DateTime from, DateTime to, int buckets) = FiveMinutes;
        RollupMetricsQuery q = Query();

        (await q.HasDataAsync(tenantId, clusterId)).Should().BeFalse();

        KubernetesOperationResult<List<TimeSeriesDataPoint>> series =
            await q.GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets);
        series.IsSuccess.Should().BeTrue();
        series.Data.Should().BeEmpty();
    }

    // ── A gauge, end to end ──

    [Fact]
    public async Task A_gauge_sums_one_reading_per_pod()
    {
        await SeedAsync(T0, 10, pod: "pod-a", count: 3);
        await SeedAsync(T0, 5, pod: "pod-b", count: 3);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(15,
                "the last reading of each pod, not every reading in the bucket");
    }

    /// <summary>
    /// Nothing outside [from, to) is plotted — and NOT reading the earlier bucket is the only
    /// thing that guarantees it.
    ///
    /// <para>There is deliberately no filter after the fact. A post-filter and a counter-only lead
    /// are redundant (measured: either alone keeps every other test here green), and the filter is
    /// the worse of the two, because <c>topk</c> orders the broken-down series by their peak before
    /// any filter could run — so a pod whose only large reading sat in a lead bucket could displace
    /// a pod that was larger inside the range.</para>
    /// </summary>
    [Fact]
    public async Task A_gauge_window_is_not_widened_so_nothing_is_plotted_before_the_range()
    {
        await SeedAsync(T0.AddSeconds(-30), 999);
        await SeedAsync(T0, 10);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        List<TimeSeriesDataPoint> points =
            (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets)).Data!;

        points.Should().ContainSingle();
        points[0].Timestamp.Should().Be(T0);
        points[0].Value.Should().Be(10);
    }

    /// <summary>
    /// Every timestamp leaving this query is UTC, whichever provider the rows came from.
    ///
    /// <para>⚠️ <b>This is the only test that can fail when the read boundary's
    /// <c>SpecifyKind</c> is removed</b> — and that is why it exists rather than being folded
    /// into the assertions above. <c>DateTime.Equals</c> ignores <c>Kind</c>, so every
    /// <c>Timestamp.Should().Be(T0)</c> in this file passes against an Unspecified timestamp just
    /// as happily. The consequence is downstream: a chart or a serializer handed an Unspecified
    /// instant reads it as local time and slides the whole x-axis by the host's offset.</para>
    ///
    /// <para>What a provider returns differs: Npgsql gives <c>Utc</c> for a <c>timestamptz</c>,
    /// SQLite gives <c>Unspecified</c> because it stored TEXT. Stamping it here is what keeps the
    /// answer from depending on which database an installation runs.</para>
    /// </summary>
    [Fact]
    public async Task Every_timestamp_handed_back_is_utc_whatever_the_provider_stored()
    {
        await SeedAsync(T0, 10);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;
        RollupMetricsQuery q = Query();

        (await q.GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data![0].Timestamp.Kind.Should().Be(DateTimeKind.Utc);

        (await q.GetSeriesByAsync(tenantId, clusterId, "cpu", null, "pod", from, to, buckets))
            .Data![0].Points[0].Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ── A counter, and the lead bucket ──

    [Fact]
    public async Task A_counter_is_read_back_as_a_per_second_rate()
    {
        await SeedAsync(T0, 100, name: "requests_total");
        await SeedAsync(T0.AddSeconds(30), 160, name: "requests_total");
        await SeedAsync(T0.AddSeconds(60), 220, name: "requests_total");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "requests_total", null, from, to, buckets))
            .Data!.Select(p => p.Value).Should().Equal([2, 2]);
    }

    /// <summary>
    /// The reason the query reads one bucket further back than it plots.
    ///
    /// <para>A counter's first bucket has no rate, so without its predecessor the series begins
    /// one step late — and because every chart's left edge is a <c>from</c> the operator chose,
    /// the symptom is a blank first interval on every counter chart, at every range, which looks
    /// like missing data rather than like arithmetic.</para>
    /// </summary>
    [Fact]
    public async Task A_counters_first_bucket_has_a_rate_because_its_predecessor_is_read_too()
    {
        await SeedAsync(T0.AddSeconds(-30), 100, name: "requests_total");
        await SeedAsync(T0, 160, name: "requests_total");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        List<TimeSeriesDataPoint> points =
            (await Query().GetSeriesAsync(tenantId, clusterId, "requests_total", null, from, to, buckets)).Data!;

        points.Should().ContainSingle("the lead bucket is a baseline, never a point");
        points[0].Timestamp.Should().Be(T0, "and the point it enables is the first one asked for");
        points[0].Value.Should().Be(2);
    }

    [Fact]
    public async Task A_pod_restarting_does_not_corrupt_the_total_read_back()
    {
        await SeedAsync(T0, 1000, name: "requests_total", pod: "pod-a");
        await SeedAsync(T0.AddSeconds(30), 1060, name: "requests_total", pod: "pod-a");
        await SeedAsync(T0, 5000, name: "requests_total", pod: "pod-b");
        await SeedAsync(T0.AddSeconds(30), 30, name: "requests_total", pod: "pod-b");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "requests_total", null, from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(3,
                "differenced per pod first: 2/s steady plus 1/s since the restart");
    }

    // ── Which rows belong to the question ──

    [Fact]
    public async Task Rows_written_at_a_different_resolution_are_not_mixed_in()
    {
        // The resolution dial can be changed, and then the table holds both widths. Differencing
        // a counter across buckets of different sizes divides by the wrong seconds, and summing a
        // gauge across them double-counts the same instant.
        await SeedAsync(T0, 10, resolutionSeconds: 30);
        await SeedAsync(T0, 999, resolutionSeconds: 60);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query(resolutionSeconds: 30).GetSeriesAsync(
                tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    [Fact]
    public async Task Another_metric_in_the_same_window_is_not_part_of_the_answer()
    {
        await SeedAsync(T0, 10, name: "cpu");
        await SeedAsync(T0, 999, name: "memory");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    [Fact]
    public async Task A_service_filter_narrows_the_answer()
    {
        await SeedAsync(T0, 10, service: "api", pod: "pod-a");
        await SeedAsync(T0, 999, service: "worker", pod: "pod-b");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", "api", from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    [Fact]
    public async Task Rows_outside_the_window_are_not_part_of_the_answer()
    {
        await SeedAsync(T0, 10);
        await SeedAsync(T0.AddHours(1), 999);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    // ── The explorer's namespace and pod scope ──

    [Fact]
    public async Task A_namespace_scope_narrows_the_answer()
    {
        await SeedAsync(T0, 10, ns: "mine", pod: "pod-a");
        await SeedAsync(T0, 999, ns: "theirs", pod: "pod-b");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(
                tenantId, clusterId, "cpu", null, from, to, buckets, default, namespaces: ["mine"]))
            .Data.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    [Fact]
    public async Task A_pod_pattern_is_applied_as_the_prefix_match_it_means()
    {
        // DeploymentPodScope builds this for Prometheus. No provider can translate a POSIX regex
        // to SQL, and evaluating it in memory would mean loading every row the cluster has first —
        // which is the shape this store exists to avoid.
        await SeedAsync(T0, 10, pod: "web-abc123");
        await SeedAsync(T0, 999, pod: "other-xyz");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(
                tenantId, clusterId, "cpu", null, from, to, buckets, default, podPattern: "^(web)-"))
            .Data.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    [Fact]
    public async Task A_pod_pattern_this_cannot_read_scopes_nothing_rather_than_everything()
    {
        // The judgement call: too many pods in a chart is visible and the operator can narrow it,
        // whereas too few is indistinguishable from the workload having no metrics at all.
        await SeedAsync(T0, 10, pod: "web-abc123");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(
                tenantId, clusterId, "cpu", null, from, to, buckets, default,
                podPattern: ".*something[0-9]+weird"))
            .Data.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    [Theory]
    [InlineData("^(web)-", new[] { "web" })]
    [InlineData("^(web|api)-", new[] { "web", "api" })]
    [InlineData("^(my\\-app)-", new[] { "my-app" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    [InlineData("web.*", new string[0])]
    [InlineData("^(web)", new string[0])]
    public void The_prefixes_read_out_of_a_pod_pattern(string? pattern, string[] expected)
        => RollupMetricsQuery.PodPrefixes(pattern).Should().Equal(expected);

    // ── Broken down ──

    [Fact]
    public async Task A_breakdown_by_pod_keeps_each_pod_separate()
    {
        await SeedAsync(T0, 10, pod: "pod-a");
        await SeedAsync(T0, 5, pod: "pod-b");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        List<MetricSeriesGroup> groups = (await Query().GetSeriesByAsync(
            tenantId, clusterId, "cpu", null, "pod", from, to, buckets)).Data!;

        groups.Select(g => g.Name).Should().Equal(["pod-a", "pod-b"], "biggest first");
        groups[0].Points.Should().ContainSingle().Which.Value.Should().Be(10);
    }

    [Fact]
    public async Task A_breakdown_by_namespace_or_service_groups_by_that_instead()
    {
        await SeedAsync(T0, 10, ns: "ns-a", service: "svc-a", pod: "pod-a");
        await SeedAsync(T0, 5, ns: "ns-a", service: "svc-b", pod: "pod-b");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;
        RollupMetricsQuery q = Query();

        List<MetricSeriesGroup> byNamespace = (await q.GetSeriesByAsync(
            tenantId, clusterId, "cpu", null, "namespace", from, to, buckets)).Data!;

        byNamespace.Should().ContainSingle("both pods are in one namespace");
        byNamespace[0].Name.Should().Be("ns-a");
        byNamespace[0].Points[0].Value.Should().Be(15);

        List<MetricSeriesGroup> byService = (await q.GetSeriesByAsync(
            tenantId, clusterId, "cpu", null, "service", from, to, buckets)).Data!;

        byService.Select(g => g.Name).Should().Equal(["svc-a", "svc-b"]);
    }

    [Fact]
    public async Task An_unknown_breakdown_dimension_falls_back_to_pod_as_the_promql_builder_does()
    {
        await SeedAsync(T0, 10, pod: "pod-a");
        await SeedAsync(T0, 5, pod: "pod-b");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesByAsync(
                tenantId, clusterId, "cpu", null, "something-else", from, to, buckets))
            .Data!.Select(g => g.Name).Should().Equal(["pod-a", "pod-b"]);
    }

    [Fact]
    public async Task A_breakdown_keeps_only_the_biggest_series_asked_for()
    {
        await SeedAsync(T0, 1, pod: "a-tiny");
        await SeedAsync(T0, 1000, pod: "z-huge");
        await SeedAsync(T0, 50, pod: "m-middling");

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesByAsync(
                tenantId, clusterId, "cpu", null, "pod", from, to, buckets, maxSeries: 2))
            .Data!.Select(g => g.Name).Should().Equal(["z-huge", "m-middling"]);
    }

    // ── The caller's bucket count ──

    [Fact]
    public async Task A_coarser_step_merges_stored_buckets_rather_than_returning_more_points()
    {
        for (int i = 0; i < 10; i++)
        {
            await SeedAsync(T0.AddSeconds(i * 30), 10 + i);
        }

        // Five minutes in ONE bucket: a 300-second step over ten stored 30-second ones.
        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, T0, T0.AddMinutes(5), buckets: 1))
            .Data.Should().ContainSingle().Which.Value.Should().Be(19,
                "the newest stored reading in the merged bucket");
    }

    [Fact]
    public async Task A_finer_step_than_the_store_holds_returns_what_is_stored()
    {
        // Sixty buckets over five minutes is a five-second step, and a store written at thirty
        // seconds does not have them. Returning fewer points is the honest answer; interpolating
        // would invent readings no exporter ever reported.
        await SeedAsync(T0, 10);
        await SeedAsync(T0.AddSeconds(30), 20);

        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, T0, T0.AddMinutes(5), buckets: 60))
            .Data!.Select(p => p.Value).Should().Equal([10, 20]);
    }

    /// <summary>
    /// A counter asked for at a FINER step than the store holds still has its first point.
    ///
    /// <para>Written because a removal check found the clause it covers unguarded: every other
    /// test here passed with <c>Math.Max(StepFor(…), Resolution)</c> reduced to <c>StepFor</c>.
    /// The step is what the lead bucket is measured in, so at a five-second step the lead reaches
    /// back five seconds — past nothing — and the thirty-second predecessor is never read. The
    /// counter is then left with a single bucket, which has no rate, and the whole series comes
    /// back EMPTY. Sixty buckets over five minutes is the explorer's own default, so this is the
    /// ordinary case rather than an edge one.</para>
    /// </summary>
    [Fact]
    public async Task A_counter_at_a_finer_step_than_the_store_holds_still_has_its_first_point()
    {
        await SeedAsync(T0.AddSeconds(-30), 100, name: "requests_total");
        await SeedAsync(T0, 160, name: "requests_total");

        // buckets: 60 over five minutes — a five-second step, against a store written at thirty.
        (await Query().GetSeriesAsync(
                tenantId, clusterId, "requests_total", null, T0, T0.AddMinutes(5), buckets: 60))
            .Data.Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Fact]
    public async Task A_series_is_ordered_oldest_first()
    {
        await SeedAsync(T0.AddSeconds(60), 30);
        await SeedAsync(T0, 10);
        await SeedAsync(T0.AddSeconds(30), 20);

        (DateTime from, DateTime to, int buckets) = FiveMinutes;

        (await Query().GetSeriesAsync(tenantId, clusterId, "cpu", null, from, to, buckets))
            .Data!.Select(p => p.Value).Should().Equal([10, 20, 30]);
    }
}
