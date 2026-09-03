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
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();

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

    // ──────── Retries ────────

    /// <summary>Walks to the retry block of the route's first (and here only) rule.</summary>
    private static Dictionary<object, object> FirstRuleRetry(string yaml)
    {
        object root = new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<object>(new StringReader(yaml))!;

        Dictionary<object, object> spec = (Dictionary<object, object>)((Dictionary<object, object>)root)["spec"];
        List<object> rules = (List<object>)spec["rules"];
        return (Dictionary<object, object>)((Dictionary<object, object>)rules[0])["retry"];
    }

    [Fact]
    public void GenerateHttpRouteYaml_RetriesConnectionFailures()
    {
        // A connection refused or reset before the backend read the request is safe to retry for
        // any method — nothing happened upstream to repeat. Without this the client owns it.
        Dictionary<object, object> retry = FirstRuleRetry(
            ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/", null)));

        retry["attempts"].Should().Be(ExternalRouteService.DefaultRetryAttempts.ToString());
        retry["backoff"].Should().Be(ExternalRouteService.DefaultRetryBackoff);
    }

    [Fact]
    public void GenerateHttpRouteYaml_RetryListsFiveOhThree()
    {
        // Istio applies a default retry policy to any route that sets none, and that default
        // retries 503 through retriable-status-codes. Setting retry replaces the default whole,
        // so leaving codes out would silently withdraw retries the cluster already has.
        List<object> codes = (List<object>)FirstRuleRetry(
            ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/", null)))["codes"];

        codes.Should().ContainSingle().Which.Should().Be("503");
    }

    [Fact]
    public void GenerateHttpRouteYaml_StreamingRoute_StillRetriesConnectionFailures()
    {
        // A timeout of 0 opts out of timeouts, not out of retries: a stream that never opened
        // because the socket was dead is exactly the case worth a second attempt.
        string yaml = ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/", 0));

        yaml.Should().NotContain("timeouts:");
        FirstRuleRetry(yaml)["attempts"].Should().Be(ExternalRouteService.DefaultRetryAttempts.ToString());
    }

    [Fact]
    public void GenerateHttpRouteYaml_PathPrefixedRoute_NestsRetryInsideTheRule()
    {
        // Same level as timeouts — a sibling of matches/backendRefs, not of the list item.
        ExternalRouteService.GenerateHttpRouteYaml(TimeoutRoute("/api", 30))
            .Should().Contain("      retry:");
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

    // ──────── Session affinity (consistentHash) ────────

    /// <summary>
    /// Parses the generated rule and walks down to a trafficPolicy, so the assertions below test
    /// the structure Istio will read rather than the substrings the generator happens to write.
    /// </summary>
    private static Dictionary<object, object> TrafficPolicy(string yaml, int? portNumber = null)
    {
        object root = new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<object>(new StringReader(yaml))!;

        Dictionary<object, object> spec = (Dictionary<object, object>)((Dictionary<object, object>)root)["spec"];
        Dictionary<object, object> policy = (Dictionary<object, object>)spec["trafficPolicy"];

        if (portNumber is null)
        {
            return policy;
        }

        List<object> portSettings = (List<object>)policy["portLevelSettings"];
        return portSettings
            .Cast<Dictionary<object, object>>()
            .Single(p => (string)((Dictionary<object, object>)p["port"])["number"] == portNumber.Value.ToString());
    }

    private static Dictionary<object, object> ConsistentHash(Dictionary<object, object> trafficPolicy) =>
        (Dictionary<object, object>)((Dictionary<object, object>)trafficPolicy["loadBalancer"])["consistentHash"];

    [Fact]
    public void GenerateBackendDestinationRuleYaml_NoAffinity_EmitsNoLoadBalancer()
    {
        // The default must leave the rule exactly as it was before affinity existed — every
        // cluster running today is on this path.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("http", 80, "TCP")], alwaysEmit: true);

        yaml.Should().NotContain("loadBalancer");
        yaml.Should().NotContain("consistentHash");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_CookieAffinity_HashesOnTheNamedCookie()
    {
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("http", 80, "TCP")], alwaysEmit: true,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.Cookie, "SESSIONID", 3600));

        Dictionary<object, object> cookie =
            (Dictionary<object, object>)ConsistentHash(TrafficPolicy(yaml))["httpCookie"];

        cookie["name"].Should().Be("SESSIONID");
        cookie["ttl"].Should().Be("3600s");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_CookieAffinity_DefaultsTheNameAndAsksForASessionCookie()
    {
        // No cookie name and no lifetime is the common case: Envoy issues the cookie itself, and
        // ttl 0s is its session cookie. The field cannot simply be left out — Istio's webhook
        // rejects an httpCookie without a ttl, and that rejection fails the whole apply.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [], alwaysEmit: true,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.Cookie, null, null));

        Dictionary<object, object> cookie =
            (Dictionary<object, object>)ConsistentHash(TrafficPolicy(yaml))["httpCookie"];

        cookie["name"].Should().Be(ExternalRouteService.DefaultAffinityCookieName);
        cookie["ttl"].Should().Be("0s");
    }

    [Theory]
    [InlineData(SessionAffinityMode.Header, "httpHeaderName")]
    [InlineData(SessionAffinityMode.QueryParameter, "httpQueryParameterName")]
    public void GenerateBackendDestinationRuleYaml_KeyedAffinity_HashesOnTheGivenName(
        SessionAffinityMode mode, string expectedField)
    {
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [], alwaysEmit: true,
            sessionAffinity: new SessionAffinitySpec(mode, "x-tenant-id", null));

        ConsistentHash(TrafficPolicy(yaml))[expectedField].Should().Be("x-tenant-id");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_SourceIpAffinity_HashesOnTheClientAddress()
    {
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [], alwaysEmit: true,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.SourceIp, null, null));

        ConsistentHash(TrafficPolicy(yaml))["useSourceIp"].Should().Be("true");
    }

    // ──────── Connection pool (Envoy/Kestrel idle-timeout race) ────────

    private static Dictionary<object, object> ConnectionPoolHttp(Dictionary<object, object> trafficPolicy) =>
        (Dictionary<object, object>)((Dictionary<object, object>)trafficPolicy["connectionPool"])["http"];

    [Fact]
    public void GenerateBackendDestinationRuleYaml_SetsAnIdleTimeoutBelowKestrelsKeepAlive()
    {
        // Envoy pools an idle upstream socket for an hour; Kestrel closes it at 130s. Whichever
        // number is larger owns the race, so ours has to be the smaller one.
        ExternalRouteService.BackendConnectionIdleTimeoutSeconds.Should().BeLessThan(130);

        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("http", 80, "TCP")], alwaysEmit: true);

        ConnectionPoolHttp(TrafficPolicy(yaml))["idleTimeout"]
            .Should().Be($"{ExternalRouteService.BackendConnectionIdleTimeoutSeconds}s");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_TlsPort_KeepsItsOwnIdleTimeout()
    {
        // Port-level settings replace the destination-level policy rather than merging with it.
        // A TLS port that only overrode tls would inherit no connection pool at all and fall back
        // to Envoy's one-hour default — the exact bug this closes, reopened on port 443.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("https", 443, "TCP")], alwaysEmit: true);

        Dictionary<object, object> port443 = TrafficPolicy(yaml, 443);

        ConnectionPoolHttp(port443)["idleTimeout"]
            .Should().Be($"{ExternalRouteService.BackendConnectionIdleTimeoutSeconds}s");

        // The settings that were already correct are still there alongside it.
        Dictionary<object, object> tls = (Dictionary<object, object>)port443["tls"];
        tls["mode"].Should().Be("SIMPLE");
        tls["insecureSkipVerify"].Should().Be("true");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_TlsPortWithAffinity_KeepsBothOverrides()
    {
        // Two things now have to be repeated into every port entry. Losing either one is silent.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("https", 443, "TCP")], alwaysEmit: true,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.Cookie, "SESSIONID", 3600));

        Dictionary<object, object> port443 = TrafficPolicy(yaml, 443);

        ConnectionPoolHttp(port443)["idleTimeout"]
            .Should().Be($"{ExternalRouteService.BackendConnectionIdleTimeoutSeconds}s");
        ((Dictionary<object, object>)ConsistentHash(port443)["httpCookie"])["name"]
            .Should().Be("SESSIONID");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_NoTlsPort_StillEmitsNothingWhenNotAlwaysEmit()
    {
        // The connection pool must not become a reason to start shipping a DestinationRule where
        // none exists today: the rule would also carry tls DISABLE, which is wrong for a service
        // whose namespace runs STRICT mesh mTLS.
        ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("http", 80, "TCP")], alwaysEmit: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_HeaderAffinityWithoutAName_EmitsNoLoadBalancer()
    {
        // A consistentHash with no field set is rejected by the API server, and that rejection
        // would take the tls settings in the same document down with it.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [], alwaysEmit: true,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.Header, "   ", null));

        yaml.Should().NotContain("loadBalancer");
        TrafficPolicy(yaml).Should().ContainKey("tls");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_AffinityAlone_IsReasonEnoughToEmitTheRule()
    {
        // A plaintext-only service on a call site that ships nothing today: without affinity
        // there is no rule to write, with affinity there has to be one.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("http", 80, "TCP")], alwaysEmit: false,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.SourceIp, null, null));

        yaml.Should().Contain("name: entkube-disable-mtls-svc");
        ConsistentHash(TrafficPolicy(yaml))["useSourceIp"].Should().Be("true");
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_UnrenderableAffinity_DoesNotForceARule()
    {
        // An affinity that renders nothing must not be the reason a call site that ships no rule
        // today starts shipping one.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system", [new KubeServicePort("http", 80, "TCP")], alwaysEmit: false,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.Header, null, null));

        yaml.Should().BeEmpty();
    }

    [Fact]
    public void GenerateBackendDestinationRuleYaml_TlsPort_KeepsTheAffinityItWouldOtherwiseLose()
    {
        // Istio does not inherit destination-level traffic settings into port-level ones, so the
        // TLS port needs its own copy or it alone would load balance freely.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "svc", "apps", "istio-system",
            [new KubeServicePort("http", 80, "TCP"), new KubeServicePort("https", 8443, "TCP")],
            alwaysEmit: true,
            sessionAffinity: new SessionAffinitySpec(SessionAffinityMode.Cookie, "SESSIONID", null));

        Dictionary<object, object> portPolicy = TrafficPolicy(yaml, portNumber: 8443);

        ((Dictionary<object, object>)portPolicy["tls"])["mode"].Should().Be("SIMPLE");
        ((Dictionary<object, object>)ConsistentHash(portPolicy)["httpCookie"])["name"]
            .Should().Be("SESSIONID");
    }

    [Fact]
    public void SessionAffinitySpec_Merge_TakesTheFirstRouteAskingForAffinity()
    {
        // One DestinationRule per Service: routes that disagree cannot both be honoured, and a
        // stable winner keeps successive applies from flapping the cluster between them.
        SessionAffinitySpec merged = SessionAffinitySpec.Merge([
            SessionAffinitySpec.None,
            new SessionAffinitySpec(SessionAffinityMode.Cookie, "FIRST", null),
            new SessionAffinitySpec(SessionAffinityMode.Header, "second", null)
        ]);

        merged.Mode.Should().Be(SessionAffinityMode.Cookie);
        merged.Key.Should().Be("FIRST");
    }

    [Fact]
    public void SessionAffinitySpec_Merge_NoRouteAsking_IsNoAffinity()
    {
        SessionAffinitySpec.Merge([SessionAffinitySpec.None, SessionAffinitySpec.None])
            .IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(SessionAffinityMode.Header, null)]
    [InlineData(SessionAffinityMode.Header, "  ")]
    [InlineData(SessionAffinityMode.QueryParameter, null)]
    public void ValidateSessionAffinity_KeyedModesWithoutAKey_AreRefused(SessionAffinityMode mode, string? key)
    {
        ExternalRouteService.ValidateSessionAffinity(mode, key).Should().NotBeNull();
    }

    [Theory]
    [InlineData(SessionAffinityMode.None, null)]
    [InlineData(SessionAffinityMode.SourceIp, null)]
    [InlineData(SessionAffinityMode.Cookie, null)]
    [InlineData(SessionAffinityMode.Header, "x-tenant-id")]
    public void ValidateSessionAffinity_AcceptsWhatCanBeRendered(SessionAffinityMode mode, string? key)
    {
        ExternalRouteService.ValidateSessionAffinity(mode, key).Should().BeNull();
    }

    [Theory]
    [InlineData(SessionAffinityMode.None)]
    [InlineData(SessionAffinityMode.SourceIp)]
    public void NormalizeAffinityKey_DropsAKeyTheModeCannotUse(SessionAffinityMode mode)
    {
        // Otherwise a header name left behind by a mode change reappears if the mode is switched
        // back, silently hashing on something nobody chose this time.
        ExternalRouteService.NormalizeAffinityKey(mode, "x-tenant-id").Should().BeNull();
    }

    [Fact]
    public void NormalizeAffinityKey_TrimsTheKeyItKeeps()
    {
        ExternalRouteService.NormalizeAffinityKey(SessionAffinityMode.Header, "  x-tenant-id  ")
            .Should().Be("x-tenant-id");
    }

    private Task<ExternalRoute> AddRouteForAffinityTestAsync() =>
        sut.AddRouteAsync(componentId, new ExternalRouteRequest
        {
            Hostname = $"affinity-{Guid.NewGuid():N}.example.com",
            ServiceName = "sticky-app",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        });

    [Fact]
    public async Task UpdateRouteSessionAffinityAsync_SwitchingAwayFromAKeyedMode_ClearsTheKey()
    {
        ExternalRoute route = await AddRouteForAffinityTestAsync();

        await sut.UpdateRouteSessionAffinityAsync(route.Id, SessionAffinityMode.Header, "x-tenant-id", null);
        await sut.UpdateRouteSessionAffinityAsync(route.Id, SessionAffinityMode.SourceIp, "x-tenant-id", 60);

        ExternalRoute saved = await db.ExternalRoutes.AsNoTracking().FirstAsync(r => r.Id == route.Id);
        saved.SessionAffinity.Should().Be(SessionAffinityMode.SourceIp);
        saved.SessionAffinityKey.Should().BeNull();
        // A ttl only means anything for a cookie; keeping it would show a lifetime on a mode
        // that has no cookie to expire.
        saved.SessionAffinityTtlSeconds.Should().BeNull();
    }

    [Fact]
    public async Task UpdateRouteSessionAffinityAsync_HeaderWithoutAName_IsRefused()
    {
        ExternalRoute route = await AddRouteForAffinityTestAsync();

        Func<Task> act = () => sut.UpdateRouteSessionAffinityAsync(route.Id, SessionAffinityMode.Header, null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*header*");
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

    // ──────── Hostnames an application route already owns ────────

    /// <summary>
    /// Puts an enabled AppRoute on the seeded cluster claiming a hostname, with one enabled
    /// deployment route behind it. That is the shape ApplyExternalRoutesAsync looks for when it
    /// decides which ExternalRoutes it is willing to send to the cluster.
    /// </summary>
    private void SeedAppRouteFor(string hostname)
    {
        // An AppDeployment hangs off an App, which hangs off a Customer — the foreign keys are
        // enforced here, so the whole chain has to exist before the route does.
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Flow" };
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "flow" };

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            EnvironmentId = envId,
            Name = "flow",
            Namespace = "flow",
            ClusterId = clusterId,
        };

        AppRoute appRoute = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Hostname = hostname,
            IsEnabled = true,
        };

        AppDeploymentRoute deploymentRoute = new()
        {
            Id = Guid.NewGuid(),
            AppRouteId = appRoute.Id,
            AppDeploymentId = deployment.Id,
            PathPrefix = "/",
            ServiceName = "flow-frontend-web",
            ServicePort = 443,
            IsEnabled = true,
        };

        db.Set<Customer>().Add(customer);
        db.Set<App>().Add(app);
        db.Set<AppDeployment>().Add(deployment);
        db.Set<AppRoute>().Add(appRoute);
        db.Set<AppDeploymentRoute>().Add(deploymentRoute);
        db.SaveChanges();
    }

    /// <summary>
    /// The outage this check exists to prevent. An application route already serves the hostname
    /// with per-path rules; an external route on the same hostname renders an object of the same
    /// name carrying a single backend, so saving one arms a trap that springs the next time any
    /// component on the cluster is applied.
    /// </summary>
    [Fact]
    public async Task AddRoute_OnAHostnameAnAppRouteServes_IsRejected()
    {
        SeedAppRouteFor("flow.sto2.entit.eu");

        ExternalRouteRequest request = new()
        {
            Hostname = "flow.sto2.entit.eu",
            ServiceName = "flow-definition-store",
            ServicePort = 443,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        Func<Task> add = () => sut.AddRouteAsync(componentId, request);

        await add.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already served by an application route*");
    }

    /// <summary>
    /// The operator may well have typed the hostname with different capitalisation in the two
    /// forms. DNS does not distinguish them and neither does the generated object name, so the
    /// check must not either.
    /// </summary>
    [Fact]
    public async Task AddRoute_MatchesAppRouteHostnameRegardlessOfCase()
    {
        SeedAppRouteFor("flow.sto2.entit.eu");

        ExternalRouteRequest request = new()
        {
            Hostname = "FLOW.STO2.ENTIT.EU",
            ServiceName = "flow-definition-store",
            ServicePort = 443,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        Func<Task> add = () => sut.AddRouteAsync(componentId, request);

        await add.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already served by an application route*");
    }

    /// <summary>
    /// A hostname no application route claims is unaffected — this check narrows nothing else.
    /// </summary>
    [Fact]
    public async Task AddRoute_OnAnUnclaimedHostname_StillSucceeds()
    {
        SeedAppRouteFor("flow.sto2.entit.eu");

        ExternalRouteRequest request = new()
        {
            Hostname = "grafana.example.com",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        };

        ExternalRoute route = await sut.AddRouteAsync(componentId, request);

        route.Hostname.Should().Be("grafana.example.com");
    }

    /// <summary>
    /// Routes saved before the check existed are still in the database, and the apply step now
    /// silently holds them back. The UI needs to be able to name them, or the operator sees a
    /// route that looks configured and a cluster that disagrees, with nothing connecting the two.
    /// </summary>
    [Fact]
    public async Task ShadowedHostnames_NamesTheRoutesTheApplyStepHoldsBack()
    {
        // Saved first, while the hostname was still free — exactly how the real one got there.
        await sut.AddRouteAsync(componentId, new ExternalRouteRequest
        {
            Hostname = "flow.sto2.entit.eu",
            ServiceName = "flow-definition-store",
            ServicePort = 443,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        });

        await sut.AddRouteAsync(componentId, new ExternalRouteRequest
        {
            Hostname = "grafana.example.com",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        });

        SeedAppRouteFor("flow.sto2.entit.eu");

        IReadOnlySet<string> shadowed = await sut.ShadowedHostnamesAsync(componentId);

        shadowed.Should().BeEquivalentTo(["flow.sto2.entit.eu"]);
    }

    /// <summary>
    /// With no application routes anywhere, nothing is held back and nothing is flagged.
    /// </summary>
    [Fact]
    public async Task ShadowedHostnames_IsEmptyWhenNoAppRouteCompetes()
    {
        await sut.AddRouteAsync(componentId, new ExternalRouteRequest
        {
            Hostname = "grafana.example.com",
            ServicePort = 80,
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt-prod"
        });

        IReadOnlySet<string> shadowed = await sut.ShadowedHostnamesAsync(componentId);

        shadowed.Should().BeEmpty();
    }
}
