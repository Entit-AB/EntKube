using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// What an indexer knows about its unsealed data after a restart.
///
/// The seal triggers — doc count, time bounds, and the age of the active index — used to live only in the
/// writing process. A restart reopened an index holding millions of documents and reported a count of
/// zero, which made <c>HasData</c> false, which made <c>RollAndSealAsync</c> return before it looked at
/// anything. The active index could then never be sealed again by any trigger: it just grew. On a pod
/// restarting more often than its roll interval this compounds without limit — 11 GB of unsealed index and
/// an empty segment catalog, on a volume that was never supposed to hold more than an hour of data.
///
/// Restarting is modelled the way it actually happens: dispose the manager and construct a new one over
/// the same data path, which is exactly what a new pod does with the same PersistentVolume.
/// </summary>
public sealed class SegmentRestartRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly ISegmentCatalog _catalog;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<SegmentManagerRegistry<LogSegmentManager>> _registries = [];
    private readonly List<string> _tempDirs = [];

    public SegmentRestartRecoveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _catalog = new EfSegmentCatalog(new TestDbContextFactory(_connection));
    }

    private static LogIngestRecord Log(DateTime ts, string body)
        => new(ts, "prod", "pod-a", "app", (short)LogLevel.Error, body, null, null);

    private string NewDataPath()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "restarttest-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void An_indexer_that_restarts_still_knows_it_has_unsealed_data()
    {
        string dataPath = NewDataPath();
        SegmentEngineOptions options = new() { DataPath = dataPath };

        LogSegmentManager first = NewManager(options);
        first.WriteLogs(_tenantId, _clusterId, [
            Log(DateTime.UtcNow.AddMinutes(-30), "before the restart"),
            Log(DateTime.UtcNow.AddMinutes(-20), "also before the restart"),
        ]);
        first.Dispose();

        LogSegmentManager restarted = NewManager(options);

        restarted.ActiveDocCount.Should().Be(2,
            "the documents are on the volume — a count kept only in the previous process is not a fact about the index");
    }

    [Fact]
    public async Task An_indexer_that_restarts_can_still_seal_what_it_already_holds()
    {
        // The consequence that mattered: a doc count of zero made HasData false, and RollAndSealAsync
        // returned null before touching anything. The unsealed index then never reached object storage.
        string dataPath = NewDataPath();
        SegmentEngineOptions options = new() { DataPath = dataPath };

        LogSegmentManager first = NewManager(options);
        first.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow.AddMinutes(-10), "written before the restart")]);
        first.Dispose();

        LogSegmentManager restarted = NewManager(options);
        TelemetrySegment? sealed_ = await restarted.RollAndSealAsync();

        sealed_.Should().NotBeNull("data that survived on the volume must still be sealable");
        sealed_!.DocCount.Should().Be(1);

        IReadOnlyList<TelemetrySegment> cataloged = await _catalog.ListOverlappingAsync(_tenantId, "logs", null, null);
        cataloged.Should().HaveCount(1, "and it must reach the catalog, or it is invisible to every query path");
    }

    [Fact]
    public void The_age_trigger_survives_a_restart()
    {
        // Measured from the process, the age of the active index returns to zero on every start — so a pod
        // restarting more often than segmentMaxAgeMinutes can never seal by age, however old its data is.
        string dataPath = NewDataPath();
        SegmentEngineOptions options = new() { DataPath = dataPath };

        LogSegmentManager first = NewManager(options);
        first.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow.AddHours(-3), "three hours old")]);
        first.Dispose();

        LogSegmentManager restarted = NewManager(options);

        restarted.ActiveAge.Should().BeGreaterThan(TimeSpan.FromHours(2),
            "the data is three hours old regardless of when this process started");
    }

    [Fact]
    public async Task Recovered_time_bounds_match_what_was_written()
    {
        string dataPath = NewDataPath();
        SegmentEngineOptions options = new() { DataPath = dataPath };
        DateTime oldest = DateTime.UtcNow.AddMinutes(-45);

        LogSegmentManager first = NewManager(options);
        first.WriteLogs(_tenantId, _clusterId, [
            Log(oldest, "oldest"),
            Log(DateTime.UtcNow.AddMinutes(-5), "newest"),
        ]);
        first.Dispose();

        LogSegmentManager restarted = NewManager(options);

        // The bounds become the sealed segment's catalog entry, so a wrong one makes the segment
        // unfindable by time range — a subtler loss than not sealing at all.
        restarted.ActiveMinTs.Should().BeCloseTo(oldest, TimeSpan.FromSeconds(1));

        TelemetrySegment? s = await restarted.RollAndSealAsync();
        s!.MinTs.Should().BeCloseTo(oldest, TimeSpan.FromSeconds(1));
        s.MaxTs.Should().BeAfter(s.MinTs);
    }

    [Fact]
    public async Task Data_left_in_the_other_active_directory_is_not_orphaned()
    {
        // A roll ping-pongs A→B. Restarting always at A meant anything left in B was on the volume,
        // charged against it, and invisible: never queried, never sealed.
        string dataPath = NewDataPath();
        SegmentEngineOptions options = new() { DataPath = dataPath };

        LogSegmentManager first = NewManager(options);
        first.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow.AddMinutes(-40), "into A")]);
        await first.RollAndSealAsync();                       // seals A, swaps the active index to B
        first.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow.AddMinutes(-10), "into B")]);
        first.Dispose();

        LogSegmentManager restarted = NewManager(options);

        restarted.ActiveDocCount.Should().Be(1, "the unsealed document lives in B, which is where the restart must resume");

        TelemetrySegment? s = await restarted.RollAndSealAsync();
        s.Should().NotBeNull();
        s!.DocCount.Should().Be(1);
    }

    public void Dispose()
    {
        foreach (SegmentManagerRegistry<LogSegmentManager> r in _registries) r.Dispose();
        _context.Dispose();
        _connection.Dispose();
        foreach (string dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
