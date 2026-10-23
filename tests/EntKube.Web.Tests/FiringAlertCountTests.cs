using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// What the monitoring overview counts as firing on a cluster.
///
/// <para><b>The defect.</b> <c>GetActiveCountsPerClusterAsync</c> counted
/// <c>Status == Active &amp;&amp; Severity == "critical"</c>, and
/// <c>TenantMonitoringOverview</c> renders a count of zero as a green <b>"Healthy"</b> badge.
/// Both halves of that predicate were wrong for what is being claimed:</para>
///
/// <list type="number">
///   <item>An on-call engineer <b>acknowledging</b> the only critical alert dropped the count to
///     zero, so the act of a person taking ownership of an incident made the dashboard report
///     that nothing was wrong. The same file already states the rule —
///     <c>AlertStats.Open => Active + Acknowledged</c> — and both sibling methods around this one
///     follow it.</item>
///   <item>A cluster with firing <b>warnings</b> read as healthy, under a card captioned "Active
///     Alerts".</item>
/// </list>
///
/// <para>⚠️ The counts stay split by severity rather than merged. Flattening a warning into the
/// red count would be the opposite error: the badge would cry emergency over something routine.
/// The distinction is what lets the caller pick a colour, so it is preserved in the data.</para>
/// </summary>
public class FiringAlertCountTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly IncidentService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid quietClusterId = Guid.NewGuid();

    public FiringAlertCountTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        TestDbContextFactory factory = new(connection);
        sut = new IncidentService(factory, new EntKube.Web.Modules.Api.DeliveryApi(factory));

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        Data.Environment environment = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production",
        };
        db.Environments.Add(environment);

        foreach ((Guid id, string name) in new[] { (clusterId, "prod"), (quietClusterId, "quiet") })
        {
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = id, TenantId = tenantId, EnvironmentId = environment.Id,
                Name = name, ApiServerUrl = $"https://{name}.example.com:6443",
            });
        }

        db.SaveChanges();
    }

    private void Add(string name, string severity, IncidentStatus status)
    {
        db.AlertIncidents.Add(new AlertIncident
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Fingerprint = name,
            AlertName = name,
            Severity = severity,
            Status = status,
            StartsAt = DateTime.UtcNow.AddHours(-3),
        });

        db.SaveChanges();
    }

    private async Task<FiringCounts> Counts() =>
        (await sut.GetActiveCountsPerClusterAsync([clusterId, quietClusterId]))
            .GetValueOrDefault(clusterId);

    /// <summary>
    /// ⭐ The defect, stated as the thing an operator does.
    ///
    /// <para>Acknowledging records that somebody has taken an incident on; it does not stop it
    /// firing. Counting only <c>Active</c> meant the dashboard went green the moment the on-call
    /// engineer pressed acknowledge — the worst possible moment to claim health, because it is
    /// exactly when somebody is working on a live problem.</para>
    /// </summary>
    [Fact]
    public async Task An_acknowledged_critical_is_still_firing()
    {
        Add("TargetDown", "critical", IncidentStatus.Acknowledged);

        FiringCounts counts = await Counts();

        counts.Critical.Should().Be(1,
            "acknowledging is a person taking ownership, not the alert clearing — "
            + "AlertStats.Open in the same service already defines open as Active + Acknowledged");
        counts.Total.Should().Be(1);
    }

    /// <summary>A warning is counted, and counted as what it is.</summary>
    [Fact]
    public async Task A_warning_counts_but_not_as_a_critical()
    {
        Add("KubePodCrashLooping", "warning", IncidentStatus.Active);

        FiringCounts counts = await Counts();

        counts.Critical.Should().Be(0);
        counts.Other.Should().Be(1, "a cluster with a firing warning is not a healthy cluster");
        counts.Total.Should().Be(1);
    }

    /// <summary>
    /// ⚠️ An unrecognised severity is counted rather than dropped.
    ///
    /// <para>The stored vocabulary in this codebase includes <c>"critical"</c>, <c>"warning"</c>,
    /// <c>"none"</c> and the empty string. Anything that is not critical lands in
    /// <see cref="FiringCounts.Other"/>, so a value nobody anticipated still reaches somebody's
    /// screen instead of being silently invisible — which is how the warnings were lost.</para>
    /// </summary>
    [Fact]
    public async Task A_severity_nobody_anticipated_is_counted_rather_than_dropped()
    {
        Add("SomethingNew", "catastrophe", IncidentStatus.Active);

        (await Counts()).Other.Should().Be(1);
    }

    /// <summary>Resolved is the one status that genuinely is not firing.</summary>
    [Fact]
    public async Task A_resolved_incident_is_not_firing()
    {
        Add("WasBroken", "critical", IncidentStatus.Resolved);

        FiringCounts counts = await Counts();

        counts.Total.Should().Be(0, "resolved means it stopped, which is the whole distinction "
            + "between it and acknowledged");
    }

    /// <summary>
    /// ⭐ A cluster with nothing firing is absent from the dictionary, and must read as quiet
    /// rather than throw.
    ///
    /// <para>⚠️ This is why <see cref="FiringCounts"/> is a <c>readonly record struct</c>. The
    /// caller does <c>perClusterCounts.GetValueOrDefault(cluster.Id)</c>, which for a struct
    /// yields <c>(0, 0)</c>; had this been a class it would have yielded <c>null</c> and the
    /// page would throw on <c>vm.Firing.Critical</c> for every healthy cluster. The test exists
    /// because that failure would appear only on the clusters with no alerts — the ones nobody
    /// is looking at.</para>
    /// </summary>
    [Fact]
    public async Task A_cluster_with_nothing_firing_is_quiet_not_missing()
    {
        Add("TargetDown", "critical", IncidentStatus.Active);

        Dictionary<Guid, FiringCounts> all =
            await sut.GetActiveCountsPerClusterAsync([clusterId, quietClusterId]);

        all.Should().NotContainKey(quietClusterId,
            "grouping returns no row for a cluster with no incidents — the default is what "
            + "stands in for it");

        FiringCounts quiet = all.GetValueOrDefault(quietClusterId);

        quiet.Critical.Should().Be(0);
        quiet.Other.Should().Be(0);
        quiet.Total.Should().Be(0);
    }

    /// <summary>
    /// The mixed case, which is what decides the badge: a red count that also says how much else
    /// is going on, rather than two numbers that cannot be told apart.
    /// </summary>
    [Fact]
    public async Task Criticals_and_warnings_are_both_kept()
    {
        Add("TargetDown", "critical", IncidentStatus.Active);
        Add("EtcdNoLeader", "critical", IncidentStatus.Acknowledged);
        Add("KubePodCrashLooping", "warning", IncidentStatus.Active);
        Add("DiskFillingUp", "warning", IncidentStatus.Acknowledged);
        Add("OldNews", "critical", IncidentStatus.Resolved);

        FiringCounts counts = await Counts();

        counts.Critical.Should().Be(2);
        counts.Other.Should().Be(2);
        counts.Total.Should().Be(4, "Total is Critical + Other, and is what a healthy-or-not "
            + "question means");
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
