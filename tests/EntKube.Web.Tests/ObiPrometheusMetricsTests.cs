using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// OBI's HTTP/gRPC/SQL RED metrics, turned on only where the cluster has a Prometheus to read them.
///
/// <para>Two failures bound this from either side. Without Prometheus, a ServiceMonitor fails the whole
/// release ("no matches for kind ServiceMonitor"), and an open port with no scraper is heap OBI never
/// gives back — it evicts expired series only inside a scrape. With Prometheus, the old form toggle set
/// <c>serviceMonitor.enabled</c> alone, and the chart renders one only when <c>service.enabled</c> is
/// true as well, so it never produced a ServiceMonitor at all.</para>
/// </summary>
public class ObiPrometheusMetricsTests
{
    private static string CatalogDefaults => ComponentCatalog.GetByKey("otel-ebpf")!.DefaultValues!;

    private static YamlMappingNode Root(string yaml)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode? At(string yaml, params string[] path)
    {
        YamlNode current = Root(yaml);
        foreach (string segment in path)
        {
            if (current is not YamlMappingNode mapping) return null;
            YamlScalarNode? key = mapping.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == segment);
            if (key is null) return null;
            current = mapping.Children[key];
        }
        return current;
    }

    private static string? Scalar(string yaml, params string[] path) => (At(yaml, path) as YamlScalarNode)?.Value;

    private static List<string?> Items(string yaml, params string[] path)
        => [.. ((YamlSequenceNode)At(yaml, path)!).Children.Select(c => ((YamlScalarNode)c).Value)];

    // ──────── With Prometheus ────────

    [Fact]
    public void With_prometheus_the_catalog_defaults_render_a_scrapable_endpoint()
    {
        string values = YamlFormMerger.EnableObiPrometheusMetrics(CatalogDefaults);

        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("9090");

        // Both, because the chart's servicemonitor.yaml is guarded by
        // `and .Values.service.enabled .Values.serviceMonitor.enabled (or $metricsPort ...)`.
        Scalar(values, "service", "enabled").Should().Be("true",
            "without the Service the chart renders no ServiceMonitor — why the old toggle never worked");
        Scalar(values, "serviceMonitor", "enabled").Should().Be("true");
        Scalar(values, "serviceMonitor", "metrics", "endpoint", "interval").Should().Be("30s");

        // The Prometheus exporter does not inherit the trace exporter's protocol list; its default is
        // every protocol, which loads every probe set on every instrumented executable.
        Items(values, "config", "data", "prometheus_export", "instrumentations")
            .Should().Equal("http", "grpc", "sql");
        Items(values, "config", "data", "metrics", "features").Should().Equal(
            ["application_red"], "`application`, the default, adds the body-size histograms");

        Items(values, "config", "data", "routes", "ignored_patterns")
            .Should().Equal("/metrics", "/healthz", "/readyz", "/livez");
        Scalar(values, "config", "data", "routes", "ignore_mode").Should().Be("metrics",
            "probes stay out of the metrics but keep appearing in traces as before");

        // The route cap the bounds put there is kept alongside.
        Scalar(values, "config", "data", "routes", "unmatched").Should().Be("low-cardinality");
        Scalar(values, "config", "data", "routes", "max_path_segment_cardinality").Should().Be("10");
    }

    /// <summary>
    /// OBI runs on the host network and Prometheus scrapes it as ITS pod, so every series arrives
    /// labelled with the OBI pod's namespace and name. Per-app queries select on <c>namespace</c>.
    /// </summary>
    [Fact]
    public void The_series_are_relabelled_onto_the_workload_they_measure()
    {
        string values = YamlFormMerger.EnableObiPrometheusMetrics(CatalogDefaults);

        YamlSequenceNode relabelings = (YamlSequenceNode)At(
            values, "serviceMonitor", "metrics", "endpoint", "metricRelabelings")!;

        List<(string? Source, string? Target, string? Regex, string? Replacement, string? Action)> rules =
        [
            .. relabelings.Children.Cast<YamlMappingNode>().Select(r => (
                ((YamlScalarNode)((YamlSequenceNode)r.Children[new YamlScalarNode("sourceLabels")]).Children.Single()).Value,
                ((YamlScalarNode)r.Children[new YamlScalarNode("targetLabel")]).Value,
                ((YamlScalarNode)r.Children[new YamlScalarNode("regex")]).Value,
                ((YamlScalarNode)r.Children[new YamlScalarNode("replacement")]).Value,
                ((YamlScalarNode)r.Children[new YamlScalarNode("action")]).Value)),
        ];

        rules.Should().Equal(
            ("k8s_namespace_name", "namespace", "(.+)", "$1", "replace"),
            ("k8s_pod_name", "pod", "(.+)", "$1", "replace"),
            ("k8s_container_name", "container", "(.+)", "$1", "replace"));

        // `(.+)`, never the default `(.*)`: OBI's own series have no k8s_* label, and an empty match
        // would overwrite their namespace with nothing.
        rules.Should().OnlyContain(r => r.Regex == "(.+)");
    }

    [Fact]
    public void Every_value_the_operator_set_is_kept()
    {
        const string operatorValues = """
            config:
              data:
                prometheus_export:
                  port: 9400
                  instrumentations: ["*"]
                metrics:
                  features: [application]
                routes:
                  ignored_patterns: [/ping]
                otel_traces_export:
                  endpoint: http://otel-collector.monitoring:4317
            serviceMonitor:
              metrics:
                endpoint:
                  interval: 15s
                  metricRelabelings:
                    - action: drop
                      sourceLabels: [url_path]
            """;

        string values = YamlFormMerger.EnableObiPrometheusMetrics(operatorValues);

        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("9400",
            "a port other than 9090 is someone moving off a port taken on the host network");
        Items(values, "config", "data", "prometheus_export", "instrumentations").Should().Equal("*");
        Items(values, "config", "data", "metrics", "features").Should().Equal("application");
        Items(values, "config", "data", "routes", "ignored_patterns").Should().Equal("/ping");
        At(values, "config", "data", "routes", "ignore_mode").Should().BeNull(
            "the operator's patterns keep upstream's default mode, which is what they were written against");
        Scalar(values, "serviceMonitor", "metrics", "endpoint", "interval").Should().Be("15s");
        ((YamlSequenceNode)At(values, "serviceMonitor", "metrics", "endpoint", "metricRelabelings")!)
            .Children.Should().ContainSingle("the operator's relabelling replaces ours, it is not appended to");

        Scalar(values, "service", "enabled").Should().Be("true");
        Scalar(values, "serviceMonitor", "enabled").Should().Be("true");
    }

    /// <summary>
    /// ⚠️ The decision the port rests on. An operator's 0 and the 0 EntKube used to default to are the
    /// same scalar at the same path, so a 0 is respected as "off" whoever wrote it — and with it the
    /// Service (one with no port is invalid) and the ServiceMonitor go off too.
    /// </summary>
    [Fact]
    public void A_zero_port_is_respected_as_off()
    {
        const string off = """
            config:
              data:
                prometheus_export:
                  port: 0
            service:
              enabled: true
            serviceMonitor:
              enabled: true
            """;

        string values = YamlFormMerger.EnableObiPrometheusMetrics(off);

        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("0");
        Scalar(values, "service", "enabled").Should().Be("false", "a Service with no port is rejected by the API server");
        Scalar(values, "serviceMonitor", "enabled").Should().Be("false");
        At(values, "config", "data", "metrics").Should().BeNull("nothing is added for metrics that are off");
        At(values, "serviceMonitor", "metrics").Should().BeNull();
    }

    [Fact]
    public void An_install_older_than_the_bounds_gets_the_default_port()
    {
        const string old = """
            config:
              data:
                otel_traces_export:
                  endpoint: http://otel-collector.monitoring:4317
            """;

        Scalar(YamlFormMerger.EnableObiPrometheusMetrics(old), "config", "data", "prometheus_export", "port")
            .Should().Be("9090", "an absent port is no one's decision — the chart's own default is 9090 too");
    }

    /// <summary>
    /// The toggle the form used to show wrote <c>serviceMonitor.enabled: false</c> into every component
    /// registered through it, and could never have produced a ServiceMonitor either way. Respecting that
    /// false would leave the port open with nothing scraping it.
    /// </summary>
    [Fact]
    public void The_dead_toggles_stored_false_does_not_hold_the_monitor_off()
    {
        string stored = CatalogDefaults + "\nserviceMonitor:\n  enabled: false\n";

        Scalar(YamlFormMerger.EnableObiPrometheusMetrics(stored), "serviceMonitor", "enabled").Should().Be("true");
    }

    [Fact]
    public void Enabling_is_idempotent()
    {
        string once = YamlFormMerger.EnableObiPrometheusMetrics(CatalogDefaults);

        YamlFormMerger.EnableObiPrometheusMetrics(once).Should().Be(once,
            "a re-serialised document is a helm upgrade that exists only because of the rewrite");
    }

    [Theory]
    [InlineData("")]
    [InlineData("resources:\n  limits:\n    memory: 1Gi")]
    [InlineData("config:\n  data: {}")]
    [InlineData("config:\n  data:\n    extensions:\n      obi:\n        version: \"2.0\"\n    file_format: \"1.0\"")]
    public void Only_a_config_v1_document_is_edited(string yaml)
    {
        // Writing config.data where it is empty would flip the chart from its Config v2 defaults to its
        // v1 ones — a reinstall with different settings — and a v2 document has none of these keys.
        YamlFormMerger.EnableObiPrometheusMetrics(yaml).Should().Be(yaml);
    }

    // ──────── Without Prometheus ────────

    [Fact]
    public void Without_prometheus_the_catalog_defaults_render_no_endpoint_and_no_monitor()
    {
        string values = YamlFormMerger.KeepObiPrometheusMetricsOff(CatalogDefaults);

        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("0",
            "an unscraped endpoint never runs its own reaper");
        Scalar(values, "serviceMonitor", "enabled").Should().NotBe("true");
        Scalar(values, "service", "enabled").Should().NotBe("true");
    }

    [Fact]
    public void Without_prometheus_a_stored_monitor_and_port_are_forced_off()
    {
        const string stored = """
            config:
              data:
                prometheus_export:
                  port: 9090
            service:
              enabled: true
            serviceMonitor:
              enabled: true
            """;

        string values = YamlFormMerger.KeepObiPrometheusMetricsOff(stored);

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false",
            "there is no kind ServiceMonitor without the Prometheus Operator — helm would fail the release");
        Scalar(values, "service", "enabled").Should().Be("false");
        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("0");
    }

    [Fact]
    public void Without_prometheus_an_absent_port_is_written_as_zero()
    {
        const string old = """
            config:
              data:
                otel_traces_export:
                  endpoint: http://otel-collector.monitoring:4317
            """;

        Scalar(YamlFormMerger.KeepObiPrometheusMetricsOff(old), "config", "data", "prometheus_export", "port")
            .Should().Be("0", "otherwise the chart's v1 default of 9090 survives the merge");
    }

    [Fact]
    public void Without_prometheus_a_v2_document_still_gets_no_monitor()
    {
        const string v2 = """
            config:
              data:
                extensions:
                  obi:
                    version: "2.0"
            serviceMonitor:
              enabled: true
            """;

        string values = YamlFormMerger.KeepObiPrometheusMetricsOff(v2);

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false");
        At(values, "config", "data", "prometheus_export").Should().BeNull("a v2 document has no such key");
    }

    [Fact]
    public void Keeping_off_is_idempotent()
    {
        string once = YamlFormMerger.KeepObiPrometheusMetricsOff(CatalogDefaults);

        YamlFormMerger.KeepObiPrometheusMetricsOff(once).Should().Be(once);
    }

    // ──────── The catalog ────────

    [Fact]
    public void The_catalog_pins_the_chart_that_ships_obi_v1()
    {
        CatalogEntry obi = ComponentCatalog.GetByKey("otel-ebpf")!;

        obi.HelmChartVersion.Should().Be("0.16.4", "chart 0.16.4's appVersion is OBI v1.0.0");
        obi.FormFields.Should().NotContain(f => f.YamlPath == "serviceMonitor.enabled",
            "the ServiceMonitor follows the cluster now; a toggle the apply step overrides would lie");
        obi.FormFields.Single(f => f.Key == "prometheus-port").DefaultValue.Should().Be("9090",
            "a default of 0 would be respected as off, and no app would get metrics unasked");
    }
}

