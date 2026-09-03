using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Agents;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the outbound-proxy support on OpenStack connections — the path that
/// lets EntKube reach a cloud whose API is restricted to an IP allowlist the
/// server is not on.
///
/// The proxy is never dialled here; what matters is that the URL is validated
/// before it is stored, that the proxy password comes out of the vault rather
/// than the connection row, and that the settings actually reach the HTTP handler.
/// </summary>
public class OpenStackProxyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly VaultService vaultService;
    private readonly OpenStackKeystoneClient keystone;
    private readonly StorageService sut;

    public OpenStackProxyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        VaultEncryptionService encryption = new(TestRootKey);
        vaultService = new VaultService(dbFactory, encryption);

        Mock<IHttpClientFactory> innerHttpFactory = new();
        innerHttpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        AgentRegistry agentRegistry = new(dbFactory, NullLogger<AgentRegistry>.Instance);
        OpenStackHttpFactory httpFactory = new(innerHttpFactory.Object, agentRegistry);
        Mock<IKubernetesClientFactory> k8sMock = new();
        ClusterEgressRelay egressRelay = new(k8sMock.Object, NullLogger<ClusterEgressRelay>.Instance);
        ClusterEgressTunnel egressTunnel = new(NullLogger<ClusterEgressTunnel>.Instance);
        keystone = new OpenStackKeystoneClient(httpFactory, vaultService, egressTunnel, dbFactory);

        OpenStackS3Service openStackS3 = new(vaultService, httpFactory, keystone);
        StorageLinkClientFactory storageClientFactory = new(vaultService, dbFactory, httpFactory, keystone);

        sut = new StorageService(
            dbFactory, vaultService, openStackS3, keystone, egressRelay, egressTunnel, agentRegistry,
            k8sMock.Object, storageClientFactory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Tenant CreateTenant()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant;
    }

    // ──────── URL validation ────────

    [Theory]
    [InlineData("socks5://10.0.0.5:1080")]
    [InlineData("socks4://proxy.internal:1080")]
    [InlineData("socks4a://proxy.internal:1080")]
    [InlineData("http://proxy.corp:3128")]
    [InlineData("https://proxy.corp:8443")]
    public void ParseProxyUri_accepts_supported_schemes(string url)
    {
        Action act = () => OpenStackHttpFactory.ParseProxyUri(url);

        act.Should().NotThrow();
    }

    [Fact]
    public void ParseProxyUri_fills_in_the_default_socks_port()
    {
        // Uri has no notion of a default port for socks5, so it would otherwise
        // leave Port at -1 and the handler would have nothing to dial.
        Uri result = OpenStackHttpFactory.ParseProxyUri("socks5://10.0.0.5");

        result.Port.Should().Be(1080);
    }

    [Fact]
    public void ParseProxyUri_preserves_an_explicit_port()
    {
        OpenStackHttpFactory.ParseProxyUri("socks5://10.0.0.5:9999").Port.Should().Be(9999);
    }

    [Theory]
    [InlineData("ftp://proxy:21")]
    [InlineData("ssh://proxy:22")]
    public void ParseProxyUri_rejects_unsupported_schemes(string url)
    {
        Action act = () => OpenStackHttpFactory.ParseProxyUri(url);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not supported*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.0.0.5:1080")]
    [InlineData("not a url")]
    public void ParseProxyUri_rejects_malformed_values(string url)
    {
        Action act = () => OpenStackHttpFactory.ParseProxyUri(url);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParseProxyUri_rejects_credentials_in_the_url()
    {
        // Credentials there would be persisted in plaintext on the connection row —
        // the username field plus the vault is the supported route.
        Action act = () => OpenStackHttpFactory.ParseProxyUri("socks5://user:secret@10.0.0.5:1080");

        act.Should().Throw<InvalidOperationException>().WithMessage("*vault*");
    }

    // ──────── Persistence ────────

    [Fact]
    public async Task CreateOpenStackConnection_stores_the_proxy_and_vaults_its_password()
    {
        Tenant tenant = CreateTenant();

        OpenStackConnection created = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "Cleura Prod", "https://identity.example.com:5000/v3",
            region: "Kna1", projectName: "proj", projectId: null,
            userDomainName: null, projectDomainName: null,
            username: "osuser", password: "ospass", s3Endpoint: null,
            proxyUrl: "socks5://10.0.0.5:1080",
            proxyUsername: "proxyuser", proxyPassword: "proxypass", routeViaClusterId: null, routeViaAgentId: null);

        OpenStackConnection stored = await db.OpenStackConnections.AsNoTracking()
            .FirstAsync(c => c.Id == created.Id);

        stored.ProxyUrl.Should().Be("socks5://10.0.0.5:1080");
        stored.ProxyUsername.Should().Be("proxyuser");

        // The proxy password must not be readable off the connection row.
        string? vaulted = await vaultService.GetOpenStackSecretValueAsync(
            tenant.Id, created.Id, OpenStackKeystoneClient.ProxyPasswordSecretName);

        vaulted.Should().Be("proxypass");
    }

    [Fact]
    public async Task CreateOpenStackConnection_rejects_a_malformed_proxy_url()
    {
        Tenant tenant = CreateTenant();

        Func<Task> act = async () => await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "Bad Proxy", "https://identity.example.com:5000/v3",
            region: null, projectName: null, projectId: null,
            userDomainName: null, projectDomainName: null,
            username: null, password: null, s3Endpoint: null,
            proxyUrl: "ftp://nope:21", proxyUsername: null, proxyPassword: null, routeViaClusterId: null, routeViaAgentId: null);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // Nothing half-written: validation runs before the row is added.
        (await db.OpenStackConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateOpenStackConnection_without_a_proxy_leaves_the_columns_null()
    {
        Tenant tenant = CreateTenant();

        OpenStackConnection created = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "Direct", "https://identity.example.com:5000/v3",
            region: null, projectName: null, projectId: null,
            userDomainName: null, projectDomainName: null,
            username: "osuser", password: "ospass", s3Endpoint: null,
            proxyUrl: null, proxyUsername: null, proxyPassword: null, routeViaClusterId: null, routeViaAgentId: null);

        OpenStackConnection stored = await db.OpenStackConnections.AsNoTracking()
            .FirstAsync(c => c.Id == created.Id);

        stored.ProxyUrl.Should().BeNull();
        stored.ProxyUsername.Should().BeNull();
    }

    // ──────── Resolution ────────

    [Fact]
    public async Task ResolveProxy_returns_null_for_a_direct_connection()
    {
        Tenant tenant = CreateTenant();

        OpenStackConnection created = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "Direct", "https://identity.example.com:5000/v3",
            null, null, null, null, null, "osuser", "ospass", null, null, null, null, null, null);

        OpenStackConnection stored = await db.OpenStackConnections.AsNoTracking()
            .FirstAsync(c => c.Id == created.Id);

        (await keystone.ResolveEgressAsync(stored)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveProxy_pulls_the_password_out_of_the_vault()
    {
        Tenant tenant = CreateTenant();

        OpenStackConnection created = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "Proxied", "https://identity.example.com:5000/v3",
            null, null, null, null, null, "osuser", "ospass", null,
            "socks5://10.0.0.5:1080", "proxyuser", "proxypass", null, null);

        OpenStackConnection stored = await db.OpenStackConnections.AsNoTracking()
            .FirstAsync(c => c.Id == created.Id);

        ResolvedEgress? egress = await keystone.ResolveEgressAsync(stored);
        OpenStackProxy? proxy = egress?.Proxy;

        proxy.Should().NotBeNull();
        proxy!.Url.Should().Be("socks5://10.0.0.5:1080");
        proxy.Username.Should().Be("proxyuser");
        proxy.Password.Should().Be("proxypass");
    }

    [Fact]
    public async Task ResolveProxy_skips_the_vault_lookup_for_an_unauthenticated_proxy()
    {
        Tenant tenant = CreateTenant();

        OpenStackConnection created = await sut.CreateOpenStackConnectionAsync(
            tenant.Id, "Tunnel", "https://identity.example.com:5000/v3",
            null, null, null, null, null, "osuser", "ospass", s3Endpoint: null,
            proxyUrl: "socks5://127.0.0.1:1080", proxyUsername: null, proxyPassword: null,
            routeViaClusterId: null, routeViaAgentId: null);

        OpenStackConnection stored = await db.OpenStackConnections.AsNoTracking()
            .FirstAsync(c => c.Id == created.Id);

        ResolvedEgress? egress = await keystone.ResolveEgressAsync(stored);
        OpenStackProxy? proxy = egress?.Proxy;

        proxy.Should().NotBeNull();
        proxy!.Username.Should().BeNull();
        proxy.Password.Should().BeNull();
    }

    // ──────── Handler wiring ────────

    [Fact]
    public void CreateClient_without_a_proxy_uses_the_default_client()
    {
        Mock<IHttpClientFactory> inner = new();
        inner.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        using OpenStackHttpFactory factory = new(inner.Object, null!);
        using HttpClient client = factory.CreateClient(null);

        client.Should().NotBeNull();
        inner.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void CreateClient_with_a_proxy_bypasses_the_default_client()
    {
        Mock<IHttpClientFactory> inner = new();

        using OpenStackHttpFactory factory = new(inner.Object, null!);
        using HttpClient client = factory.CreateClient(new ResolvedEgress(new OpenStackProxy("socks5://10.0.0.5:1080")));

        client.Should().NotBeNull();
        inner.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CreateAwsHttpClientFactory_is_null_without_a_proxy()
    {
        using OpenStackHttpFactory factory = new(new Mock<IHttpClientFactory>().Object, null!);

        factory.CreateAwsHttpClientFactory(null).Should().BeNull();
    }

    [Fact]
    public void CreateAwsHttpClientFactory_opts_out_of_sdk_client_caching()
    {
        // The SDK must not cache or dispose a client whose handler this factory owns.
        using OpenStackHttpFactory factory = new(new Mock<IHttpClientFactory>().Object, null!);

        Amazon.Runtime.HttpClientFactory? awsFactory =
            factory.CreateAwsHttpClientFactory(new ResolvedEgress(new OpenStackProxy("socks5://10.0.0.5:1080")));

        awsFactory.Should().NotBeNull();
        awsFactory!.UseSDKHttpClientCaching(new Amazon.S3.AmazonS3Config()).Should().BeFalse();
        awsFactory.DisposeHttpClientsAfterUse(new Amazon.S3.AmazonS3Config()).Should().BeFalse();
    }

    [Fact]
    public void Distinct_proxy_settings_get_distinct_sdk_config_identities()
    {
        // The SDK keys its own bookkeeping off this string, so two proxies that
        // differ only by credentials must not collapse onto one identity.
        using OpenStackHttpFactory factory = new(new Mock<IHttpClientFactory>().Object, null!);
        Amazon.S3.AmazonS3Config config = new();

        string a = factory.CreateAwsHttpClientFactory(
            new ResolvedEgress(new OpenStackProxy("socks5://10.0.0.5:1080", "user", "one")))!.GetConfigUniqueString(config);
        string b = factory.CreateAwsHttpClientFactory(
            new ResolvedEgress(new OpenStackProxy("socks5://10.0.0.5:1080", "user", "two")))!.GetConfigUniqueString(config);
        string sameAsA = factory.CreateAwsHttpClientFactory(
            new ResolvedEgress(new OpenStackProxy("socks5://10.0.0.5:1080", "user", "one")))!.GetConfigUniqueString(config);

        a.Should().NotBe(b);
        a.Should().Be(sameAsA);
    }
}
