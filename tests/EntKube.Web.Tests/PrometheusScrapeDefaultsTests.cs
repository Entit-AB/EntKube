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

    private static List<YamlMappingNode> AnnotationJobRules(string yaml) =>
        ((YamlSequenceNode)((YamlMappingNode)((YamlSequenceNode)Spec(yaml)["additionalScrapeConfigs"])
            .Cast<YamlMappingNode>().Single(j => ((YamlScalarNode)j["job_name"]).Value == "kubernetes-pods-annotated"))
            ["relabel_configs"]).Cast<YamlMappingNode>().ToList();

    private static List<string?> Sources(YamlMappingNode rule) =>
        rule.Children.TryGetValue(new YamlScalarNode("source_labels"), out YamlNode? s)
            ? ((YamlSequenceNode)s).Select(l => ((YamlScalarNode)l).Value).ToList()
            : [];

    /// <summary>
    /// Prometheus discovers a pod once per container, and the port annotation points every one of them at
    /// the same address. They are one target only while nothing distinguishes them; a label taken from the
    /// container scraped a pod with a sidecar twice and doubled every sum over it.
    /// </summary>
    [Fact]
    public void Nothing_from_the_container_becomes_a_label_so_a_pod_is_one_target()
    {
        List<YamlMappingNode> writesLabel = AnnotationJobRules(CatalogDefaults)
            .Where(r => Target(r) is { } t && !t.StartsWith("__", StringComparison.Ordinal)).ToList();

        writesLabel.Should().NotBeEmpty("the namespace and pod labels are what every query selects on");
        writesLabel.SelectMany(Sources).Should().NotContain(l => l!.Contains("container"));

        // labelmap copies by pattern, so it has no source_labels to look at.
        AnnotationJobRules(CatalogDefaults)
            .Where(r => r.Children.TryGetValue(new YamlScalarNode("action"), out YamlNode? a) && ((YamlScalarNode)a).Value == "labelmap")
            .Select(r => ((YamlScalarNode)r["regex"]).Value).Should().OnlyContain(regex => !regex!.Contains("container"));
    }

    /// <summary>
    /// A container with no port, in a pod that names none, has only the pod's port 80 for an address. It was
    /// a target that could never answer, and kube-prometheus-stack alerts on those.
    /// </summary>
    [Fact]
    public void A_container_with_no_port_is_dropped_unless_the_pod_names_one()
    {
        YamlMappingNode drop = AnnotationJobRules(CatalogDefaults)
            .Single(r => Sources(r).Contains("__meta_kubernetes_pod_container_port_number"));

        ((YamlScalarNode)drop["action"]).Value.Should().Be("drop");
        Sources(drop).Should().Equal(
            "__meta_kubernetes_pod_annotation_prometheus_io_port", "__meta_kubernetes_pod_container_port_number");
        // Joined with ';' — a bare separator is "both empty", and either one alone keeps the target.
        ((YamlScalarNode)drop["regex"]).Value.Should().Be(";");
    }

    /// <summary>The job as first shipped — what an install from #490 has stored.</summary>
    private static string InstallWithFirstJob(string extraRule = "", string extraJob = "") => $$"""
        prometheus:
          prometheusSpec:
            retention: 30d
            additionalScrapeConfigs:
            {{extraJob}}
            - relabel_configs:
              - source_labels: ["__meta_kubernetes_pod_annotation_prometheus_io_scrape"]
                action: keep
                regex: 'true'
              - source_labels: [__meta_kubernetes_pod_phase]
                action: drop
                regex: Pending|Succeeded|Failed|Unknown
              - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_scheme]
                action: replace
                regex: (https?)
                target_label: __scheme__
              - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_path]
                action: replace
                regex: (.+)
                target_label: __metrics_path__
              - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_port, __meta_kubernetes_pod_ip]
                action: replace
                regex: (\d+);(([A-Fa-f0-9]{1,4}::?){1,7}[A-Fa-f0-9]{1,4})
                replacement: '[$2]:$1'
                target_label: __address__
              - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_port, __meta_kubernetes_pod_ip]
                action: replace
                regex: (\d+);((([0-9]+?)(\.|$)){4})
                replacement: $2:$1
                target_label: __address__
              - source_labels: [__meta_kubernetes_namespace]
                target_label: namespace
              - source_labels: [__meta_kubernetes_pod_name]
                target_label: pod
              - source_labels: [__meta_kubernetes_pod_container_name]
                target_label: container
              - action: labelmap
                regex: __meta_kubernetes_pod_label_(app_kubernetes_io_name|app_kubernetes_io_instance)
              {{extraRule}}
              job_name: kubernetes-pods-annotated
              kubernetes_sd_configs:
              - role: pod
        """;

    /// <summary>
    /// The fix has to reach the installs that have the bug. Keys in another order and quotes in another style
    /// on purpose: saving the component form rewrites both, and neither makes the job the operator's.
    /// </summary>
    [Fact]
    public void The_first_shipped_job_is_replaced_by_the_corrected_one()
    {
        string repaired = YamlFormMerger.EnsurePrometheusScrapeDefaults(
            InstallWithFirstJob(extraJob: "- job_name: their-own"), CatalogDefaults);

        AnnotationJobRules(repaired).Select(Text).Should().Equal(AnnotationJobRules(CatalogDefaults).Select(Text));
        ((YamlSequenceNode)Spec(repaired)["additionalScrapeConfigs"]).Cast<YamlMappingNode>()
            .Select(j => ((YamlScalarNode)j["job_name"]).Value).Should().Equal("their-own", "kubernetes-pods-annotated");
        Scalar(Spec(repaired), "retention").Should().Be("30d");

        YamlFormMerger.EnsurePrometheusScrapeDefaults(repaired, CatalogDefaults).Should().Be(repaired, "once is enough");
    }

    [Fact]
    public void A_job_the_operator_edited_is_theirs()
    {
        string edited = InstallWithFirstJob(extraRule: "- {action: labeldrop, regex: pod_template_hash}");
        string after = YamlFormMerger.EnsurePrometheusScrapeDefaults(edited, CatalogDefaults);

        AnnotationJobRules(after).Select(Target).Should().Contain("container", "an edited job is left exactly as it is");
        AnnotationJobRules(after).Should().HaveCount(11);
    }

    /// <summary>One rule as text. Not <c>BeEquivalentTo</c> on the nodes: it walks their graph and does not finish.</summary>
    private static string Text(YamlMappingNode rule) => string.Join(" | ", rule.Children
        .OrderBy(kv => ((YamlScalarNode)kv.Key).Value, StringComparer.Ordinal)
        .Select(kv => $"{((YamlScalarNode)kv.Key).Value}={(kv.Value is YamlSequenceNode list
            ? string.Join(",", list.Select(n => ((YamlScalarNode)n).Value)) : ((YamlScalarNode)kv.Value).Value)}"));

    private static string? Target(YamlMappingNode r) =>
        r.Children.TryGetValue(new YamlScalarNode("target_label"), out YamlNode? t) ? ((YamlScalarNode)t).Value : null;

    [Fact]
    public void The_chart_is_pinned()
    {
        ComponentCatalog.GetByKey("kube-prometheus-stack")!.HelmChartVersion.Should().Be("93.9.0");
    }
}