/// <summary>
/// The same gating, through the apply path: <see cref="ComponentLifecycleService.GetInstallCommandAsync"/>
/// asks Catalog whether the cluster has Prometheus and renders the OBI values accordingly.
/// </summary>
public class ObiPrometheusMetricsApplyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ComponentLifecycleService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();
    private readonly Guid strangersClusterId = Guid.NewGuid();

    public ObiPrometheusMetricsApplyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        foreach ((Guid tenant, Guid cluster) in new[] { (tenantId, clusterId), (strangerId, strangersClusterId) })
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = $"t-{tenant:N}", Slug = $"t-{tenant:N}" });
            Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant, Name = "prod" };
            db.Environments.Add(env);
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = cluster, TenantId = tenant, EnvironmentId = env.Id,
                Name = $"c-{cluster:N}", ApiServerUrl = "https://k8s.example",
            });
        }
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        VaultService vault = new(factory,
            new VaultEncryptionService(Convert.FromBase64String(TestServices.TestRootKeyBase64)),
            TestServices.NoClusterAccess);
        sut = TestServices.BuildLifecycle(factory, vault);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddObi(string values)
    {
        ClusterComponent obi = new()
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = "otel-ebpf", ComponentType = "HelmChart",
            HelmChartName = "opentelemetry-ebpf-instrumentation", ReleaseName = "otel-ebpf",
            Namespace = "monitoring", HelmValues = values,
        };
        db.ClusterComponents.Add(obi);
        db.SaveChanges();
        return obi.Id;
    }

    private void AddPrometheus(Guid cluster, ComponentStatus status)
    {
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = cluster, Name = "kube-prometheus-stack", ComponentType = "HelmChart",
            HelmChartName = "kube-prometheus-stack", Namespace = "monitoring", Status = status,
        });
        db.SaveChanges();
    }

    private async Task<string> RenderAsync(Guid componentId)
        => (await sut.GetInstallCommandAsync(tenantId, componentId)).ValuesYaml!;

    private static string? Scalar(string yaml, params string[] path) => YamlFormMerger.ExtractValue(yaml, string.Join('.', path));

    [Fact]
    public async Task On_a_cluster_with_prometheus_the_metrics_are_turned_on()
    {
        AddPrometheus(clusterId, ComponentStatus.Installed);
        Guid obi = AddObi(ComponentCatalog.GetByKey("otel-ebpf")!.DefaultValues!);

        string values = await RenderAsync(obi);

        Scalar(values, "serviceMonitor", "enabled").Should().Be("true");
        Scalar(values, "service", "enabled").Should().Be("true");
        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("9090");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ComponentStatus.Failed)]
    [InlineData(ComponentStatus.NotInstalled)]
    public async Task On_a_cluster_without_an_installed_prometheus_no_monitor_is_rendered(ComponentStatus? prometheus)
    {
        if (prometheus is { } status) AddPrometheus(clusterId, status);
        Guid obi = AddObi(ComponentCatalog.GetByKey("otel-ebpf")!.DefaultValues! + "\nserviceMonitor:\n  enabled: true\n");

        string values = await RenderAsync(obi);

        Scalar(values, "serviceMonitor", "enabled").Should().NotBe("true",
            "helm fails the whole release on a ServiceMonitor where the CRD does not exist");
        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("0");
    }

    [Fact]
    public async Task Another_tenants_prometheus_does_not_count()
    {
        AddPrometheus(strangersClusterId, ComponentStatus.Installed);
        Guid obi = AddObi(ComponentCatalog.GetByKey("otel-ebpf")!.DefaultValues!);

        Scalar(await RenderAsync(obi), "serviceMonitor", "enabled").Should().NotBe("true");
    }

    /// <summary>
    /// ⚠️ Pins the ORDER of the two OBI steps. The memory bounds write <c>prometheus_export.port: 0</c>
    /// where the block is absent; run first, that 0 would be read as the operator's "off" and an old
    /// install would never get metrics.
    /// </summary>
    [Fact]
    public async Task An_install_older_than_the_bounds_gets_metrics_too()
    {
        AddPrometheus(clusterId, ComponentStatus.Installed);
        Guid obi = AddObi("""
            config:
              data:
                otel_traces_export:
                  endpoint: http://otel-collector.monitoring:4317
            """);

        string values = await RenderAsync(obi);

        Scalar(values, "config", "data", "prometheus_export", "port").Should().Be("9090");
        Scalar(values, "serviceMonitor", "enabled").Should().Be("true");
    }

    [Fact]
    public async Task The_stored_values_are_not_rewritten()
    {
        AddPrometheus(clusterId, ComponentStatus.Installed);
        string stored = ComponentCatalog.GetByKey("otel-ebpf")!.DefaultValues!;
        Guid obi = AddObi(stored);

        await RenderAsync(obi);

        db.ChangeTracker.Clear();
        (await db.ClusterComponents.SingleAsync(c => c.Id == obi)).HelmValues.Should().Be(stored,
            "the decision is the cluster's, re-taken at every apply — writing it back would make it the operator's");
    }
}
