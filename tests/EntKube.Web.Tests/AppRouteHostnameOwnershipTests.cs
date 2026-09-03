using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the rule that decides which ExternalRoutes get an HTTPRoute applied.
///
/// The story these tests protect: a hostname can be described twice — once as an AppRoute, which
/// renders one rule per path (the rewrites putting /gateway in front of one service and /engine in
/// front of another), and once as an ExternalRoute, which can only say "this whole host goes to
/// this one service". Both generators name the object ToListenerName(hostname) + "-route", so the
/// two descriptions are really one object, and the one applied last is the one that survives.
///
/// ApplyExternalRoutesAsync re-applies every ExternalRoute on the cluster, so before this rule
/// existed, installing any unrelated component flattened such a hostname down to a single
/// catch-all backend — and every path that backend did not serve began answering 404, with nothing
/// in the deploy that touched the app to explain it. That is a real outage
/// (flow.sto2.entit.eu, 2026-08-27), not a hypothetical.
/// </summary>
public class AppRouteHostnameOwnershipTests
{
    // ── Helpers ──

    /// <summary>An AppRoute claiming a hostname. Its rules do not matter here, only the claim.</summary>
    private static AppRoute AppRouteFor(string hostname) =>
        new() { Id = Guid.NewGuid(), Hostname = hostname };

    /// <summary>An ExternalRoute for a hostname, terminating TLS unless told otherwise.</summary>
    private static ExternalRoute ExternalRouteFor(
        string hostname, TlsMode tlsMode = TlsMode.ClusterIssuer) =>
        new()
        {
            Id = Guid.NewGuid(),
            Hostname = hostname,
            ServiceName = "some-backend",
            ServicePort = 443,
            TlsMode = tlsMode,
        };

    // ── The rule ──

    /// <summary>
    /// The case that took prod down. An AppRoute and an ExternalRoute both name the same
    /// hostname, so applying the ExternalRoute's single-rule HTTPRoute would overwrite the
    /// AppRoute's per-path rules. We refuse to emit it.
    /// </summary>
    [Fact]
    public void ExternalRoute_sharing_a_hostname_with_an_AppRoute_is_not_applied()
    {
        IReadOnlySet<string> owned = ComponentLifecycleService.HostnamesOwnedByAppRoutes(
            [AppRouteFor("flow.sto2.entit.eu")]);

        ComponentLifecycleService.WouldOverwriteAppRoute(
            ExternalRouteFor("flow.sto2.entit.eu"), owned)
            .Should().BeTrue();
    }

    /// <summary>
    /// The ordinary case must keep working: a hostname no AppRoute claims is still applied
    /// exactly as before. This rule narrows nothing else.
    /// </summary>
    [Fact]
    public void ExternalRoute_on_its_own_hostname_is_still_applied()
    {
        IReadOnlySet<string> owned = ComponentLifecycleService.HostnamesOwnedByAppRoutes(
            [AppRouteFor("flow.sto2.entit.eu")]);

        ComponentLifecycleService.WouldOverwriteAppRoute(
            ExternalRouteFor("registry.sto2.entit.eu"), owned)
            .Should().BeFalse();
    }

    /// <summary>
    /// With no AppRoutes on the cluster at all, every ExternalRoute is applied — the behaviour
    /// every cluster had before this rule existed.
    /// </summary>
    [Fact]
    public void No_AppRoutes_means_nothing_is_skipped()
    {
        IReadOnlySet<string> owned = ComponentLifecycleService.HostnamesOwnedByAppRoutes([]);

        ComponentLifecycleService.WouldOverwriteAppRoute(
            ExternalRouteFor("flow.sto2.entit.eu"), owned)
            .Should().BeFalse();
    }

    /// <summary>
    /// DNS does not care about case, and neither does the generated object name, so an operator
    /// who capitalised the hostname in one form and not the other has still described one
    /// hostname twice.
    /// </summary>
    [Fact]
    public void Hostname_ownership_ignores_case()
    {
        IReadOnlySet<string> owned = ComponentLifecycleService.HostnamesOwnedByAppRoutes(
            [AppRouteFor("Flow.Sto2.Entit.EU")]);

        ComponentLifecycleService.WouldOverwriteAppRoute(
            ExternalRouteFor("flow.sto2.entit.eu"), owned)
            .Should().BeTrue();
    }

    /// <summary>
    /// A passthrough ExternalRoute renders a TLSRoute, not an HTTPRoute. It shares the object
    /// name but not the kind, so it cannot overwrite the AppRoute's route and stays exempt —
    /// skipping it would break SNI routing for no benefit.
    /// </summary>
    [Fact]
    public void Passthrough_routes_are_exempt_because_they_render_a_different_kind()
    {
        IReadOnlySet<string> owned = ComponentLifecycleService.HostnamesOwnedByAppRoutes(
            [AppRouteFor("flow.sto2.entit.eu")]);

        ComponentLifecycleService.WouldOverwriteAppRoute(
            ExternalRouteFor("flow.sto2.entit.eu", TlsMode.Passthrough), owned)
            .Should().BeFalse();
    }

    // ── Why the collision exists at all ──

    /// <summary>
    /// The collision this rule defends against, shown directly: both generators produce an object
    /// with the same name for the same hostname, one carrying the app's per-path rules and one
    /// carrying a single catch-all backend. If this ever stops being true the rule can be
    /// reconsidered — until then, one of them has to yield.
    /// </summary>
    [Fact]
    public void Both_generators_name_the_same_object_for_a_hostname()
    {
        const string hostname = "flow.sto2.entit.eu";

        AppRoute appRoute = new()
        {
            Id = Guid.NewGuid(),
            Hostname = hostname,
        };

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            Name = "flow",
            Namespace = "flow",
            ClusterId = Guid.NewGuid(),
        };

        // Two paths going to two different services — precisely what a single-rule route destroys.
        List<AppDeploymentRoute> deploymentRoutes = [
            new()
            {
                Id = Guid.NewGuid(),
                AppRouteId = appRoute.Id,
                AppDeploymentId = deployment.Id,
                AppDeployment = deployment,
                PathPrefix = "/gateway",
                RewritePath = "/",
                ServiceName = "flow-definition-store",
                ServicePort = 443,
                GatewayName = "istio-ingress-external",
                GatewayNamespace = "istio-system",
            },
            new()
            {
                Id = Guid.NewGuid(),
                AppRouteId = appRoute.Id,
                AppDeploymentId = deployment.Id,
                AppDeployment = deployment,
                PathPrefix = "/",
                ServiceName = "flow-frontend-web",
                ServicePort = 443,
                GatewayName = "istio-ingress-external",
                GatewayNamespace = "istio-system",
            },
        ];

        string appRouteYaml = AppRouteService.GenerateHttpRouteYaml(appRoute, deploymentRoutes);
        string externalRouteYaml = ExternalRouteService.GenerateHttpRouteYaml(
            ExternalRouteFor(hostname));

        const string sharedName = "name: flow-sto2-entit-eu-route";
        appRouteYaml.Should().Contain(sharedName);
        externalRouteYaml.Should().Contain(sharedName);

        // The AppRoute's version routes each path to its own service...
        appRouteYaml.Should().Contain("flow-definition-store");
        appRouteYaml.Should().Contain("flow-frontend-web");

        // ...while the ExternalRoute's version knows only one backend, which is why letting it
        // win costs the frontend its routing.
        externalRouteYaml.Should().Contain("some-backend");
        externalRouteYaml.Should().NotContain("flow-frontend-web");
    }
}
