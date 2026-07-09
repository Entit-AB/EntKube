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
/// The trace-SUMMARY index (the <c>traces</c> signal): per-(trace, namespace, batch) partials written from
/// span ingest and merged at query time by <see cref="SegmentTraceService.ListTracesAsync"/>. Verifies the
/// merge matches the span-grouping baseline (start/duration/spanCount/errorCount/root), across batches,
/// with de-dup of retried batches, and namespace-scoped vs whole-trace summaries — plus seal + retention.
/// </summary>
public sealed class SegmentTraceSummaryEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly ClusterTenantResolver _resolver;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<string> _tempDirs = [];
    private SegmentManagerRegistry<SpanSegmentManager>? _spanReg;
    private SegmentManagerRegistry<TraceSummarySegmentManager>? _traceReg;

    // Real data lands here; the window comfortably brackets it. A warmup partial one day earlier pushes the
    // index cutoff back so this natural window routes to the index (not the pre-index span fallback).
    private readonly DateTime _t0 = DateTime.UtcNow.AddMinutes(-5);
    private DateTime From => _t0.AddMinutes(-1);
    private DateTime To => _t0.AddMinutes(5);

    public SegmentTraceSummaryEngineTests()
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
            Id = _clusterId, TenantId = _tenantId, EnvironmentId = envId, Name = "c1",
            ApiServerUrl = "https://k8s.example.com",
        });
        _context.SaveChanges();

        _factory = new TestDbContextFactory(_connection);
        _resolver = new ClusterTenantResolver(_factory);
    }

    private static SpanIngestRecord Span(
        DateTime ts, string trace, string span, string? parent, string name, string service,
        short kind, double dur, short status, string ns = "prod")
        => new(ts, trace, span, parent, name, service, kind, dur, status, ns, "pod-1", null);

    private (TraceSummarySegmentManager mgr, SegmentTraceService svc) Setup()
    {
        string dp(string sig) => Path.Combine(Path.GetTempPath(), $"{sig}-" + Guid.NewGuid().ToString("N"));
        SegmentManagerRegistry<T> reg<T>(Func<Guid, T> f) where T : SegmentManagerBase => new(f);

        string spanPath = dp("spansum"); _tempDirs.Add(spanPath);
        string tracePath = dp("tracesum"); _tempDirs.Add(tracePath);
        Directory.CreateDirectory(Path.Combine(spanPath, "blobs"));
        Directory.CreateDirectory(Path.Combine(tracePath, "blobs"));
        var spanOpts = new SegmentEngineOptions { DataPath = spanPath };
        var traceOpts = new SegmentEngineOptions { DataPath = tracePath };
        var spanStore = new LocalSegmentBlobStore(Path.Combine(spanPath, "blobs"));
        var traceStore = new LocalSegmentBlobStore(Path.Combine(tracePath, "blobs"));

        _spanReg = reg(tid => new SpanSegmentManager(tid, _factory, spanStore, spanOpts, NullLogger<SpanSegmentManager>.Instance));
        _traceReg = reg(tid => new TraceSummarySegmentManager(tid, _factory, traceStore, traceOpts, NullLogger<TraceSummarySegmentManager>.Instance));

        TraceSummarySegmentManager mgr = _traceReg.For(_tenantId);
        // Warmup partial a day before the window → cutoff older than From → the list routes to the index.
        mgr.WriteFromSpanBatch(_tenantId, _clusterId,
            [Span(_t0.AddDays(-1), "warmup", "w", null, "warmup", "warmup-svc", 2, 1, 0)]);

        var svc = new SegmentTraceService(_spanReg, _traceReg, _factory, _resolver, NullLogger<SegmentTraceService>.Instance);
        return (mgr, svc);
    }

    [Fact]
    public async Task IndexPath_MergesSummary_MatchesBaseline()
    {
        var (mgr, svc) = Setup();
        mgr.WriteFromSpanBatch(_tenantId, _clusterId,
        [
            Span(_t0, "t1", "s1", null, "GET /", "frontend", 2, 100, 0),
            Span(_t0.AddMilliseconds(10), "t1", "s2", "s1", "call-backend", "backend", 3, 60, 2),
            Span(_t0.AddSeconds(1), "t2", "s3", null, "GET /home", "frontend", 2, 200, 0),
        ]);

        var res = await svc.ListTracesAsync(_clusterId, service: null, From, To);
        res.IsSuccess.Should().BeTrue();
        res.Data!.Should().HaveCount(2);
        TraceSummary t1 = res.Data!.Single(t => t.TraceId == "t1");
        t1.SpanCount.Should().Be(2);
        t1.ErrorCount.Should().Be(1);
        t1.RootService.Should().Be("frontend");
        t1.RootName.Should().Be("GET /");
        t1.DurationMs.Should().BeApproximately(100, 0.5);
        res.Data![0].TraceId.Should().Be("t2"); // ordered by start desc (t2 starts 1s after t1)
    }

    [Fact]
    public async Task IndexPath_MergesAcrossBatches_RootArrivesLate()
    {
        var (mgr, svc) = Setup();
        // Child span in batch 1 (no root yet), root in batch 2 — merge must reassemble the whole trace.
        mgr.WriteFromSpanBatch(_tenantId, _clusterId,
            [Span(_t0.AddMilliseconds(10), "t1", "s2", "s1", "call-backend", "backend", 3, 60, 2)]);
        mgr.WriteFromSpanBatch(_tenantId, _clusterId,
            [Span(_t0, "t1", "s1", null, "GET /", "frontend", 2, 100, 0)]);

        TraceSummary t1 = (await svc.ListTracesAsync(_clusterId, service: null, From, To)).Data!.Single();
        t1.SpanCount.Should().Be(2);
        t1.ErrorCount.Should().Be(1);
        t1.RootService.Should().Be("frontend");   // root flipped in from the 2nd batch
        t1.DurationMs.Should().BeApproximately(100, 0.5);
    }

    [Fact]
    public async Task IndexPath_DedupesRetriedBatch()
    {
        var (mgr, svc) = Setup();
        SpanIngestRecord[] batch =
        [
            Span(_t0, "t1", "s1", null, "GET /", "frontend", 2, 100, 0),
            Span(_t0.AddMilliseconds(10), "t1", "s2", "s1", "db", "backend", 3, 60, 2),
        ];
        mgr.WriteFromSpanBatch(_tenantId, _clusterId, batch);
        mgr.WriteFromSpanBatch(_tenantId, _clusterId, batch);   // OTLP 5xx retry — identical batch

        TraceSummary t1 = (await svc.ListTracesAsync(_clusterId, service: null, From, To)).Data!.Single();
        t1.SpanCount.Should().Be(2);     // NOT 4 — partial_id dedup
        t1.ErrorCount.Should().Be(1);
    }

    [Fact]
    public async Task IndexPath_NamespaceFilter_IsScoped_Unfiltered_IsWholeTrace()
    {
        var (mgr, svc) = Setup();
        // Cross-namespace trace: frontend root in "web", backend child in "api".
        mgr.WriteFromSpanBatch(_tenantId, _clusterId,
        [
            Span(_t0, "t1", "s1", null, "GET /", "frontend", 2, 100, 0, ns: "web"),
            Span(_t0.AddMilliseconds(10), "t1", "s2", "s1", "db", "backend", 3, 60, 2, ns: "api"),
        ]);

        // Unfiltered → whole trace.
        TraceSummary whole = (await svc.ListTracesAsync(_clusterId, service: null, From, To)).Data!.Single();
        whole.SpanCount.Should().Be(2);
        whole.ErrorCount.Should().Be(1);
        whole.RootService.Should().Be("frontend");

        // Namespace-scoped to "api" → only the backend span; root falls back to earliest ns span.
        TraceSummary apiOnly = (await svc.ListTracesAsync(
            _clusterId, service: null, From, To, namespaces: ["api"])).Data!.Single();
        apiOnly.SpanCount.Should().Be(1);
        apiOnly.ErrorCount.Should().Be(1);
        apiOnly.RootService.Should().Be("backend");
    }

    [Fact]
    public async Task IndexPath_ServiceAndErrorFilters()
    {
        var (mgr, svc) = Setup();
        mgr.WriteFromSpanBatch(_tenantId, _clusterId,
        [
            Span(_t0, "t1", "s1", null, "GET /", "frontend", 2, 100, 0),
            Span(_t0.AddMilliseconds(10), "t1", "s2", "s1", "db", "backend", 3, 60, 2),
            Span(_t0.AddSeconds(1), "t2", "s3", null, "GET /home", "frontend", 2, 200, 0), // no error
        ]);

        // involves "backend" → only t1.
        (await svc.ListTracesAsync(_clusterId, service: "backend", From, To))
            .Data!.Should().ContainSingle(t => t.TraceId == "t1");
        // errorsOnly → only t1 (t2 has no error).
        (await svc.ListTracesAsync(_clusterId, service: null, From, To, errorsOnly: true))
            .Data!.Should().ContainSingle(t => t.TraceId == "t1");
    }

    [Fact]
    public async Task IndexPath_Seal_ThenQuery_And_Retention()
    {
        var (mgr, svc) = Setup();
        mgr.WriteFromSpanBatch(_tenantId, _clusterId,
            [Span(_t0, "t1", "s1", null, "GET /", "frontend", 2, 100, 0)]);

        (await mgr.RollAndSealAsync())!.Signal.Should().Be("traces");
        (await svc.ListTracesAsync(_clusterId, service: null, From, To))
            .Data!.Should().ContainSingle(t => t.TraceId == "t1");   // survives seal

        await using (ApplicationDbContext db = _factory.CreateDbContext())
            (await db.TelemetrySegments.CountAsync(s => s.Signal == "traces")).Should().Be(1);
    }

    public void Dispose()
    {
        _spanReg?.Dispose();
        _traceReg?.Dispose();
        _context.Dispose();
        _connection.Dispose();
        foreach (string d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* temp */ }
    }
}
