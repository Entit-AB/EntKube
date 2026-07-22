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
/// Severity-tiered log retention: WARN+ logs route to the long-retention "logs" tier and DEBUG/INFO to the
/// short-retention "logs_debug" tier, queries union both (skipping the verbose tier for WARN+ requests), and
/// the verbose tier expires on its own shorter window while the important tier survives.
/// </summary>
public sealed class SegmentLogTierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly ClusterTenantResolver _resolver;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _tempDirs = [];

    public SegmentLogTierTests()
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

    private static LogIngestRecord Log(DateTime ts, string ns, string pod, short sev, string body)
        => new(ts, ns, pod, "app", sev, body, null, null);

    private (LogTierRegistries tiers, SegmentEngineOptions options,
             SegmentManagerRegistry<LogSegmentManager> imp, SegmentManagerRegistry<VerboseLogSegmentManager> verb) Tiers(
        int verboseRetentionDays = 14)
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "tiertest-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dataPath);
        string blobsDir = Path.Combine(dataPath, "blobs");
        Directory.CreateDirectory(blobsDir);
        var options = new SegmentEngineOptions
        {
            DataPath = dataPath, RetentionDays = 90, TieredLogRetention = true,
            VerboseLogRetentionDays = verboseRetentionDays,
        };
        var store = new LocalSegmentBlobStore(blobsDir);
        var imp = new SegmentManagerRegistry<LogSegmentManager>(tid =>
            new LogSegmentManager(tid, _factory, store, options, NullLogger<LogSegmentManager>.Instance));
        var verb = new SegmentManagerRegistry<VerboseLogSegmentManager>(tid =>
            new VerboseLogSegmentManager(tid, _factory, store, options, NullLogger<LogSegmentManager>.Instance));
        _disposables.Add(imp); _disposables.Add(verb);
        return (new LogTierRegistries(imp, verb, options), options, imp, verb);
    }

    // Mimics SegmentTelemetryStore.WriteLogsAsync: split by severity, write each tier.
    private static void Write(LogTierRegistries tiers, Guid tenantId, Guid clusterId, params LogIngestRecord[] records)
    {
        (IReadOnlyList<LogIngestRecord> imp, IReadOnlyList<LogIngestRecord> verb) = tiers.Split(records);
        if (imp.Count > 0) tiers.Important.For(tenantId).WriteLogs(tenantId, clusterId, imp);
        if (verb.Count > 0) tiers.Verbose.For(tenantId).WriteLogs(tenantId, clusterId, verb);
    }

    [Fact]
    public async Task WritesRouteBySeverity_QueriesUnionTiers_AndMinLevelSkipsVerbose()
    {
        (LogTierRegistries tiers, _, _, _) = Tiers();
        DateTime t0 = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        Write(tiers, _tenantId, _clusterId,
            Log(t0.AddSeconds(1), "prod", "api-1", (short)LogLevel.Info, "info: handled request"),
            Log(t0.AddSeconds(2), "prod", "api-1", (short)LogLevel.Debug, "debug: cache lookup"),
            Log(t0.AddSeconds(3), "prod", "api-1", (short)LogLevel.Warn, "warn: retrying"),
            Log(t0.AddSeconds(4), "prod", "api-1", (short)LogLevel.Error, "error: boom"));

        var svc = new SegmentLogService(tiers, _resolver, NullLogger<SegmentLogService>.Instance);
        var window = (from: t0, to: t0.AddMinutes(1));

        // No min level → union of both tiers: all four lines.
        var all = await svc.QueryAsync(_clusterId, new LogQueryFilter { Namespaces = ["prod"] }, window.from, window.to);
        all.Data!.SelectMany(s => s.Entries).Should().HaveCount(4);

        // MinLevel=Warn → verbose tier skipped; only the two WARN+ lines (which live in the important tier).
        var warnPlus = await svc.QueryAsync(
            _clusterId, new LogQueryFilter { Namespaces = ["prod"], MinLevel = LogLevel.Warn }, window.from, window.to);
        List<LokiLogEntry> warnEntries = warnPlus.Data!.SelectMany(s => s.Entries).ToList();
        warnEntries.Should().HaveCount(2);
        warnEntries.Should().OnlyContain(e => e.DetectedLevel >= LogLevel.Warn);

        // Histogram unions tiers: 4 total, 1 error.
        var hist = await svc.GetHistogramAsync(
            _clusterId, new LogQueryFilter { Namespaces = ["prod"] }, window.from, window.to, buckets: 4);
        hist.Data!.Sum(b => b.Total).Should().Be(4);
        hist.Data!.Sum(b => b.Errors).Should().Be(1);
    }

    [Fact]
    public async Task VerboseTier_SealsUnderOwnSignal_AndExpiresEarly_WhileImportantSurvives()
    {
        (LogTierRegistries tiers, _, SegmentManagerRegistry<LogSegmentManager> imp,
            SegmentManagerRegistry<VerboseLogSegmentManager> verb) = Tiers(verboseRetentionDays: 7);

        DateTime old = DateTime.UtcNow.AddDays(-30);
        Write(tiers, _tenantId, _clusterId,
            Log(old, "prod", "api-1", (short)LogLevel.Info, "old info noise"),
            Log(old, "prod", "api-1", (short)LogLevel.Error, "old error worth keeping"));

        await imp.For(_tenantId).RollAndSealAsync();
        await verb.For(_tenantId).RollAndSealAsync();

        // Cataloged under distinct signals.
        await using (ApplicationDbContext db = _factory.CreateDbContext())
        {
            (await db.TelemetrySegments.CountAsync(s => s.Signal == "logs")).Should().Be(1);
            (await db.TelemetrySegments.CountAsync(s => s.Signal == "logs_debug")).Should().Be(1);
        }

        // Retention: the 30-day-old verbose segment is past its 7-day window; the important one is not (90d).
        (await verb.For(_tenantId).DropExpiredAsync()).Should().Be(1);
        (await imp.For(_tenantId).DropExpiredAsync()).Should().Be(0);

        await using (ApplicationDbContext db = _factory.CreateDbContext())
        {
            (await db.TelemetrySegments.CountAsync(s => s.Signal == "logs_debug")).Should().Be(0); // verbose gone
            (await db.TelemetrySegments.CountAsync(s => s.Signal == "logs")).Should().Be(1);        // error kept
        }
    }

    public void Dispose()
    {
        foreach (IDisposable d in _disposables) d.Dispose();
        _context.Dispose();
        _connection.Dispose();
        foreach (string d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* temp */ }
    }
}
