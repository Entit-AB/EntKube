using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.Serialization;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for weighted (canary) traffic splitting in generated HTTPRoutes.
///
/// The property that matters most: a route WITHOUT a canary must generate exactly what
/// it generated before this feature existed. If it did not, every deployment in the
/// fleet would report as drifted the first time this shipped.
/// </summary>
public class CanaryRoutingTests
{
    private static AppDeploymentRoute Route(
        string? canaryService = null, int canaryWeight = 0, int? canaryPort = null) => new()
    {
        Id = Guid.NewGuid(),
        ServiceName = "storefront",
        ServicePort = 80,
        CanaryServiceName = canaryService,
        CanaryWeight = canaryWeight,
        CanaryServicePort = canaryPort,
    };

    private static string Render(AppDeploymentRoute route) =>
        AppRouteService.RenderBackendRefs(route, "acme-prod", "        ");

    /// <summary>Parses the fragment as a YAML list so structure is checked, not string shape.</summary>
    private static List<Dictionary<object, object>> Parse(string backends)
    {
        string yaml = string.Join("\n", backends.Split('\n').Select(l =>
            l.Length > 8 ? l[8..] : l));
        object? parsed = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        return ((List<object>)parsed!).Cast<Dictionary<object, object>>().ToList();
    }

    // ── No canary: unchanged behaviour ──

    [Fact]
    public void A_route_without_a_canary_emits_one_backend_and_no_weight()
    {
        string backends = Render(Route());

        backends.Should().NotContain("weight");
        Parse(backends).Should().ContainSingle()
            .Which["name"].Should().Be("storefront");
    }

    [Fact]
    public void A_canary_service_with_zero_weight_emits_no_canary_backend()
    {
        // Configuring the service but leaving the weight at zero is how an operator
        // stages a canary before sending it traffic. It must send none.
        string backends = Render(Route(canaryService: "storefront-canary", canaryWeight: 0));

        Parse(backends).Should().ContainSingle();
        backends.Should().NotContain("canary");
    }

    [Fact]
    public void A_weight_without_a_canary_service_sends_everything_to_stable()
    {
        // Half-configured must fail safe: a weight alone has nowhere to send traffic,
        // and inventing a destination would be worse than ignoring the weight.
        Parse(Render(Route(canaryWeight: 50))).Should().ContainSingle()
            .Which["name"].Should().Be("storefront");
    }

    // ── Splitting ──

    [Fact]
    public void A_canary_splits_traffic_between_two_backends()
    {
        List<Dictionary<object, object>> backends =
            Parse(Render(Route("storefront-canary", 10)));

        backends.Should().HaveCount(2);
        backends[0]["name"].Should().Be("storefront");
        backends[0]["weight"].Should().Be("90");
        backends[1]["name"].Should().Be("storefront-canary");
        backends[1]["weight"].Should().Be("10");
    }

    [Fact]
    public void Weights_always_sum_to_one_hundred()
    {
        // Gateway API treats weights as relative shares, so this is for the human reading
        // the manifest — but a pair that does not sum to 100 reads as a mistake.
        foreach (int weight in new[] { 1, 5, 25, 50, 75, 99 })
        {
            List<Dictionary<object, object>> backends = Parse(Render(Route("canary", weight)));

            int total = backends.Sum(b => int.Parse((string)b["weight"]));
            total.Should().Be(100, $"weight {weight} should split 100");
        }
    }

    [Fact]
    public void One_hundred_percent_sends_everything_to_the_canary()
    {
        List<Dictionary<object, object>> backends = Parse(Render(Route("storefront-canary", 100)));

        backends[0]["weight"].Should().Be("0");
        backends[1]["weight"].Should().Be("100");
    }

    [Fact]
    public void A_negative_weight_falls_back_to_no_canary()
    {
        // Clamping to zero means "send nothing to the canary", which is the safe reading
        // of a nonsensical stored value — not "send an undefined share".
        string backends = Render(Route("canary", -10));

        Parse(backends).Should().ContainSingle().Which["name"].Should().Be("storefront");
        backends.Should().NotContain("weight");
    }

    [Fact]
    public void A_weight_above_one_hundred_is_clamped_to_one_hundred()
    {
        // An out-of-range weight reaching the gateway produces undefined splitting.
        List<Dictionary<object, object>> backends = Parse(Render(Route("canary", 150)));

        backends[0]["weight"].Should().Be("0");
        backends[1]["weight"].Should().Be("100");
    }

    [Fact]
    public void The_canary_defaults_to_the_stable_port()
    {
        // Two Services fronting the same application almost always expose the same port,
        // and requiring it to be restated is a field to get wrong for no benefit.
        Parse(Render(Route("canary", 20)))[1]["port"].Should().Be("80");
    }

    [Fact]
    public void An_explicit_canary_port_is_used()
    {
        Parse(Render(Route("canary", 20, canaryPort: 8080)))[1]["port"].Should().Be("8080");
    }

    [Fact]
    public void Both_backends_are_rendered_in_the_routes_namespace()
    {
        List<Dictionary<object, object>> backends = Parse(Render(Route("canary", 30)));

        backends.Should().OnlyContain(b => (string)b["namespace"] == "acme-prod");
    }

    // ── The whole HTTPRoute still parses ──

    [Fact]
    public void The_generated_httproute_is_valid_yaml_with_a_canary()
    {
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = Guid.NewGuid(), Hostname = "shop.example.com" };
        AppDeploymentRoute dr = Route("storefront-canary", 25);
        dr.AppRoute = route;

        string yaml = AppRouteService.GenerateHttpRouteYaml(route, [dr]);

        Action parse = () => new DeserializerBuilder().Build().Deserialize<object>(yaml);
        parse.Should().NotThrow();

        yaml.Should().Contain("storefront-canary");
        yaml.Should().Contain("weight: 75");
        yaml.Should().Contain("weight: 25");
    }
}
