using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.ScrapeMonitors;
using EntKube.Contracts.DataServices;
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
    private readonly RecordingMonitors monitors = new();

    public MonitoringPanelRenderTests()
    {
        EntKube.Web.Modules.Api.CatalogApi catalog = new(testDb.Factory);
        EntKube.Web.Modules.Api.DeliveryApi delivery = new(testDb.Factory);
        VaultService vault = testDb.CreateVaultService();

        Services.AddSingleton(cluster.Prometheus);
        Services.AddSingleton<IDbContextFactory<DataServicesDbContext>>(testDb.Factory);
        Services.AddSingleton<IScrapeMonitors>(monitors);
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
        monitors.Answer = () => KubernetesOperationResult<ScrapeMonitorPlan>.Failure("The cluster refused the PodMonitors: Forbidden");

        Button(cut.Find("[data-database='web/shop']"), ApplyButton).Click();

        // Beside the button, on its own card, and red.
        cut.WaitForAssertion(() => cut.Find("[data-database='web/shop'] [data-apply] .text-danger").TextContent
            .Should().Contain("The cluster refused the PodMonitors: Forbidden"));
        cut.Find("[data-database='billing/shop']").QuerySelectorAll(".text-danger").Should().BeEmpty();
    }

    // ──────── Apply monitors now ────────

    /// <summary>The button's name, as the diagnosis's remedy spells it. One string, so they cannot drift apart.</summary>
    private static readonly string ApplyButton =
        System.Text.RegularExpressions.Regex.Match(ScrapeDiagnostician.ApplyMonitors, "\"([^\"]+)\"").Groups[1].Value;

    private const string WebShopPods = """{"matchLabels":{"cnpg.io/cluster":"shop","cnpg.io/podRole":"instance"}}""";

    /// <summary>The engine's row for a data service, keyed the way the engine keys it.</summary>
    private static MonitorPlanRow RowFor(ManagedServiceKind kind, string ns, string name, MonitorState state, string detail)
    {
        string key = ScrapeTargetCatalog.For([], [new ManagedServiceSummary(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, name, ns, ManagedServiceStatus.Running,
            IsOperatorManaged: true, IsInCluster: true, HasExporter: false)])[0].Key;
        return new MonitorPlanRow(key, name, kind.ToString(), ns, null, state, MonitorChange.None, detail, []);
    }

    private static KubernetesOperationResult<ScrapeMonitorPlan> Planned(params MonitorPlanRow[] rows) =>
        KubernetesOperationResult<ScrapeMonitorPlan>.Success(new ScrapeMonitorPlan(rows, [], []));

    [Fact]
    public void The_remedy_names_a_button_the_database_panel_has()
    {
        ApplyButton.Should().Be("Apply monitors now");

        TwoDatabasesCalledShop().FindAll("[data-database='web/shop'] button")
            .Select(b => b.TextContent.Trim()).Should().Contain(ApplyButton);
    }

    /// <summary>
    /// ⭐ The button rebuilt the database's whole Cluster spec from EntKube's rows, which could roll an
    /// imported or restored database. Now it asks the engine, which writes monitors and nothing else.
    /// </summary>
    [Fact]
    public void Apply_monitors_now_applies_the_clusters_monitors_once_and_diagnoses_again()
    {
        IRenderedComponent<DatabaseMonitoringPanel> cut = TwoDatabasesCalledShop();
        monitors.Answer = () => Planned(
            RowFor(ManagedServiceKind.Postgres, "billing", "shop", MonitorState.Managed, "Billing's row."),
            RowFor(ManagedServiceKind.Postgres, "web", "shop", MonitorState.Managed, "Web's row: scraped by EntKube's monitor."));
        monitors.OnApply = () => cluster.ServeList(PodMonitorsPath, PodMonitor("web", "entkube-cnpg-shop", WebShopPods));

        IElement web = cut.Find("[data-database='web/shop']");
        web.TextContent.Should().Contain("every database and component on this Kubernetes cluster",
            "the button applies the whole cluster's monitors, and says so");
        Button(web, ApplyButton).Click();

        cut.WaitForAssertion(() => cut.Find("[data-database='web/shop']").TextContent
            .Should().Contain("web/entkube-cnpg-shop selects database 'shop' and Prometheus reads it"));
        monitors.Applied.Should().Equal([(cluster.TenantId, cluster.ClusterId)]);
        cluster.Requests.Should().Contain("/api/v1/namespaces/web/pods", "the diagnosis ran again after the apply");

        string said = cut.Find("[data-database='web/shop'] [data-apply]").TextContent;
        said.Should().Contain("Web's row: scraped by EntKube's monitor.").And.NotContain("Billing's row.");
    }

    /// <summary>
    /// A database whose Cluster still asks CNPG for a PodMonitor keeps the operator's, and the engine
    /// adds none beside it. Nothing on the card may then send anybody to change the Cluster.
    /// </summary>
    [Fact]
    public void A_database_the_cnpg_operator_still_monitors_is_not_told_to_change_anything()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("web", "shop", WebShopPods));
        IRenderedComponent<DatabaseMonitoringPanel> cut = TwoDatabasesCalledShop();
        monitors.Answer = () => Planned(RowFor(ManagedServiceKind.Postgres, "web", "shop", MonitorState.CoveredElsewhere,
            "PodMonitor web/shop, which EntKube did not create, already selects it. EntKube adds no monitor of its own, "
            + "because two would count everything twice."));

        Button(cut.Find("[data-database='web/shop']"), ApplyButton).Click();

        cut.WaitForAssertion(() => cut.Find("[data-database='web/shop']").TextContent
            .Should().Contain("web/shop selects database 'shop' and Prometheus reads it"));
        string card = cut.Find("[data-database='web/shop']").TextContent;
        card.Should().Contain("already selects it");
        card.Should().NotContainAny(["enablePodMonitor", "Cluster resource", "Cluster spec", "spec pushed", "re-appl", "Re-appl", "Enable metrics"]);
        cut.Find("[data-database='web/shop'] pre").TextContent.Should().NotContain(ApplyButton,
            "the diagnosis has nothing to remedy where the operator's monitor is read");
    }

    [Fact]
    public void An_apply_that_throws_is_shown_beside_the_button_and_the_panel_stays()
    {
        IRenderedComponent<DatabaseMonitoringPanel> cut = TwoDatabasesCalledShop();
        monitors.OnApply = () => throw new InvalidOperationException("Vault is sealed.");

        Button(cut.Find("[data-database='web/shop']"), ApplyButton).Click();

        cut.WaitForAssertion(() => cut.Find("[data-database='web/shop'] [data-apply] .text-danger").TextContent
            .Should().Contain("Vault is sealed."));
        Button(cut.Find("[data-database='web/shop']"), ApplyButton).HasAttribute("disabled").Should().BeFalse();
    }

    /// <summary>The buttons that rebuilt a database's spec and wrote a broker's monitor are gone, not hidden.</summary>
    [Fact]
    public void Nothing_but_the_engine_writes_a_monitor_any_more()
    {
        typeof(CnpgService).GetMethod("ReapplyClusterSpecAsync").Should().BeNull(
            "it rebuilt an imported or restored database's spec from EntKube's rows and could restart it");
        typeof(RabbitMQService).GetMethod("EnableMetricsScrapeAsync").Should().BeNull(
            "the engine owns <name>-metrics now, and a second writer of it would undo the first");
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

    [Fact]
    public void A_broker_diagnosis_that_could_not_read_the_monitors_says_so_and_does_not_blame_prometheus()
    {
        cluster.Query = q => q.Contains("or vector(0)") ? Series("0") : NoSeries;
        cluster.Refuse(ServiceMonitorsPath, System.Net.HttpStatusCode.Forbidden);
        IRenderedComponent<RabbitMQMonitoringPanel> cut = Brokers(Broker("mq", "orders"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Why is this empty?")).Click();

        cut.WaitForAssertion(() => cut.Find("[data-diagnosis-error]").TextContent
            .Should().Contain("Could not read the ServiceMonitors"));
        cut.Markup.Should().NotContain("Prometheus query failed").And.NotContain("No ServiceMonitor");
    }

    private const string OrdersServices =
        """{"matchLabels":{"app.kubernetes.io/name":"orders","app.kubernetes.io/component":"rabbitmq"}}""";

    private void UnscrapedOrders()
    {
        cluster.Query = q => q.Contains("or vector(0)") ? Series("0") : NoSeries;
        cluster.ServeList("/api/v1/namespaces/mq/services",
            Labelled("mq", "orders", "\"app.kubernetes.io/name\":\"orders\",\"app.kubernetes.io/component\":\"rabbitmq\""));
    }

    [Fact]
    public void Apply_monitors_now_on_a_broker_applies_the_clusters_monitors_once_and_diagnoses_again()
    {
        UnscrapedOrders();
        monitors.Answer = () => Planned(
            RowFor(ManagedServiceKind.RabbitMq, "mq", "orders", MonitorState.Managed, "Orders' row: scraped by EntKube's monitor."));
        monitors.OnApply = () => cluster.ServeList(ServiceMonitorsPath, ServiceMonitor("mq", "orders-metrics", OrdersServices));
        IRenderedComponent<RabbitMQMonitoringPanel> cut = Brokers(Broker("mq", "orders"));

        cut.Markup.Should().Contain("every database and component on this Kubernetes cluster");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == ApplyButton).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("mq/orders-metrics selects RabbitMQ 'orders' and Prometheus reads it"));
        monitors.Applied.Should().Equal([(cluster.TenantId, cluster.ClusterId)]);
        cut.Find("[data-apply]").TextContent.Should().Contain("Orders' row: scraped by EntKube's monitor.");
    }

    [Fact]
    public void A_failed_broker_apply_is_shown_beside_the_button()
    {
        UnscrapedOrders();
        monitors.Answer = () => KubernetesOperationResult<ScrapeMonitorPlan>.Failure("Cancelled. Nothing was changed.");
        IRenderedComponent<RabbitMQMonitoringPanel> cut = Brokers(Broker("mq", "orders"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == ApplyButton).Click();

        cut.WaitForAssertion(() => cut.Find("[data-apply] .text-danger").TextContent
            .Should().Contain("Cancelled. Nothing was changed."));
        monitors.Applied.Should().ContainSingle();
    }

    /// <summary>A broker EntKube did not install gets no monitor from the engine, so it gets no button.</summary>
    [Fact]
    public void An_external_broker_has_no_apply_button()
    {
        cluster.Query = q => q.Contains("or vector(0)") ? Series("0") : NoSeries;

        Brokers(Broker("legacy", "rabbit", external: true)).FindAll("button")
            .Select(b => b.TextContent.Trim()).Should().NotContain(ApplyButton);
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

    [Fact]
    public void A_keycloak_diagnosis_that_could_not_read_the_monitors_keeps_the_panel_and_says_so()
    {
        cluster.Refuse(ServiceMonitorsPath, System.Net.HttpStatusCode.Forbidden);

        IRenderedComponent<KeycloakMonitoringPanel> cut = Render<KeycloakMonitoringPanel>(ps => ps
            .Add(p => p.TenantId, cluster.TenantId).Add(p => p.ClusterId, cluster.ClusterId)
            .Add(p => p.Namespace, "iam").Add(p => p.ReleaseName, "sso"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Why is this empty?"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Why is this empty?")).Click();

        cut.WaitForAssertion(() => cut.Find("[data-diagnosis-error]").TextContent
            .Should().Contain("Could not read the ServiceMonitors"));
        cut.Markup.Should().Contain("Why is this empty?").And.NotContain("Prometheus query failed");
    }

    /// <summary>The engine, as a panel sees it: what was asked of it, and what it answers.</summary>
    private sealed class RecordingMonitors : IScrapeMonitors
    {
        public List<(Guid Tenant, Guid Cluster)> Applied { get; } = [];

        public Func<KubernetesOperationResult<ScrapeMonitorPlan>> Answer { get; set; } =
            () => KubernetesOperationResult<ScrapeMonitorPlan>.Success(new ScrapeMonitorPlan([], [], []));

        /// <summary>What the apply does to the cluster, before it answers.</summary>
        public Action? OnApply { get; set; }

        public Task<KubernetesOperationResult<ScrapeMonitorPlan>> PlanAsync(
            Guid tenantId, Guid clusterId, CancellationToken ct = default) => Task.FromResult(Answer());

        public async Task<KubernetesOperationResult<ScrapeMonitorPlan>> ApplyAsync(
            Guid tenantId, Guid clusterId, CancellationToken ct = default)
        {
            Applied.Add((tenantId, clusterId));
            await Task.Yield();
            OnApply?.Invoke();
            return Answer();
        }
    }
}
