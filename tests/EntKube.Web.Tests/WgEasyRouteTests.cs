using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for how the wg-easy web UI is exposed.
///
/// It used to be exposed by an HTTPRoute written directly into the component's manifest. A route
/// that arrives that way is invisible to the Gateway generator, so the hostname never got an HTTPS
/// listener or a certificate — and the route, having no listener of its own to attach to, bound to
/// the port-80 listener instead. The WireGuard admin UI, password field included, answered over
/// cleartext HTTP, and nothing in the Gateway or the route's own status said so.
///
/// The fix is not "add TLS to that route" but "stop hand-writing the route": registering the
/// hostname as an ExternalRoute is what earns it a listener, a Certificate and listener pinning.
/// </summary>
public class WgEasyRouteTests
{
    private static CatalogEntry WgEasy() =>
        ComponentCatalog.Entries.Single(e => e.Key == "wg-easy");

    [Fact]
    public void The_component_manifest_no_longer_ships_its_own_httproute()
    {
        string manifest = WgEasy().DefaultValues ?? "";

        manifest.Should().NotContain("kind: HTTPRoute");
        manifest.Should().NotContain("wg-easy-ui");
    }

    [Fact]
    public void The_manifest_still_ships_the_wireguard_udp_path()
    {
        // The UDP proxy is the component's actual job and is unrelated to the UI route — this is
        // here so a future tidy-up of the manifest cannot quietly take the VPN with it.
        string manifest = WgEasy().DefaultValues ?? "";

        manifest.Should().Contain("kind: EnvoyFilter");
        manifest.Should().Contain("wg_easy_udp_cluster");
        manifest.Should().Contain("51820");
    }

    [Fact]
    public void The_ui_port_is_the_one_the_chart_serves_on()
    {
        // 51821 is the web UI; 51820 is WireGuard itself. Routing the UI to 51820 would hand
        // HTTP requests to the VPN data plane.
        string manifest = WgEasy().DefaultValues ?? "";

        manifest.Should().Contain("containerPort: 51821");
    }

    [Fact]
    public void The_public_host_field_still_exists_for_the_route_to_read()
    {
        // SaveWgEasyConfigIfNeededAsync reads this field by key to build the ExternalRoute;
        // renaming it in the catalog would silently stop the UI being exposed at all.
        WgEasy().FormFields.Should().Contain(f => f.Key == "wg-host");
    }
}
