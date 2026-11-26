using System.Net;
using System.Text.Json.Nodes;
using EntKube.Web.Services;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;
using static EntKube.Web.Tests.ScrapedClusterFake;

namespace EntKube.Web.Tests;

/// <summary>
/// "Why is it empty?" — what the diagnosis says about a database, a broker or a Keycloak whose
/// metrics are missing, over a cluster that answers the same calls a real one does.
///
/// <para>The rules come from the CRDs, not from what a Prometheus usually looks like: an absent
/// selector selects nothing, an unreadable list is not an empty one, and a monitor is the
/// workload's when it selects it, whatever it is called.</para>
/// </summary>
public sealed class ScrapeDiagnosisTests
{
    private const string ShopPods = """{"matchLabels":{"cnpg.io/cluster":"shop","cnpg.io/podRole":"instance"}}""";
    private const string OrdersServices =
        """{"matchLabels":{"app.kubernetes.io/name":"orders","app.kubernetes.io/component":"rabbitmq"}}""";

    private readonly ScrapedClusterFake cluster = new();

    public ScrapeDiagnosisTests()
    {
        cluster.ServeList(PrometheusesPath, ReadsEverything);
        cluster.ServeList(PodMonitorsPath);
        cluster.ServeList(ServiceMonitorsPath);
        cluster.ServeList("/api/v1/namespaces/data/pods",
            DatabasePod("shop-1", "10.0.0.1"), DatabasePod("shop-2", "10.0.0.2"), DatabasePod("shop-3", "10.0.0.3"));
        cluster.ServeList("/api/v1/namespaces/mq/services", BrokerService("orders"));
        cluster.ServeList("/api/v1/namespaces/mq/pods", BrokerPod("orders-server-0"));
    }

    private const string OrdersLabels = "\"app.kubernetes.io/name\":\"orders\",\"app.kubernetes.io/component\":\"rabbitmq\"";

    /// <summary>A Service of the broker 'orders', with the operator's labels and its Prometheus port.</summary>
    private static string BrokerService(string name, string moreLabels = "", string selector = "\"app.kubernetes.io/name\":\"orders\"") =>
        $$$"""{"metadata":{"name":"{{{name}}}","namespace":"mq","labels":{{{{OrdersLabels}}}{{{moreLabels}}}}},"spec":{"selector":{{{{selector}}}},"ports":[{"name":"prometheus","port":15692,"targetPort":15692}]}}""";

    /// <summary>A running broker pod that serves the Prometheus plugin.</summary>
    private static string BrokerPod(string name, string moreLabels = "", string ip = "10.0.1.10") =>
        $$$"""{"metadata":{"name":"{{{name}}}","namespace":"mq","labels":{"app.kubernetes.io/name":"orders"{{{moreLabels}}}}},"spec":{"containers":[{"name":"rabbitmq","ports":[{"name":"prometheus","containerPort":15692}]}]},"status":{"phase":"Running","podIP":"{{{ip}}}"}}""";

    /// <summary>
    /// A running instance pod of the database 'shop', as CNPG labels it and with the exporter port it
    /// declares. A monitor ends at a running pod's own address, as Prometheus scrapes it.
    /// </summary>
    private static string DatabasePod(string name, string ip, string moreLabels = "") =>
        $$$"""{"metadata":{"name":"{{{name}}}","namespace":"data","labels":{"cnpg.io/cluster":"shop","cnpg.io/podRole":"instance","team":"shop"{{{moreLabels}}}}},"spec":{"containers":[{"name":"postgres","ports":[{"name":"metrics","containerPort":9187}]}]},"status":{"phase":"Running","podIP":"{{{ip}}}"}}""";

    private Task<KubernetesOperationResult<ScrapeDiagnosis>> AskAboutShop() =>
        cluster.Prometheus.DiagnoseCnpgScrapeAsync(cluster.TenantId, cluster.ClusterId, "shop", "data");

    private async Task<ScrapeDiagnosis> DiagnoseShop() => Answered(await AskAboutShop());

    private async Task<ScrapeDiagnosis> Diagnose(MonitoredWorkload workload) => Answered(
        await cluster.Prometheus.DiagnoseServiceMonitorScrapeAsync(cluster.TenantId, cluster.ClusterId, workload));

    private static ScrapeDiagnosis Answered(KubernetesOperationResult<ScrapeDiagnosis> result)
    {
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Data!;
    }

    private static string Said(ScrapeDiagnosis d) => string.Join("\n", d.Findings);

    // ──────── The ordinary answers ────────

