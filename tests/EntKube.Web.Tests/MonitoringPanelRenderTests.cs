using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static EntKube.Web.Tests.ScrapedClusterFake;

namespace EntKube.Web.Tests;

/// <summary>
/// The database, RabbitMQ and Keycloak monitoring panels over a Prometheus that answers: which
/// card a button acts on, and what an unscraped workload is told.
/// </summary>
public sealed class MonitoringPanelRenderTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb = new(TestRootKey);
    private readonly ScrapedClusterFake cluster = new();

    public MonitoringPanelRenderTests()
    {
        EntKube.Web.Modules.Api.CatalogApi catalog = new(testDb.Factory);
        EntKube.Web.Modules.Api.DeliveryApi delivery = new(testDb.Factory);
        VaultService vault = testDb.CreateVaultService();

        Services.AddSingleton(cluster.Prometheus);
        Services.AddSingleton<IDbContextFactory<DataServicesDbContext>>(testDb.Factory);
        Services.AddSingleton(new CnpgService(
            testDb.Factory, vault, TestServices.NoClusterAccess, catalog, delivery, TestServices.NewGate()));
        Services.AddSingleton(new RabbitMQService(
            testDb.Factory, TestServices.NoClusterAccess, vault, catalog, delivery));

        cluster.ServeList(PrometheusesPath, ReadsEverything);
        cluster.ServeList(PodMonitorsPath);
        cluster.ServeList(ServiceMonitorsPath);

        using ApplicationDbContext db = testDb.CreateContext();
        Guid env = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = cluster.TenantId, Name = "Owner", Slug = "owner" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = env, TenantId = cluster.TenantId, Name = "prod" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster.ClusterId, TenantId = cluster.TenantId, EnvironmentId = env,
            Name = "prod", ApiServerUrl = "https://k8s.example.com",
        });
        db.SaveChanges();
    }

    public new void Dispose()
    {
        testDb.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Seed(params object[] rows)
    {
        using ApplicationDbContext db = testDb.CreateContext();
        db.AddRange(rows);
        db.SaveChanges();
    }

    private CnpgCluster Database(string ns, string name) => new()
    {
        Id = Guid.NewGuid(), TenantId = cluster.TenantId, KubernetesClusterId = cluster.ClusterId,
        Name = name, Namespace = ns, PostgresVersion = "17", StorageSize = "10Gi",
    };

    private RabbitMQCluster Broker(string ns, string name, bool external = false) => new()
    {
        Id = Guid.NewGuid(), TenantId = cluster.TenantId, KubernetesClusterId = cluster.ClusterId,
        Name = name, Namespace = ns, RabbitMQVersion = "4.2", StorageSize = "10Gi",
        IsOperatorManaged = !external, StatefulSetName = external ? name : null,
    };

    // ──────── Two databases of one name ────────

    private IRenderedComponent<DatabaseMonitoringPanel> TwoDatabasesCalledShop()
    {
        Seed(Database("billing", "shop"), Database("web", "shop"));
        cluster.ServeDatabasePods("billing", "shop");
        cluster.ServeDatabasePods("web", "shop");

        IRenderedComponent<DatabaseMonitoringPanel> cut = Render<DatabaseMonitoringPanel>(
            ps => ps.Add(p => p.ClusterId, cluster.ClusterId));
        cut.WaitForAssertion(() => cut.FindAll("[data-database]").Should().HaveCount(2));
        return cut;
    }

    private static IElement Button(IElement card, string text) =>
        card.QuerySelectorAll("button").Single(b => b.TextContent.Contains(text));

    /// <summary>⭐ The button on the second card diagnosed the first database and showed it on both.</summary>
    [Fact]
    public void Diagnosing_the_second_database_of_a_name_diagnoses_the_second()
    {
        IRenderedComponent<DatabaseMonitoringPanel> cut = TwoDatabasesCalledShop();
        cluster.ServeList(PodMonitorsPath, PodMonitor("billing", "shop", """{"matchLabels":{"cnpg.io/cluster":"shop"}}"""));

        Button(cut.Find("[data-database='web/shop']"), "Why is it empty?").Click();

        cut.WaitForAssertion(() => cut.Find("[data-database='web/shop']").TextContent
            .Should().Contain("No PodMonitor", "the only monitor is the other namespace's").And.Contain("Apply monitors now"));
        cluster.Requests.Should().Contain("/api/v1/namespaces/web/pods")
            .And.NotContain("/api/v1/namespaces/billing/pods");
        cut.Find("[data-database='billing/shop']").TextContent.Should().NotContain("No PodMonitor");
    }

    /// <summary>The other button had the same defect and a worse outcome: it applied the wrong database.</summary>
    [Fact]
    public void What_the_second_databases_other_button_did_is_shown_on_the_second_card_alone()
    {
        IRenderedComponent<DatabaseMonitoringPanel> cut = TwoDatabasesCalledShop();

        Button(cut.Find("[data-database='web/shop']"), "Enable metrics").Click();

        // No cluster credential in this test, so the apply is refused, and where it says so is the point.
        cut.WaitForAssertion(() => cut.Find("[data-database='web/shop']").QuerySelectorAll(".text-danger")
            .Should().ContainSingle());
        cut.Find("[data-database='billing/shop']").QuerySelectorAll(".text-danger").Should().BeEmpty();
    }

    [Fact]
    public void A_diagnosis_that_could_not_read_the_monitors_is_an_error_on_the_card_and_not_a_finding()
    {
        IRenderedComponent<DatabaseMonitoringPanel> cut = TwoDatabasesCalledShop();
        cluster.Refuse(PodMonitorsPath, System.Net.HttpStatusCode.Forbidden);

        Button(cut.Find("[data-database='web/shop']"), "Why is it empty?").Click();

        cut.WaitForAssertion(() => cut.Find("[data-database='web/shop'] .text-danger").TextContent
            .Should().Contain("Could not read the PodMonitors"));
        cut.Find("[data-database='web/shop']").TextContent.Should().NotContain("No PodMonitor");
    }

    // ──────── A database's sizes ────────

    [Fact]
    public void Each_database_of_a_three_instance_cluster_is_listed_once()
    {
        PrometheusService.CnpgPanelQueries("web", "shop").Sizes.Should().Be(
            "max by (datname) (cnpg_pg_database_size_bytes{namespace=\"web\",pod=~\"shop-[0-9]+\",datname!=\"\"})");
    }

    // ──────── RabbitMQ ────────

    private IRenderedComponent<RabbitMQMonitoringPanel> Brokers(params RabbitMQCluster[] brokers)
    {
        Seed([.. brokers]);
        IRenderedComponent<RabbitMQMonitoringPanel> cut = Render<RabbitMQMonitoringPanel>(ps => ps
            .Add(p => p.ClusterId, cluster.ClusterId).Add(p => p.TenantId, cluster.TenantId));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading RabbitMQ metrics"));
        return cut;
    }

    private static bool IsNodeCount(string query) => query.StartsWith("sum(rabbitmq_identity_info", StringComparison.Ordinal);

    /// <summary>One node that reports file descriptors, and sockets only when asked to.</summary>
    private void ScrapedBroker(bool sockets) => cluster.Query = q =>
        IsNodeCount(q) ? Series("1")
        : q.Contains("_fds") || (sockets && q.Contains("_tcp_sockets")) ? Series("100", "\"rabbitmq_node\":\"rabbit@orders-0\"")
        : q.Contains("or vector(0)") ? Series("0") : NoSeries;

    [Fact]
    public void An_unscraped_broker_is_not_called_healthy_and_its_diagnosis_can_be_reached()
    {
        cluster.Query = q => q.Contains("or vector(0)") ? Series("0") : NoSeries;

        IRenderedComponent<RabbitMQMonitoringPanel> cut = Brokers(Broker("mq", "orders"));

        cut.Markup.Should().Contain("Prometheus holds no metrics for this broker").And.Contain("Why is this empty?")
            .And.NotContain("Healthy");
    }

    [Fact]
    public void A_scraped_idle_broker_is_shown_with_its_figures()
    {
        ScrapedBroker(sockets: false);

        IRenderedComponent<RabbitMQMonitoringPanel> cut = Brokers(Broker("mq", "orders"));

        cut.Markup.Should().Contain("Healthy").And.Contain("File descriptors")
            .And.NotContain("Prometheus holds no metrics");
    }

    [Fact]
    public void A_broker_that_reports_no_sockets_has_no_sockets_meter()
    {
        ScrapedBroker(sockets: false);

        Brokers(Broker("mq", "orders")).FindAll("th").Select(h => h.TextContent)
            .Should().Contain("File descriptors").And.NotContain("Sockets");
    }

    [Fact]
    public void A_broker_that_does_report_sockets_has_the_meter()
    {
        ScrapedBroker(sockets: true);

        IRenderedComponent<RabbitMQMonitoringPanel> cut = Brokers(Broker("mq", "orders"));

        cut.FindAll("th").Select(h => h.TextContent).Should().Contain("Sockets");
        cut.Markup.Should().Contain("100 / 100");
    }

    [Fact]
    public void A_broker_whose_queries_were_refused_is_not_called_healthy()
    {
        cluster.Query = q => IsNodeCount(q)
            ? Series("1")
            : (System.Net.HttpStatusCode.OK, """{"status":"error","error":"multiple matches for labels"}""");

        Brokers(Broker("mq", "orders")).Markup.Should()
            .Contain("multiple matches for labels").And.NotContain("Healthy");
    }

    [Fact]
    public void A_broker_that_could_not_be_asked_about_is_not_called_unscraped()
    {
        cluster.Query = _ => (System.Net.HttpStatusCode.ServiceUnavailable, "");

        Brokers(Broker("mq", "orders")).Markup.Should()
            .Contain("Prometheus query failed").And.NotContain("Prometheus holds no metrics");
    }

    [Fact]
    public void An_external_broker_is_diagnosed_by_its_own_stateful_set_and_told_to_make_its_own_monitor()
    {
        cluster.Query = q => q.Contains("or vector(0)") ? Series("0") : NoSeries;
        cluster.ServeList("/api/v1/namespaces/legacy/services",
            """{"metadata":{"name":"rabbit","namespace":"legacy","labels":{"chart":"rabbitmq"}},"spec":{"selector":{"app":"rabbit"}}}""");
        cluster.Serve("/apis/apps/v1/namespaces/legacy/statefulsets/rabbit",
            """{"metadata":{"name":"rabbit"},"spec":{"selector":{},"serviceName":"rabbit","template":{"metadata":{"labels":{"app":"rabbit"}}}}}""");

        IRenderedComponent<RabbitMQMonitoringPanel> cut = Brokers(Broker("legacy", "rabbit", external: true));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Why is this empty?")).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No ServiceMonitor").And.Contain("by hand"));
        cut.Markup.Should().NotContain("Apply monitors now");
        cluster.Queries.Should().Contain(q => q.Contains("namespace=~\"legacy|\""));
    }

    // ──────── Keycloak ────────

    [Fact]
    public void An_unscraped_keycloak_is_told_to_apply_monitors_and_not_to_edit_helm_values()
    {
        cluster.ServeList("/api/v1/namespaces/iam/services", Labelled("iam", "sso-keycloakx-http",
            "\"app.kubernetes.io/name\":\"keycloakx\",\"app.kubernetes.io/instance\":\"sso\",\"app.kubernetes.io/component\":\"http\""));

        IRenderedComponent<KeycloakMonitoringPanel> cut = Render<KeycloakMonitoringPanel>(ps => ps
            .Add(p => p.TenantId, cluster.TenantId).Add(p => p.ClusterId, cluster.ClusterId)
            .Add(p => p.Namespace, "iam").Add(p => p.ReleaseName, "sso"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Why is this empty?"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Why is this empty?")).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No ServiceMonitor"));
        cut.Markup.Should().Contain("Apply monitors now")
            .And.NotContain("serviceMonitor.enabled").And.NotContain("switched off");
    }
}
