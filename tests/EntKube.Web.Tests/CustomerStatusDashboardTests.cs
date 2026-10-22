using System.Globalization;
using Bunit;
using EntKube.Web.Components.Pages.Portal;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The uptime a customer reads on their own portal.
///
/// <para><b>The defect this was written for.</b> The figure was built with
/// <c>u = u.HasValue ? (u + pct) / 2 : pct</c> over an app's deployments — a running pairwise
/// mean, not a mean. For three deployments it yields <c>a/4 + b/4 + c/2</c>: the last counted
/// double and the first a quarter. Correct for one or two deployments and wrong for every app
/// with more, and because the weighting follows the order the deployments happen to come back in,
/// the number looks plausible whatever it is.</para>
///
/// <para>⚠️ There was no test of any kind for this component before — the only mention of it
/// anywhere in the suite was its line in <see cref="RazorGodContextRatchetTests"/>. That is the
/// same shape as the cost-rate routes: a customer-facing figure with full logic and no
/// coverage.</para>
///
/// <para>The arithmetic is chosen so the two answers cannot be confused: three deployments at
/// 100%, 100% and 40% mean <b>80</b>, where the pairwise version gives <b>70</b>. Both are
/// asserted — the right one present, the wrong one absent — so this cannot pass by rendering
/// nothing.</para>
/// </summary>
public class CustomerStatusDashboardTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();
    private Customer customer = null!;

    public CustomerStatusDashboardTests()
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
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// One app with three deployments whose health histories are 100%, 100% and 40%.
    /// </summary>
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

        Customer seeded = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Contoso" };
        db.Customers.Add(seeded);

        // ⚠️ App carries no TenantId: the tenant is reached through Customer. That traversal is
        // Delivery's own predicate, which is why GetDeploymentsForTenantAsync exists and why the
        // page no longer does it by hand.
        App app = new()
        {
            Id = Guid.NewGuid(), CustomerId = seeded.Id, Name = "billing",
        };
        db.Apps.Add(app);

        // Healthy counts chosen so the mean (80) and the pairwise mean (70) differ.
        AddDeployment(app.Id, environment.Id, clusterId, "billing-a", healthy: 5, total: 5);
        AddDeployment(app.Id, environment.Id, clusterId, "billing-b", healthy: 5, total: 5);
        AddDeployment(app.Id, environment.Id, clusterId, "billing-c", healthy: 2, total: 5);

        db.SaveChanges();

        customer = db.Customers
            .Include(c => c.Apps)
            .AsNoTracking()
            .Single(c => c.Id == seeded.Id);
    }

    private void AddDeployment(
        Guid appId, Guid environmentId, Guid clusterId, string name, int healthy, int total)
    {
        Guid deploymentId = Guid.NewGuid();

        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId,
            AppId = appId,
            EnvironmentId = environmentId,
            ClusterId = clusterId,
            Name = name,
            Namespace = name,
            HealthStatus = HealthStatus.Healthy,
        });

        // Well inside both windows, so 7d and 30d agree and a single assertion covers each.
        DateTime at = DateTime.UtcNow.AddHours(-1);

        for (int i = 0; i < total; i++)
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(),
                DeploymentId = deploymentId,
                SnapshotAt = at.AddMinutes(-5 * i),
                HealthStatus = i < healthy ? HealthStatus.Healthy : HealthStatus.Degraded,
            });
        }
    }

    [Fact]
    public void The_uptime_shown_is_the_mean_across_an_apps_deployments()
    {
        IRenderedComponent<CustomerStatusDashboard> cut =
            Render<CustomerStatusDashboard>(ps => ps.Add(p => p.Customer, customer));

        cut.WaitForState(() => !cut.Markup.Contains("Loading"), TimeSpan.FromSeconds(10));

        // ⚠️ Built the same way the page builds it, never hardcoded. The markup comes from
        // ToString("F2"), which is culture-sensitive: on this Swedish host the page renders
        // "80,00%". The first version of this test asserted "80.00%" and failed against a page
        // that had computed 80 perfectly well — a test can be wrong about a right answer, and a
        // hardcoded decimal separator is one of the easier ways to manage it.
        string mean = 80d.ToString("F2", CultureInfo.CurrentCulture) + "%";
        string pairwise = 70d.ToString("F2", CultureInfo.CurrentCulture) + "%";

        cut.Markup.Should().Contain(mean,
            "100, 100 and 40 average to 80 — one figure per app, across its deployments");

        cut.Markup.Should().NotContain(pairwise,
            "70 is what the running pairwise mean produced: ((100 + 100) / 2 + 40) / 2");
    }

    /// <summary>
    /// The health ratios come from <c>IDeliveryApi</c>, so this page no longer reads Delivery's
    /// tables itself — the reason it could finally come off <c>ApplicationDbContext</c>. Rendering
    /// at all is the assertion: the component resolves <c>TelemetryDbContext</c> now, and a page
    /// still asking for the all-seeing context would fail to resolve its injection.
    /// </summary>
    [Fact]
    public void The_dashboard_renders_without_the_all_seeing_context()
    {
        IRenderedComponent<CustomerStatusDashboard> cut =
            Render<CustomerStatusDashboard>(ps => ps.Add(p => p.Customer, customer));

        cut.WaitForState(() => !cut.Markup.Contains("Loading"), TimeSpan.FromSeconds(10));

        cut.Markup.Should().Contain("billing", "the app row has to be there for the rest to mean anything");
    }
}
