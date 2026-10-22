using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether a deployment that met its objective is reported as having breached it.
///
/// <para><b>The defect.</b> The error budget is <c>(100 - achieved) / (100 - target)</c> — both
/// terms small differences of numbers near 100. <c>Availability</c> rounded <c>achieved</c> to two
/// decimals <em>before</em> that division, which snaps any availability within half a hundredth of
/// a two-decimal target onto the target exactly. After that the numerator and denominator are the
/// same expression, bit for bit, and the quotient is exactly <c>1.0</c>.</para>
///
/// <para>⚠️ <c>IsBreached</c> was then <c>&gt;= 1.0</c>, contradicting the contract written three
/// lines above it on <c>BudgetConsumedFraction</c>: "0 = pristine, 1.0 = exhausted, &gt;1 =
/// breached". So <c>OperationsAdvisor</c> raised a <b>Critical, Overdue</b> finding titled "SLO
/// breached", whose own text read <c>"Availability 99.9% is below the 99.9% objective"</c> —
/// served to clients over <c>GET /api/v1/advisor/findings</c>. The message contradicting itself is
/// the tell.</para>
///
/// <para>Two independent causes, so two independent tests: one for the rounding and one for the
/// comparison. A fix for either alone still reports a false breach for some input.</para>
/// </summary>
public sealed class ErrorBudgetRoundingTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid deploymentId = Guid.NewGuid();

    public ErrorBudgetRoundingTests()
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
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, ClusterId = clusterId, EnvironmentId = envId,
            Name = "billing", Namespace = "billing", IsManaged = true,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ErrorBudgetService Sut => new(new DeliveryApi(factory), new SupportApi(factory));

    private void Target(double percent) => db.SlaTargets.Add(new SlaTarget
    {
        Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId, AppId = appId,
        TargetPercent = percent, MeasurementWindowDays = 7,
    });

    /// <summary>Samples spread inside the window, so every one is counted by both windows.</summary>
    private void Samples(int healthy, int degraded)
    {
        for (int i = 0; i < healthy + degraded; i++)
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(),
                DeploymentId = deploymentId,
                HealthStatus = i < healthy ? Data.HealthStatus.Healthy : Data.HealthStatus.Degraded,
                SnapshotAt = DateTime.UtcNow.AddHours(-1).AddSeconds(-i),
            });
        }
    }

    private async Task<ErrorBudgetStatus> StatusAsync()
    {
        await db.SaveChangesAsync();
        return (await Sut.GetTenantErrorBudgetsAsync(tenantId)).Single();
    }

    /// <summary>
    /// ⭐ <b>The rounding.</b> 1000 healthy of 1001 is 99.90010%, which <em>exceeds</em> a 99.9%
    /// objective — the budget is 99.9% spent, not 100%. Rounded to two decimals first it became
    /// exactly 99.9, numerator and denominator collapsed to the same value, and the budget read
    /// 1.0.
    /// </summary>
    [Fact]
    public async Task An_availability_just_above_the_target_is_not_a_breach()
    {
        Target(99.9);
        Samples(healthy: 1000, degraded: 1);

        ErrorBudgetStatus status = await StatusAsync();

        status.IsBreached.Should().BeFalse(
            "99.90010% meets a 99.9% objective; only rounding made it look exhausted");

        status.BudgetConsumedFraction.Should().BeApproximately(0.999d, 0.001d,
            "the budget is very nearly spent, which is a different statement from breached");

        status.AchievedPercent.Should().Be(99.9d,
            "the reported figure is still rounded for display — it is read, not divided");
    }

    /// <summary>
    /// <b>The comparison.</b> 99 healthy of 100 against a 99.0% objective is exactly 99.0%:
    /// availability equal to the target, which <em>meets</em> it, and a budget exactly spent.
    /// <c>BudgetConsumedFraction</c>'s own contract calls 1.0 "exhausted" and reserves "breached"
    /// for above it; <c>IsBreached</c> said otherwise.
    ///
    /// <para>This case survives the rounding fix untouched — 99.0 needs no rounding — so it
    /// isolates the comparison.</para>
    /// </summary>
    [Fact]
    public async Task A_budget_exactly_spent_is_exhausted_but_not_breached()
    {
        Target(99.0);
        Samples(healthy: 99, degraded: 1);

        ErrorBudgetStatus status = await StatusAsync();

        status.BudgetConsumedFraction.Should().Be(1.0d, "exactly the allowance, exactly spent");

        status.IsBreached.Should().BeFalse(
            "1.0 is exhausted and >1 is breached, which is what the field's own doc comment says");
    }

    /// <summary>
    /// A real shortfall is still caught. ⚠️ Without this the two tests above could be satisfied by
    /// a fix that simply stopped reporting breaches, which would be the worse defect of the two —
    /// a missed objective is the thing the advisor exists to raise.
    /// </summary>
    [Fact]
    public async Task A_genuine_shortfall_is_still_a_breach()
    {
        Target(99.9);
        Samples(healthy: 990, degraded: 10);

        ErrorBudgetStatus status = await StatusAsync();

        status.AchievedPercent.Should().Be(99.0d);
        status.BudgetConsumedFraction.Should().BeApproximately(10.0d, 0.01d,
            "a 1% error rate against a 0.1% allowance is ten budgets");
        status.IsBreached.Should().BeTrue();
    }
}
