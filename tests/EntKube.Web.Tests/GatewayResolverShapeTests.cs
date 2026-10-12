using EntKube.Contracts.Catalog;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Which ingress controller the route generators think is installed, and whether Catalog's entity
/// and Catalog's contract answer that the same way.
///
/// <para><b>These had no tests at all.</b> Five public resolvers, twenty-odd callers across five
/// modules, and the thing they decide is a <c>GatewayClass</c> — which, handed wrong, makes the
/// controller <em>silently ignore</em> the Gateway resource. Nothing fails, nothing logs; routes
/// simply never serve. That is the worst shape of untested code to change the signature of, which
/// is why these exist now rather than later.</para>
///
/// <para><b>The property the decoupling rests on.</b> The resolvers used to take
/// <c>IEnumerable&lt;ClusterComponent&gt;</c>; they now take <see cref="IComponentNaming"/>, which
/// both the entity and <see cref="InstalledComponent"/> satisfy. Every case below is asserted
/// through <em>both</em> shapes, because "the contract gives the same answer" is the whole claim —
/// and it is a claim about four fields being carried faithfully, which a DTO can quietly get
/// wrong.</para>
/// </summary>
public class GatewayResolverShapeTests
{
    private static ClusterComponent Entity(
        string name, string? chart = null, string? ns = null, string? release = null) => new()
        {
            Id = Guid.NewGuid(), ClusterId = Guid.NewGuid(), Name = name,
            ComponentType = "Helm", Status = EntKube.Web.Data.ComponentStatus.Installed,
            HelmChartName = chart, Namespace = ns, ReleaseName = release,
        };

    private static InstalledComponent Dto(
        string name, string? chart = null, string? ns = null, string? release = null) => new(
            Id: Guid.NewGuid(), TenantId: Guid.NewGuid(), ClusterId: Guid.NewGuid(),
            ClusterName: "c", EnvironmentId: Guid.NewGuid(), Name: name,
            ComponentType: "Helm", Status: EntKube.Contracts.Catalog.ComponentStatus.Installed,
            Namespace: ns, ReleaseName: release, HelmChartName: chart, HelmChartVersion: null,
            HelmValues: null, Configuration: null, LastError: null, InstalledAt: null);

    /// <summary>
    /// Runs one case through both shapes and returns the two answers, so every assertion below is
    /// automatically an agreement assertion as well.
    /// </summary>
    private static (T FromEntity, T FromContract) Both<T>(
        Func<IEnumerable<IComponentNaming>, T> resolve,
        params (string Name, string? Chart, string? Ns, string? Release)[] components) =>
        (resolve([.. components.Select(c => Entity(c.Name, c.Chart, c.Ns, c.Release))]),
         resolve([.. components.Select(c => Dto(c.Name, c.Chart, c.Ns, c.Release))]));

    private static void Agree<T>((T FromEntity, T FromContract) answers)
        => answers.FromContract.Should().Be(answers.FromEntity,
            "the contract carries the same four fields the entity does; if these diverge, a caller "
            + "that moved onto ICatalogApi silently starts generating routes for a different "
            + "gateway than its neighbours");

    // ════════════════════════════════════════════════════════════════
    //  GatewayClass
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Traefik_by_name_is_traefik()
    {
        var a = Both(ExternalRouteService.ResolveGatewayClass, ("traefik", null, null, null));
        Agree(a);
        a.FromEntity.Should().Be("traefik");
    }

    [Fact]
    public void Traefik_by_chart_name_is_traefik()
    {
        // The component is often named for the release, so the chart is the other way it is known.
        var a = Both(ExternalRouteService.ResolveGatewayClass, ("ingress", "traefik", null, null));
        Agree(a);
        a.FromEntity.Should().Be("traefik");
    }

    /// <summary>
    /// Istio is the default, and that is load-bearing rather than tidy: a cluster with no
    /// recognised controller still gets "istio", so the mesh paths run. Returning something else
    /// for an unknown cluster would disable L4 routes and mTLS on it.
    /// </summary>
    [Fact]
    public void Anything_else_is_istio()
    {
        var a = Both(ExternalRouteService.ResolveGatewayClass, ("cert-manager", "cert-manager", null, null));
        Agree(a);
        a.FromEntity.Should().Be("istio");
    }

