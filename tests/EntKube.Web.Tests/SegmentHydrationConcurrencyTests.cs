using EntKube.Telemetry;
using EntKube.Web.Services;
using EntKube.Web.Data;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// How many cold segments a query fetches from object storage at once.
///
/// <para><b>The defect.</b> A query opens a reader over EVERY segment overlapping its window
/// before it reads a document, and a segment outside the warm tier must be downloaded and
/// unpacked first. That was a sequential <c>foreach … await</c>, so the round-trip was paid once
/// per segment, in series. Segments roll hourly, so a week-old log window is a hundred-odd of
/// them: the query took a hundred round-trips where it needed about a dozen.</para>
///
/// <para>⚠️ From the outside this looks exactly like a lost index — the search itself was always
/// fast, and nothing in the logs said "I am downloading 140 segments one at a time".</para>
///
/// <para><b>Why these tests assert on observed CONCURRENCY and not on elapsed time.</b> A
/// wall-clock assertion would be the obvious way to write this and the wrong one: it measures the
/// machine as much as the code, and this repo has already had a timing test report a 13×
/// regression that turned out to be two VMs and a Spotlight re-index. Peak concurrency is the
/// property the change is actually about, and it is the same number on a busy machine as on an
/// idle one. If hydration is sequential the peak is 1, whatever the hardware is doing.</para>
/// </summary>
public sealed class SegmentHydrationConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly ISegmentCatalog _catalog;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<string> _tempDirs = [];

    public SegmentHydrationConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _catalog = new EfSegmentCatalog(new TestDbContextFactory(_connection));
    }

    /// <summary>
    /// Counts downloads and records the peak number running at once. The small delay is what makes
    /// overlap observable at all: without it each download finishes before the next starts even
    /// when they are issued concurrently, and the peak would read 1 for a correct implementation.
    /// </summary>
    private sealed class CountingBlobStore(ISegmentBlobStore inner, TimeSpan delay) : ISegmentBlobStore
    {
        private int _inFlight;
        private int _peak;

        public int Gets;

        public int Peak => Volatile.Read(ref _peak);

        /// <summary>Mirrors the wrapped store: the warm tier is only exercised when it is on.</summary>
        public bool IsConfigured => inner.IsConfigured;

        public Task PutAsync(string key, string localFilePath, CancellationToken ct = default)
            => inner.PutAsync(key, localFilePath, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => inner.DeleteAsync(key, ct);

        public async Task GetAsync(string key, string destFilePath, CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref _inFlight);

            // Compare-and-swap rather than a plain assignment: two downloads can observe their own
            // count simultaneously, and a lost update here would under-report the peak — which
            // would make a sequential implementation look parallel.
            int seen = Volatile.Read(ref _peak);
            while (now > seen && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
                seen = Volatile.Read(ref _peak);
            }

            try
            {
                await Task.Delay(delay, ct);
                await inner.GetAsync(key, destFilePath, ct);
                Interlocked.Increment(ref Gets);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static LogIngestRecord Log(DateTime ts, string body)
        => new(ts, "prod", "pod-a", "app", (short)EntKube.Web.Services.LogLevel.Error, body, null, null);

    private string NewDataPath()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "hydrate-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dataPath);
        Directory.CreateDirectory(Path.Combine(dataPath, "blobs"));
        return dataPath;
    }

    /// <summary>Seals <paramref name="count"/> one-record segments and evicts every one of them.</summary>
    private async Task<(LogSegmentManager Manager, CountingBlobStore Store, DateTime From, DateTime To)>
        ColdSegmentsAsync(int count, int hydrationConcurrency)
    {
        string dataPath = NewDataPath();
        CountingBlobStore store = new(
            new LocalSegmentBlobStore(Path.Combine(dataPath, "blobs")),
            TimeSpan.FromMilliseconds(40));

        SegmentEngineOptions options = new()
        {
            DataPath = dataPath,
            RetentionDays = 90,
            WarmRetentionDays = 0,                            // nothing stays local
            MaxHydrationConcurrency = hydrationConcurrency,
        };

        LogSegmentManager mgr = new(
            _tenantId, _catalog, store, options, NullLogger<LogSegmentManager>.Instance);

        DateTime newest = DateTime.UtcNow.AddMinutes(-1);

        for (int i = 0; i < count; i++)
        {
            mgr.WriteLogs(_tenantId, _clusterId, [Log(newest.AddMinutes(-i), $"seg {i}")]);
            await mgr.RollAndSealAsync();
        }

        await mgr.TrimWarmTierAsync();
        store.Gets = 0;   // ignore anything the seal/trim path did; count only the query's fetches

        return (mgr, store, newest.AddMinutes(-count - 5), newest.AddMinutes(5));
    }

    /// <summary>
    /// ⭐ The fix: a query over many cold segments fetches them concurrently.
    ///
    /// <para>Asserted as "peak &gt; 1", which is the smallest claim that distinguishes the two
    /// implementations — a sequential loop cannot reach 2 no matter how slow the store is. The
    /// upper bound is asserted in the same breath, because unbounded parallelism here would be its
    /// own defect: these downloads unpack onto the disk the volume guard is watching.</para>
    /// </summary>
    [Fact]
    public async Task A_query_over_cold_segments_fetches_them_concurrently()
    {
        (LogSegmentManager mgr, CountingBlobStore store, DateTime from, DateTime to) =
            await ColdSegmentsAsync(count: 12, hydrationConcurrency: 8);

        int docs = await mgr.QueryAsync(from, to, s => s.IndexReader.NumDocs);

        docs.Should().Be(12, "every sealed segment must still be in the answer — a faster wrong "
            + "answer is not the goal");

        store.Gets.Should().Be(12, "each cold segment is fetched exactly once, so the parallelism "
            + "is not hiding duplicated work");

        store.Peak.Should().BeGreaterThan(1,
            "a sequential foreach-await cannot have two downloads in flight at once, whatever the "
            + "hardware is doing — this is the assertion the whole change rests on");

        store.Peak.Should().BeLessThanOrEqualTo(8,
            "and it must stay bounded: unpacking an unbounded burst targets the same disk the "
            + "volume guard reclaims at 70%");
    }

    /// <summary>
    /// The bound is the option, not a constant — and setting it to 1 restores exactly the old
    /// sequential behaviour, which is the escape hatch the option's doc promises.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task The_concurrency_never_exceeds_the_configured_limit(int limit)
    {
        (LogSegmentManager mgr, CountingBlobStore store, DateTime from, DateTime to) =
            await ColdSegmentsAsync(count: 9, hydrationConcurrency: limit);

        int docs = await mgr.QueryAsync(from, to, s => s.IndexReader.NumDocs);

        docs.Should().Be(9);
        store.Peak.Should().BeLessThanOrEqualTo(limit);
    }

    /// <summary>
    /// ⚠️ A failed download must not be cached as a failure.
    ///
    /// <para>The acquire path removes a faulted entry so the next query retries, and the parallel
    /// version has to keep that true while also releasing the siblings that already succeeded.
    /// This asserts the recoverable half — the query fails, and a later query over the same window
    /// succeeds once the store is healthy. A leaked reference is not directly observable from
    /// here; what is observable is that the engine is still usable afterwards, which a poisoned
    /// cache entry would prevent.</para>
    /// </summary>
    [Fact]
    public async Task A_failed_fetch_leaves_the_engine_usable()
    {
        (LogSegmentManager mgr, CountingBlobStore store, DateTime from, DateTime to) =
            await ColdSegmentsAsync(count: 6, hydrationConcurrency: 4);

        string blobs = Path.Combine(_tempDirs[^1], "blobs");
        // Keys contain '/', which LocalSegmentBlobStore turns into directories, so the archives are
        // nested rather than flat — the first version of this test looked only at the top level and
        // found nothing, which asserted its way to a failure rather than a false pass.
        string[] archives = Directory.GetFiles(blobs, "*", SearchOption.AllDirectories);
        archives.Should().NotBeEmpty("the sealed archives must be there to be broken");

        string hidden = archives[0] + ".moved";
        File.Move(archives[0], hidden);

        Func<Task> firstTry = () => mgr.QueryAsync(from, to, s => s.IndexReader.NumDocs);
        await firstTry.Should().ThrowAsync<Exception>(
            "a segment the catalog lists but object storage cannot supply is a real failure and "
            + "must not be silently answered with a partial result");

        File.Move(hidden, archives[0]);

        int docs = await mgr.QueryAsync(from, to, s => s.IndexReader.NumDocs);
        docs.Should().Be(6, "the failed open must not have been cached, or this would keep failing");
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();

        foreach (string dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* a reader may still hold a handle; the temp dir is disposable either way */ }
        }
    }
}
