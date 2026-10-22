using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The severity breakdown on the operations analytics tab, and the two different sets it was
/// built from.
///
/// <para><b>The defect.</b> <c>GetStatsForTenantAsync</c> runs two queries on purpose.
/// <c>Total</c> counts incidents that <em>started</em> inside the window; <c>Critical</c> and
/// <c>Warning</c> come from a second query with <b>no date predicate</b>, so a long-running alert
/// still shows — and a comment says exactly that about each. Both are right as counts.</para>
///
/// <para>⚠️ The tab then computed <c>critPct = Critical * 100 / Total</c>. A numerator from one
/// set over a denominator from another is a share of nothing. Three open criticals that began
/// before the window, against one incident inside it, give <c>critPct = 300</c> — a bar rendered
/// at <c>width:300%</c>, <c>otherPct = 100 - 300 = -200</c> so no "Other" bar at all (its
/// <c>&gt; 0</c> guard is false), and an "Other" badge reading <b>-2</b>.</para>
///
/// <para>The fix divides by <c>Open</c> — <c>Active + Acknowledged</c>, which is exactly the set
/// the severity counts come from — so the three shares sum to 100 by construction and the bars
/// agree with the badges beneath them. Nothing in the service changed; the counts were always
/// right.</para>
/// </summary>
public class AlertSeverityShareTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly IncidentService sut;
    private readonly Guid tenantId = Guid.NewGuid();

    public AlertSeverityShareTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        TestDbContextFactory factory = new(connection);
        sut = new IncidentService(factory, new EntKube.Web.Modules.Api.DeliveryApi(factory));

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        Data.Environment environment = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production" };
        db.Environments.Add(environment);

        Guid clusterId = Guid.NewGuid();
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environment.Id,
            Name = "prod", ApiServerUrl = "https://prod.example.com:6443",
        });

        // Three criticals still open, started long before any window anyone would ask for.
        for (int i = 0; i < 3; i++)
        {
            Add(clusterId, $"long-running-{i}", "critical", IncidentStatus.Active, daysAgo: 60);
        }

        // One incident inside the window, resolved — so the window total is 1.
        Add(clusterId, "inside-window", "warning", IncidentStatus.Resolved, daysAgo: 5);

        db.SaveChanges();
    }

    private void Add(Guid clusterId, string name, string severity, IncidentStatus status, int daysAgo)
        => db.AlertIncidents.Add(new AlertIncident
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Fingerprint = name,
            AlertName = name,
            Severity = severity,
            Status = status,
            StartsAt = DateTime.UtcNow.AddDays(-daysAgo),
        });

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The premise, asserted rather than assumed: the severity counts and the window total really
    /// are different sets, and the severity count can exceed the total. If that stopped being
    /// true this file's reason for existing would be gone, and the test below would be vacuous.
    /// </summary>
    [Fact]
    public async Task The_severity_counts_and_the_window_total_count_different_things()
    {
        AlertStats stats = await sut.GetStatsForTenantAsync(tenantId, 30);

        stats.Total.Should().Be(1, "only one incident started inside the 30-day window");
        stats.Critical.Should().Be(3, "severity counts ignore the window on purpose");
        stats.Open.Should().Be(3, "Active + Acknowledged is the set the severity counts come from");

        stats.Critical.Should().BeGreaterThan(stats.Total,
            "this is the whole hazard: a severity count can exceed the window total, so their "
            + "ratio is not a percentage of anything");
    }

    /// <summary>
    /// A severity share, computed the way the tab computes it. ⚠️ The arithmetic is duplicated
    /// here deliberately: the razor expression is not reachable from a test, so this pins the
    /// property the expression has to have, and <see cref="The_severity_card_divides_by_open"/>
    /// pins that the razor uses this denominator.
    /// </summary>
    [Fact]
    public async Task A_severity_share_over_the_open_count_cannot_exceed_a_hundred()
    {
        AlertStats stats = await sut.GetStatsForTenantAsync(tenantId, 30);

        int critPct = (int)(stats.Critical * 100.0 / stats.Open);
        int warnPct = (int)(stats.Warning * 100.0 / stats.Open);
        int otherPct = 100 - critPct - warnPct;

        critPct.Should().Be(100, "all three open incidents are critical");
        otherPct.Should().Be(0);
        (stats.Open - stats.Critical - stats.Warning).Should().Be(0,
            "the 'Other' badge is a count of the same set, so it cannot go negative");

        // And the number the old denominator produced, named so this cannot pass by accident.
        ((int)(stats.Critical * 100.0 / stats.Total)).Should().Be(300,
            "dividing by the window total is what rendered a bar at width:300%");
    }

    /// <summary>
    /// The razor expression itself, pinned by reading the source: there is no harness for this
    /// component and the expression lives inline in markup, so the choice of denominator is not
    /// otherwise observable from a test.
    /// </summary>
    [Fact]
    public void The_severity_card_divides_by_open()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/EntKube.Web/Components/Pages/Tenants/TenantOpsAnalyticsTab.razor"));

        File.Exists(path).Should().BeTrue(
            "this test reads the component's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        string source = File.ReadAllText(path);

        source.Should().Contain("stats.Critical * 100.0 / stats.Open",
            "a severity count divided by the window total is a share of nothing");

        source.Should().NotContain("/ stats.Total",
            "no proportion on this card may use the window total as its denominator");
    }
}
