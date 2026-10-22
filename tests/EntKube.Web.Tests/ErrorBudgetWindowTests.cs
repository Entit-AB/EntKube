using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>ErrorBudgetService</c> measures each deployment over <b>its own</b> SLA window.
///
/// <para><b>This is the service's first test, and it was written because its windowing was
/// restructured.</b> It used to load every health sample in the widest window across the tenant
/// and slice each deployment's own window out in memory; it now asks
/// <c>IDeliveryApi.GetHealthRatiosAsync</c> once per distinct window, with the database doing the
/// counting. Nothing proved the per-deployment windows were respected before that change, so
/// "behaviour preserved" was a claim with nothing behind it.</para>
///
/// <para>The fixture makes the two windows disagree on purpose: a deployment on a 7-day target and
/// one on a 30-day target, with failures that fall only in the older part of the range. If the
/// windows were conflated in either direction — everyone measured over the widest, or everyone
/// over the narrowest — these numbers move.</para>
/// </summary>
public sealed class ErrorBudgetWindowTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid weeklyApp = Guid.NewGuid();
    private readonly Guid monthlyApp = Guid.NewGuid();
    private readonly Guid weeklyDeployment = Guid.NewGuid();
    private readonly Guid monthlyDeployment = Guid.NewGuid();

    public ErrorBudgetWindowTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        Guid envId = Guid.NewGuid(), clusterId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "t", Slug = "t" });
        db.Environments.Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "prod" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "c1", ApiServerUrl = "https://c1.example",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "cust" });

        foreach ((Guid appId, Guid depId, string name) in new[]
        {
            (weeklyApp, weeklyDeployment, "weekly"),
            (monthlyApp, monthlyDeployment, "monthly"),
        })
        {
            db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = name });
            db.AppDeployments.Add(new AppDeployment
            {
                Id = depId, AppId = appId, ClusterId = clusterId, EnvironmentId = envId,
                Name = name, Namespace = name, IsManaged = true,
            });
        }

        // Same target percentage, different measurement windows — so only the window can explain
        // a difference in the achieved figure.
        db.SlaTargets.Add(new SlaTarget
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId, AppId = weeklyApp,
            TargetPercent = 99.0, MeasurementWindowDays = 7,
        });
        db.SlaTargets.Add(new SlaTarget
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId, AppId = monthlyApp,
            TargetPercent = 99.0, MeasurementWindowDays = 30,
        });

        db.SaveChanges();
    }

    private void Sample(Guid deploymentId, Data.HealthStatus status, double daysAgo)
        => db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            HealthStatus = status,
            SnapshotAt = DateTime.UtcNow.AddDays(-daysAgo),
        });

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ErrorBudgetService Sut => new(new DeliveryApi(factory), new SupportApi(factory));

    /// <summary>
    /// The same history, read through two different windows, gives two different answers.
    ///
    /// <para>Both deployments get one healthy sample inside the last day and one degraded sample
    /// twenty days back. The 7-day deployment cannot see the failure, so it is 100%; the 30-day one
    /// can, so it is 50%. Conflating the windows collapses both to the same number.</para>
    /// </summary>
    [Fact]
    public async Task Each_deployment_is_measured_over_its_own_sla_window()
    {
        foreach (Guid dep in new[] { weeklyDeployment, monthlyDeployment })
        {
            Sample(dep, Data.HealthStatus.Healthy, 0.5);
            Sample(dep, Data.HealthStatus.Degraded, 20);
        }
        await db.SaveChangesAsync();

        List<ErrorBudgetStatus> statuses = await Sut.GetTenantErrorBudgetsAsync(tenantId);

        statuses.Should().HaveCount(2);
        statuses.Single(s => s.DeploymentId == weeklyDeployment)
            .AchievedPercent.Should().Be(100, "the 20-day-old failure is outside a 7-day window");
        statuses.Single(s => s.DeploymentId == monthlyDeployment)
            .AchievedPercent.Should().Be(50, "a 30-day window sees one healthy and one degraded");
    }

    /// <summary>
    /// The burn window is one day and is its own question, not a slice of the SLA window.
    ///
    /// <para>The failures are all inside the last day, so recent burn is worse than the window
    /// average — which is the whole point of reporting it. If the burn rate were computed over the
    /// SLA window it would equal the consumed fraction and tell an operator nothing.</para>
    /// </summary>
    [Fact]
    public async Task The_recent_burn_window_is_narrower_than_the_sla_window()
    {
        // 30-day deployment: healthy for most of the month, failing today.
        Sample(monthlyDeployment, Data.HealthStatus.Healthy, 25);
        Sample(monthlyDeployment, Data.HealthStatus.Healthy, 20);
        Sample(monthlyDeployment, Data.HealthStatus.Degraded, 0.1);
        await db.SaveChangesAsync();

        ErrorBudgetStatus status = (await Sut.GetTenantErrorBudgetsAsync(tenantId))
            .Single(s => s.DeploymentId == monthlyDeployment);

        status.AchievedPercent.Should().BeApproximately(66.67, 0.01, "two of three over 30 days");

        // Over the last day the only sample is the degraded one, so the recent rate is worse than
        // the window rate. Asserting the ORDER rather than the value keeps this about the windows.
        status.BurnRateRecent.Should().NotBeNull();
        status.BudgetConsumedFraction.Should().NotBeNull();
        status.BurnRateRecent.Should().BeGreaterThan(status.BudgetConsumedFraction!.Value,
            "every failure is in the last day, so the recent window must look worse");
    }

    /// <summary>
    /// A deployment with no samples in its window reports no figure, not 0%.
    ///
    /// <para>The contract omits a deployment that has nothing in the interval, so the service sees
    /// a missing entry rather than 0/0. Reporting 0% would make a workload that has never been
    /// sampled — one deployed five minutes ago — look like a total outage, and it is the error
    /// budget that an on-call rota is paged from.</para>
    /// </summary>
    [Fact]
    public async Task A_deployment_with_no_samples_reports_no_availability_rather_than_zero()
    {
        Sample(monthlyDeployment, Data.HealthStatus.Healthy, 1);
        await db.SaveChangesAsync();

        List<ErrorBudgetStatus> statuses = await Sut.GetTenantErrorBudgetsAsync(tenantId);

        statuses.Single(s => s.DeploymentId == weeklyDeployment)
            .AchievedPercent.Should().BeNull();
        statuses.Single(s => s.DeploymentId == monthlyDeployment)
            .AchievedPercent.Should().Be(100);
    }

    [Fact]
    public async Task A_stranger_gets_no_error_budgets_for_this_tenant()
    {
        Sample(monthlyDeployment, Data.HealthStatus.Healthy, 1);
        await db.SaveChangesAsync();

        (await Sut.GetTenantErrorBudgetsAsync(Guid.NewGuid())).Should().BeEmpty();
        (await Sut.GetTenantErrorBudgetsAsync(tenantId)).Should().NotBeEmpty();
    }
}