    [Fact]
    public void An_empty_cluster_is_istio()
    {
        var a = Both(ExternalRouteService.ResolveGatewayClass);
        Agree(a);
        a.FromEntity.Should().Be("istio");
    }

    [Fact]
    public void Traefik_wins_over_istio_when_both_are_installed()
    {
        var a = Both(ExternalRouteService.ResolveGatewayClass,
            ("istio", null, "istio-system", null), ("traefik", null, "traefik", null));
        Agree(a);
        a.FromEntity.Should().Be("traefik", "the class check asks only whether Traefik is there");
    }

    [Theory]
    [InlineData("Traefik")]
    [InlineData("TRAEFIK")]
    public void The_name_match_ignores_case(string name)
    {
        var a = Both(ExternalRouteService.ResolveGatewayClass, (name, null, null, null));
        Agree(a);
        a.FromEntity.Should().Be("traefik");
    }

    // ════════════════════════════════════════════════════════════════
    //  Gateway name and namespace
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Traefik_resolves_to_its_own_gateway_and_namespace()
    {
        var a = Both(ExternalRouteService.ResolveGateway, ("traefik", null, null, null));
        Agree(a);
        a.FromEntity.Should().Be(("traefik-gateway", "traefik"));
    }

    /// <summary>
    /// Istio's gateway is named after the <em>release</em>, not the component — the chart installs
    /// one Gateway per release and two installs on a cluster are two gateways.
    /// </summary>
    [Fact]
    public void Istio_resolves_to_its_release_name_and_namespace()
    {
        var a = Both(ExternalRouteService.ResolveGateway,
            ("istio", null, "istio-system", "my-gateway"));
        Agree(a);
        a.FromEntity.Should().Be(("my-gateway", "istio-system"));
    }

    [Fact]
    public void An_istio_component_with_no_release_falls_back_to_its_name()
    {
        var a = Both(ExternalRouteService.ResolveGateway, ("istio", null, "istio-system", null));
        Agree(a);
        a.FromEntity.Should().Be(("istio", "istio-system"));
    }

    /// <summary>
    /// No recognised controller gives <c>default-gateway</c> / <c>default</c>, which is the pair a
    /// fixture that forgets to seed components will see — the reason a test asserting
    /// <c>istio-system</c> against an unseeded cluster fails, and the right answer is to seed it.
    /// </summary>
    [Fact]
    public void An_unrecognised_cluster_resolves_to_the_default_pair()
    {
        var a = Both(ExternalRouteService.ResolveGateway);
        Agree(a);
        a.FromEntity.Should().Be(("default-gateway", "default"));
    }

    /// <summary>
    /// The L4 gateway is a fixed name in whatever namespace the HTTP gateway resolved to, so a
    /// Traefik cluster still gets the L4 Gateway in <c>traefik</c> — even though the callers refuse
    /// L4 routes there. The namespace must track, or an Istio cluster's TCP listeners would be
    /// generated into the wrong namespace.
    /// </summary>
    [Fact]
    public void The_l4_gateway_keeps_its_fixed_name_in_the_resolved_namespace()
    {
        var a = Both(ExternalRouteService.ResolveL4Gateway, ("istio", null, "istio-system", "gw"));
        Agree(a);
        a.FromEntity.Should().Be((ExternalRouteService.L4GatewayName, "istio-system"));
    }

    // ════════════════════════════════════════════════════════════════
    //  IngressClass (cert-manager HTTP-01)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void The_ingress_class_follows_the_controller()
    {
        var traefik = Both(ExternalRouteService.ResolveIngressClass, ("traefik", null, null, null));
        Agree(traefik);
        traefik.FromEntity.Should().Be("traefik");

        var istio = Both(ExternalRouteService.ResolveIngressClass, ("istio", null, "istio-system", null));
        Agree(istio);
        istio.FromEntity.Should().Be("istio");
    }
}
