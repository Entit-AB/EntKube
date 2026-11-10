using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The VM console: a cookie-authenticated websocket bridged to KubeVirt's vnc/console subresource.
///
/// <para>The endpoint is the security boundary, so it is tested end to end on a real Kestrel host with
/// real tenant memberships: who gets bridged, and — as much the point — who is refused <b>before the
/// cluster is asked anything</b>.</para>
/// </summary>
public class VirtualMachineConsoleTests : IAsyncLifetime
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private const string Alice = "user-alice";     // member of the owning tenant
    private const string Mallory = "user-mallory"; // member of another tenant only

    private readonly InterceptingTestDb testDb = new(TestRootKey);
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private ApplicationDbContext db = null!;
    private WebApplication app = null!;
    private Uri baseUri = null!;

    private Guid tenantId;
    private Guid clusterId;

    /// <summary>The far end of the most recent upstream connection — what KubeVirt would be.</summary>
    private WebSocket? kubevirt;

    public async Task InitializeAsync()
    {
        db = testDb.CreateContext();
        tenantId = Guid.NewGuid();
        Guid otherTenant = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        clusterId = Guid.NewGuid();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "Owner", Slug = $"owner-{tenantId:N}" },
            new Tenant { Id = otherTenant, Name = "Other", Slug = $"other-{otherTenant:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId, Name = "prod", ApiServerUrl = "https://k8s:6443"
        });
        db.Users.AddRange(
            new ApplicationUser { Id = Alice, UserName = "alice@example.com", Email = "alice@example.com" },
            new ApplicationUser { Id = Mallory, UserName = "mallory@example.com", Email = "mallory@example.com" });
        Guid ownerRole = Guid.NewGuid(), otherRole = Guid.NewGuid();
        db.TenantRoles.AddRange(
            new TenantRole { Id = ownerRole, TenantId = tenantId, Name = "Member" },
            new TenantRole { Id = otherRole, TenantId = otherTenant, Name = "Member" });
        db.TenantMemberships.AddRange(
            new TenantMembership { UserId = Alice, TenantId = tenantId, RoleId = ownerRole },
            new TenantMembership { UserId = Mallory, TenantId = otherTenant, RoleId = otherRole });
        await db.SaveChangesAsync();

        VaultService vault = testDb.CreateVaultService();
        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        k8s.Setup(f => f.ConnectSubresourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                (WebSocket ours, WebSocket theirs) = await PairAsync();
                kubevirt = theirs;
                return ours;
            });

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(HeaderAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.Scheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new UserAccessService(testDb.Factory));
        builder.Services.AddSingleton(Service());

        app = builder.Build();
        app.UseWebSockets();
        app.MapVirtualMachineConsole();
        await app.StartAsync();
        baseUri = new Uri(app.Urls.First());
    }

    public async Task DisposeAsync()
    {
        kubevirt?.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await db.DisposeAsync();
        testDb.Dispose();
    }

    private VirtualMachineService Service() => new(
        testDb.Factory,
        new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
        new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
        new EntKube.Web.Services.ClusterChanges.ClusterChangeGate(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance),
        new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
        NullLogger<VirtualMachineService>.Instance);

    /// <summary>Signs a request in as whoever the X-Test-User header names, as the cookie would.</summary>
    private sealed class HeaderAuth(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? user = Request.Headers["X-Test-User"];
            if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());

            ClaimsPrincipal principal = new(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user), new Claim(ClaimTypes.Name, user)], Scheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
        }
    }

    // ──────── Helpers ────────

    /// <summary>Two websockets joined over loopback TCP, as a browser and a server would be.</summary>
    private static async Task<(WebSocket Client, WebSocket Server)> PairAsync()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        TcpClient client = new();
        Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        TcpClient server = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        return (WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, TimeSpan.Zero),
                WebSocket.CreateFromStream(server.GetStream(), isServer: true, null, TimeSpan.Zero));
    }

    private async Task<(ClientWebSocket Socket, HttpStatusCode Status)> OpenAsync(
        string? user, string? origin = "same", string kind = "serial", Guid? tenant = null)
    {
        ClientWebSocket socket = new();
        socket.Options.CollectHttpResponseDetails = true;
        if (user is not null) socket.Options.SetRequestHeader("X-Test-User", user);
        if (origin is not null)
            socket.Options.SetRequestHeader("Origin", origin == "same" ? $"http://{baseUri.Authority}" : origin);

        string path = VmConsole.PathFor(tenant ?? tenantId, clusterId, "vms", "web-01",
            kind == "vnc" ? VirtualMachineConsoleKind.Vnc : VirtualMachineConsoleKind.Serial);
        if (kind is not ("vnc" or "serial")) path = path[..path.LastIndexOf('/')] + "/" + kind;

        Uri uri = new($"ws://{baseUri.Authority}{path}");
        try
        {
            await socket.ConnectAsync(uri, CancellationToken.None);
        }
        catch (WebSocketException) { }

        return (socket, socket.HttpStatusCode);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        byte[] buffer = new byte[1024];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult r = await socket.ReceiveAsync(buffer, timeout.Token);
        return Encoding.UTF8.GetString(buffer, 0, r.Count);
    }

    private void UpstreamNeverOpened() =>
        k8s.Verify(f => f.ConnectSubresourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

    // ──────── The endpoint ────────

    [Fact]
    public async Task A_member_is_bridged_to_the_vms_serial_console_both_ways()
    {
        (ClientWebSocket browser, HttpStatusCode status) = await OpenAsync(Alice);
        using (browser)
        {
            status.Should().Be(HttpStatusCode.SwitchingProtocols);

            await browser.SendAsync(Encoding.UTF8.GetBytes("ls\r"), WebSocketMessageType.Text, true, CancellationToken.None);
            (await ReceiveTextAsync(kubevirt!)).Should().Be("ls\r");

            await kubevirt!.SendAsync(Encoding.UTF8.GetBytes("file.txt\r\n"), WebSocketMessageType.Binary, true, CancellationToken.None);
            (await ReceiveTextAsync(browser)).Should().Be("file.txt\r\n");
        }

        k8s.Verify(f => f.ConnectSubresourceAsync("subresources.kubevirt.io/v1", "virtualmachineinstances",
            "web-01", "vms", "console", "plain.kubevirt.io", It.IsAny<string>(), It.IsAny<CancellationToken>()));
        testDb.CreateContext().AuditEvents.Should().ContainSingle(e =>
            e.Action == "vm.console" && e.ResourceName == "vms/web-01" && e.PerformedBy == Alice);
    }

    [Fact]
    public async Task Vnc_reaches_the_vnc_subresource()
    {
        (ClientWebSocket browser, HttpStatusCode status) = await OpenAsync(Alice, kind: "vnc");
        browser.Dispose();

        status.Should().Be(HttpStatusCode.SwitchingProtocols);
        k8s.Verify(f => f.ConnectSubresourceAsync(It.IsAny<string>(), "virtualmachineinstances", "web-01", "vms",
            "vnc", "plain.kubevirt.io", It.IsAny<string>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_member_of_another_tenant_is_refused_before_the_cluster_is_asked()
    {
        (ClientWebSocket browser, HttpStatusCode status) = await OpenAsync(Mallory);
        browser.Dispose();

        status.Should().Be(HttpStatusCode.NotFound, "whether the tenant exists is not confirmed to a stranger");
        UpstreamNeverOpened();
    }

    [Fact]
    public async Task Naming_your_own_tenant_does_not_reach_another_tenants_cluster()
    {
        // Mallory names a tenant she does belong to, with the owner's cluster id. Membership passes;
        // the cluster seam must still refuse, because that cluster is not her tenant's.
        Guid malloryTenant = testDb.CreateContext().TenantMemberships.Single(m => m.UserId == Mallory).TenantId;

        (ClientWebSocket browser, HttpStatusCode status) = await OpenAsync(Mallory, tenant: malloryTenant);
        browser.Dispose();

        status.Should().Be(HttpStatusCode.BadGateway);
        UpstreamNeverOpened();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData(null)]
    public async Task A_page_on_another_site_cannot_open_a_console_as_the_visitor(string? origin)
    {
        (ClientWebSocket browser, HttpStatusCode status) = await OpenAsync(Alice, origin: origin);
        browser.Dispose();

        status.Should().Be(HttpStatusCode.Forbidden);
        UpstreamNeverOpened();
    }

    [Fact]
    public async Task Nobody_signed_in_gets_nothing()
    {
        (ClientWebSocket browser, HttpStatusCode status) = await OpenAsync(user: null);
        browser.Dispose();

        status.Should().Be(HttpStatusCode.Unauthorized);
        UpstreamNeverOpened();
    }

    [Fact]
    public async Task An_unknown_console_kind_is_refused()
    {
        (ClientWebSocket browser, HttpStatusCode status) = await OpenAsync(Alice, kind: "exec");
        browser.Dispose();

        status.Should().Be(HttpStatusCode.Forbidden);
        UpstreamNeverOpened();
    }

    // ──────── The pieces ────────

    [Theory]
    [InlineData("https://entkube.example", "https", "entkube.example", true)]
    [InlineData("https://entkube.example", "https", "entkube.example:443", true)]
    [InlineData("http://localhost:5000", "http", "localhost:5000", true)]
    [InlineData("https://ENTKUBE.example", "https", "entkube.example", true)]
    [InlineData("https://entkube.example.evil.com", "https", "entkube.example", false)]
    [InlineData("https://entkube.example:8443", "https", "entkube.example", false)]
    [InlineData("null", "https", "entkube.example", false)]
    [InlineData("", "https", "entkube.example", false)]
    public void Same_origin_means_same_host_and_port(string origin, string scheme, string host, bool expected)
    {
        ConsoleRequest.IsSameOrigin(origin, scheme, new HostString(host)).Should().Be(expected);
    }

    [Fact]
    public void The_page_builds_exactly_the_path_the_endpoint_routes()
    {
        RoutePattern pattern = RoutePatternFactory.Parse(
            VirtualMachineConsoleEndpoint.RoutePrefix + "/{tenantId:guid}/{clusterId:guid}/{ns}/{name}/{kind}");
        TemplateMatcher matcher = new(TemplateParser.Parse(pattern.RawText!), new RouteValueDictionary());
        RouteValueDictionary values = [];

        matcher.TryMatch(VmConsole.PathFor(tenantId, clusterId, "vms", "web-01", VirtualMachineConsoleKind.Vnc), values)
            .Should().BeTrue();
        values["tenantId"].Should().Be(tenantId.ToString());
        values["ns"].Should().Be("vms");
        values["name"].Should().Be("web-01");
        values["kind"].Should().Be("vnc");
    }

    [Fact]
    public async Task The_bridge_closes_the_browser_when_the_vm_side_ends()
    {
        (WebSocket browserSide, WebSocket browser) = await PairAsync();
        (WebSocket upstream, WebSocket vm) = await PairAsync();

        Task pump = ConsoleBridge.PumpAsync(browserSide, upstream, CancellationToken.None);

        await vm.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "guest gone", CancellationToken.None);

        byte[] buffer = new byte[16];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult result = await browser.ReceiveAsync(buffer, timeout.Token);

        result.MessageType.Should().Be(WebSocketMessageType.Close);
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_websocket_uri_keeps_the_api_servers_host_port_and_path_prefix()
    {
        KubernetesClientFactory.ToWebSocketUri(new Uri("https://k8s.example:6443"), "/apis/x/v1/namespaces/a/b/c/vnc")
            .ToString().Should().Be("wss://k8s.example:6443/apis/x/v1/namespaces/a/b/c/vnc");
        KubernetesClientFactory.ToWebSocketUri(new Uri("https://rancher.example/k8s/clusters/c-1/"), "/apis/x/vnc")
            .ToString().Should().Be("wss://rancher.example/k8s/clusters/c-1/apis/x/vnc");
        KubernetesClientFactory.ToWebSocketUri(new Uri("http://127.0.0.1:8001"), "apis/x")
            .ToString().Should().Be("ws://127.0.0.1:8001/apis/x");
    }
}
