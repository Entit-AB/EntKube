using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The Lucene/S3 telemetry segment engine (RUM): verifies <see cref="SegmentRumService"/> over
/// <see cref="RumSegmentManager"/> returns the same DTOs as the old Postgres RUM path — site overview with
/// Web-Vitals p75, top pages/errors, page-view series, session summaries, and session detail — and that
/// RUM events round-trip through a sealed segment. RUM is site-scoped (tenant_id + site_id).
/// </summary>
public sealed class SegmentRumEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _siteId = Guid.NewGuid();
    private readonly List<SegmentManagerRegistry<RumSegmentManager>> _registries = [];
    private SegmentManagerRegistry<RumSegmentManager>? _registry;
    private readonly List<string> _tempDirs = [];
    private readonly DateTime _t0 = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    public SegmentRumEngineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _factory = new TestDbContextFactory(_connection);
    }

    private RumSegmentManager NewManager()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "rumtest-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dataPath);
        string blobsDir = Path.Combine(dataPath, "blobs");
        Directory.CreateDirectory(blobsDir);
        var store = new LocalSegmentBlobStore(blobsDir);
        var options = new SegmentEngineOptions { DataPath = dataPath };
        _registry = new SegmentManagerRegistry<RumSegmentManager>(tid =>
            new RumSegmentManager(tid, _factory, store, options, NullLogger<RumSegmentManager>.Instance));
        _registries.Add(_registry);
        return _registry.For(_tenantId);
    }

    private (RumSegmentManager mgr, SegmentRumService svc, DateTime from, DateTime to) Scenario()
    {
        RumSegmentManager mgr = NewManager();
        mgr.WritePageViews(_tenantId, _siteId,
        [
            new RumPageViewRecord(_t0, "s1", "v1", "/", null, 100, 20, 2000, 0.1, 50, 900, "Chrome", "macOS", "desktop"),
            new RumPageViewRecord(_t0.AddSeconds(10), "s1", "v2", "/home", null, 200, 30, 3000, 0.2, 80, 1200, "Chrome", "macOS", "desktop"),
            new RumPageViewRecord(_t0.AddSeconds(20), "s2", "v3", "/", null, 150, 25, 2500, 0.05, 40, 1000, "Firefox", "Windows", "desktop"),
        ]);
        mgr.WriteErrors(_tenantId, _siteId,
            [new RumErrorRecord(_t0.AddSeconds(11), "s1", "v2", "/home", "TypeError x is undefined", "stack", "app.js")]);
        mgr.WriteResources(_tenantId, _siteId,
            [new RumResourceRecord(_t0.AddSeconds(12), "s1", "v2", "/home", "app.js", "script", 30, 200, null)]);
        var svc = new SegmentRumService(_registry!, NullLogger<SegmentRumService>.Instance);
        return (mgr, svc, _t0.AddMinutes(-1), _t0.AddMinutes(5));
    }

    [Fact]
    public async Task Overview_ComputesCountsAndWebVitalsP75()
    {
        var (_, svc, from, to) = Scenario();
        RumSiteOverview? o = await svc.GetOverviewAsync(_tenantId, _siteId, from, to);
        o.Should().NotBeNull();
        o!.PageViews.Should().Be(3);
        o.Sessions.Should().Be(2);
        o.Errors.Should().Be(1);
        o.LcpP75.Should().BeApproximately(2750, 0.1); // p75_cont of {2000,2500,3000}
        o.ClsP75.Should().BeApproximately(0.15, 0.001);
        o.AvgLoadMs.Should().BeApproximately(150, 0.1);
    }

    [Fact]
    public async Task TopPages_And_TopErrors()
    {
        var (_, svc, from, to) = Scenario();

        var pages = await svc.GetTopPagesAsync(_tenantId, _siteId, from, to);
        pages.Should().HaveCount(2);
        pages[0].Path.Should().Be("/");     // 2 views, ranked first
        pages[0].Views.Should().Be(2);
        pages[0].LcpP75.Should().BeApproximately(2375, 0.1); // p75_cont of {2000,2500}

        var errors = await svc.GetTopErrorsAsync(_tenantId, _siteId, from, to);
        errors.Should().ContainSingle();
        errors[0].Message.Should().Be("TypeError x is undefined");
        errors[0].Count.Should().Be(1);
    }

    [Fact]
    public async Task Sessions_And_SessionDetail()
    {
        var (_, svc, from, to) = Scenario();

        var sessions = await svc.GetSessionsAsync(_tenantId, _siteId, from, to);
        sessions.Should().HaveCount(2);
        sessions[0].SessionId.Should().Be("s2"); // most-recent last-seen first
        RumSessionSummary s1 = sessions.Single(s => s.SessionId == "s1");
        s1.Views.Should().Be(2);
        s1.Errors.Should().Be(1);
        s1.LastPath.Should().Be("/home");

        RumSessionDetail? detail = await svc.GetSessionDetailAsync(_tenantId, _siteId, "s1", from, to);
        detail.Should().NotBeNull();
        detail!.Views.Should().HaveCount(2);
        detail.Errors.Should().ContainSingle();
        detail.Resources.Should().ContainSingle();
        detail.Resources[0].Name.Should().Be("app.js");
    }

    [Fact]
    public async Task PageViewSeries_BucketsViews()
    {
        var (_, svc, from, to) = Scenario();
        var series = await svc.GetPageViewSeriesAsync(_tenantId, _siteId, from, to, buckets: 12);
        series.Sum(p => p.Value).Should().Be(3);
    }

    [Fact]
    public async Task Seal_ThenQuery_RoundTripsRum()
    {
        var (mgr, svc, from, to) = Scenario();

        TelemetrySegment? sealed1 = await mgr.RollAndSealAsync();
        sealed1.Should().NotBeNull();
        sealed1!.Signal.Should().Be("rum");
        sealed1.DocCount.Should().Be(5); // 3 pv + 1 err + 1 res

        await using (ApplicationDbContext db = _factory.CreateDbContext())
            (await db.TelemetrySegments.CountAsync(s => s.Signal == "rum")).Should().Be(1);

        // Data now only in the sealed segment — overview must fetch it back.
        RumSiteOverview? o = await svc.GetOverviewAsync(_tenantId, _siteId, from, to);
        o!.PageViews.Should().Be(3);
        o.Errors.Should().Be(1);
    }

    public void Dispose()
    {
        foreach (SegmentManagerRegistry<RumSegmentManager> r in _registries) r.Dispose();
        _context.Dispose();
        _connection.Dispose();
        foreach (string d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* temp */ }
    }
}
