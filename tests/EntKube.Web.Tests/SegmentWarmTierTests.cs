using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The WARM tier: sealed segments held on local disk between the hot active index and the cold archives in
/// object storage. What matters is that the tier is genuinely bounded — by age and by size — because
/// before this existed a local copy was only removed when retention deleted the segment outright, so it
/// grew to the whole retention window and could fill the volume.
///
/// The property these tests defend hardest is that eviction is <b>not</b> data loss: an evicted segment is
/// still cataloged and still in object storage, so it must still be queryable, just via a download.
/// </summary>
public sealed class SegmentWarmTierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly ISegmentCatalog _catalog;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<SegmentManagerRegistry<LogSegmentManager>> _registries = [];
    private readonly List<string> _tempDirs = [];

    public SegmentWarmTierTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _factory = new TestDbContextFactory(_connection);
        _catalog = new EfSegmentCatalog(_factory);
    }

    private static LogIngestRecord Log(DateTime ts, string body)
        => new(ts, "prod", "pod-a", "app", (short)LogLevel.Error, body, null, null);

    private string NewDataPath()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "warmtest-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dataPath);
        Directory.CreateDirectory(Path.Combine(dataPath, "blobs"));
        return dataPath;
    }

    private LogSegmentManager NewManager(SegmentEngineOptions options)
    {
        var store = new LocalSegmentBlobStore(Path.Combine(options.DataPath, "blobs"));
        var registry = new SegmentManagerRegistry<LogSegmentManager>(tid =>
            new LogSegmentManager(tid, _catalog, store, options, NullLogger<LogSegmentManager>.Instance));
        _registries.Add(registry);
        return registry.For(_tenantId);
    }

    /// <summary>Directories under the manager's warm-tier cache — the segments physically on local disk.</summary>
    private static int ResidentSegments(string dataPath, Guid tenantId)
    {
        string cacheRoot = Path.Combine(dataPath, tenantId.ToString("N"), "cache", "logs");
        return Directory.Exists(cacheRoot) ? Directory.GetDirectories(cacheRoot).Length : 0;
    }

    [Fact]
    public async Task A_freshly_sealed_segment_stays_on_local_disk()
    {
        string dataPath = NewDataPath();
        LogSegmentManager mgr = NewManager(new SegmentEngineOptions { DataPath = dataPath, WarmRetentionDays = 3 });

        mgr.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow, "recent")]);
        await mgr.RollAndSealAsync();

        // Sealing adopts the files it just wrote rather than downloading them back.
        ResidentSegments(dataPath, _tenantId).Should().Be(1);

        await mgr.TrimWarmTierAsync();
        ResidentSegments(dataPath, _tenantId).Should().Be(1, "a segment inside the warm window stays local");
    }

    [Fact]
    public async Task A_segment_older_than_the_warm_window_is_evicted_from_local_disk()
    {
        string dataPath = NewDataPath();
        LogSegmentManager mgr = NewManager(new SegmentEngineOptions
        {
            DataPath = dataPath, RetentionDays = 90, WarmRetentionDays = 3,
        });

        mgr.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow.AddDays(-30), "old")]);
        await mgr.RollAndSealAsync();
        ResidentSegments(dataPath, _tenantId).Should().Be(1);

        await mgr.TrimWarmTierAsync();

        ResidentSegments(dataPath, _tenantId).Should().Be(0, "its newest event is well past the warm window");
    }

    [Fact]
    public async Task An_evicted_segment_is_still_queryable_from_object_storage()
    {
        string dataPath = NewDataPath();
        LogSegmentManager mgr = NewManager(new SegmentEngineOptions
        {
            DataPath = dataPath, RetentionDays = 90, WarmRetentionDays = 0,   // evict everything immediately
        });

        DateTime ts = DateTime.UtcNow.AddHours(-1);
        mgr.WriteLogs(_tenantId, _clusterId, [Log(ts, "needle in the cold tier")]);
        await mgr.RollAndSealAsync();
        await mgr.TrimWarmTierAsync();

        ResidentSegments(dataPath, _tenantId).Should().Be(0, "the warm window is zero, so nothing stays local");

        // This is the whole point: eviction frees disk, it does not lose data.
        long hits = await mgr.QueryAsync(ts.AddMinutes(-5), ts.AddMinutes(5),
            searcher => searcher.IndexReader.NumDocs);
        hits.Should().Be(1, "the segment is still cataloged and still in object storage");

        ResidentSegments(dataPath, _tenantId).Should().Be(1, "querying it re-cached it locally");
    }

    [Fact]
    public async Task The_size_ceiling_evicts_even_when_everything_is_inside_the_age_window()
    {
        string dataPath = NewDataPath();
        LogSegmentManager mgr = NewManager(new SegmentEngineOptions
        {
            DataPath = dataPath,
            WarmRetentionDays = 90,   // age would keep all of them…
            WarmMaxBytes = 1,         // …but the tier is allowed one byte
        });

        for (int i = 0; i < 3; i++)
        {
            mgr.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow.AddMinutes(-i), $"seg {i}")]);
            await mgr.RollAndSealAsync();
        }
        ResidentSegments(dataPath, _tenantId).Should().Be(3);

        await mgr.TrimWarmTierAsync();

        // Least-recently-used first, until the tier fits. Nothing fits in one byte.
        ResidentSegments(dataPath, _tenantId).Should().Be(0);
    }

    [Fact]
    public async Task No_size_ceiling_means_the_age_window_alone_decides()
    {
        string dataPath = NewDataPath();
        LogSegmentManager mgr = NewManager(new SegmentEngineOptions
        {
            DataPath = dataPath, WarmRetentionDays = 90, WarmMaxBytes = 0,
        });

        mgr.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow, "recent")]);
        await mgr.RollAndSealAsync();

        await mgr.TrimWarmTierAsync();

        ResidentSegments(dataPath, _tenantId).Should().Be(1, "WarmMaxBytes=0 disables the size bound");
    }

    [Fact]
    public async Task Eviction_does_not_disturb_a_query_already_reading_the_segment()
    {
        string dataPath = NewDataPath();
        LogSegmentManager mgr = NewManager(new SegmentEngineOptions
        {
            DataPath = dataPath, RetentionDays = 90, WarmRetentionDays = 0,
        });

        DateTime ts = DateTime.UtcNow.AddHours(-1);
        mgr.WriteLogs(_tenantId, _clusterId, [Log(ts, "read me while you delete me")]);
        await mgr.RollAndSealAsync();

        // Evict from inside the query, while this searcher still holds the segment's reader open. The files
        // must survive until the reader is genuinely released, or the read below faults.
        long hits = await mgr.QueryAsync(ts.AddMinutes(-5), ts.AddMinutes(5), searcher =>
        {
            mgr.TrimWarmTierAsync().GetAwaiter().GetResult();
            return searcher.IndexReader.NumDocs;
        });

        hits.Should().Be(1);
    }

    [Fact]
    public async Task Retention_still_removes_a_segment_the_warm_tier_already_evicted()
    {
        string dataPath = NewDataPath();
        LogSegmentManager mgr = NewManager(new SegmentEngineOptions
        {
            DataPath = dataPath, RetentionDays = 7, WarmRetentionDays = 0,
        });

        mgr.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow.AddDays(-30), "ancient")]);
        await mgr.RollAndSealAsync();
        await mgr.TrimWarmTierAsync();

        (await mgr.DropExpiredAsync()).Should().Be(1);

        await using ApplicationDbContext db = _factory.CreateDbContext();
        (await db.TelemetrySegments.CountAsync()).Should().Be(0, "retention drops the catalog row too");
    }

    public void Dispose()
    {
        foreach (SegmentManagerRegistry<LogSegmentManager> r in _registries) r.Dispose();
        _context.Dispose();
        _connection.Dispose();
        foreach (string d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* temp */ }
    }
}
