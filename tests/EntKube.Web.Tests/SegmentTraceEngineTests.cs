using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>
/// The Lucene/S3 telemetry segment engine (spans/traces): verifies <see cref="SegmentTraceService"/> over
/// <see cref="SpanSegmentManager"/> returns the same DTOs as the old Postgres trace path — service list,
/// trace summaries (wall-clock duration, error/span counts, root span), the waterfall, RED aggregates,
/// the service-map self-join, and per-service stats — and that spans round-trip through a sealed segment.
/// </summary>
public sealed class SegmentTraceEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly ClusterTenantResolver _resolver;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<SegmentManagerRegistry<SpanSegmentManager>> _registries = [];
    private SegmentManagerRegistry<SpanSegmentManager>? _registry;
    private SegmentManagerRegistry<TraceSummarySegmentManager>? _traceReg;
    private readonly List<string> _tempDirs = [];

    public SegmentTraceEngineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        Guid envId = Guid.NewGuid();
        _context.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", Slug = "acme" });
        _context.Environments.Add(new Environment { Id = envId, TenantId = _tenantId, Name = "prod" });
        _context.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = _clusterId, TenantId = _tenantId, EnvironmentId = envId,
            Name = "c1", ApiServerUrl = "https://k8s.example.com",
        });
        _context.SaveChanges();

        _factory = new TestDbContextFactory(_connection);
        _resolver = new ClusterTenantResolver(_factory);
    }

    private SpanSegmentManager NewManager(int retentionDays = 90, int maxCachedReaders = 256)
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "spantest-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dataPath);
        string blobsDir = Path.Combine(dataPath, "blobs");
        Directory.CreateDirectory(blobsDir);
        var options = new SegmentEngineOptions
        {
            DataPath = dataPath, RetentionDays = retentionDays, MaxCachedReaders = maxCachedReaders,
        };
        var store = new LocalSegmentBlobStore(blobsDir);
        _registry = new SegmentManagerRegistry<SpanSegmentManager>(tid =>
            new SpanSegmentManager(tid, _factory, store, options, NullLogger<SpanSegmentManager>.Instance));
        _registries.Add(_registry);
        return _registry.For(_tenantId);
    }

    // A SegmentTraceService with an (empty, unless fed) trace-summary registry. These tests write spans
    // directly to the span manager, so the traces index stays empty → the list routes to the span path.
    private SegmentTraceService TraceSvc()
    {
        if (_traceReg is null)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "tracesum-" + Guid.NewGuid().ToString("N"));
            _tempDirs.Add(dataPath);
            string blobsDir = Path.Combine(dataPath, "blobs");
            Directory.CreateDirectory(blobsDir);
            var options = new SegmentEngineOptions { DataPath = dataPath };
            var store = new LocalSegmentBlobStore(blobsDir);
            _traceReg = new SegmentManagerRegistry<TraceSummarySegmentManager>(tid =>
                new TraceSummarySegmentManager(tid, _factory, store, options, NullLogger<TraceSummarySegmentManager>.Instance));
        }
        return new SegmentTraceService(_registry!, _traceReg, _factory, _resolver, NullLogger<SegmentTraceService>.Instance);
    }

    private static SpanIngestRecord Span(
        DateTime ts, string trace, string span, string? parent, string name, string service,
        short kind, double dur, short status)
        => new(ts, trace, span, parent, name, service, kind, dur, status, "prod", "pod-1", null);

    // A representative topology: trace t1 = frontend(SERVER) -> backend(CLIENT, errors);
    // trace t2 = a second frontend request. All recent so the 24h-bounded service list sees them.
    private (SpanSegmentManager mgr, DateTime from, DateTime to, SegmentTraceService svc) Scenario()
    {
        SpanSegmentManager mgr = NewManager();
        DateTime t0 = DateTime.UtcNow.AddMinutes(-5);
        mgr.WriteSpans(_tenantId, _clusterId,
        [
            Span(t0, "t1", "s1", null, "GET /", "frontend", 2, 100, 0),
            Span(t0.AddMilliseconds(10), "t1", "s2", "s1", "call-backend", "backend", 3, 60, 2),
            Span(t0.AddSeconds(1), "t2", "s3", null, "GET /home", "frontend", 2, 200, 0),
        ]);
        var svc = TraceSvc();
        return (mgr, t0.AddMinutes(-1), t0.AddMinutes(5), svc);
    }

    [Fact]
    public async Task Services_And_ListTraces_And_Waterfall()
    {
        var (_, from, to, svc) = Scenario();

        (await svc.GetServicesAsync(_clusterId)).Data.Should().BeEquivalentTo(["backend", "frontend"]);

        var traces = await svc.ListTracesAsync(_clusterId, service: null, from, to);
        traces.IsSuccess.Should().BeTrue();
        traces.Data!.Should().HaveCount(2);
        TraceSummary t1 = traces.Data!.Single(t => t.TraceId == "t1");
        t1.SpanCount.Should().Be(2);
        t1.ErrorCount.Should().Be(1);
        t1.RootService.Should().Be("frontend");
        t1.RootName.Should().Be("GET /");
        t1.DurationMs.Should().BeApproximately(100, 0.5); // wall-clock: max end (root s1: 0+100) - min start (0)

        var waterfall = await svc.GetTraceAsync(_clusterId, "t1");
        waterfall.Data!.Select(s => s.SpanId).Should().ContainInOrder("s1", "s2");
    }

    [Fact]
    public async Task ListTraces_FilteredByService_ReturnsFullTraceSummaries()
    {
        // Two-phase path (service selected, no per-trace filter): phase 1 finds traces involving
        // "backend"; phase 2 must load the WHOLE trace so the summary sees the frontend root too.
        var (_, from, to, svc) = Scenario();

        var traces = await svc.ListTracesAsync(_clusterId, service: "backend", from, to);
        traces.IsSuccess.Should().BeTrue();
        TraceSummary t1 = traces.Data!.Should().ContainSingle(t => t.TraceId == "t1").Subject;
        t1.SpanCount.Should().Be(2);            // both spans, not just the backend one
        t1.RootService.Should().Be("frontend"); // root came from the non-filtered service
        t1.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task ServiceMap_JoinsChildToParentAcrossServices()
    {
        var (_, from, to, svc) = Scenario();

        var map = await svc.GetServiceMapAsync(_clusterId, from, to);
        map.IsSuccess.Should().BeTrue();
        ServiceEdge edge = map.Data!.Should().ContainSingle().Subject;
        edge.From.Should().Be("frontend");
        edge.To.Should().Be("backend");
        edge.Calls.Should().Be(1);
        edge.Errors.Should().Be(1);
        edge.AvgMs.Should().BeApproximately(60, 0.5);
    }

    [Fact]
    public async Task ServiceStats_And_Red_OverInboundSpans()
    {
        var (_, from, to, svc) = Scenario();

        var stats = await svc.GetServiceStatsAsync(_clusterId, "frontend", from, to);
        stats.IsSuccess.Should().BeTrue();
        stats.Data!.Count.Should().Be(2);   // s1 + s3, both SERVER (inbound)
        stats.Data!.Errors.Should().Be(0);
        stats.Data!.P95Ms.Should().Be(200);  // percentile_disc(0.95) of {100,200}

        var red = await svc.GetServiceRedAsync(_clusterId, "frontend", from, to, buckets: 12);
        red.IsSuccess.Should().BeTrue();
        red.Data!.Sum(b => b.Count).Should().Be(2);
        red.Data!.Sum(b => b.Errors).Should().Be(0);
    }

    [Fact]
    public async Task Seal_ThenQuery_RoundTripsSpans()
    {
        var (mgr, from, to, svc) = Scenario();

        TelemetrySegment? sealed1 = await mgr.RollAndSealAsync();
        sealed1.Should().NotBeNull();
        sealed1!.Signal.Should().Be("spans");
        sealed1.DocCount.Should().Be(3);

        await using (ApplicationDbContext db = _factory.CreateDbContext())
            (await db.TelemetrySegments.CountAsync(s => s.Signal == "spans")).Should().Be(1);

        // Spans now live only in the sealed segment; the waterfall must fetch it back.
        var waterfall = await svc.GetTraceAsync(_clusterId, "t1");
        waterfall.Data!.Should().HaveCount(2);

        var traces = await svc.ListTracesAsync(_clusterId, service: "backend", from, to);
        traces.Data!.Should().ContainSingle(t => t.TraceId == "t1");
    }

    [Fact]
    public async Task ReaderCache_ServesRepeatQueries_AndEvictsOnRetention()
    {
        SpanSegmentManager mgr = NewManager(retentionDays: 7);
        DateTime old = DateTime.UtcNow.AddDays(-20);   // beyond the manager's 7-day retention
        mgr.WriteSpans(_tenantId, _clusterId,
        [
            Span(old, "t1", "s1", null, "GET /", "frontend", 2, 100, 0),
            Span(old.AddMilliseconds(10), "t1", "s2", "s1", "call-backend", "backend", 3, 60, 2),
        ]);
        var svc = TraceSvc();

        await mgr.RollAndSealAsync();
        DateTime from = old.AddMinutes(-1), to = old.AddMinutes(5);

        // First query opens + caches the sealed reader; the second must hit the cache with the same result.
        var q1 = await svc.ListTracesAsync(_clusterId, service: null, from, to);
        var q2 = await svc.ListTracesAsync(_clusterId, service: null, from, to);
        q1.Data!.Should().ContainSingle(t => t.TraceId == "t1");
        q2.Data!.Should().ContainSingle(t => t.TraceId == "t1");

        // Retention drops the 20-day-old segment; its cached reader is evicted without breaking anything.
        (await mgr.DropExpiredAsync()).Should().Be(1);

        var q3 = await svc.ListTracesAsync(_clusterId, service: null, from, to);
        q3.IsSuccess.Should().BeTrue();
        q3.Data!.Should().BeEmpty();
    }

    [Fact]
    public async Task ReaderCache_UnderTinyCap_ReopensAndReturnsAllData()
    {
        // Cap of 1 open reader, but three sealed segments — eviction + reopen must not drop data.
        SpanSegmentManager mgr = NewManager(maxCachedReaders: 1);
        DateTime t0 = DateTime.UtcNow.AddMinutes(-30);
        for (int i = 0; i < 3; i++)
        {
            mgr.WriteSpans(_tenantId, _clusterId,
                [Span(t0.AddMinutes(i), $"t{i}", "s", null, "op", "svc", 2, 10, 0)]);
            await mgr.RollAndSealAsync();
        }
        var svc = TraceSvc();
        DateTime from = t0.AddMinutes(-1), to = t0.AddMinutes(10);

        var q1 = await svc.ListTracesAsync(_clusterId, service: null, from, to);
        q1.Data!.Select(t => t.TraceId).Should().BeEquivalentTo(["t0", "t1", "t2"]);

        // Repeat to exercise the reopen-after-eviction path under the cap.
        var q2 = await svc.ListTracesAsync(_clusterId, service: null, from, to);
        q2.Data!.Should().HaveCount(3);
    }

    public void Dispose()
    {
        foreach (SegmentManagerRegistry<SpanSegmentManager> r in _registries) r.Dispose();
        _traceReg?.Dispose();
        _context.Dispose();
        _connection.Dispose();
        foreach (string d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* temp */ }
    }
}
