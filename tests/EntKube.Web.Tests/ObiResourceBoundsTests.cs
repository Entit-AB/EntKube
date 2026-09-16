using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.Serialization;

namespace EntKube.Web.Tests;

/// <summary>
/// The OpenTelemetry eBPF Instrumentation agent's memory bounds, applied to a component that was
/// registered before those bounds existed.
///
/// Every one of them lives under <c>config.data</c>, and <see cref="ComponentLifecycleService"/>'s
/// catalog fill-in is deliberately top-level only — so an install made before 2026-09-10 has a
/// <c>config</c> key, the whole block counts as "present", and the fix reaches new installs and nothing
/// else. The agent then keeps running with thirteen protocol probe sets per instrumented executable,
/// full-size preallocated eBPF maps, and a RED-metrics endpoint that nothing scrapes — which is what a
/// multi-gigabyte tracing agent is made of.
/// </summary>
public class ObiResourceBoundsTests
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private static Dictionary<object, object> Parse(string yaml) =>
        (Dictionary<object, object>)Yaml.Deserialize<object>(yaml)!;

    private static Dictionary<object, object> ConfigData(string yaml) =>
        (Dictionary<object, object>)((Dictionary<object, object>)Parse(yaml)["config"])["data"];

    /// <summary>
    /// The values EntKube actually stored for this component before the hardening — note
    /// <c>instrumentations</c> at the TOP LEVEL of the config, where OBI does not read it, so no protocol
    /// selection was ever in effect.
    /// </summary>
    private const string PreHardeningValues = """
        resources:
          requests:
            cpu: 100m
            memory: 256Mi
          limits:
            cpu: 500m
            memory: 1Gi
        k8sCache:
          replicas: 1
          resources:
            requests:
              cpu: 50m
              memory: 128Mi
        config:
          data:
            discovery:
              instrument:
                - k8s_namespace: "*"
              exclude_otel_instrumented_services: true
            attributes:
              kubernetes:
                enable: true
            instrumentations:
              - "*"
            ebpf:
              context_propagation: all
            otel_traces_export:
              endpoint: http://otel-collector.monitoring:4317
              protocol: grpc
            otel_metrics_export:
              endpoint: http://otel-collector.monitoring:4317
              protocol: grpc
        """;

    [Fact]
    public void FillsTheBoundsAnOlderInstallNeverHad()
    {
        string repaired = YamlFormMerger.EnsureObiResourceBounds(PreHardeningValues);
        Dictionary<object, object> data = ConfigData(repaired);

        // Three protocol probe sets instead of the thirteen OBI loads when nothing is selected.
        var traces = (Dictionary<object, object>)data["otel_traces_export"];
        ((List<object>)traces["instrumentations"]).Should().BeEquivalentTo(["http", "grpc", "sql"]);

        // eBPF maps are preallocated kernel memory charged to this pod; halve them.
        var maps = (Dictionary<object, object>)((Dictionary<object, object>)data["ebpf"])["maps_config"];
        maps["global_scale_factor"].Should().Be("-1");

        // The chart's own :9090 default would otherwise survive the merge, with unbounded route
        // cardinality behind it, for metrics nothing reads.
        ((Dictionary<object, object>)data["prometheus_export"])["port"].Should().Be("0");
        var routes = (Dictionary<object, object>)data["routes"];
        routes["unmatched"].Should().Be("low-cardinality");
        routes["max_path_segment_cardinality"].Should().Be("10");

        // The metadata cache is a second Go process under its own limit.
        var cacheEnv = (Dictionary<object, object>)((Dictionary<object, object>)Parse(repaired)["k8sCache"])["env"];
        cacheEnv["GOMEMLIMIT"].Should().Be("350MiB");
    }

    [Fact]
    public void LeavesEverySettingTheOperatorAlreadyMade()
    {
        string repaired = YamlFormMerger.EnsureObiResourceBounds(PreHardeningValues);
        Dictionary<object, object> data = ConfigData(repaired);

        // Present means decided — including the two expensive ones. `context_propagation: all` attaches
        // traffic-control and socket programs to every socket on the node and an OTLP metrics endpoint
        // costs heap for the whole export interval, but rewriting either behind the operator's back is
        // not this function's job: both are form fields, so changing them stays their decision.
        ((Dictionary<object, object>)data["ebpf"])["context_propagation"].Should().Be("all");
        ((Dictionary<object, object>)data["otel_metrics_export"])["endpoint"]
            .Should().Be("http://otel-collector.monitoring:4317");

        // Discovery, resources and the rest survive untouched.
        var discovery = (Dictionary<object, object>)data["discovery"];
        discovery["exclude_otel_instrumented_services"].Should().Be("true");
        var limits = (Dictionary<object, object>)((Dictionary<object, object>)Parse(repaired)["resources"])["limits"];
        limits["memory"].Should().Be("1Gi");
    }

    [Fact]
    public void IsIdempotent_AndLeavesCurrentInstallsAlone()
    {
        string once = YamlFormMerger.EnsureObiResourceBounds(PreHardeningValues);
        string twice = YamlFormMerger.EnsureObiResourceBounds(once);

        // A component already carrying the bounds is not rewritten at all — no churn in the stored
        // values, and no helm upgrade that exists only because the document was re-serialized.
        twice.Should().Be(once);
    }

    [Fact]
    public void DoesNothingWithoutAConfigBlock()
    {
        const string noConfig = """
            resources:
              limits:
                memory: 1Gi
            """;
        YamlFormMerger.EnsureObiResourceBounds(noConfig).Should().Be(noConfig);
        YamlFormMerger.EnsureObiResourceBounds("").Should().Be("");
    }

    /// <summary>
    /// The catalog's own defaults must already satisfy the repair — otherwise a fresh install and a
    /// repaired one disagree, and this function becomes a second, divergent source of truth.
    /// </summary>
    [Fact]
    public void CatalogDefaults_AlreadyCarryEveryBound()
    {
        CatalogEntry obi = ComponentCatalog.Entries.Single(e => e.Key == "otel-ebpf");
        string defaults = obi.DefaultValues!;

        YamlFormMerger.EnsureObiResourceBounds(defaults).Should().Be(defaults);
    }

    /// <summary>
    /// Every memory lever the catalog documents must be reachable from the component's edit form.
    /// A setting that can only be changed by hand-editing stored YAML cannot be changed at all by the
    /// person looking at a 2Gi pod.
    /// </summary>
    [Fact]
    public void EveryMemoryLever_IsEditable()
    {
        CatalogEntry obi = ComponentCatalog.Entries.Single(e => e.Key == "otel-ebpf");
        List<string?> paths = [.. obi.FormFields.Select(f => f.YamlPath)];

        paths.Should().Contain("config.data.ebpf.context_propagation");
        paths.Should().Contain("config.data.otel_metrics_export.endpoint");
        paths.Should().Contain("config.data.ebpf.maps_config.global_scale_factor");
        paths.Should().Contain("config.data.prometheus_export.port");
        paths.Should().Contain("env.GOMEMLIMIT");
        paths.Should().Contain("resources.limits.memory");
    }
}
