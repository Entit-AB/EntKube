using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Harbor published through the cluster's own gateway, with the chart's bundled nginx proxy left
/// uninstalled.
///
/// <para>Harbor is two Services behind one hostname — the core, which serves the API, the token
/// service and the registry's <c>/v2/</c> endpoints, and the portal, which serves the UI. The chart
/// ships an nginx deployment whose only job is to split those paths, and it omits that deployment
/// only when told something in front is doing the split. EntKube is that something: the split lives
/// in the catalog entry, and the HTTPRoute it owns carries it. These tests hold the two halves of
/// that bargain together — if the split is dropped, half the registry answers the wrong service;
/// if the expose mode goes back to clusterIP, nginx comes back.</para>
/// </summary>
public class HarborGatewayRouteTests
{
    private static CatalogEntry Harbor =>
        ComponentCatalog.GetByKey("harbor") ?? throw new InvalidOperationException("harbor entry is gone");

    private static ExternalRoute HarborRoute(string releaseName = "harbor", string pathPrefix = "/")
    {
        Guid componentId = Guid.NewGuid();

        return new ExternalRoute
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            Hostname = "registry.example.com",
            ServiceName = $"{releaseName}-core",
            ServicePort = 80,
            PathPrefix = pathPrefix,
            GatewayName = "traefik-gateway",
            GatewayNamespace = "traefik",
            RequestTimeoutSeconds = ExternalRouteService.RegistryRequestTimeoutSeconds,
            Component = new ClusterComponent
            {
                Id = componentId,
                ClusterId = Guid.NewGuid(),
                Name = "harbor",
                ComponentType = "HelmChart",
                HelmChartName = "harbor",
                ReleaseName = releaseName,
                Namespace = "harbor"
            }
        };
    }

    /// <summary>
    /// The chart keeps its nginx proxy for every expose type except "ingress" and "route", so this
    /// value is the whole of what decides whether a second proxy is installed in front of the one
    /// the cluster already runs.
    /// </summary>
    [Fact]
    public void The_defaults_pick_the_one_expose_mode_that_installs_no_nginx()
    {
        YamlFormMerger.ExtractValue(Harbor.DefaultValues ?? "", "expose.type")
            .Should().Be("ingress");
    }

    /// <summary>
    /// "ingress" leaves an Ingress behind. Pinned to a class no controller watches it carries no
    /// traffic — but only while the class in the defaults is the same one the service writes back
    /// on every install.
    /// </summary>
    [Fact]
    public void The_ingress_the_chart_leaves_behind_is_pinned_to_a_class_nothing_serves()
    {
        YamlFormMerger.ExtractValue(Harbor.DefaultValues ?? "", "expose.ingress.className")
            .Should().Be(HarborService.UnusedIngressClass);
    }

    /// <summary>
    /// The paths are Harbor's, not a guess: everything the chart's own route template sends to core
    /// has to be here, or those requests reach the portal and the docker client gets HTML.
    /// </summary>
    [Fact]
    public void The_catalog_splits_the_hostname_the_way_the_chart_does()
    {
        Harbor.RouteBackends.Should().HaveCount(2);

        RouteBackend core = Harbor.RouteBackends.First(b => b.ServiceNameTemplate.EndsWith("-core"));
        core.PathPrefixes.Should().BeEquivalentTo(["/api/", "/service/", "/v2/", "/c/"]);
        core.Port.Should().Be(80);

        RouteBackend portal = Harbor.RouteBackends.First(b => b.ServiceNameTemplate.EndsWith("-portal"));
        portal.PathPrefixes.Should().BeEquivalentTo(["/"]);
    }

    /// <summary>
    /// A Service name in the catalog is a template, never a literal — see
    /// <see cref="ExternalRouteService.ChartFullname"/> for why a literal is wrong on every cluster
    /// whose release is not named after the chart.
    /// </summary>
    [Fact]
    public void The_split_names_services_by_template_not_by_literal()
    {
        Harbor.RouteBackends.Should().OnlyContain(b => b.ServiceNameTemplate.StartsWith("{fullname}"));
    }

    [Theory]
    // A release named after the chart collapses: "harbor", not "harbor-harbor".
    [InlineData("harbor", "harbor")]
    [InlineData("harbor-prod", "harbor-prod")]
    // Anything else keeps the chart name as a suffix, exactly as the Helm scaffold does.
    [InlineData("registry", "registry-harbor")]
    public void The_chart_fullname_follows_the_release_name(string releaseName, string expected)
    {
        ExternalRouteService.ChartFullname(releaseName, "harbor").Should().Be(expected);
    }

    [Fact]
    public void The_route_sends_the_api_and_registry_paths_to_core_and_the_rest_to_the_portal()
    {
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(HarborRoute());

        yaml.Should().Contain("name: harbor-core");
        yaml.Should().Contain("name: harbor-portal");

        foreach (string path in new[] { "/api/", "/service/", "/v2/", "/c/" })
        {
            yaml.Should().Contain($"value: {path}");
        }

        // Two rules, not one: the whole point is that the hostname is no longer a single backend.
        yaml.Split("    - matches:").Should().HaveCount(3);
    }

    /// <summary>
    /// The failure this prevents is silent: a route to the nginx Service keeps resolving right up
    /// until the upgrade that removes nginx, and then the hostname answers nothing.
    /// </summary>
    [Fact]
    public void The_route_never_points_at_the_nginx_service_the_chart_no_longer_installs()
    {
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(HarborRoute());

        yaml.Should().NotContain("name: harbor\n");
        yaml.Should().NotContain("- name: harbor\n");
    }

    /// <summary>
    /// Backend Service names come from the release, so a Harbor installed under another name routes
    /// to that release's Services rather than to a cluster-wide guess.
    /// </summary>
    [Fact]
    public void A_release_not_named_after_the_chart_routes_to_its_own_services()
    {
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(HarborRoute(releaseName: "registry"));

        yaml.Should().Contain("name: registry-harbor-core");
        yaml.Should().Contain("name: registry-harbor-portal");
    }

    /// <summary>
    /// Timeouts, HSTS and the retry policy are the platform's stance on a hostname, not a property
    /// of one backend — splitting the hostname across two Services must not drop them from either.
    /// </summary>
    [Fact]
    public void Every_rule_carries_the_same_policy_a_single_backend_route_would()
    {
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(HarborRoute());

        yaml.Split($"request: {ExternalRouteService.RegistryRequestTimeoutSeconds}s")
            .Should().HaveCount(3);
        yaml.Split("Strict-Transport-Security").Should().HaveCount(3);
        yaml.Split("retry:").Should().HaveCount(3);
    }

    /// <summary>
    /// An operator who set an explicit path asked for one path to reach one place. The chart's own
    /// layout is not an answer to that, so the split stands aside.
    /// </summary>
    [Fact]
    public void An_explicit_path_prefix_keeps_the_single_backend_it_asked_for()
    {
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(
            HarborRoute(pathPrefix: "/registry"));

        yaml.Should().Contain("value: /registry");
        yaml.Should().NotContain("harbor-portal");
    }

    /// <summary>
    /// Components that do not declare a split are untouched by any of this — which is all of them
    /// but Harbor.
    /// </summary>
    [Fact]
    public void A_component_with_no_split_still_renders_one_backend()
    {
        ExternalRoute route = HarborRoute();
        route.Component!.Name = "kube-prometheus-stack";
        route.Component.HelmChartName = "kube-prometheus-stack";
        route.Component.ReleaseName = "kube-prometheus-stack";
        route.ServiceName = "kube-prometheus-stack-grafana";

        string yaml = ExternalRouteService.GenerateHttpRouteYaml(route);

        yaml.Should().Contain("name: kube-prometheus-stack-grafana");
        yaml.Split("backendRefs:").Should().HaveCount(2);
    }

    /// <summary>
    /// The route record names one Service even though the HTTPRoute names two — the core, because
    /// that is the half a question about "what is behind this hostname" is usually about.
    /// </summary>
    [Fact]
    public void The_route_record_carries_the_core_service()
    {
        ExternalRouteService.PrimaryBackendService("harbor", "harbor", "registry")
            .Should().Be("registry-harbor-core");
    }

    /// <summary>A component with no split has no primary backend to name — its route keeps its own.</summary>
    [Fact]
    public void A_component_with_no_split_has_no_primary_backend()
    {
        ExternalRouteService.PrimaryBackendService(
            "kube-prometheus-stack", "kube-prometheus-stack", "kube-prometheus-stack")
            .Should().BeNull();
    }

    /// <summary>
    /// A DestinationRule exists per Service. Harbor's hostname reaches two, and a list that named
    /// only the route's own backend would leave the portal without one.
    /// </summary>
    [Fact]
    public void Both_halves_of_the_hostname_are_reported_as_backends()
    {
        ExternalRouteService.BackendServiceNames(HarborRoute())
            .Should().BeEquivalentTo(["harbor-core", "harbor-portal"]);
    }
}
