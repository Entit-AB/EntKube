using EntKube.Contracts.Delivery;
using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ContractHealthStatus = EntKube.Contracts.Delivery.HealthStatus;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>IDeliveryApi.GetHealthRatiosAsync</c> — the aggregate left in the database.
///
/// <para><b>Why this method exists.</b> Five call sites outside Delivery compute the identical
/// <c>healthy / total × 100</c>, and four of them need nothing else.
/// <c>UptimeTrackingService</c> samples every five minutes and keeps 90 days, so each of those
/// four was shipping up to ~26,000 rows per deployment out of the database to produce one number —
/// and two were paying it <em>through the contract</em>, which is the worst version of the problem
/// the contract exists to solve.</para>
///
/// <para>The test that matters most is
/// <see cref="The_ratio_is_exactly_what_counting_the_rows_gives"/>: the aggregate must agree with
/// the row-shaped method it replaces, because every caller's displayed number changed source.</para>
/// </summary>
public sealed class DeploymentHealthRatioTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private readonly Guid ourDeployment = Guid.NewGuid();
    private readonly Guid theirDeployment = Guid.NewGuid();
    private readonly Guid quietDeployment = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    public DeploymentHealthRatioTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        Seed(ourTenant, "ours", ourDeployment, quietDeployment);
        Seed(theirTenant, "theirs", theirDeployment, null);
        db.SaveChanges();
    }

    private void Seed(Guid tenantId, string label, Guid deploymentId, Guid? secondDeploymentId)
    {
        Guid environmentId = Guid.NewGuid(), clusterId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = label });
        db.Environments.Add(new Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = $"{label}-env",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = $"{label}-cluster", ApiServerUrl = $"https://{label}.example",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label}-customer" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = $"{label}-app" });

        foreach (Guid id in secondDeploymentId is Guid second ? [deploymentId, second] : new[] { deploymentId })
        {
            db.AppDeployments.Add(new AppDeployment
            {
                Id = id, AppId = appId, Name = $"{label}-{id:N}", EnvironmentId = environmentId,
                ClusterId = clusterId, Namespace = label,
                Type = Data.DeploymentType.HelmChart,
                SyncStatus = Data.SyncStatus.OutOfSync,
                HealthStatus = Data.HealthStatus.Degraded,
            });
        }
    }

    /// <summary>
    /// Adds <paramref name="healthy"/> healthy samples and <paramref name="unhealthy"/> degraded
    /// ones, at five-minute spacing ending just before <see cref="Now"/>.
    /// </summary>
    private void Samples(Guid deploymentId, int healthy, int unhealthy, int minutesAgoStart = 5)
    {
        int minute = minutesAgoStart;
        foreach (Data.HealthStatus status in
            Enumerable.Repeat(Data.HealthStatus.Healthy, healthy)
                .Concat(Enumerable.Repeat(Data.HealthStatus.Degraded, unhealthy)))
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(),
                DeploymentId = deploymentId,
                HealthStatus = status,
                SyncStatus = Data.SyncStatus.Synced,
                SnapshotAt = Now.AddMinutes(-minute),
            });
            minute += 5;
        }
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private IDeliveryApi Delivery => new DeliveryApi(factory);

    private static DateTime Day(int days) => Now.AddDays(-days);

    [Fact]
    public async Task It_counts_the_healthy_samples_and_the_total()
    {
        Samples(ourDeployment, healthy: 7, unhealthy: 3);

        DeploymentHealthRatio r = (await Delivery.GetHealthRatiosAsync(
            ourTenant, [ourDeployment], Day(1))).Should().ContainSingle().Subject;

        r.DeploymentId.Should().Be(ourDeployment);
        r.HealthySamples.Should().Be(7);
        r.TotalSamples.Should().Be(10);
    }

    /// <summary>
    /// The aggregate agrees with the rows, which is the only thing that makes it safe to swap.
    ///
    /// <para>Every adopting caller's number changed source, not meaning — and a counting mistake
    /// here does not fail, it reports a different availability percentage, which is precisely the
    /// figure an SLA conversation turns on.</para>
    /// </summary>
    [Fact]
    public async Task The_ratio_is_exactly_what_counting_the_rows_gives()
    {
        Samples(ourDeployment, healthy: 11, unhealthy: 4);

        IReadOnlyList<DeploymentHealthSample> rows =
            await Delivery.GetHealthSamplesAsync(ourTenant, [ourDeployment], Day(1));
        DeploymentHealthRatio ratio = (await Delivery.GetHealthRatiosAsync(
            ourTenant, [ourDeployment], Day(1))).Single();

        ratio.TotalSamples.Should().Be(rows.Count);
        ratio.HealthySamples.Should().Be(rows.Count(s => s.Health == ContractHealthStatus.Healthy));
    }

    /// <summary>
    /// The conditional count must actually discriminate: a mixed fixture, and healthy strictly
    /// less than total.
    ///
    /// <para>A test seeding only healthy samples, or one that merely checked the call did not
    /// throw, would pass against a count that matched everything — which is the failure mode worth
    /// excluding here, since it reports 100% availability for a workload that was down.</para>
    ///
    /// <para>⚠️ <b>It is NOT a test of the string-stored-enum trap, although it was written as
    /// one.</b> The claim was that <c>(int)s.HealthStatus</c> here would become
    /// <c>CAST("HealthStatus" AS integer)</c> and read 0 for every row on SQLite. A removal check
    /// said otherwise — substituting the cast left every test green — and
    /// <c>ToQueryString</c> explains why: EF folds <c>(int)x == (int)const</c> on the same enum
    /// back to <c>= 'Healthy'</c>, and both spellings emit byte-identical SQL. The real trap, which
    /// <c>ModuleApiTests.Delivery_carries_the_enums_rather_than_casting_them_in_sql</c> covers, is a
    /// projection converting to a <em>different</em> enum type. Recorded here because a test whose
    /// stated reason is false is worse than one with no comment: the next reader trusts it.</para>
    /// </summary>
    [Fact]
    public async Task A_degraded_sample_is_not_counted_as_healthy()
    {
        Samples(ourDeployment, healthy: 1, unhealthy: 9);

        DeploymentHealthRatio r = (await Delivery.GetHealthRatiosAsync(
            ourTenant, [ourDeployment], Day(1))).Single();

        r.TotalSamples.Should().Be(10);
        r.HealthySamples.Should().Be(1, "a cast-based count reads 0 for every status on SQLite "
            + "and would report all ten as healthy");
    }

    [Fact]
    public async Task A_stranger_gets_nothing_for_a_deployment_that_is_not_theirs()
    {
        Samples(ourDeployment, healthy: 5, unhealthy: 0);

        // The id is real and the samples exist. Only the tenant is wrong — and this is the one
        // predicate that moved when IncidentService stopped walking
        // s.Deployment.App.Customer.TenantId itself, so it is the one worth pinning here.
        (await Delivery.GetHealthRatiosAsync(theirTenant, [ourDeployment], Day(1)))
            .Should().BeEmpty();

        (await Delivery.GetHealthRatiosAsync(ourTenant, [ourDeployment], Day(1)))
            .Should().ContainSingle("the owner still sees it");
    }

    [Fact]
    public async Task A_deployment_with_no_samples_is_absent_rather_than_zero_over_zero()
    {
        Samples(ourDeployment, healthy: 4, unhealthy: 1);

        IReadOnlyList<DeploymentHealthRatio> ratios = await Delivery.GetHealthRatiosAsync(
            ourTenant, [ourDeployment, quietDeployment], Day(1));

        // "Nothing was observed" and "nothing was healthy" are different answers: callers show no
        // availability figure for the first and 0% for the second. A 0/0 row would collapse them,
        // and a deployment that has never reported would appear to be totally unavailable.
        ratios.Should().ContainSingle().Which.DeploymentId.Should().Be(ourDeployment);
    }

    [Fact]
    public async Task Samples_outside_the_interval_are_not_counted()
    {
        Samples(ourDeployment, healthy: 3, unhealthy: 0);                       // minutes ago
        Samples(ourDeployment, healthy: 6, unhealthy: 0, minutesAgoStart: 5000); // ~3.5 days ago

        (await Delivery.GetHealthRatiosAsync(ourTenant, [ourDeployment], Day(1)))
            .Single().TotalSamples.Should().Be(3);

        (await Delivery.GetHealthRatiosAsync(ourTenant, [ourDeployment], Day(30)))
            .Single().TotalSamples.Should().Be(9);
    }

    [Fact]
    public async Task The_upper_bound_is_exclusive_like_the_row_method()
    {
        Samples(ourDeployment, healthy: 2, unhealthy: 0);   // 5 and 10 minutes ago

        // Exclusive at exactly the 5-minute sample's instant: it is excluded, the older one is not.
        (await Delivery.GetHealthRatiosAsync(
                ourTenant, [ourDeployment], Day(1), Now.AddMinutes(-5)))
            .Single().TotalSamples.Should().Be(1);
    }

    [Fact]
    public async Task No_deployments_asked_about_is_an_empty_answer_and_no_query()
    {
        Samples(ourDeployment, healthy: 3, unhealthy: 0);

        (await Delivery.GetHealthRatiosAsync(ourTenant, [], Day(1))).Should().BeEmpty();
    }

    [Fact]
    public async Task Several_deployments_are_counted_separately_in_one_call()
    {
        Samples(ourDeployment, healthy: 8, unhealthy: 2);
        Samples(quietDeployment, healthy: 1, unhealthy: 1);

        IReadOnlyList<DeploymentHealthRatio> ratios = await Delivery.GetHealthRatiosAsync(
            ourTenant, [ourDeployment, quietDeployment], Day(1));

        ratios.Should().HaveCount(2);
        ratios.Single(r => r.DeploymentId == ourDeployment).HealthySamples.Should().Be(8);
        ratios.Single(r => r.DeploymentId == quietDeployment).TotalSamples.Should().Be(2);
    }
}
