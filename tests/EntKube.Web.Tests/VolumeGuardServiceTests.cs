using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The disk watermark, end to end: what the guard actually DOES when the volume it measures is over the
/// line, as opposed to what <see cref="VolumeBudget"/> decides it should do.
///
/// <para>The step these exist for is sealing under pressure — "when the disk approaches 70%, offload the
/// oldest records onto the appointed object storage" (docs/telemetry-central-again.md). Before it, the
/// guard could only evict copies of segments that were ALREADY sealed; an active index full of the very
/// records it was supposed to offload was untouchable, and the guard's own log told the operator to go
/// and lower the roll thresholds by hand.</para>
///
/// <para>The measurement is the only thing faked: a test cannot fill a real volume. Everything the
/// measurement drives — the ordering, the sealing, the upload, the eviction — runs for real against a
/// Lucene index and a blob store on disk.</para>
/// </summary>
public sealed class VolumeGuardServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly ISegmentCatalog _catalog;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<SegmentManagerRegistry<LogSegmentManager>> _registries = [];
    private readonly List<string> _tempDirs = [];

    public VolumeGuardServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _catalog = new EfSegmentCatalog(new TestDbContextFactory(_connection));
    }

    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>A volume at <paramref name="usedPercent"/> of 20 GiB — the one fact the guard cannot measure here.</summary>
    private static VolumeState Disk(double usedPercent)
    {
        const long total = 20 * Gb;
        return new VolumeState(total, total - (long)(total * usedPercent / 100d));
    }

    private static LogIngestRecord Log(DateTime ts, string body)
        => new(ts, "prod", "pod-a", "app", (short)LogLevel.Error, body, null, null);

    private string NewDataPath()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "volguard-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dataPath);
        Directory.CreateDirectory(Path.Combine(dataPath, "blobs"));
        return dataPath;
    }

    private (VolumeGuardService Guard, LogSegmentManager Manager, string BlobDir) Build(
        SegmentEngineOptions options, Func<VolumeState> measure)
    {
        string blobDir = Path.Combine(options.DataPath, "blobs");
        var store = new LocalSegmentBlobStore(blobDir);
        var registry = new SegmentManagerRegistry<LogSegmentManager>(tid =>
            new LogSegmentManager(tid, _catalog, store, options, NullLogger<LogSegmentManager>.Instance));
        _registries.Add(registry);

        LogSegmentManager manager = registry.For(_tenantId);

        return (new VolumeGuardService(
            [registry], options, NullLogger<VolumeGuardService>.Instance, measure), manager, blobDir);
    }

    private async Task<int> SealedSegmentCountAsync() =>
        (await _catalog.ListOverlappingAsync(_tenantId, "logs", null, null, default)).Count;

    [Fact]
    public async Task A_volume_over_the_mark_seals_the_active_index_into_object_storage()
    {
        SegmentEngineOptions options = new() { DataPath = NewDataPath(), VolumeHighWaterPercent = 70 };
        (VolumeGuardService guard, LogSegmentManager manager, string blobDir) = Build(options, () => Disk(90));

        manager.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow, "unsealed and taking up room")]);
        manager.ActiveDocCount.Should().BeGreaterThan(0, "the records start out in the active index");
        (await SealedSegmentCountAsync()).Should().Be(0);

        await guard.ReclaimOnceAsync();

        // The records are now an archive in the assigned storage, which is what "offload to S3" means
        // here, and the catalogue knows about it so they are still queryable.
        (await SealedSegmentCountAsync()).Should().Be(1);
        Directory.GetFiles(blobDir, "*", SearchOption.AllDirectories).Should().NotBeEmpty();
        manager.ActiveDocCount.Should().Be(0, "the active index was rolled, not copied");
    }

    [Fact]
    public async Task A_volume_with_room_to_spare_is_left_entirely_alone()
    {
        SegmentEngineOptions options = new() { DataPath = NewDataPath(), VolumeHighWaterPercent = 70 };
        (VolumeGuardService guard, LogSegmentManager manager, _) = Build(options, () => Disk(40));

        manager.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow, "nowhere near the mark")]);
        long before = manager.ActiveDocCount;

        await guard.ReclaimOnceAsync();

        // Sealing early would quietly replace the size and age roll policy with "whenever the guard
        // looked", and fill object storage with small segments nobody asked for.
        manager.ActiveDocCount.Should().Be(before);
        (await SealedSegmentCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unmeasurable_volume_changes_nothing()
    {
        // A failed stat reports zero bytes. Reading that as "full" would seal — or worse, delete — on
        // the strength of a syscall that did not work.
        SegmentEngineOptions options = new() { DataPath = NewDataPath(), VolumeHighWaterPercent = 70 };
        (VolumeGuardService guard, LogSegmentManager manager, _) = Build(options, () => new VolumeState(0, 0));

        manager.WriteLogs(_tenantId, _clusterId, [Log(DateTime.UtcNow, "still here")]);
        long before = manager.ActiveDocCount;

        await guard.ReclaimOnceAsync();

        manager.ActiveDocCount.Should().Be(before);
        (await SealedSegmentCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sealing_under_pressure_keeps_the_records_readable()
    {
        // The whole promise of tiering rather than deleting: a segment that moved to the cold tier is
        // still part of every answer. If this ever failed, the guard would be silently destroying
        // telemetry while reporting that it had offloaded it.
        SegmentEngineOptions options = new() { DataPath = NewDataPath(), VolumeHighWaterPercent = 70 };
        (VolumeGuardService guard, LogSegmentManager manager, _) = Build(options, () => Disk(95));

        DateTime ts = DateTime.UtcNow;
        manager.WriteLogs(_tenantId, _clusterId, [Log(ts, "find me after the offload")]);

        await guard.ReclaimOnceAsync();

        (await SealedSegmentCountAsync()).Should().Be(1, "precondition: it was sealed");

        // Asked of the SEALED tier specifically: answering from the active index would prove nothing,
        // since the point is that the records left it.
        bool found = await manager.AnyAsync(
            SegmentScope.Sealed, ts.AddMinutes(-5), ts.AddMinutes(5),
            searcher => searcher.IndexReader.NumDocs > 0);

        found.Should().BeTrue("an offloaded segment is tiered, not archived away");
    }

    [Fact]
    public async Task Nothing_to_seal_and_nothing_to_evict_is_not_an_error()
    {
        // An empty store over the mark: the disk is full of something else. The guard has to come back
        // having done nothing rather than loop, throw, or delete the one thing it can see.
        SegmentEngineOptions options = new() { DataPath = NewDataPath(), VolumeHighWaterPercent = 70 };
        (VolumeGuardService guard, _, _) = Build(options, () => Disk(99));

        await guard.Invoking(g => g.ReclaimOnceAsync()).Should().NotThrowAsync();
        (await SealedSegmentCountAsync()).Should().Be(0);
    }

    public void Dispose()
    {
        foreach (SegmentManagerRegistry<LogSegmentManager> registry in _registries) registry.Dispose();
        _context.Dispose();
        _connection.Dispose();

        foreach (string dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* a test temp dir that outlives the run is not a failure */ }
        }
    }
}
