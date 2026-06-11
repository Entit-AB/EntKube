using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for ExternalRouteService — manages exposing components externally
/// via Gateway API HTTPRoutes. Covers route creation, validation, duplicate
/// detection, YAML generation, and gateway resolution.
/// </summary>
public class ExternalRouteServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly ExternalRouteService sut;
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid componentId = Guid.NewGuid();

    public ExternalRouteServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        // Seed a cluster with traefik installed and a monitoring component.

        Guid tenantId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Tenant tenant = new() { Id = tenantId, Name = "RouteTenant", Slug = "route" };
        Data.Environment env = new() { Id = envId, TenantId = tenantId, Name = "production" };
        KubernetesCluster cluster = new()
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = envId,
            Name = "route-cluster",
            ApiServerUrl = "https://k8s.example.com",
            Kubeconfig = "apiVersion: v1\nkind: Config"
        };

        ClusterComponent traefik = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = "traefik",
            ComponentType = "HelmChart",
            Namespace = "traefik",
            Status = ComponentStatus.Installed
        };

        ClusterComponent monitoring = new()
        {
            Id = componentId,
            ClusterId = clusterId,
            Name = "kube-prometheus-stack",
            ComponentType = "HelmChart",
            Namespace = "monitoring",
            ReleaseName = "kube-prometheus-stack",
            Status = ComponentStatus.Installed
        };

        db.Set<Tenant>().Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        db.ClusterComponents.AddRange(traefik, monitoring);
        db.SaveChanges();

        sut = new ExternalRouteService(dbFactory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ──────── Route creation ────────

    [Fact]
    public async Task AddRoute_WithClusterIssuer_CreatesRoute()
    {
        // The simplest happy path — expose Grafana with Let's Encrypt.

        ExternalRouteRequest request = new()
        {
            Hostname = "grafana.example.com",
            ServiceName = "kube-prometheus-stack-grafana",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);

        route.Hostname.Should().Be("grafana.example.com");
        route.ServiceName.Should().Be("kube-prometheus-stack-grafana");
        route.ServicePort.Should().Be(80);
        route.TlsMode.Should().Be(TlsMode.ClusterIssuer);
        route.ClusterIssuerName.Should().Be("letsencrypt-prod");
        route.GatewayName.Should().Be("traefik-gateway");
        route.GatewayNamespace.Should().Be("traefik");
    }

    [Fact]
    public async Task AddRoute_WithManualTls_CreatesRoute()
    {
        // Manual TLS — operator provides their own certificate.

        ExternalRouteRequest request = new()
        {
            Hostname = "prometheus.example.com",
            ServicePort = 9090,
            TlsMode = TlsMode.Manual,
            TlsCertificate = "-----BEGIN CERTIFICATE-----\nMIIB...\n-----END CERTIFICATE-----",
            TlsPrivateKey = "-----BEGIN PRIVATE KEY-----\nMIIE...\n-----END PRIVATE KEY-----"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);

        route.TlsMode.Should().Be(TlsMode.Manual);
        route.TlsCertificate.Should().StartWith("-----BEGIN CERTIFICATE-----");
        route.TlsPrivateKey.Should().StartWith("-----BEGIN PRIVATE KEY-----");
    }

    [Fact]
    public async Task AddRoute_DefaultsServiceNameFromComponent()
    {
        // When no service name is specified, use the component's release name.

        ExternalRouteRequest request = new()
        {
            Hostname = "alerts.example.com",
            ServicePort = 9093,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);

        route.ServiceName.Should().Be("kube-prometheus-stack");
    }

    // ──────── Validation ────────

    [Fact]
    public async Task AddRoute_EmptyHostname_Throws()
    {
        ExternalRouteRequest request = new()
        {
            Hostname = "  ",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        Func<Task> act = () => sut.AddRouteAsync(componentId, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Hostname is required*");
    }

    [Fact]
    public async Task AddRoute_ClusterIssuer_MissingIssuerName_Throws()
    {
        ExternalRouteRequest request = new()
        {
            Hostname = "app.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = null
        };

        Func<Task> act = () => sut.AddRouteAsync(componentId, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ClusterIssuer name is required*");
    }

    [Fact]
    public async Task AddRoute_ManualTls_MissingCert_Throws()
    {
        ExternalRouteRequest request = new()
        {
            Hostname = "app.example.com",
            TlsMode = TlsMode.Manual,
            TlsCertificate = null
        };

        Func<Task> act = () => sut.AddRouteAsync(componentId, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*TLS certificate is required*");
    }

    [Fact]
    public async Task AddRoute_DuplicateHostname_Throws()
    {
        // Can't use the same hostname twice on the same cluster.

        ExternalRouteRequest request = new()
        {
            Hostname = "unique.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        await sut.AddRouteAsync(componentId, request);

        Func<Task> duplicate = () => sut.AddRouteAsync(componentId, request);

        await duplicate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in use*");
    }

    // ──────── Route retrieval and deletion ────────

    [Fact]
    public async Task GetRoutes_ReturnsComponentRoutes()
    {
        ExternalRouteRequest request1 = new()
        {
            Hostname = "a.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        ExternalRouteRequest request2 = new()
        {
            Hostname = "b.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        await sut.AddRouteAsync(componentId, request1);
        await sut.AddRouteAsync(componentId, request2);

        List<ExternalRoute> routes = await sut.GetRoutesAsync(componentId);

        routes.Should().HaveCount(2);
        routes.Select(r => r.Hostname).Should().BeEquivalentTo(["a.example.com", "b.example.com"]);
    }

    [Fact]
    public async Task DeleteRoute_RemovesRoute()
    {
        ExternalRouteRequest request = new()
        {
            Hostname = "deleteme.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);
        await sut.DeleteRouteAsync(route.Id);

        List<ExternalRoute> routes = await sut.GetRoutesAsync(componentId);
        routes.Should().BeEmpty();
    }

    // ──────── YAML generation ────────

    [Fact]
    public async Task GenerateHttpRouteYaml_ClusterIssuer_IncludesAnnotation()
    {
        ExternalRouteRequest request = new()
        {
            Hostname = "grafana.example.com",
            ServiceName = "kube-prometheus-stack-grafana",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);
        string yaml = await sut.GenerateHttpRouteYamlAsync(route.Id);

        yaml.Should().Contain("kind: HTTPRoute");
        yaml.Should().Contain("grafana.example.com");
        yaml.Should().Contain("cert-manager.io/cluster-issuer: \"letsencrypt-prod\"");
        yaml.Should().Contain("name: traefik-gateway");
        yaml.Should().Contain("kube-prometheus-stack-grafana");
    }

    [Fact]
    public async Task GenerateHttpRouteYaml_ManualTls_ReferencesSecret()
    {
        ExternalRouteRequest request = new()
        {
            Hostname = "manual.example.com",
            ServiceName = "my-service",
            ServicePort = 443,
            TlsMode = TlsMode.Manual,
            TlsCertificate = "-----BEGIN CERTIFICATE-----\ntest\n-----END CERTIFICATE-----"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);
        string yaml = await sut.GenerateHttpRouteYamlAsync(route.Id);

        yaml.Should().Contain("kind: HTTPRoute");
        yaml.Should().Contain("manual.example.com");
        yaml.Should().Contain("my-service-tls");
        yaml.Should().Contain("kind: Secret");
    }

    [Fact]
    public void GenerateTlsSecretYaml_ManualMode_GeneratesSecret()
    {
        ExternalRoute route = new()
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            Hostname = "secure.example.com",
            ServiceName = "my-svc",
            ServicePort = 443,
            TlsMode = TlsMode.Manual,
            TlsCertificate = "CERT_DATA",
            TlsPrivateKey = "KEY_DATA",
            Component = new ClusterComponent
            {
                Id = componentId,
                ClusterId = clusterId,
                Name = "test",
                ComponentType = "HelmChart",
                Namespace = "apps"
            }
        };

        string yaml = ExternalRouteService.GenerateTlsSecretYaml(route);

        yaml.Should().Contain("kind: Secret");
        yaml.Should().Contain("type: kubernetes.io/tls");
        yaml.Should().Contain("namespace: apps");
        yaml.Should().Contain("my-svc-tls");
    }

    [Fact]
    public void GenerateTlsSecretYaml_ClusterIssuerMode_ReturnsEmpty()
    {
        // No Secret needed for automatic TLS — cert-manager handles it.

        ExternalRoute route = new()
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            Hostname = "auto.example.com",
            ServiceName = "svc",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        string yaml = ExternalRouteService.GenerateTlsSecretYaml(route);

        yaml.Should().BeEmpty();
    }

    // ──────── Gateway resolution ────────

    [Fact]
    public async Task AddRoute_ResolvesTraefikGateway()
    {
        // Cluster has Traefik installed, so gateway should resolve to traefik-gateway.

        ExternalRouteRequest request = new()
        {
            Hostname = "gw-test.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);

        route.GatewayName.Should().Be("traefik-gateway");
        route.GatewayNamespace.Should().Be("traefik");
    }
}
