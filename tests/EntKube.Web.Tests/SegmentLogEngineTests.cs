using System.Diagnostics;
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
/// The Lucene/S3 telemetry segment engine (logs): verifies the query backend
/// (<see cref="SegmentLogService"/> over <see cref="LogSegmentManager"/>) returns the same DTOs as the old
/// Postgres path for search, trace-correlation, label dropdowns, and the volume histogram; that a filtered
/// search over a realistic row count stays well under the ~5s the Postgres store took; and that sealing a
/// segment to (local) object storage, cataloging it, and querying it back round-trips — including retention.
/// </summary>
public sealed class SegmentLogEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly ClusterTenantResolver _resolver;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<SegmentManagerRegistry<LogSegmentManager>> _registries = [];
    private SegmentManagerRegistry<LogSegmentManager>? _registry;
    private readonly List<string> _tempDirs = [];

    public SegmentLogEngineTests()
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

    private static LogIngestRecord Log(DateTime ts, string ns, string pod, short sev, string body, string? trace = null)
        => new(ts, ns, pod, "app", sev, body, trace, null);

    // Builds a fresh per-tenant registry (temp data path + local object storage) and returns this test's
    // tenant manager from it. NewService() then wraps the same registry.
    private LogSegmentManager NewManager(int retentionDays = 14)
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "segtest-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dataPath);
        string blobsDir = Path.Combine(dataPath, "blobs");
        Directory.CreateDirectory(blobsDir);
        var options = new SegmentEngineOptions { DataPath = dataPath, RetentionDays = retentionDays };
        var store = new LocalSegmentBlobStore(blobsDir);
        _registry = new SegmentManagerRegistry<LogSegmentManager>(tid =>
            new LogSegmentManager(tid, _factory, store, options, NullLogger<LogSegmentManager>.Instance));
        _registries.Add(_registry);
        return _registry.For(_tenantId);
    }

    private LogSegmentManager ManagerWith(params LogIngestRecord[] records)
    {
        LogSegmentManager mgr = NewManager();
        mgr.WriteLogs(_tenantId, _clusterId, records);
        return mgr;
    }

    // Single-tier LogTierRegistries (no verbose registry) — matches these tests, which write every severity
    // straight to the one LogSegmentManager. Tiering behaviour has its own coverage in SegmentLogTierTests.
    private SegmentLogService NewService()
        => new(new LogTierRegistries(_registry!, null, new SegmentEngineOptions()),
               _resolver, NullLogger<SegmentLogService>.Instance);

    private SegmentManagerRegistry<LogSegmentManager> Registry(int retentionDays = 14)
    {
        NewManager(retentionDays);
        return _registry!;
    }

    [Fact]
    public async Task Query_FiltersByNamespaceTextAndSeverity_NewestFirst()
    {
        DateTime t0 = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);
        LogSegmentManager mgr = ManagerWith(
            Log(t0.AddSeconds(1), "prod", "api-1", 2, "connection established to database"),
            Log(t0.AddSeconds(2), "prod", "api-2", 4, "ERROR failed to reach database timeout", "abc123"),
            Log(t0.AddSeconds(3), "staging", "worker-1", 4, "ERROR unrelated namespace"),
            Log(t0.AddSeconds(4), "prod", "api-1", 4, "ERROR null reference in handler"));
        SegmentLogService svc = NewService();

        var filter = new LogQueryFilter { Namespaces = ["prod"], Text = "database", MinLevel = LogLevel.Error };
        var result = await svc.QueryAsync(_clusterId, filter, t0, t0.AddMinutes(1));

        result.IsSuccess.Should().BeTrue();
        List<LokiLogEntry> entries = result.Data!.SelectMany(s => s.Entries).ToList();
        entries.Should().ContainSingle();
        entries[0].Line.Should().Contain("failed to reach database");
        entries[0].DetectedLevel.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task QueryByTrace_ReturnsAllLinesForTrace()
    {
        DateTime t0 = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);
        LogSegmentManager mgr = ManagerWith(
            Log(t0.AddSeconds(1), "prod", "api-1", 2, "start request", "trace-xyz"),
            Log(t0.AddSeconds(2), "prod", "api-1", 4, "boom", "trace-xyz"),
            Log(t0.AddSeconds(3), "prod", "api-1", 2, "other request", "trace-other"));

        var result = await NewService().QueryByTraceAsync(_clusterId, "trace-xyz");

        result.IsSuccess.Should().BeTrue();
        result.Data!.SelectMany(s => s.Entries).Should().HaveCount(2);
    }

    [Fact]
    public async Task Labels_And_Histogram_AreComputed()
    {
        DateTime t0 = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);
        LogSegmentManager mgr = ManagerWith(
            Log(t0.AddSeconds(1), "prod", "api-1", 2, "a"),
            Log(t0.AddSeconds(2), "prod", "api-2", 4, "b"),
            Log(t0.AddSeconds(3), "staging", "worker-1", 1, "c"));
        SegmentLogService svc = NewService();

        // Wide discovery window: the fixtures are timestamped at a fixed absolute date, so scan well
        // past it (the default is only the last hour) to discover every namespace/pod regardless of age.
        const int allTime = 10_000_000;
        (await svc.GetNamespacesAsync(_clusterId, allTime)).Data.Should().BeEquivalentTo(["prod", "staging"]);
        (await svc.GetPodsAsync(_clusterId, "prod", allTime)).Data.Should().BeEquivalentTo(["api-1", "api-2"]);

        var hist = await svc.GetHistogramAsync(
            _clusterId, new LogQueryFilter { Namespaces = ["prod", "staging"] }, t0, t0.AddMinutes(1), buckets: 4);
        hist.IsSuccess.Should().BeTrue();
        hist.Data!.Sum(b => b.Total).Should().Be(3);
        hist.Data!.Sum(b => b.Errors).Should().Be(1);
    }

    [Fact]
    public async Task Seal_ThenQuery_RoundTripsThroughObjectStorage()
    {
        DateTime t0 = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);
        LogSegmentManager mgr = ManagerWith(
            Log(t0.AddSeconds(1), "prod", "api-1", 2, "before seal one"),
            Log(t0.AddSeconds(2), "prod", "api-1", 4, "ERROR before seal two", "t-1"));

        TelemetrySegment? sealed1 = await mgr.RollAndSealAsync();
        sealed1.Should().NotBeNull();
        sealed1!.DocCount.Should().Be(2);
        mgr.ActiveDocCount.Should().Be(0); // active index rotated to a fresh empty one

        // Catalog row persisted.
        await using (ApplicationDbContext db = _factory.CreateDbContext())
            (await db.TelemetrySegments.CountAsync()).Should().Be(1);

        // The logs are now only in the sealed segment; a query must fetch it back from object storage.
        SegmentLogService svc = NewService();
        var result = await svc.QueryAsync(
            _clusterId, new LogQueryFilter { Namespaces = ["prod"] }, t0, t0.AddMinutes(1));
        result.IsSuccess.Should().BeTrue();
        result.Data!.SelectMany(s => s.Entries).Should().HaveCount(2);

        // A write after sealing lands in the new active index and is unioned with the sealed segment.
        mgr.WriteLogs(_tenantId, _clusterId, [Log(t0.AddSeconds(3), "prod", "api-1", 2, "after seal three")]);
        var after = await svc.QueryAsync(_clusterId, new LogQueryFilter { Namespaces = ["prod"] }, t0, t0.AddMinutes(1));
        after.Data!.SelectMany(s => s.Entries).Should().HaveCount(3);
    }

    [Fact]
    public async Task Retention_DropsExpiredSegments()
    {
        DateTime old = DateTime.UtcNow.AddDays(-30);
        LogSegmentManager mgr = NewManager(retentionDays: 14);
        mgr.WriteLogs(_tenantId, _clusterId, [Log(old, "prod", "api-1", 2, "ancient line")]);
        await mgr.RollAndSealAsync();

        await using (ApplicationDbContext db = _factory.CreateDbContext())
            (await db.TelemetrySegments.CountAsync()).Should().Be(1);

        int dropped = await mgr.DropExpiredAsync();
        dropped.Should().Be(1);

        await using (ApplicationDbContext db = _factory.CreateDbContext())
            (await db.TelemetrySegments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Search_Over_50k_Logs_Is_SubSecond()
    {
        DateTime t0 = new(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);
        var records = new List<LogIngestRecord>(50_000);
        for (int i = 0; i < 50_000; i++)
        {
            short sev = (short)(i % 25 == 0 ? 4 : 2);
            string body = i % 25 == 0 ? $"ERROR request {i} failed with timeout" : $"handled request {i} ok";
            records.Add(Log(t0.AddMilliseconds(i * 10), "prod", $"api-{i % 8}", sev, body,
                i % 25 == 0 ? $"trace-{i}" : null));
        }
        LogSegmentManager mgr = NewManager();
        mgr.WriteLogs(_tenantId, _clusterId, records);
        SegmentLogService svc = NewService();

        var filter = new LogQueryFilter { Namespaces = ["prod"], Text = "timeout", MinLevel = LogLevel.Error };
        var sw = Stopwatch.StartNew();
        var result = await svc.QueryAsync(_clusterId, filter, t0, t0.AddDays(1), limit: 200);
        sw.Stop();

        result.IsSuccess.Should().BeTrue();
        result.Data!.SelectMany(s => s.Entries).Should().NotBeEmpty();
        // Sub-second in isolation; the CI bound is loose because the full suite runs test classes in
        // parallel and starves the CPU. Even so this is far under the >5s the Postgres store took.
        sw.ElapsedMilliseconds.Should().BeLessThan(3000);
    }

    [Fact]
    public async Task Tenants_AreIsolated_NoCrossTenantLogs()
    {
        // A second tenant + cluster. Telemetry must be tenant-scoped: neither tenant can see the other's logs.
        Guid tenantB = Guid.NewGuid(), clusterB = Guid.NewGuid(), envB = Guid.NewGuid();
        _context.Tenants.Add(new Tenant { Id = tenantB, Name = "Beta", Slug = "beta-" + tenantB.ToString("N")[..8] });
        _context.Environments.Add(new Environment { Id = envB, TenantId = tenantB, Name = "prod" });
        _context.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterB, TenantId = tenantB, EnvironmentId = envB, Name = "c2", ApiServerUrl = "https://k8s.example.com",
        });
        _context.SaveChanges();

        DateTime t0 = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        SegmentManagerRegistry<LogSegmentManager> reg = Registry();
        reg.For(_tenantId).WriteLogs(_tenantId, _clusterId, [Log(t0, "prod", "api-1", 2, "tenant A private log")]);
        reg.For(tenantB).WriteLogs(tenantB, clusterB, [Log(t0, "prod", "api-1", 2, "tenant B private log")]);

        // Seal tenant A so its data lives only in a sealed (tenant-scoped) segment; B stays in its active index.
        await reg.For(_tenantId).RollAndSealAsync();

        SegmentLogService svc = NewService();
        var window = (from: t0.AddMinutes(-1), to: t0.AddMinutes(1));
        var a = await svc.QueryAsync(_clusterId, new LogQueryFilter { Namespaces = ["prod"] }, window.from, window.to);
        var b = await svc.QueryAsync(clusterB, new LogQueryFilter { Namespaces = ["prod"] }, window.from, window.to);

        a.Data!.SelectMany(s => s.Entries).Should().ContainSingle().Which.Line.Should().Contain("tenant A");
        b.Data!.SelectMany(s => s.Entries).Should().ContainSingle().Which.Line.Should().Contain("tenant B");

        // Cross-tenant catalog isolation: each tenant's sealed/active segments are tagged with its TenantId.
        await using ApplicationDbContext db = _factory.CreateDbContext();
        (await db.TelemetrySegments.CountAsync(s => s.TenantId == tenantB)).Should().Be(0); // B not sealed yet
        (await db.TelemetrySegments.Where(s => s.Signal == "logs").AllAsync(s => s.TenantId == _tenantId)).Should().BeTrue();
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
