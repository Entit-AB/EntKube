using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.Serialization;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for filling in catalog defaults a component was registered before.
///
/// Catalog DefaultValues are read once, at registration. From then on the component carries its
/// own copy, so a fix shipped in the catalog reaches new installs and nothing else — which is how
/// a gateway can be upgraded to a current chart and still come back with one replica and no
/// PodDisruptionBudget, long after the catalog grew both. Subcharts do not have this problem
/// because they re-read their defaults on every apply, and that asymmetry is what makes the
/// failure so confusing in practice: the same edit lands for istiod and vanishes for the gateway.
///
/// The rule being tested is narrow on purpose. Absent means unconsidered, and gets filled in.
/// Present means decided, and is left alone — at any depth, with any value.
/// </summary>
public class CatalogDefaultFillInTests
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private static Dictionary<object, object> Parse(string yaml) =>
        (Dictionary<object, object>)Yaml.Deserialize<object>(yaml)!;

    /// <summary>The values actually stored for a long-lived gateway, taken from a live cluster.</summary>
    private const string StoredGatewayValues = """
        resources:
          requests:
            cpu: 100m
            memory: 128Mi
        service:
          ports:
          - name: status-port
            port: 15021
            protocol: TCP
            targetPort: 15021
          - name: http2
            port: 80
            protocol: TCP
            targetPort: 80
          type: LoadBalancer
        """;

    private const string CatalogDefaults = """
        service:
          type: LoadBalancer
          ports:
            - name: status-port
              port: 15021
            - name: https-mtls
              port: 8443

        # High availability
        autoscaling:
          minReplicas: 2
        podDisruptionBudget:
          minAvailable: 1

        resources:
          requests:
            memory: 999Mi
            cpu: 999m
        """;

    [Fact]
    public void A_missing_key_is_filled_in()
    {
        string merged = ComponentLifecycleService.FillMissingCatalogDefaults(
            StoredGatewayValues, CatalogDefaults)!;

        var parsed = Parse(merged);
        parsed.Should().ContainKey("autoscaling");
        parsed.Should().ContainKey("podDisruptionBudget");
    }

    [Fact]
    public void A_key_the_operator_already_set_is_never_touched()
    {
        // The catalog asks for 999Mi. The operator said 128Mi. The operator wins.
        string merged = ComponentLifecycleService.FillMissingCatalogDefaults(
            StoredGatewayValues, CatalogDefaults)!;

        var requests = (Dictionary<object, object>)
            ((Dictionary<object, object>)Parse(merged)["resources"])["requests"];

        requests["memory"].Should().Be("128Mi");
        requests["cpu"].Should().Be("100m");
    }

    [Fact]
    public void A_customised_list_does_not_grow_from_the_catalog()
    {
        // service.ports is present, so it is left alone in full. Growing it would open a port on
        // an internet-facing LoadBalancer that nobody asked for.
        string merged = ComponentLifecycleService.FillMissingCatalogDefaults(
            StoredGatewayValues, CatalogDefaults)!;

        var ports = (List<object>)((Dictionary<object, object>)Parse(merged)["service"])["ports"];

        ports.Should().HaveCount(2);
        merged.Should().NotContain("https-mtls");
    }

    [Fact]
    public void Empty_stored_values_are_left_empty()
    {
        // Every catalog key is "missing" from an empty document. Pouring the whole default in is
        // not a fill-in, it is a reinstall with different settings.
        ComponentLifecycleService.FillMissingCatalogDefaults("", CatalogDefaults).Should().Be("");
        ComponentLifecycleService.FillMissingCatalogDefaults(null, CatalogDefaults).Should().BeNull();
    }

    [Fact]
    public void Subchart_markers_in_comments_survive()
    {
        // These markers live in YAML comments and drive whether a subchart is installed at all.
        // A parse/serialise round trip would drop them and silently disable istiod.
        const string stored = """
            # subchart:istiod=true
            resources:
              requests:
                cpu: 100m
            """;

        string merged = ComponentLifecycleService.FillMissingCatalogDefaults(stored, CatalogDefaults)!;

        merged.Should().Contain("# subchart:istiod=true");
    }

    [Fact]
    public void The_result_is_still_valid_yaml()
    {
        string merged = ComponentLifecycleService.FillMissingCatalogDefaults(
            StoredGatewayValues, CatalogDefaults)!;

        Action parse = () => Parse(merged);
        parse.Should().NotThrow();
    }

    [Fact]
    public void Nothing_is_added_when_the_catalog_has_nothing_new()
    {
        string merged = ComponentLifecycleService.FillMissingCatalogDefaults(
            StoredGatewayValues, "resources:\n  requests:\n    cpu: 1")!;

        merged.Should().Be(StoredGatewayValues);
    }

    [Fact]
    public void The_live_gateway_entry_would_gain_its_ha_settings()
    {
        // End to end against the real catalog entry, so a future rename of these keys shows up
        // here rather than as a gateway that quietly stays on one replica.
        CatalogEntry gateway = ComponentCatalog.Entries.Single(e => e.Key == "istio");

        string merged = ComponentLifecycleService.FillMissingCatalogDefaults(
            StoredGatewayValues, gateway.DefaultValues)!;

        var parsed = Parse(merged);
        ((Dictionary<object, object>)parsed["autoscaling"])["minReplicas"].Should().Be("2");
        ((Dictionary<object, object>)parsed["podDisruptionBudget"])["minAvailable"].Should().Be("1");
    }
}
