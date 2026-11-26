using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Contracts.DataServices;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// What kubectl is handed, and whether a live monitor already is that. A comparison that finds a
/// difference nothing can remove writes an audit row every pass for ever; one that misses an edit
/// leaves a database unscraped with a row that says it is not.
/// </summary>
public class ScrapeMonitorManifestTests
{
    private readonly ScrapeWorld world = new();

    /// <summary>One of everything that has a shape of its own.</summary>
    private IReadOnlyList<PlannedMonitor> AWideSet()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "true");
        world.CnpgPod("true");
        world.DataService(ManagedServiceKind.Redis, "caches", "sessions");
        world.Pod("caches", "sessions-leader-0", ScrapeWorld.Labels(("app", "sessions-leader"), ("redis_setup_type", "cluster")),
            ScrapeWorld.Container("redis-exporter", ("redis-exporter", 9121)));
        world.DataService(ManagedServiceKind.Kafka, "streams", "events");
        world.Pod("streams", "events-pool-0", ScrapeWorld.Labels(("strimzi.io/cluster", "events"), ("strimzi.io/kind", "Kafka")),
            ScrapeWorld.Container("kafka", ("tcp-prometheus", 9404)));
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events");
        world.Component("descheduler", "kube-system", "descheduler");
        world.Pod("kube-system", "descheduler-0", ScrapeWorld.Labels(
            ("app.kubernetes.io/name", "descheduler"), ("app.kubernetes.io/instance", "descheduler")),
            ScrapeWorld.Container("descheduler", (null, 10258)));
        world.Component("tetragon", "tetragon", "tetragon");
        Dictionary<string, string> tetragon = ScrapeWorld.Labels(
            ("app.kubernetes.io/name", "tetragon"), ("app.kubernetes.io/instance", "tetragon"));
        world.Pod("tetragon", "tetragon-0", tetragon, ScrapeWorld.Container("tetragon"));
        world.Service("tetragon", "tetragon", tetragon, tetragon, ("metrics", 2112, "2112"));

        IReadOnlyList<PlannedMonitor> planned = world.Plan().Apply;
        planned.Should().HaveCount(6, "the set this file compares should have something in it");
        return planned;
    }

    // ── The text ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One input, one document per monitor, and each reads back as exactly the object it was made
    /// from. Walked by hand: comparing YamlDotNet's nodes with FluentAssertions never terminates.
    /// </summary>
    [Fact]
    public void The_manifests_are_one_input_that_reads_back_as_what_was_meant()
    {
        IReadOnlyList<PlannedMonitor> planned = AWideSet();

        YamlStream stream = [];
        stream.Load(new StringReader(ScrapeMonitorManifest.Render(planned.Select(p => p.Manifest))));

        stream.Documents.Should().HaveCount(planned.Count);
        stream.Documents.Select(d => AsJson(d.RootNode).ToJsonString())
            .Should().Equal(planned.Select(p => p.Manifest.ToJsonString()));
    }

    /// <summary>A database called "true" has a label whose value is the word, not the truth value.</summary>
    [Fact]
    public void A_value_that_looks_like_something_else_stays_a_string()
    {
        string yaml = ScrapeMonitorManifest.Render(AWideSet().Select(p => p.Manifest));

        yaml.Should().Contain("cnpg.io/cluster: \"true\"");
        yaml.Should().Contain("targetPort: 10258", "and a number stays a number");
        yaml.Should().Contain("insecureSkipVerify: true");
    }

    [Theory]
    [InlineData("on")]
    [InlineData("No")]
    [InlineData("null")]
    [InlineData("1st")]
    public void A_key_YAML_would_read_as_something_else_is_quoted(string key)
    {
        string yaml = ScrapeMonitorManifest.Render([new JsonObject { [key] = "x" }]);

        yaml.Should().Be($"\"{key}\": \"x\"\n");
    }

    [Fact]
    public void What_is_deleted_is_named_and_nothing_more()
    {
        string yaml = ScrapeMonitorManifest.RenderNames(
        [
            new MonitorIdentity(MonitorKind.PodMonitor, "databases", "entkube-cnpg-a"),
            new MonitorIdentity(MonitorKind.ServiceMonitor, "queues", "events-metrics"),
        ]);

        yaml.Should().Be("""
            apiVersion: "monitoring.coreos.com/v1"
            kind: "PodMonitor"
            metadata:
              name: "entkube-cnpg-a"
              namespace: "databases"
            ---
            apiVersion: "monitoring.coreos.com/v1"
            kind: "ServiceMonitor"
            metadata:
              name: "events-metrics"
              namespace: "queues"

            """);
    }

    // ── The comparison ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ScrapeWorld.Apply</c> leaves them the way the API server does, with <c>action: replace</c>
    /// filled into the relabelling that does not say.
    /// </summary>
    [Fact]
    public void What_was_applied_is_not_applied_again()
    {
        AWideSet();
        world.Apply(world.Plan());

        world.Monitors.Single(m => m.Identity.Name == "entkube-tetragon-tetragon")
            .Spec["endpoints"]![0]!["relabelings"]![0]!["action"]!.GetValue<string>().Should().Be("replace");
        world.Plan().HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Nor_is_one_an_operator_has_tuned()
    {
        LiveMonitor live = Applied();
        live.Spec["podMetricsEndpoints"]![0]!["interval"] = "15s";
        live.Spec["sampleLimit"] = 10000;
        live.Spec["namespaceSelector"] = new JsonObject();

        Difference(live).Should().Be(MonitorDifference.None);
    }

    public static TheoryData<string, Action<LiveMonitor>> Edits => new()
    {
        { "a port", m => m.Spec["podMetricsEndpoints"]![0]!["port"] = "status" },
        { "a path", m => m.Spec["podMetricsEndpoints"]![0]!["path"] = "/probe" },
        { "the scheme", m => m.Spec["podMetricsEndpoints"]![0]!["scheme"] = "https" },
        { "an endpoint added", m => m.Spec["podMetricsEndpoints"]!.AsArray().Add(new JsonObject { ["port"] = "status" }) },
        { "every endpoint gone", m => m.Spec["podMetricsEndpoints"] = new JsonArray() },
        { "the endpoints themselves", m => m.Spec.Remove("podMetricsEndpoints") },
        { "a selector's value", m => m.Spec["selector"]!["matchLabels"]!["cnpg.io/cluster"] = "other" },
        { "a label of the selector", m => m.Spec["selector"]!["matchLabels"]!.AsObject().Remove("cnpg.io/podRole") },
        { "the selector", m => m.Spec.Remove("selector") },
        { "the ownership label", m => ((Dictionary<string, string>)m.Labels).Remove(ScrapeMonitorManifest.OwnershipLabel) },
        { "the release label", m => ((Dictionary<string, string>)m.Labels)["release"] = "other" },
        { "the owner", m => ((Dictionary<string, string>)m.Annotations).Clear() },
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public void An_edit_is_noticed(string what, Action<LiveMonitor> edit)
    {
        LiveMonitor live = Applied();
        edit(live);

        Difference(live).Should().Be(MonitorDifference.Apply, $"{what} was changed");
    }

    public static TheoryData<string, Action<LiveMonitor>> Narrowings => new()
    {
        { "a label", m => m.Spec["selector"]!["matchLabels"]!["team"] = "payments" },
        {
            "an expression", m => m.Spec["selector"]!["matchExpressions"] = new JsonArray(
                new JsonObject { ["key"] = "team", ["operator"] = "Exists" })
        },
        { "where it looks", m => m.Spec["namespaceSelector"] = new JsonObject { ["matchNames"] = new JsonArray("elsewhere") } },
        { "where it looks, wider", m => m.Spec["namespaceSelector"] = new JsonObject { ["any"] = true } },
    };

    /// <summary>kubectl apply removes only what it once wrote, so these survive every apply.</summary>
    [Theory]
    [MemberData(nameof(Narrowings))]
    public void What_was_added_to_the_selector_needs_the_monitor_made_again(string what, Action<LiveMonitor> edit)
    {
        LiveMonitor live = Applied();
        edit(live);

        Difference(live).Should().Be(MonitorDifference.Replace, $"{what} was added");
    }

    private JsonObject desired = [];

    private LiveMonitor Applied()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        ScrapeMonitorPlan plan = world.Plan();
        desired = plan.Apply.Single().Manifest;
        return world.Apply(plan).Monitors.Single();
    }

    private MonitorDifference Difference(LiveMonitor live) => ScrapeMonitorManifest.Compare(desired, live);

    /// <summary>
    /// The documents of one kubectl input, each as the JSON object it reads back as. The service
    /// tests' cluster applies and deletes what this finds, not what the planner meant.
    /// </summary>
    internal static IReadOnlyList<JsonObject> Documents(string yaml)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(yaml));
        return [.. stream.Documents.Select(d => AsJson(d.RootNode).AsObject())];
    }

    /// <summary>A YAML node as JSON: quoted is a string, plain is whatever it looks like.</summary>
    internal static JsonNode AsJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c =>
            KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, (JsonNode?)AsJson(c.Value)))),
        YamlSequenceNode list => new JsonArray([.. list.Children.Select(AsJson)]),
        YamlScalarNode { Style: ScalarStyle.DoubleQuoted } text => JsonValue.Create(text.Value!)!,
        YamlScalarNode plain => JsonNode.Parse(plain.Value!)!,
        _ => throw new JsonException($"unexpected {node.NodeType}"),
    };
}
