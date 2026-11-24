using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// What a kube-prometheus-stack collects: the catalog's defaults for new installs, and the repair
/// that carries the scrape settings to installs made before them.
///
/// <para>Before the repair, the monitor-selector flags reached new installs only — the top-level
/// fill-in cannot see inside a <c>prometheus</c> key that already exists — so an older Prometheus
/// kept ignoring every monitor CNPG, Strimzi or RabbitMQ created, and the CNPG panel told operators
/// to paste the flags in by hand.</para>
/// </summary>
public class PrometheusScrapeDefaultsTests
{
    private static string CatalogDefaults => ComponentCatalog.GetByKey("kube-prometheus-stack")!.DefaultValues;

    /// <summary>What an install from before the selector flags and the scrape job stored.</summary>
    private const string OlderInstall = """
        grafana:
          enabled: true
        prometheus:
          prometheusSpec:
            retention: 30d
            resources:
              requests:
                memory: 1Gi
        """;

    private static YamlMappingNode Spec(string yaml)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return (YamlMappingNode)((YamlMappingNode)root["prometheus"])["prometheusSpec"];
    }

    private static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? n) ? ((YamlScalarNode)n).Value : null;

    [Fact]
    public void An_older_install_is_given_the_selector_flags_and_the_scrape_job()
    {
        YamlMappingNode spec = Spec(YamlFormMerger.EnsurePrometheusScrapeDefaults(OlderInstall, CatalogDefaults));

        foreach (string flag in new[]
                 {
                     "podMonitorSelectorNilUsesHelmValues", "serviceMonitorSelectorNilUsesHelmValues",
                     "probeSelectorNilUsesHelmValues", "scrapeConfigSelectorNilUsesHelmValues",
                     "ruleSelectorNilUsesHelmValues",
                 })
        {
            Scalar(spec, flag).Should().Be("false", "{0} decides whether other operators' monitors are scraped", flag);
        }

        var jobs = (YamlSequenceNode)spec["additionalScrapeConfigs"];
        ((YamlScalarNode)((YamlMappingNode)jobs[0])["job_name"]).Value.Should().Be("kubernetes-pods-annotated");
    }

    [Fact]
    public void What_the_operator_set_is_kept()
    {
        string own = OlderInstall.Replace("    retention: 30d", """
                retention: 30d
                podMonitorSelectorNilUsesHelmValues: true
                additionalScrapeConfigs:
                  - job_name: their-own
            """);

        YamlMappingNode spec = Spec(YamlFormMerger.EnsurePrometheusScrapeDefaults(own, CatalogDefaults));

        Scalar(spec, "podMonitorSelectorNilUsesHelmValues").Should().Be("true");
        var jobs = (YamlSequenceNode)spec["additionalScrapeConfigs"];
        jobs.Should().ContainSingle();
        ((YamlScalarNode)((YamlMappingNode)jobs[0])["job_name"]).Value.Should().Be("their-own");
        Scalar(spec, "retention").Should().Be("30d");
    }

    /// <summary>
    /// Adding storage to a running Prometheus changes its StatefulSet's volumes, and the history kept
    /// on the pod so far does not move with it. That is the operator's decision on the form.
    /// </summary>
    [Fact]
    public void Storage_is_never_added_to_an_existing_install_on_the_side()
    {
        YamlFormMerger.PrometheusSpecFillIns.Should().NotContain("storageSpec");
        Spec(YamlFormMerger.EnsurePrometheusScrapeDefaults(OlderInstall, CatalogDefaults))
            .Children.Should().NotContainKey(new YamlScalarNode("storageSpec"));
    }

    [Fact]
    public void An_install_that_already_has_everything_is_returned_unchanged()
    {
        string once = YamlFormMerger.EnsurePrometheusScrapeDefaults(OlderInstall, CatalogDefaults);

        YamlFormMerger.EnsurePrometheusScrapeDefaults(once, CatalogDefaults).Should().Be(once);
    }

    [Fact]
    public void A_new_install_keeps_its_history_on_a_volume()
    {
        var storage = (YamlScalarNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)
            Spec(CatalogDefaults)["storageSpec"])["volumeClaimTemplate"])["spec"])["resources"])["requests"]["storage"];

        storage.Value.Should().Be("20Gi");
        ComponentCatalog.GetByKey("kube-prometheus-stack")!.FormFields
            .Should().Contain(f => f.YamlPath == "prometheus.prometheusSpec.storageSpec.volumeClaimTemplate.spec.resources.requests.storage"
                && f.DefaultValue == "20Gi");
    }

    /// <summary>
    /// Only pods that ask: the first rule keeps a target only when its pod is annotated
    /// <c>prometheus.io/scrape: "true"</c>, so nothing is scraped that nobody opted into.
    /// </summary>
    [Fact]
    public void The_scrape_job_keeps_only_annotated_running_pods_and_honours_port_path_and_scheme()
    {
        var job = (YamlMappingNode)((YamlSequenceNode)Spec(CatalogDefaults)["additionalScrapeConfigs"])[0];
        var rules = ((YamlSequenceNode)job["relabel_configs"]).Cast<YamlMappingNode>().ToList();

        YamlMappingNode first = rules[0];
        ((YamlScalarNode)first["action"]).Value.Should().Be("keep");
        ((YamlScalarNode)((YamlSequenceNode)first["source_labels"])[0]).Value
            .Should().Be("__meta_kubernetes_pod_annotation_prometheus_io_scrape");
        ((YamlScalarNode)first["regex"]).Value.Should().Be("true");

        rules.Should().Contain(r => Target(r) == "__metrics_path__")
            .And.Contain(r => Target(r) == "__scheme__")
            .And.Contain(r => Target(r) == "__address__");
        rules.Should().Contain(r => r.Children.ContainsKey(new YamlScalarNode("regex"))
            && ((YamlScalarNode)r["regex"]).Value!.Contains("Pending"), "pods that are not running have nothing to scrape");

    }

    private static string? Target(YamlMappingNode r) =>
        r.Children.TryGetValue(new YamlScalarNode("target_label"), out YamlNode? t) ? ((YamlScalarNode)t).Value : null;

    [Fact]
    public void The_chart_is_pinned()
    {
        ComponentCatalog.GetByKey("kube-prometheus-stack")!.HelmChartVersion.Should().Be("93.9.0");
    }
}