    [Fact]
    public async Task EntKubes_own_monitor_that_prometheus_reads_is_reported_as_wired()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "entkube-cnpg-shop", ShopPods));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.WiringIsRight.Should().BeTrue();
        d.Findings.Should().ContainSingle().Which.Should().Contain("data/entkube-cnpg-shop");
        d.Remedy.Should().Contain("Targets");
    }

    [Fact]
    public async Task A_database_without_a_monitor_is_told_to_apply_monitors()
    {
        ScrapeDiagnosis d = await DiagnoseShop();

        d.MonitorExists.Should().BeFalse();
        Said(d).Should().Contain("No PodMonitor");
        d.Remedy.Should().Contain("Apply monitors now");
    }

    [Fact]
    public async Task A_monitor_prometheus_does_not_select_is_told_to_apply_monitors_and_never_to_paste_helm_values()
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus",
            """ "podMonitorSelector":{"matchLabels":{"release":"prom"}},"podMonitorNamespaceSelector":{} """));
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods, labels: "\"team\":\"shop\""));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.MonitorExists.Should().BeTrue();
        d.SelectorAcceptsMonitor.Should().BeFalse();
        Said(d).Should().Contain("release=prom").And.Contain("team=shop");
        d.Remedy.Should().Contain("Apply monitors now");
        d.Remedy.Should().NotContain("SelectorNilUsesHelmValues").And.NotContain("prometheusSpec");
    }

    // ──────── An absent selector selects nothing ────────

    [Fact]
    public async Task An_absent_pod_monitor_selector_selects_nothing()
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus", """ "podMonitorNamespaceSelector":{} """));
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "entkube-cnpg-shop", ShopPods));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectorAcceptsMonitor.Should().BeFalse("the CRD: a null label selector matches no objects");
        Said(d).Should().Contain("no podMonitorSelector");
        d.Remedy.Should().Contain("cannot help", "no monitor counts while Prometheus reads none");
    }

    [Fact]
    public async Task An_absent_service_monitor_selector_selects_nothing()
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus",
            """ "podMonitorSelector":{},"serviceMonitorNamespaceSelector":{} """));
        cluster.ServeList(ServiceMonitorsPath, ServiceMonitor("mq", "orders-metrics", OrdersServices));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectorAcceptsMonitor.Should().BeFalse();
        Said(d).Should().Contain("no serviceMonitorSelector");
    }

    [Fact]
    public async Task An_empty_selector_selects_every_monitor()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods));

        (await DiagnoseShop()).SelectorAcceptsMonitor.Should().BeTrue();
    }

    [Fact]
    public async Task An_absent_namespace_selector_confines_prometheus_to_its_own_namespace()
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus", """ "podMonitorSelector":{} """));
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectorAcceptsMonitor.Should().BeFalse("a monitor Prometheus never looks at is not accepted, whatever its labels");
        Said(d).Should().Contain("no podMonitorNamespaceSelector");
    }

    [Fact]
    public async Task A_monitor_in_prometheus_own_namespace_is_read_without_a_namespace_selector()
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus", """ "podMonitorSelector":{} """));
        cluster.ServeList(PodMonitorsPath, PodMonitor(PrometheusNamespace, "all-databases", ShopPods,
            more: ""","namespaceSelector":{"any":true}"""));

        (await DiagnoseShop()).WiringIsRight.Should().BeTrue();
    }

    [Fact]
    public async Task A_namespace_selector_is_held_against_the_namespace_labels()
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus",
            """ "podMonitorSelector":{},"podMonitorNamespaceSelector":{"matchLabels":{"scrape":"yes"}} """));
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods));
        cluster.ServeList("/api/v1/namespaces", Labelled("", "data", "\"scrape\":\"no\""));

        ScrapeDiagnosis no = await DiagnoseShop();
        no.SelectorAcceptsMonitor.Should().BeFalse();
        Said(no).Should().Contain("scrape=yes");

        cluster.ServeList("/api/v1/namespaces", Labelled("", "data", "\"scrape\":\"yes\""));
        (await DiagnoseShop()).SelectorAcceptsMonitor.Should().BeTrue();
    }

    // ──────── matchExpressions ────────

    [Theory]
    [InlineData("""{"key":"release","operator":"In","values":["prom","other"]}""", true)]
    [InlineData("""{"key":"release","operator":"In","values":["other"]}""", false)]
    [InlineData("""{"key":"release","operator":"NotIn","values":["prom"]}""", false)]
    [InlineData("""{"key":"release","operator":"NotIn","values":["other"]}""", true)]
    [InlineData("""{"key":"absent","operator":"NotIn","values":["x"]}""", true)]   // NotIn selects what lacks the key
    [InlineData("""{"key":"release","operator":"Exists"}""", true)]
    [InlineData("""{"key":"absent","operator":"Exists"}""", false)]
    [InlineData("""{"key":"release","operator":"DoesNotExist"}""", false)]
    [InlineData("""{"key":"absent","operator":"DoesNotExist"}""", true)]
    public async Task Match_expressions_are_evaluated(string expression, bool selected)
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus",
            $$""" "podMonitorSelector":{"matchExpressions":[{{expression}}]},"podMonitorNamespaceSelector":{} """));
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods, labels: "\"release\":\"prom\""));

        (await DiagnoseShop()).SelectorAcceptsMonitor.Should().Be(selected);
    }

    [Theory]
    [InlineData("""{"matchExpressions":[{"key":"release","operator":"Matches","values":["p.*"]}]}""")]
    [InlineData("""{"matchExpressions":[{"key":"release","operator":"In"}]}""")]
    public async Task A_selector_that_is_not_understood_is_neither_accepted_nor_refused(string selector)
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus",
            $$""" "podMonitorSelector":{{selector}},"podMonitorNamespaceSelector":{} """));
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods, labels: "\"release\":\"prom\""));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectorAcceptsMonitor.Should().BeFalse();
        Said(d).Should().Contain("Cannot tell whether Prometheus reads").And.NotContain("does not select");
    }

    /// <summary>One requirement that is definitely unmet decides it, however odd the others are.</summary>
    [Fact]
    public void An_unmet_requirement_refuses_even_beside_one_that_is_not_understood()
    {
        JsonNode selector = JsonNode.Parse(
            """{"matchLabels":{"release":"prom"},"matchExpressions":[{"key":"a","operator":"Matches"}]}""")!;

        // The diagnosis and the engine read selectors with one class, so they cannot disagree on one.
        LabelSelector.Selects(LabelSelector.From(selector), new Dictionary<string, string> { ["release"] = "other" }).Should().BeFalse();
        LabelSelector.Selects(LabelSelector.From(selector), new Dictionary<string, string> { ["release"] = "prom" }).Should().BeNull();
    }

    // ──────── A failed read is a failure ────────

    /// <summary>⭐ A 403 on the monitors was "No PodMonitor exists", with advice to create one.</summary>
    [Theory]
    [InlineData(PodMonitorsPath, "the PodMonitors", HttpStatusCode.Forbidden)]
    [InlineData(PodMonitorsPath, "the PodMonitors", HttpStatusCode.GatewayTimeout)]
    [InlineData(PrometheusesPath, "the Prometheus resource", HttpStatusCode.Forbidden)]
    [InlineData("/api/v1/namespaces/data/pods", "the pods in data", HttpStatusCode.Forbidden)]
    public async Task A_read_the_cluster_refuses_is_a_failure_and_names_what_could_not_be_read(
        string path, string what, HttpStatusCode status)
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods));
        cluster.Refuse(path, status);

        KubernetesOperationResult<ScrapeDiagnosis> result = await AskAboutShop();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain($"Could not read {what}").And.Contain(status.ToString());
    }

    [Fact]
    public async Task A_read_that_times_out_is_a_failure_that_says_so()
    {
        cluster.Hanging = PodMonitorsPath;

        KubernetesOperationResult<ScrapeDiagnosis> result = await AskAboutShop();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Could not read the PodMonitors: the cluster did not answer in time.");
    }

    [Fact]
    public async Task An_answer_that_is_not_a_list_is_a_failure_too()
    {
        cluster.Serve(PodMonitorsPath, """{"kind":"Status"}""");

        KubernetesOperationResult<ScrapeDiagnosis> result = await AskAboutShop();

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Could not read the PodMonitors");
    }

    /// <summary>The one 404 that is an answer: the cluster has no such type.</summary>
    [Fact]
    public async Task A_cluster_without_the_operators_types_is_told_so()
    {
        cluster.Refuse(PodMonitorsPath, HttpStatusCode.NotFound);

        ScrapeDiagnosis d = await DiagnoseShop();

        Said(d).Should().Contain("no PodMonitor resource type");
        d.Remedy.Should().NotContain("Apply monitors now");
    }

    // ──────── Which Prometheus ────────

    [Fact]
    public async Task The_components_own_prometheus_is_asked_and_not_whichever_comes_first()
    {
        cluster.ServeList(PrometheusesPath,
            Prom("other", """ "podMonitorSelector":{"matchLabels":{"x":"y"}},"podMonitorNamespaceSelector":{} """, ns: "elsewhere"),
            ReadsEverything);
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "shop", ShopPods));

        (await DiagnoseShop()).SelectorAcceptsMonitor.Should().BeTrue();
    }

    [Fact]
    public async Task Several_prometheuses_none_of_them_the_components_is_cannot_tell()
    {
        cluster.ServeList(PrometheusesPath,
            Prom("a", """ "podMonitorSelector":{} """, ns: "one"), Prom("b", """ "podMonitorSelector":{} """, ns: "two"));

        ScrapeDiagnosis d = await DiagnoseShop();

        Said(d).Should().Contain("Cannot tell which Prometheus").And.Contain("one/a").And.Contain("two/b");
        d.SelectorAcceptsMonitor.Should().BeFalse();
    }

    // ──────── Whose monitor ────────

    /// <summary>⭐ <c>app</c> is a substring of <c>my-app-metrics</c>, and that decided it.</summary>
    [Fact]
    public async Task The_monitor_of_my_app_is_not_the_monitor_of_app()
    {
        cluster.ServeList("/api/v1/namespaces/mq/services",
            Labelled("mq", "app", "\"app.kubernetes.io/name\":\"app\",\"app.kubernetes.io/component\":\"rabbitmq\""),
            Labelled("mq", "my-app", "\"app.kubernetes.io/name\":\"my-app\",\"app.kubernetes.io/component\":\"rabbitmq\""));
        cluster.ServeList(ServiceMonitorsPath, ServiceMonitor("mq", "my-app-metrics",
            """{"matchLabels":{"app.kubernetes.io/name":"my-app","app.kubernetes.io/component":"rabbitmq"}}"""));

        ScrapeDiagnosis app = await Diagnose(MonitoredWorkload.RabbitMQ("app", "mq"));
        app.MonitorExists.Should().BeFalse();
        app.Remedy.Should().Contain("Apply monitors now");

        (await Diagnose(MonitoredWorkload.RabbitMQ("my-app", "mq"))).WiringIsRight.Should().BeTrue();
    }

    [Fact]
    public async Task A_monitor_is_found_by_what_it_selects_whatever_it_is_called_and_wherever_it_is()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("ops", "every-postgres",
            """{"matchExpressions":[{"key":"cnpg.io/cluster","operator":"Exists"}]}""",
            more: ""","namespaceSelector":{"matchNames":["data"]}"""));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.WiringIsRight.Should().BeTrue();
        Said(d).Should().Contain("ops/every-postgres");
    }

    [Fact]
    public async Task A_monitor_in_another_namespace_that_does_not_reach_this_one_is_not_the_databases()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("other", "shop", ShopPods));

        (await DiagnoseShop()).MonitorExists.Should().BeFalse();
    }

    /// <summary>Judged by the labels the pods really carry, not only by the two every database has.</summary>
    [Fact]
    public async Task A_monitor_that_selects_by_a_label_of_the_users_own_is_found()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "team-shop", """{"matchLabels":{"team":"shop"}}"""));

        (await DiagnoseShop()).WiringIsRight.Should().BeTrue();
    }

    [Fact]
    public async Task A_monitor_of_another_port_is_not_a_monitor_of_the_metrics()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "status", ShopPods, endpoints: """{"port":"status"}"""));

        (await DiagnoseShop()).MonitorExists.Should().BeFalse();
    }

    [Fact]
    public async Task EntKubes_own_monitor_that_selects_something_else_is_said_to_exist()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "entkube-cnpg-shop", """{"matchLabels":{"cnpg.io/cluster":"old"}}"""));

        ScrapeDiagnosis d = await DiagnoseShop();

        Said(d).Should().Contain("entkube-cnpg-shop exists but does not select").And.Contain("cnpg.io/cluster=old");
        d.Remedy.Should().Contain("Apply monitors now");
    }

    [Fact]
    public async Task A_database_with_no_pods_is_judged_by_the_labels_its_pods_get_and_that_is_said()
    {
        cluster.ServeList("/api/v1/namespaces/data/pods");
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "entkube-cnpg-shop", ShopPods));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectorAcceptsMonitor.Should().BeTrue();
        Said(d).Should().Contain("No pods of database 'shop'");
    }

    // ──────── Scraped twice ────────

    /// <summary>
    /// The doubling case: the operator's PodMonitor and EntKube's, both read. Every number on the
    /// panel is then twice what it should be, and the old diagnosis called it wired.
    /// </summary>
    [Fact]
    public async Task Two_monitors_that_select_the_same_database_are_said_to_double_it()
    {
        cluster.ServeList(PodMonitorsPath,
            PodMonitor("data", "shop", ShopPods),
            PodMonitor("data", "entkube-cnpg-shop", ShopPods, endpoints: """{"portNumber":9187}"""));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectedTwice.Should().BeTrue();
        d.WiringIsRight.Should().BeFalse();
        d.Findings[0].Should().Contain("data/shop").And.Contain("data/entkube-cnpg-shop").And.Contain("2 PodMonitors");
        d.Remedy.Should().Contain("Apply monitors now");
    }

    /// <summary>
    /// ⭐ A PodMonitor for the primary and one for the replicas: both select the database and both
    /// name its metrics port, but no pod is selected by both. Nothing is doubled, and deleting
    /// "all but one of them" would lose one role's metrics.
    /// </summary>
    [Fact]
    public async Task Two_monitors_that_select_different_pods_of_one_database_do_not_double_it()
    {
        cluster.ServeList("/api/v1/namespaces/data/pods",
            DatabasePod("shop-1", "10.0.0.1", ",\"cnpg.io/instanceRole\":\"primary\""),
            DatabasePod("shop-2", "10.0.0.2", ",\"cnpg.io/instanceRole\":\"replica\""));
        cluster.ServeList(PodMonitorsPath,
            PodMonitor("data", "db-primary", """{"matchLabels":{"cnpg.io/cluster":"shop","cnpg.io/instanceRole":"primary"}}"""),
            PodMonitor("data", "db-replica", """{"matchLabels":{"cnpg.io/cluster":"shop","cnpg.io/instanceRole":"replica"}}"""));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectedTwice.Should().BeFalse("no pod is scraped by both");
        d.WiringIsRight.Should().BeTrue();
        Said(d).Should().NotContain("scrape the same endpoint");
        d.Remedy.Should().NotContain("Delete");
    }

    /// <summary>The same split, one pod in both: that one pod is doubled, and that is said.</summary>
    [Fact]
    public async Task Two_monitors_that_share_one_pod_of_a_database_double_it()
    {
        cluster.ServeList("/api/v1/namespaces/data/pods",
            DatabasePod("shop-1", "10.0.0.1", ",\"cnpg.io/instanceRole\":\"primary\""),
            DatabasePod("shop-2", "10.0.0.2", ",\"cnpg.io/instanceRole\":\"replica\""));
        cluster.ServeList(PodMonitorsPath,
            PodMonitor("data", "db-primary", """{"matchLabels":{"cnpg.io/cluster":"shop","cnpg.io/instanceRole":"primary"}}"""),
            PodMonitor("data", "db-all", """{"matchLabels":{"cnpg.io/cluster":"shop"}}"""));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectedTwice.Should().BeTrue();
        d.Findings[0].Should().Contain("data/db-primary").And.Contain("data/db-all");
    }

    /// <summary>
    /// Two ServiceMonitors, one for each of two Services in front of the same broker pod: one pod
    /// endpoint, reached twice. This is the doubling a ServiceMonitor causes most often.
    /// </summary>
    [Fact]
    public async Task Two_service_monitors_through_two_services_to_one_pod_double_it()
    {
        cluster.ServeList("/api/v1/namespaces/mq/services",
            BrokerService("orders", ",\"svc\":\"client\""), BrokerService("orders-nodes", ",\"svc\":\"nodes\""));
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "client", """{"matchLabels":{"svc":"client"}}"""),
            ServiceMonitor("mq", "nodes", """{"matchLabels":{"svc":"nodes"}}"""));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().BeTrue();
        d.Findings[0].Should().Contain("mq/client").And.Contain("mq/nodes");
    }

    /// <summary>Two ServiceMonitors on two Services whose pods are not the same pods double nothing.</summary>
    [Fact]
    public async Task Two_service_monitors_through_services_in_front_of_different_pods_do_not_double_anything()
    {
        cluster.ServeList("/api/v1/namespaces/mq/services",
            BrokerService("orders-a", ",\"svc\":\"a\"", "\"pod\":\"a\""),
            BrokerService("orders-b", ",\"svc\":\"b\"", "\"pod\":\"b\""));
        cluster.ServeList("/api/v1/namespaces/mq/pods",
            BrokerPod("orders-server-0", ",\"pod\":\"a\""), BrokerPod("orders-server-1", ",\"pod\":\"b\"", ip: "10.0.1.11"));
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "a", """{"matchLabels":{"svc":"a"}}"""),
            ServiceMonitor("mq", "b", """{"matchLabels":{"svc":"b"}}"""));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().BeFalse();
        d.WiringIsRight.Should().BeTrue();
    }

    /// <summary>
    /// ⭐ A ServiceMonitor endpoint's <c>targetPort</c> is the POD's container port, by name or by
    /// number: prometheus-operator keeps <c>__meta_kubernetes_pod_container_port_name</c> (or
    /// <c>_number</c>) equal to it. One monitor by the Service port's name and one by the pod port's
    /// name both end at port 15692 of the one broker pod, and every sum counts it twice.
    /// </summary>
    [Fact]
    public async Task A_monitor_by_service_port_and_one_by_the_pods_port_name_double_one_broker_pod()
    {
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "by-port", OrdersServices),
            ServiceMonitor("mq", "by-target", OrdersServices, endpoints: """{"targetPort":"prometheus"}"""));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().BeTrue("both end at port 15692 of orders-server-0");
        d.Findings[0].Should().Contain("mq/by-port").And.Contain("mq/by-target");
    }

    /// <summary>
    /// The same by number, where the Service's own port is another number: the Service listens on
    /// 80 and sends to the pod's port called prometheus (15692). A targetPort of 15692 is that pod
    /// port, whatever the Service port is called or numbered.
    /// </summary>
    [Fact]
    public async Task A_target_port_number_is_the_pods_port_and_not_the_services()
    {
        cluster.ServeList("/api/v1/namespaces/mq/services",
            $$$"""{"metadata":{"name":"orders","namespace":"mq","labels":{{{{OrdersLabels}}}}},"spec":{"selector":{"app.kubernetes.io/name":"orders"},"ports":[{"name":"prometheus","port":80,"targetPort":"prometheus"}]}}""");
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "by-port", OrdersServices),
            ServiceMonitor("mq", "by-target", OrdersServices, endpoints: """{"targetPort":15692}"""));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().BeTrue("both end at port 15692 of orders-server-0");
        d.Findings[0].Should().Contain("mq/by-port").And.Contain("mq/by-target");
    }

    /// <summary>A targetPort the pod does not declare by that name ends nowhere, and doubles nothing.</summary>
    [Fact]
    public async Task A_target_port_name_the_pod_does_not_declare_doubles_nothing()
    {
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "by-port", OrdersServices),
            ServiceMonitor("mq", "by-target", OrdersServices, endpoints: """{"targetPort":"prometheus-tls"}"""));

        (await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"))).SelectedTwice.Should().BeFalse();
    }

    /// <summary>
    /// A scrape that asks with parameters (RabbitMQ's <c>?family=</c>) gets other series than the
    /// port's own, by the engine's one rule for that (<see cref="ScrapedEndpoints.ServesItsOwnMetrics"/>):
    /// the diagnosis follows a monitor to its pods with the engine's own function, so the two agree.
    /// </summary>
    [Fact]
    public async Task A_monitor_that_asks_with_parameters_doubles_nothing_by_the_engines_rule()
    {
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "detailed", OrdersServices, endpoints: """{"port":"prometheus","path":"/metrics/detailed"}"""),
            ServiceMonitor("mq", "queues", OrdersServices,
                endpoints: """{"port":"prometheus","path":"/metrics/detailed","params":{"family":["queue_coarse_metrics"]}}"""));

        (await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"))).SelectedTwice.Should().BeFalse();
    }

    /// <summary>
    /// params-double: the engine's rule for "does a foreign monitor give our series" leaves out a scrape
    /// that asks with parameters. That is no answer to "do two monitors double each other": two that ask
    /// the same port, path and parameters are the same scrape twice, and every sum over it is doubled.
    /// Parameters are compared as a set: the order of the keys and of a key's values does not make
    /// another scrape, another value does.
    /// </summary>
    [Theory]
    [InlineData("/metrics/detailed", """{"family":["queue_coarse_metrics"]}""", """{"family":["queue_coarse_metrics"]}""", true,
        "both ask /metrics/detailed for the same family")]
    [InlineData("/metrics/detailed", """{"family":["queue_coarse_metrics","queue_consumer_count"],"vhost":["/"]}""",
        """{"vhost":["/"],"family":["queue_consumer_count","queue_coarse_metrics"]}""", true,
        "the same parameters in another order are the same query")]
    [InlineData("/metrics/detailed", """{"family":["queue_coarse_metrics"]}""", """{"family":["queue_consumer_count"]}""", false,
        "another family is other series")]
    [InlineData("/probe", """{"target":["db:5432"]}""", """{"target":["db:5432"]}""", true,
        "two monitors probing the same address through the same exporter are one probe twice")]
    public async Task Two_monitors_that_ask_with_the_same_parameters_double_one_broker(
        string path, string first, string second, bool doubled, string because)
    {
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "queues-a", OrdersServices,
                endpoints: $$$"""{"port":"prometheus","path":"{{{path}}}","params":{{{first}}}}"""),
            ServiceMonitor("mq", "queues-b", OrdersServices,
                endpoints: $$$"""{"port":"prometheus","path":"{{{path}}}","params":{{{second}}}}"""));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().Be(doubled, because);
        if (doubled) d.Findings[0].Should().Contain("mq/queues-a").And.Contain("mq/queues-b");
    }

    /// <summary>
    /// undeclared-targetport: a ServiceMonitor endpoint's numeric <c>targetPort</c> keeps a target only where
    /// <c>__meta_kubernetes_pod_container_port_number</c> equals it (prometheus-operator promcfg.go), and
    /// Prometheus's endpoints discovery sets that label only from a container port the pod declares with
    /// that number. The Service still reaches the port by number, so the monitor by Service port scrapes it;
    /// the one by targetPort scrapes nothing, and nothing is doubled.
    /// </summary>
    [Fact]
    public async Task A_target_port_number_the_pod_does_not_declare_doubles_nothing()
    {
        cluster.ServeList("/api/v1/namespaces/mq/pods",
            """{"metadata":{"name":"orders-server-0","namespace":"mq","labels":{"app.kubernetes.io/name":"orders"}},"spec":{"containers":[{"name":"rabbitmq"}]},"status":{"phase":"Running","podIP":"10.0.1.10"}}""");
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "by-port", OrdersServices),
            ServiceMonitor("mq", "by-target", OrdersServices, endpoints: """{"targetPort":15692}"""));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().BeFalse("Prometheus drops the targetPort target: no container declares 15692");
        d.WiringIsRight.Should().BeTrue();
    }

    /// <summary>
    /// sd-extra-targets: a Service that leads to the broker only on AMQP still has the broker pod in its Endpoints,
    /// and Prometheus's endpoints discovery adds a target for each port the pod declares that the Endpoints object
    /// does not carry (discovery/kubernetes/endpoints.go, the seenPods loop). A monitor by <c>targetPort</c> 15692, or
    /// with no port at all, keeps the one on 15692 (prometheus-operator promcfg.go), so with a monitor by port through
    /// the broker's own Service the broker is scraped twice; one by <c>port</c> keeps no such target.
    /// </summary>
    [Theory]
    [InlineData("""{"targetPort":15692}""", true)]
    [InlineData("""{"targetPort":"prometheus"}""", true)]
    [InlineData("""{}""", true)]
    // An empty port and a targetPort of 0 or "" are no port to prometheus-operator (promcfg.go tests
    // ep.Port != "", then StrVal != "" or IntVal != 0), so each keeps every target as {} does.
    [InlineData("""{"port":""}""", true)]
    [InlineData("""{"targetPort":0}""", true)]
    [InlineData("""{"targetPort":""}""", true)]
    [InlineData("""{"port":"","targetPort":15692}""", true)]
    [InlineData("""{"port":"amqp"}""", false)]
    public async Task A_monitor_through_a_service_without_the_metrics_port_still_reaches_it(string endpoint, bool doubled)
    {
        cluster.ServeList("/api/v1/namespaces/mq/services",
            BrokerService("orders"),
            $$$"""{"metadata":{"name":"orders-amqp","namespace":"mq","labels":{{{{OrdersLabels}}},"plane":"amqp"}},"spec":{"selector":{"app.kubernetes.io/name":"orders"},"ports":[{"name":"amqp","port":5672,"targetPort":5672}]}}""");
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "by-port", OrdersServices),
            ServiceMonitor("mq", "by-amqp", """{"matchLabels":{"plane":"amqp"}}""", endpoints: endpoint));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().Be(doubled);
        if (doubled) Said(d).Should().Contain("mq/by-port").And.Contain("mq/by-amqp");
    }

    [Fact]
    public async Task Two_monitors_of_which_prometheus_reads_one_do_not_double_anything()
    {
        cluster.ServeList(PrometheusesPath, Prom("prom-prometheus",
            """ "podMonitorSelector":{"matchLabels":{"release":"prom"}},"podMonitorNamespaceSelector":{} """));
        cluster.ServeList(PodMonitorsPath,
            PodMonitor("data", "shop", ShopPods),
            PodMonitor("data", "entkube-cnpg-shop", ShopPods, labels: "\"release\":\"prom\""));

        ScrapeDiagnosis d = await DiagnoseShop();

        d.SelectedTwice.Should().BeFalse();
        d.SelectorAcceptsMonitor.Should().BeTrue();
        Said(d).Should().Contain("data/shop exists but Prometheus does not select it");
    }

    [Fact]
    public async Task Two_monitors_on_two_paths_of_one_port_do_not_double_anything()
    {
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "orders-metrics", OrdersServices),
            ServiceMonitor("mq", "detailed", OrdersServices, endpoints: """{"port":"prometheus","path":"/metrics/detailed"}"""));

        (await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"))).SelectedTwice.Should().BeFalse();
    }

    [Fact]
    public async Task Two_monitors_neither_of_them_entkubes_are_the_users_to_remove()
    {
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "one", OrdersServices), ServiceMonitor("mq", "two", OrdersServices));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"));

        d.SelectedTwice.Should().BeTrue();
        d.Remedy.Should().Be("Delete all but one of them.");
    }

    /// <summary>Two monitors can end at one pod only where two reach its namespace: only then are its pods read.</summary>
    [Fact]
    public async Task The_pods_behind_a_brokers_services_are_read_only_where_two_monitors_could_meet_there()
    {
        cluster.ServeList(ServiceMonitorsPath, ServiceMonitor("mq", "orders-metrics", OrdersServices));
        (await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"))).WiringIsRight.Should().BeTrue();
        cluster.Requests.Should().NotContain("/api/v1/namespaces/mq/pods");

        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "one", OrdersServices), ServiceMonitor("mq", "two", OrdersServices));
        (await Diagnose(MonitoredWorkload.RabbitMQ("orders", "mq"))).SelectedTwice.Should().BeTrue();
        cluster.Requests.Should().Contain("/api/v1/namespaces/mq/pods");
    }

    /// <summary>The pods behind the Services could not be read: a failure, and never "nothing is doubled".</summary>
    [Fact]
    public async Task Pods_behind_the_services_that_cannot_be_read_are_a_failure()
    {
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("mq", "one", OrdersServices), ServiceMonitor("mq", "two", OrdersServices));
        cluster.Refuse("/api/v1/namespaces/mq/pods", HttpStatusCode.Forbidden);

        KubernetesOperationResult<ScrapeDiagnosis> result = await cluster.Prometheus.DiagnoseServiceMonitorScrapeAsync(
            cluster.TenantId, cluster.ClusterId, MonitoredWorkload.RabbitMQ("orders", "mq"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Could not read the pods in mq");
    }

    // ──────── The remedy, held against the engine's plan ────────

    private const string ShopKey = "cnpg/data/shop";

    private static KubernetesOperationResult<ScrapeMonitorPlan> PlanWith(MonitorState state, MonitorChange change, string detail) =>
        KubernetesOperationResult<ScrapeMonitorPlan>.Success(new ScrapeMonitorPlan(
            [new MonitorPlanRow(ShopKey, "shop", "PostgreSQL", "data", null, state, change, detail, [])], [], []));

    /// <summary>EntKube's monitor is one of two: the button removes it, but only if the engine says it will.</summary>
    [Fact]
    public async Task Ours_doubling_another_is_told_to_press_only_where_the_engine_removes_ours()
    {
        cluster.ServeList(PodMonitorsPath,
            PodMonitor("data", "shop", ShopPods),
            PodMonitor("data", "entkube-cnpg-shop", ShopPods, endpoints: """{"portNumber":9187}"""));
        ScrapeDiagnosis d = await DiagnoseShop();
        d.RemedyExpects.Should().Be(ApplyExpectation.RemovesOurs);

        ScrapeDiagnostician.AgainstThePlan(d, PlanWith(MonitorState.CoveredElsewhere, MonitorChange.Remove, "Ours goes."), ShopKey)
            .Remedy.Should().Be(ScrapeDiagnostician.RemoveOurs);

        ScrapeDiagnostician.AgainstThePlan(d, PlanWith(MonitorState.NotSelectable, MonitorChange.None, "Two Services lead to it."), ShopKey)
            .Remedy.Should().Be("Two Services lead to it.");

        ScrapeDiagnostician.AgainstThePlan(d, PlanWith(MonitorState.Managed, MonitorChange.Create, "Created."), ShopKey)
            .Remedy.Should().Be("Created.", "a create is not the removal the remedy counts on");
    }

    /// <summary>
    /// ⭐ The diagnosis reads monitors only, so for a database with none it found "Prometheus was
    /// never told to scrape it", while the remedy beside it was the engine's row saying its pods are
    /// scraped by annotation, or by another job. Where the engine's row says something scrapes it
    /// without a monitor, the finding says that instead.
    /// </summary>
    [Theory]
    [InlineData(MonitorState.Annotated, "prometheus.io annotations")]
    [InlineData(MonitorState.CoveredElsewhere, "already scrapes it without one")]
    [InlineData(MonitorState.PartlyCovered, "scrapes part of it without one")]
    public async Task Where_the_engine_says_it_is_scraped_without_a_monitor_no_finding_says_it_never_was(
        MonitorState state, string said)
    {
        ScrapeDiagnosis d = await DiagnoseShop();
        Said(d).Should().Contain("so Prometheus was never told to scrape it", "the diagnosis alone sees only monitors");

        ScrapeDiagnosis held = ScrapeDiagnostician.AgainstThePlan(d, PlanWith(state, MonitorChange.None, "The engine's sentence."), ShopKey);

        held.Remedy.Should().Be("The engine's sentence.");
        Said(held).Should().NotContain("never told")
            .And.Contain("No PodMonitor on this cluster selects the metrics port of database 'shop'")
            .And.Contain(said);
        held.Findings.Should().HaveCount(d.Findings.Count, "the one finding is reworded, nothing else is touched");
    }

    /// <summary>Where the engine says nothing scrapes it, nothing was told to, and the finding stands.</summary>
    [Theory]
    [InlineData(MonitorState.NotRunning)]
    [InlineData(MonitorState.BlockedByNetworkPolicy)]
    [InlineData(MonitorState.NotSelectable)]
    public async Task Where_the_engine_says_nothing_scrapes_it_the_finding_that_nothing_was_told_to_stands(MonitorState state)
    {
        ScrapeDiagnosis d = await DiagnoseShop();

        ScrapeDiagnosis held = ScrapeDiagnostician.AgainstThePlan(d, PlanWith(state, MonitorChange.None, "The engine's sentence."), ShopKey);

        held.Findings.Should().Equal(d.Findings);
    }

    [Theory]
    [InlineData(MonitorPlanScope.NoPrometheus, "did not install Prometheus")]
    [InlineData(MonitorPlanScope.NoMonitorKinds, "no PodMonitor or ServiceMonitor resource type")]
    public async Task A_plan_with_nothing_to_plan_is_never_the_button(MonitorPlanScope scope, string said)
    {
        ScrapeDiagnosis d = await DiagnoseShop();

        string? remedy = ScrapeDiagnostician.AgainstThePlan(
            d, KubernetesOperationResult<ScrapeMonitorPlan>.Success(ScrapeMonitorPlan.Nothing(scope)), ShopKey).Remedy;

        remedy.Should().Contain(said).And.NotContain("Press");
    }

    /// <summary>A remedy that is not the button needs no plan, and none is read for it.</summary>
    [Fact]
    public async Task A_remedy_that_is_not_the_button_is_left_alone_and_no_plan_is_read()
    {
        cluster.ServeList(PodMonitorsPath, PodMonitor("data", "entkube-cnpg-shop", ShopPods));
        KubernetesOperationResult<ScrapeDiagnosis> diagnosed = await AskAboutShop();
        PlanThatMustNotBeRead monitors = new();

        KubernetesOperationResult<ScrapeDiagnosis> checkedOne = await ScrapeDiagnostician.CheckedAgainstThePlanAsync(
            diagnosed, monitors, cluster.TenantId, cluster.ClusterId, ShopKey);

        checkedOne.Data!.Remedy.Should().Be(diagnosed.Data!.Remedy).And.Contain("Targets");
        monitors.Asked.Should().BeFalse();
    }

    private sealed class PlanThatMustNotBeRead : IScrapeMonitors
    {
        public bool Asked { get; private set; }

        public Task<KubernetesOperationResult<ScrapeMonitorPlan>> PlanAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
        {
            Asked = true;
            throw new InvalidOperationException("The plan was read.");
        }

        public Task<KubernetesOperationResult<ScrapeMonitorPlan>> ApplyAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Nothing applies here.");
    }

    // ──────── Keycloak, and a broker EntKube did not install ────────

    [Fact]
    public async Task Keycloak_is_found_by_the_service_of_its_own_release()
    {
        cluster.ServeList("/api/v1/namespaces/iam/services", Labelled("iam", "sso-keycloakx-http",
            "\"app.kubernetes.io/name\":\"keycloakx\",\"app.kubernetes.io/instance\":\"sso\",\"app.kubernetes.io/component\":\"http\""));
        cluster.ServeList(ServiceMonitorsPath, ServiceMonitor("iam", "entkube-keycloak-sso",
            """{"matchLabels":{"app.kubernetes.io/instance":"sso","app.kubernetes.io/component":"http"}}""",
            endpoints: """{"port":"http-internal","path":"/auth/metrics"}"""));

        (await Diagnose(MonitoredWorkload.Keycloak("sso", "iam"))).WiringIsRight.Should().BeTrue();

        ScrapeDiagnosis other = await Diagnose(MonitoredWorkload.Keycloak("sso2", "iam"));
        other.MonitorExists.Should().BeFalse("another release's monitor is not this one's");
        other.Remedy.Should().Contain("Apply monitors now").And.NotContain("serviceMonitor.enabled");
    }

    [Fact]
    public async Task An_external_broker_is_found_by_the_services_in_front_of_its_pods()
    {
        cluster.ServeList("/api/v1/namespaces/legacy/services",
            """{"metadata":{"name":"rabbit","namespace":"legacy","labels":{"chart":"rabbitmq"}},"spec":{"selector":{"app":"rabbit"}}}""",
            """{"metadata":{"name":"web","namespace":"legacy","labels":{"chart":"web"}},"spec":{"selector":{"app":"web"}}}""");
        cluster.Serve("/apis/apps/v1/namespaces/legacy/statefulsets/rabbit",
            """{"metadata":{"name":"rabbit"},"spec":{"selector":{},"serviceName":"rabbit","template":{"metadata":{"labels":{"app":"rabbit","x":"y"}}}}}""");
        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("legacy", "unrelated-rabbit-web", """{"matchLabels":{"chart":"web"}}""", endpoints: """{"port":"metrics"}"""));

        ScrapeDiagnosis none = await Diagnose(MonitoredWorkload.ExternalRabbitMQ("rabbit", "legacy"));
        none.MonitorExists.Should().BeFalse("that monitor is the web server's");
        none.Remedy.Should().Contain("did not install").And.NotContain("Press \"Apply monitors now\"");

        cluster.ServeList(ServiceMonitorsPath,
            ServiceMonitor("legacy", "mq", """{"matchLabels":{"chart":"rabbitmq"}}""", endpoints: """{"port":"metrics"}"""));
        (await Diagnose(MonitoredWorkload.ExternalRabbitMQ("rabbit", "legacy"))).WiringIsRight.Should().BeTrue();
    }

    /// <summary>Without its Services nothing is known about which monitor is its own, so nothing is claimed.</summary>
    [Fact]
    public async Task An_external_broker_whose_stateful_set_is_gone_is_cannot_tell_and_not_unmonitored()
    {
        cluster.ServeList("/api/v1/namespaces/legacy/services");
        cluster.ServeList(ServiceMonitorsPath, ServiceMonitor("legacy", "mq", """{"matchLabels":{"chart":"rabbitmq"}}"""));

        ScrapeDiagnosis d = await Diagnose(MonitoredWorkload.ExternalRabbitMQ("rabbit", "legacy"));

        Said(d).Should().Contain("cannot be told").And.NotContain("No ServiceMonitor on this cluster");
    }
}
