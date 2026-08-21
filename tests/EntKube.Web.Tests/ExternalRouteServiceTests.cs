using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

        sut = new ExternalRouteService(dbFactory, NullLogger<ExternalRouteService>.Instance);
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
    public async Task GenerateHttpRouteYaml_ClusterIssuer_IncludesCertificate()
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
        string yaml = await sut.GenerateFullManifestYamlAsync(route.Id);

        // ClusterIssuer mode appends a cert-manager Certificate (issuerRef → ClusterIssuer),
        // not an ingress-shim annotation — TLS terminates at the Gateway listener.
        yaml.Should().Contain("kind: HTTPRoute");
        yaml.Should().Contain("grafana.example.com");
        yaml.Should().Contain("kind: Certificate");
        yaml.Should().Contain("kind: ClusterIssuer");
        yaml.Should().Contain("name: letsencrypt-prod");
        yaml.Should().Contain("name: traefik-gateway");
        yaml.Should().Contain("kube-prometheus-stack-grafana");
    }

    [Fact]
    public async Task GenerateTlsSecret_ManualTls_ReferencesSecret()
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

        // The applied route manifest is just the HTTPRoute (TLS terminates at the gateway)…
        string yaml = await sut.GenerateHttpRouteYamlAsync(route.Id);
        yaml.Should().Contain("kind: HTTPRoute");
        yaml.Should().Contain("manual.example.com");

        // …the manually-supplied certificate is applied as a separate TLS Secret.
        string secret = ExternalRouteService.GenerateTlsSecretYaml(route);
        secret.Should().Contain("kind: Secret");
        secret.Should().Contain("my-service-tls");
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

    // ──────── Request timeouts ────────

    private ExternalRoute TimeoutRoute(string pathPrefix, int? timeoutSeconds) => new()
    {
        Id = Guid.NewGuid(),
        ComponentId = componentId,
        Hostname = "timeout.example.com",
        ServiceName = "svc",
        ServicePort = 80,
        PathPrefix = pathPrefix,
        GatewayName = "traefik-gateway",
        GatewayNamespace = "traefik",
        RequestTimeoutSeconds = timeoutSeconds,
        Component = new ClusterComponent
        {
            Id = componentId,
            ClusterId = clusterId,
            Name = "test",
            ComponentType = "HelmChart",
            Namespace = "apps"
        }
    };

    [Fact]
    public void GenerateHttpRouteYaml_NoTimeoutSet_AppliesPlatformDefault()
    {
        // Without a timeouts block the gateway waits forever, so a wedged upstream hangs the
        // browser instead of failing fast. Routes that set nothing get the platform default.

        string yaml = ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/", null));

        yaml.Should().Contain("      timeouts:");
        yaml.Should().Contain($"        request: {ExternalRouteService.DefaultRequestTimeoutSeconds}s");
        yaml.Should().Contain($"        backendRequest: {ExternalRouteService.DefaultRequestTimeoutSeconds}s");
    }

    [Fact]
    public void GenerateHttpRouteYaml_ExplicitTimeout_UsesIt()
    {
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/", 15));

        yaml.Should().Contain("        request: 15s");
        yaml.Should().Contain("        backendRequest: 15s");
        yaml.Should().NotContain($"{ExternalRouteService.DefaultRequestTimeoutSeconds}s");
    }

    [Fact]
    public void GenerateHttpRouteYaml_ZeroTimeout_EmitsNoTimeoutsBlock()
    {
        // 0 is the escape hatch for long-lived streams (websockets, ts2021): the rule carries
        // no timeouts at all, so the gateway keeps its own no-timeout behaviour.

        string yaml = ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/", 0));

        yaml.Should().NotContain("timeouts:");
        yaml.Should().NotContain("backendRequest:");
    }

    [Fact]
    public void GenerateHttpRouteYaml_PathPrefixedRoute_NestsTimeoutsInsideTheRule()
    {
        // The timeouts block is a sibling of matches/backendRefs, not of the list item —
        // one indent level deeper than the rule's "- " marker.

        string yaml = ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/api", 30));

        yaml.Should().Contain("    - matches:");
        yaml.Should().Contain("      backendRefs:");
        yaml.Should().Contain("      timeouts:");
        yaml.Should().Contain("        request: 30s");
    }

    [Fact]
    public async Task AddRoute_PersistsRequestTimeout()
    {
        ExternalRouteRequest request = new()
        {
            Hostname = "stream.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod",
            RequestTimeoutSeconds = 0
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);

        route.RequestTimeoutSeconds.Should().Be(0);
    }

    [Fact]
    public async Task UpdateRouteTimeout_ChangesStoredValue()
    {
        ExternalRoute route = await sut.AddRouteAsync(componentId, new ExternalRouteRequest
        {
            Hostname = "adjust.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        });

        await sut.UpdateRouteTimeoutAsync(route.Id, 120);

        List<ExternalRoute> routes = await sut.GetRoutesAsync(componentId);
        routes.Single(r => r.Id == route.Id).RequestTimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public async Task UpdateRouteTimeout_NegativeValue_Throws()
    {
        ExternalRoute route = await sut.AddRouteAsync(componentId, new ExternalRouteRequest
        {
            Hostname = "negative.example.com",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        });

        Func<Task> act = () => sut.UpdateRouteTimeoutAsync(route.Id, -1);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ──────── Backend TLS (Istio DestinationRule) ────────

    [Theory]
    [InlineData("https", null, true)]
    [InlineData("tls", null, true)]
    [InlineData("https-keycloak", null, true)]
    [InlineData("tls-grpc", null, true)]
    [InlineData("http", null, false)]
    [InlineData("management", null, false)]
    [InlineData(null, "https", true)]
    [InlineData(null, "tls", true)]
    [InlineData(null, "http", false)]
    [InlineData(null, null, false)]
    public void IsTlsBackendPort_JudgesByNameAndAppProtocol(string? name, string? appProtocol, bool expected)
    {
        ExternalRouteService.IsTlsBackendPort(new KubeServicePort(name, 8443, "TCP", appProtocol))
            .Should().Be(expected);
    }

    [Fact]
    public void IsTlsBackendPort_PortNumberAloneIsNotEvidence()
    {
        // 8443 with a plaintext name must stay plaintext — guessing from the number would
        // break a working backend, which is the expensive direction to be wrong in.
        ExternalRouteService.IsTlsBackendPort(new KubeServicePort("http", 8443, "TCP"))
            .Should().BeFalse();
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_TlsPort_OverridesTheServiceWideDisable()
    {
        // keycloakx-http shape: plaintext 80, TLS 8443, plaintext management 9000.
        List<KubeServicePort> ports =
        [
            new("http", 80, "TCP"),
            new("https", 8443, "TCP"),
            new("management", 9000, "TCP")
        ];

        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "keycloak-keycloakx-http", "identity", "istio-system", ports, alwaysEmit: true);

        yaml.Should().Contain("host: keycloak-keycloakx-http.identity.svc.cluster.local");
        yaml.Should().Contain("      mode: DISABLE");
        yaml.Should().Contain("    portLevelSettings:");
        yaml.Should().Contain("          number: 8443");
        yaml.Should().Contain("          mode: SIMPLE");
        yaml.Should().Contain("          insecureSkipVerify: true");

        // Only the TLS port is overridden — the plaintext ports keep the service-wide DISABLE.
        yaml.Should().NotContain("number: 80");
        yaml.Should().NotContain("number: 9000");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_KeepsTheExistingResourceName()
    {
        // Renaming would orphan the old service-wide rule in the cluster, where it would keep
        // breaking the very port this one fixes.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("https", 443, "TCP")], alwaysEmit: true);

        yaml.Should().Contain("name: entkube-disable-mtls-svc");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_NoTlsPort_EmitsNothingWhenNotAlwaysEmit()
    {
        // Call sites that don't already ship a DestinationRule must not start shipping one for
        // a plaintext-only service — that would change clusters that work today.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("http", 80, "TCP")], alwaysEmit: false);

        yaml.Should().BeEmpty();
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_UnknownPorts_FallsBackToTodaysServiceWideRule()
    {
        // Service unreadable → empty port list. The rule must be exactly what this call site
        // applied before, not a guess.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [], alwaysEmit: true);

        yaml.Should().Contain("      mode: DISABLE");
        yaml.Should().NotContain("portLevelSettings");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_MultipleTlsPorts_ListsEachInPortOrder()
    {
        List<KubeServicePort> ports =
        [
            new("https-alt", 8443, "TCP"),
            new("http", 80, "TCP"),
            new(null, 443, "TCP", "https")
        ];

        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", ports, alwaysEmit: true);

        yaml.IndexOf("number: 443", StringComparison.Ordinal)
            .Should().BeLessThan(yaml.IndexOf("number: 8443", StringComparison.Ordinal));
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
