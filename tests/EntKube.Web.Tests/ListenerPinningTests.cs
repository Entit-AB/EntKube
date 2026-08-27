using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.Serialization;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for which Gateway listener each generated route attaches to.
///
/// The bug these exist to prevent: a parentRef without a sectionName attaches to EVERY listener
/// whose hostname matches, and the port-80 redirect listener — having no hostname of its own —
/// matches all of them. The app's route and the redirect route then both sit on port 80, Gateway
/// API hands the request to the more specific hostname (the app's), and the site answers in
/// cleartext over HTTP while looking, from the Gateway resource alone, exactly like a cluster that
/// redirects to HTTPS. Nothing about it fails loudly; it just quietly serves plaintext.
///
/// So the property under test is not "the YAML contains a sectionName" but "every route names the
/// listeners it belongs on, and those listeners exist on the Gateway".
/// </summary>
public class ListenerPinningTests
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    private static Dictionary<object, object> Parse(string yaml) =>
        (Dictionary<object, object>)Yaml.Deserialize<object>(yaml)!;

    private static List<string> SectionNames(string routeYaml) =>
        ((List<object>)Parse(routeYaml)
            .GetValueOrDefault("spec")!
            .As<Dictionary<object, object>>()["parentRefs"])
        .Cast<Dictionary<object, object>>()
        .Select(p => (string?)p.GetValueOrDefault("sectionName") ?? "<none>")
        .ToList();

    private static ExternalRoute External(string hostname = "shop.example.com") => new()
    {
        Id = Guid.NewGuid(),
        Hostname = hostname,
        ServiceName = "storefront",
        ServicePort = 80,
        PathPrefix = "/",
        TlsMode = TlsMode.ClusterIssuer,
        ClusterIssuerName = "letsencrypt-prod",
        GatewayName = "istio-ingress-external",
        GatewayNamespace = "istio-system",
        Component = new ClusterComponent { Name = "storefront", Namespace = "shop", ComponentType = "HelmChart" }
    };

    private static AppRoute App(
        string hostname = "shop.example.com",
        bool requireClientCert = false,
        bool clientCertOnly = false,
        ClientCaBundle? bundle = null) => new()
    {
        Id = Guid.NewGuid(),
        Hostname = hostname,
        TlsMode = TlsMode.ClusterIssuer,
        ClusterIssuerName = "letsencrypt-prod",
        RequireClientCertificate = requireClientCert,
        ClientCertificateOnly = clientCertOnly,
        ClientCaBundle = bundle
    };

    private static AppDeploymentRoute Deployment(AppRoute route, string pathPrefix = "/", string? rewrite = null) => new()
    {
        Id = Guid.NewGuid(),
        AppRoute = route,
        ServiceName = "storefront",
        ServicePort = 80,
        PathPrefix = pathPrefix,
        RewritePath = rewrite,
        IsEnabled = true,
        GatewayName = "istio-ingress-external",
        GatewayNamespace = "istio-system",
        AppDeployment = new AppDeployment { Namespace = "shop", Name = "storefront" }
    };

    // ── The cleartext bug ──

    [Fact]
    public void An_external_route_attaches_only_to_its_own_https_listener()
    {
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(External());

        SectionNames(yaml).Should().Equal("shop-example-com");
    }

    [Fact]
    public void An_app_route_attaches_only_to_its_own_https_listener()
    {
        AppRoute route = App();
        string yaml = AppRouteService.GenerateHttpRouteYaml(route, [Deployment(route)]);

        SectionNames(yaml).Should().Equal("shop-example-com");
    }

    [Fact]
    public void No_generated_route_lands_on_the_port_80_listener()
    {
        // Port 80 belongs to the redirect and to ACME solvers. An app route arriving there is
        // the whole cleartext failure, so assert its absence directly rather than by implication.
        AppRoute route = App();

        foreach (string yaml in new[]
        {
            ExternalRouteService.GenerateHttpRouteYaml(External()),
            AppRouteService.GenerateHttpRouteYaml(route, [Deployment(route)]),
        })
        {
            SectionNames(yaml).Should().NotContain(ExternalRouteService.HttpListenerName);
            SectionNames(yaml).Should().NotContain("<none>");
        }
    }

    [Fact]
    public void Every_section_name_a_route_claims_exists_on_the_gateway()
    {
        // The two generators have to agree on listener naming; a route pinned to a listener the
        // Gateway does not have attaches to nothing at all, which is a harder outage than the one
        // being fixed. This is the test that catches a rename in either of them.
        ExternalRoute external = External("api.example.com");
        AppRoute app = App("app.example.com");

        string gateway = ExternalRouteService.GenerateGatewayYaml(
            "istio-ingress-external", "istio-system", [external], [app]);

        List<string> listeners = gateway
            .Split("\n---\n")[0]
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("- name: "))
            .Select(l => l.Trim()["- name: ".Length..])
            .ToList();

        SectionNames(ExternalRouteService.GenerateHttpRouteYaml(external))
            .Should().BeSubsetOf(listeners);
        SectionNames(AppRouteService.GenerateHttpRouteYaml(app, [Deployment(app)]))
            .Should().BeSubsetOf(listeners);
    }

    // ── mTLS: the hostname can live on two listeners at once ──

    [Fact]
    public void A_route_requiring_a_client_certificate_attaches_to_both_listeners()
    {
        ClientCaBundle bundle = new() { Id = Guid.NewGuid(), Name = "partner-ca", ListenerPort = 8443 };
        AppRoute route = App(requireClientCert: true, bundle: bundle);

        SectionNames(AppRouteService.GenerateHttpRouteYaml(route, [Deployment(route)]))
            .Should().Equal("shop-example-com", "shop-example-com-mtls-8443");
    }

    [Fact]
    public void A_client_certificate_only_route_leaves_443_entirely()
    {
        ClientCaBundle bundle = new() { Id = Guid.NewGuid(), Name = "partner-ca", ListenerPort = 9443 };
        AppRoute route = App(requireClientCert: true, clientCertOnly: true, bundle: bundle);

        SectionNames(AppRouteService.GenerateHttpRouteYaml(route, [Deployment(route)]))
            .Should().Equal("shop-example-com-mtls-9443");
    }

    [Fact]
    public void An_unloaded_trust_anchor_fails_instead_of_guessing_a_port()
    {
        // Guessing the default port would pin the route to a listener the Gateway may not have,
        // and the hostname would go dark with a green checkmark next to it.
        AppRoute route = App(requireClientCert: true, bundle: null);

        Action generate = () => AppRouteService.GenerateHttpRouteYaml(route, [Deployment(route)]);

        generate.Should().Throw<InvalidOperationException>().WithMessage("*trust anchor*");
    }

    // ── HSTS ──

    [Fact]
    public void Generated_routes_carry_an_hsts_response_header()
    {
        AppRoute route = App();

        foreach (string yaml in new[]
        {
            ExternalRouteService.GenerateHttpRouteYaml(External()),
            AppRouteService.GenerateHttpRouteYaml(route, [Deployment(route)]),
        })
        {
            yaml.Should().Contain("Strict-Transport-Security");
            yaml.Should().Contain(ExternalRouteService.HstsHeaderValue);

            // Never preload: it is a one-way door and not ours to walk through.
            yaml.Should().NotContain("preload");
        }
    }

    [Fact]
    public void A_rewrite_and_hsts_share_one_filters_block()
    {
        // Two `filters:` keys in one rule is invalid YAML, and the rewrite is what makes the
        // path-prefixed routes work at all — so parse the result rather than string-matching it.
        AppRoute route = App();
        string yaml = AppRouteService.GenerateHttpRouteYaml(route, [Deployment(route, "/api", rewrite: "/")]);

        var rules = (List<object>)Parse(yaml)["spec"].As<Dictionary<object, object>>()["rules"];
        var filters = (List<object>)rules.Cast<Dictionary<object, object>>().Single()["filters"];

        filters.Cast<Dictionary<object, object>>().Select(f => (string)f["type"])
            .Should().Equal("URLRewrite", "ResponseHeaderModifier");
    }

    // ── Which namespaces may attach ──

    [Fact]
    public void Tls_listeners_admit_only_labelled_namespaces_while_port_80_stays_open()
    {
        // Port 80 must stay open to All: cert-manager creates ACME solver routes in its own
        // namespace, nothing labels that namespace, and a solver that cannot attach is a
        // certificate that cannot renew.
        string gateway = ExternalRouteService.GenerateGatewayYaml(
            "istio-ingress-external", "istio-system", [External("api.example.com")], [App("app.example.com")])
            .Split("\n---\n")[0];

        var listeners = ((List<object>)Parse(gateway)["spec"].As<Dictionary<object, object>>()["listeners"])
            .Cast<Dictionary<object, object>>()
            .ToDictionary(
                l => (string)l["name"],
                l => (string)((Dictionary<object, object>)((Dictionary<object, object>)l["allowedRoutes"])["namespaces"])["from"]);

        listeners[ExternalRouteService.HttpListenerName].Should().Be("All");
        listeners.Where(l => l.Key != ExternalRouteService.HttpListenerName)
            .Should().OnlyContain(l => l.Value == "Selector");
    }

    [Fact]
    public void The_namespace_selector_matches_the_label_entkube_stamps()
    {
        // The selector and the labelling step are two halves of one mechanism living in different
        // files; if they drift apart every route on the cluster detaches at once.
        string gateway = ExternalRouteService.GenerateGatewayYaml(
            "istio-ingress-external", "istio-system", [External()], null).Split("\n---\n")[0];

        gateway.Should().Contain(
            $"{ExternalRouteService.RouteNamespaceLabel}: {ExternalRouteService.RouteNamespaceLabelValue}");
    }

    // ── ACME solvers stay on port 80 ──

    [Fact]
    public void The_acme_http01_solver_is_pinned_to_the_port_80_listener()
    {
        // Unpinned, the solver route also attaches to the hostname's live HTTPS listener and
        // shadows /.well-known/acme-challenge/ on the running site for as long as it survives.
        ClusterComponent istio = new()
        {
            Name = "istio",
            ComponentType = "HelmChart",
            Namespace = "istio-system",
            ReleaseName = "istio-ingress-external"
        };

        string issuer = LetsEncryptSolverBuilder.Apply(
            "apiVersion: cert-manager.io/v1\nkind: ClusterIssuer\nmetadata:\n  name: letsencrypt-prod\nspec:\n  acme: {}\n",
            new Dictionary<string, string> { ["enable-http01"] = "true" },
            [istio]);

        issuer.Should().Contain($"sectionName: {ExternalRouteService.HttpListenerName}");
    }
}

file static class YamlCastExtensions
{
    public static Dictionary<object, object> As<T>(this object value) => (Dictionary<object, object>)value;
}
