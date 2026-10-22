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
/// The window an SLA verdict is measured over, on the page the customer reads.
///
/// <para><b>The defect.</b> A <c>SlaTarget</c> carries <c>MeasurementWindowDays</c> beside
/// <c>TargetPercent</c>, and the two are one statement: "99% over 7 days" is a different
/// commitment from "99% over 30". <c>CustomerSlaReport</c> fetched uptime for a literal 7 and a
/// literal 30, projected only <c>TargetPercent</c> into its row, and judged
/// <c>Meeting SLA</c> / <c>Below SLA</c> against the <b>30-day</b> figure whatever the target
/// said. <c>MeasurementWindowDays</c> appeared nowhere in the file.</para>
///
/// <para>⚠️ Every other reader of a target honours the stored window —
/// <c>SlaTargetManager</c> measures compliance from
/// <c>UtcNow.AddDays(-t.MeasurementWindowDays)</c>, and <c>ErrorBudgetService</c> groups its
/// health-ratio calls by the window each target names. This page was the one place that did not,
/// and it is the only one of the three a customer sees.</para>
///
/// <para>The fixture makes the two answers opposite rather than merely different: a 7-day target
/// at 99%, with the last 7 days fully healthy and the fortnight before it fully unhealthy. Over
/// 7 days that is 100% — met. Over 30 it is 50% — not met. So the old code renders
/// <c>Below SLA</c> and the corrected code renders <c>Meeting SLA</c>, and the test asserts the
/// right badge is present AND the wrong one absent.</para>
/// </summary>
public class CustomerSlaReportWindowTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();
    private Guid customerId, appId;
    private List<AppDeployment> deployments = [];

    public CustomerSlaReportWindowTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);

        Seed(windowDays: 7);

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

    private void Seed(int windowDays)
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

        customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });

        appId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });

        Guid deploymentId = Guid.NewGuid();
        AppDeployment deployment = new()
        {
            Id = deploymentId, AppId = appId, EnvironmentId = environment.Id, ClusterId = clusterId,
            Name = "billing-prod", Namespace = "billing", HealthStatus = HealthStatus.Healthy,
        };
        db.AppDeployments.Add(deployment);

        db.SlaTargets.Add(new SlaTarget
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId, AppId = appId,
            TargetPercent = 99.0, MeasurementWindowDays = windowDays,
        });

        // Healthy inside the 7-day window.
        for (int i = 0; i < 10; i++)
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(), DeploymentId = deploymentId,
                SnapshotAt = DateTime.UtcNow.AddDays(-1).AddMinutes(-5 * i),
                HealthStatus = HealthStatus.Healthy,
            });
        }

        // Unhealthy outside it but inside 30 days, so the two windows disagree.
        for (int i = 0; i < 10; i++)
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(), DeploymentId = deploymentId,
                SnapshotAt = DateTime.UtcNow.AddDays(-15).AddMinutes(-5 * i),
                HealthStatus = HealthStatus.Degraded,
            });
        }

        db.SaveChanges();
        deployments = [deployment];
    }

    private IRenderedComponent<CustomerSlaReport> RenderReport()
    {
        IRenderedComponent<CustomerSlaReport> cut = Render<CustomerSlaReport>(ps => ps
            .Add(p => p.Deployments, deployments)
            .Add(p => p.CustomerId, customerId)
            .Add(p => p.TenantId, tenantId));

        cut.WaitForState(() => !cut.Markup.Contains("Loading"), TimeSpan.FromSeconds(10));
        return cut;
    }

    [Fact]
    public void The_verdict_is_measured_over_the_window_the_target_names()
    {
        IRenderedComponent<CustomerSlaReport> cut = RenderReport();

        cut.Markup.Should().Contain("Meeting SLA",
            "the target is 99% over 7 days and the last 7 days were 100% healthy");

        cut.Markup.Should().NotContain("Below SLA",
            "50% is the 30-day figure, which this target does not describe and which the page "
            + "used to judge against regardless");
    }

    /// <summary>
    /// The window is shown beside the target, so the figure the badge rests on is legible rather
    /// than implied. ⚠️ Culture-formatted: the percentage is built the way the page builds it
    /// rather than typed, because <c>ToString("F1")</c> renders "99,0" on a Swedish host.
    /// </summary>
    [Fact]
    public void The_window_is_shown_next_to_the_target_it_qualifies()
    {
        IRenderedComponent<CustomerSlaReport> cut = RenderReport();

        cut.Markup.Should().Contain($"{99.0.ToString("F1", CultureInfo.CurrentCulture)}% / 7d",
            "a target percentage without its window is half a statement");
    }
}
