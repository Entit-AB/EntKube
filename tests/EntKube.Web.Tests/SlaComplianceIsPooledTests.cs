using System.Globalization;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// How an SLA target's compliance is aggregated across the deployments in its scope.
///
/// <para><b>Why this test exists at all.</b> The page moved off <c>ApplicationDbContext</c> and now
/// asks <c>IDeliveryApi.GetHealthRatiosAsync</c> instead of counting health snapshots itself. That
/// contract returns a ratio <em>per deployment</em>, and the obvious way to combine per-deployment
/// ratios — averaging them — is <b>not</b> what this page has ever reported.</para>
///
/// <para>It pools: every sample in scope counted once, so a busy deployment weighs more than a
/// quiet one. The removed query selected every snapshot across the whole scope and divided
/// healthy by total, and summing <c>HealthySamples</c> and <c>TotalSamples</c> is that same
/// arithmetic with the counting left in the database.</para>
///
/// <para>⚠️ The two agree whenever deployments have equal sample counts, which is why the fixture
/// makes them <b>unequal</b>: ten-of-ten and one-of-five pool to 11/15 = <b>73.33%</b> and average
/// to (100 + 20) / 2 = <b>60%</b>. A test built on equal counts would pass against either and
/// would have let a silent change of meaning through.</para>
/// </summary>
public class SlaComplianceIsPooledTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();

    public SlaComplianceIsPooledTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);

        Seed();

        EntKube.Web.Modules.Api.DeliveryApi delivery = new(factory);
        Services.AddEveryContext(factory);
        Services.AddSingleton<EntKube.Contracts.Delivery.IDeliveryApi>(delivery);
        Services.AddSingleton(new IncidentService(factory, delivery));
        Services.AddSingleton(new ToastService());
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Seed()
    {
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        Data.Environment environment = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production" };
        db.Environments.Add(environment);

        Guid clusterId = Guid.NewGuid();
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environment.Id,
            Name = "prod", ApiServerUrl = "https://prod.example.com:6443",
        });

        Guid customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });

        Guid appId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });

        // Unequal sample counts, so pooling and averaging disagree.
        AddDeployment(appId, environment.Id, clusterId, "busy", healthy: 10, total: 10);
        AddDeployment(appId, environment.Id, clusterId, "quiet", healthy: 1, total: 5);

        db.SlaTargets.Add(new SlaTarget
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId, AppId = appId,
            TargetPercent = 99.0, MeasurementWindowDays = 30,
        });

        db.SaveChanges();
    }

    private void AddDeployment(
        Guid appId, Guid environmentId, Guid clusterId, string name, int healthy, int total)
    {
        Guid id = Guid.NewGuid();

        db.AppDeployments.Add(new AppDeployment
        {
            Id = id, AppId = appId, EnvironmentId = environmentId, ClusterId = clusterId,
            Name = name, Namespace = name, HealthStatus = HealthStatus.Healthy,
        });

        for (int i = 0; i < total; i++)
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(),
                DeploymentId = id,
                SnapshotAt = DateTime.UtcNow.AddHours(-1).AddMinutes(-i),
                HealthStatus = i < healthy ? HealthStatus.Healthy : HealthStatus.Degraded,
            });
        }
    }

    [Fact]
    public void Compliance_pools_every_sample_in_scope_rather_than_averaging_the_deployments()
    {
        IRenderedComponent<SlaTargetManager> cut =
            Render<SlaTargetManager>(ps => ps.Add(p => p.TenantId, tenantId));

        cut.WaitForState(() => !cut.Markup.Contains("Loading"), TimeSpan.FromSeconds(10));

        // ⚠️ Built the way the page builds it: ToString("F2") is culture-sensitive and renders
        // "73,33" on this Swedish host. Typing the digits would fail against correct code.
        string pooled = (11d / 15d * 100d).ToString("F2", CultureInfo.CurrentCulture);
        string averaged = 60d.ToString("F2", CultureInfo.CurrentCulture);

        cut.Markup.Should().Contain(pooled,
            "11 healthy samples of 15 in scope is 73.33% — every sample counted once");

        cut.Markup.Should().NotContain(averaged,
            "60 is the mean of the two deployments' ratios, which weighs a five-sample "
            + "deployment the same as a ten-sample one and is not what this page reports");
    }
}
