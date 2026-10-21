using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Writing metric points into the rollup store.
///
/// <para>The property worth most here is that a RETRIED batch merges rather than duplicates. A
/// collector retries on 5xx and can deliver the same batch twice; two collectors can report the
/// same scope. If either produced a second row, every reading of that bucket would be doubled —
/// silently, and in proportion to how unreliable the network was.</para>
/// </summary>
public sealed class MetricRollupWriterTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    public MetricRollupWriterTests()
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

    /// <summary>
    /// A writer at <paramref name="resolutionSeconds"/>. Ingest is enabled explicitly, because it
    /// is off by default until the query side is wired up — a writer that accepted metrics with
    /// nothing able to read them would fill a table for no one.
    /// </summary>
    private MetricRollupWriter Writer(int resolutionSeconds = 30, bool enabled = true)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:AcceptMetricsIngest"] = enabled ? "true" : "false",
                ["Telemetry:MetricsResolutionSeconds"] = resolutionSeconds.ToString(),
            })
            .Build();

        return new MetricRollupWriter(factory, config, NullLogger<MetricRollupWriter>.Instance);
    }

    private static MetricPoint P(DateTime at, double value, string name = "cpu", string? pod = "pod-a") =>
        new(name, "svc", "ns", pod, at, value);

    private List<MetricRollup> Rows()
    {
        // The test factory implements several IDbContextFactory<T>; say which one is wanted.
        using TelemetryDbContext db =
            ((IDbContextFactory<TelemetryDbContext>)factory).CreateDbContext();
        return [.. db.MetricRollups.OrderBy(r => r.BucketStart).ThenBy(r => r.Name)];
    }

    [Fact]
    public async Task A_batch_becomes_one_row_per_bucket()
    {
        await Writer().WriteAsync(tenantId, clusterId,
            [P(T0, 10), P(T0.AddSeconds(5), 30), P(T0.AddSeconds(45), 7)]);

        List<MetricRollup> rows = Rows();
        rows.Should().HaveCount(2, "two buckets at thirty seconds");

        rows[0].Count.Should().Be(2);
        rows[0].Sum.Should().Be(40);
        rows[0].Min.Should().Be(10);
        rows[0].Max.Should().Be(30);
        rows[0].ResolutionSeconds.Should().Be(30, "the width is stored, not assumed");
        rows[0].TenantId.Should().Be(tenantId);
        rows[0].ClusterId.Should().Be(clusterId);

        rows[1].Count.Should().Be(1);
        rows[1].Sum.Should().Be(7);
    }

    [Fact]
    public async Task The_same_batch_delivered_twice_merges_into_one_row()
    {
        // The retry case, and the one that would be invisible if it went wrong: a second row for
        // the same scope and bucket doubles every reading of it.
        MetricPoint[] batch = [P(T0, 10), P(T0.AddSeconds(5), 30)];

        MetricRollupWriter writer = Writer();
        await writer.WriteAsync(tenantId, clusterId, batch);
        await writer.WriteAsync(tenantId, clusterId, batch);

        List<MetricRollup> rows = Rows();
        rows.Should().ContainSingle("one scope, one bucket, one row");

        // Merged, so the counts add up — the store is a sum of what arrived, and a duplicate
        // delivery is indistinguishable from two real scrapes of the same value.
        rows[0].Count.Should().Be(4);
        rows[0].Sum.Should().Be(80);
        rows[0].Min.Should().Be(10);
        rows[0].Max.Should().Be(30);
    }

    [Fact]
    public async Task A_later_batch_extends_the_range_rather_than_replacing_it()
    {
        MetricRollupWriter writer = Writer();
        await writer.WriteAsync(tenantId, clusterId, [P(T0, 50)]);
        await writer.WriteAsync(tenantId, clusterId, [P(T0.AddSeconds(10), 5)]);

        MetricRollup row = Rows().Single();
        row.Min.Should().Be(5, "the lower value arrived second and is still the minimum");
        row.Max.Should().Be(50);
        row.Last.Should().Be(5, "the later delivery is the better claim on the bucket's latest");
    }

    [Fact]
    public async Task Each_scope_and_each_metric_gets_its_own_row()
    {
        await Writer().WriteAsync(tenantId, clusterId,
            [P(T0, 1, pod: "pod-a"), P(T0, 2, pod: "pod-b"), P(T0, 3, name: "memory")]);

        Rows().Should().HaveCount(3);
    }

    [Fact]
    public async Task Another_clusters_points_never_join_this_clusters_rows()
    {
        Guid otherCluster = Guid.NewGuid();

        MetricRollupWriter writer = Writer();
        await writer.WriteAsync(tenantId, clusterId, [P(T0, 10)]);
        await writer.WriteAsync(tenantId, otherCluster, [P(T0, 10)]);

        List<MetricRollup> rows = Rows();
        rows.Should().HaveCount(2, "same metric, same bucket, different cluster — different series");
        rows.Select(r => r.ClusterId).Should().BeEquivalentTo([clusterId, otherCluster]);
    }

    [Fact]
    public async Task Rows_written_at_one_resolution_are_not_merged_into_another()
    {
        // Changing the setting must not silently reinterpret history: a 30s bucket and a 300s
        // bucket starting at the same instant describe different things, and a reader that added
        // them together would be wrong with no error anywhere.
        await Writer(resolutionSeconds: 30).WriteAsync(tenantId, clusterId, [P(T0, 10)]);
        await Writer(resolutionSeconds: 300).WriteAsync(tenantId, clusterId, [P(T0, 10)]);

        List<MetricRollup> rows = Rows();
        rows.Should().HaveCount(2);
        rows.Select(r => r.ResolutionSeconds).Order().Should().Equal([30, 300]);
    }

    [Fact]
    public async Task At_the_scrape_interval_every_scrape_is_its_own_row()
    {
        // "Raw samples in Postgres", as asked for: the resolution is the dial, not the schema.
        List<MetricPoint> scrapes = [.. Enumerable.Range(0, 5).Select(i => P(T0.AddSeconds(i * 15), i))];

        await Writer(resolutionSeconds: 15).WriteAsync(tenantId, clusterId, scrapes);

        List<MetricRollup> rows = Rows();
        rows.Should().HaveCount(5);
        rows.Should().OnlyContain(r => r.Count == 1);
    }

    [Fact]
    public async Task Nothing_is_written_while_metrics_ingest_is_off()
    {
        // Off by default: the query side is not wired up, so accepting metrics would fill a table
        // nothing reads. The endpoint still answers 2xx — a collector that cannot deliver retries
        // for ever and is OOM-killed for it.
        int accepted = await Writer(enabled: false).WriteAsync(tenantId, clusterId, [P(T0, 10)]);

        accepted.Should().Be(0);
        Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_batch_touches_nothing()
    {
        (await Writer().WriteAsync(tenantId, clusterId, [])).Should().Be(0);
        Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_batch_of_only_unusable_points_writes_no_row()
    {
        // The fold drops NaN, infinities and nameless points. A batch of nothing but those must
        // not leave an empty row behind, which would read as a bucket with no observations.
        int accepted = await Writer().WriteAsync(tenantId, clusterId,
            [P(T0, double.NaN), new MetricPoint("", null, null, null, T0, 1)]);

        accepted.Should().Be(0);
        Rows().Should().BeEmpty();
    }
}
