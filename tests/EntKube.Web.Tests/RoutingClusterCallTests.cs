using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What <see cref="KubernetesOperationsService"/>'s routing, gateway, L4 and mesh paths actually
/// send to a cluster.
///
/// <para><b>Why these, and why now.</b> This service is 3,500 lines across 32 public methods and
/// had seven tests, every one of which asserted the same thing: that a cluster with no kubeconfig
/// is refused. Nothing observed a single manifest, delete or patch, because the kubectl invocation
/// sat behind a <c>private static</c> method no test could reach. The credential-custody work
/// (docs/decomposition.md §4.0.1) routes these paths through <see cref="IClusterClient"/>, which
/// is also the first seam that makes them observable — so the coverage arrives with the seam
/// rather than after it.</para>
///
/// <para>These assert on <em>what is sent to the cluster</em>, not on the database row. A route
/// whose manifest is no longer applied, applied to the wrong namespace, or deleted under the wrong
/// resource type leaves its row looking perfectly correct — and the symptom shows up as a hostname
/// that silently stops serving.</para>
/// </summary>
public class RoutingClusterCallTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly KubernetesOperationsService sut;

    /// <summary>Every manifest handed to the cluster, in the order it was applied.</summary>
    private readonly List<string> applied = [];

    /// <summary>Every delete asked of the cluster, as (kind, name, namespace).</summary>
    private readonly List<(string Kind, string Name, string Namespace)> deleted = [];

    /// <summary>Every strategic patch asked of the cluster, as (resource, name, namespace, patch).</summary>
    private readonly List<(string Resource, string Name, string Namespace, string Patch)> patched = [];

    public RoutingClusterCallTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string manifest, string _, CancellationToken _) => applied.Add(manifest))
            .ReturnsAsync("applied");

        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string kind, string name, string ns, string _, CancellationToken _) =>
                deleted.Add((kind, name, ns)))
            .Returns(Task.CompletedTask);

        k8s.Setup(x => x.PatchStrategicAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string res, string name, string ns, string patch, string _, CancellationToken _) =>
                patched.Add((res, name, ns, patch)))
            .Returns(Task.CompletedTask);

        // Default: the Gateway API experimental CRDs are installed.
        k8s.Setup(x => x.ResourceExistsAsync("crd", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);

        sut = new KubernetesOperationsService(
            testDb.Factory,
            new AuditService(testDb.Factory),
            new KyvernoPolicyService(testDb.Factory, k8s.Object, gate, NullLogger<KyvernoPolicyService>.Instance),
            gate,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Services.Rollouts.NoOpRolloutStarter(),
            NullLogger<KubernetesOperationsService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────── Fixture ────────

    private Guid tenantId, clusterId, deploymentId;

    /// <summary>
    /// A tenant with one cluster (kubeconfig in the vault, as production stores it), one app and
    /// one deployment in namespace "billing-ns". The cluster runs Istio, because L4 routing requires
    /// an Istio ingress gateway — and it is the installed component that decides the gateway's
    /// namespace, so seeding it is what makes "istio-system" below the real answer.
    /// </summary>
    private async Task SeedAsync(bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        deploymentId = Guid.NewGuid();
        Guid envId = Guid.NewGuid(), customerId = Guid.NewGuid(), appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = "istio",
            ComponentType = "ServiceMesh", Namespace = "istio-system",
            Status = ComponentStatus.Installed,
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "billing-deploy", Type = DeploymentType.Manual,
            EnvironmentId = envId, ClusterId = clusterId, Namespace = "billing-ns",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }
    }

    private async Task<AppL4Route> SeedL4RouteAsync(
        L4Protocol protocol = L4Protocol.Tcp, bool managed = true, int externalPort = 5432)
    {
        AppDeployment deployment = db.AppDeployments.First(d => d.Id == deploymentId);

        AppL4Route route = new()
        {
            Id = Guid.NewGuid(),
            AppId = deployment.AppId,
            AppDeploymentId = deploymentId,
            Protocol = protocol,
            ExternalPort = externalPort,
            ServiceName = "billing-db",
            ServicePort = 5432,
            GatewayName = ExternalRouteService.L4GatewayName,
            GatewayNamespace = "istio-system",
            IsEnabled = true,
            IsManaged = managed,
        };
        db.AppL4Routes.Add(route);
        await db.SaveChangesAsync();
        return route;
    }

    /// <summary>
    /// A manifest whose own <c>kind</c> is this one. Matching bare "kind: TCPRoute" would also hit
    /// the Gateway, which names the kind it admits under <c>allowedRoutes.kinds</c> — indented,
    /// which is exactly what the leading newline rules out.
    /// </summary>
    private static string Document(string kind) => $"\nkind: {kind}\n";

    /// <summary>Makes the dedicated L4 Gateway read back with one assigned address.</summary>
    private void GatewayReportsAddress(string address) =>
        k8s.Setup(x => x.GetJsonAsync(
                $"gateway/{ExternalRouteService.L4GatewayName}", It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync($"{{\"status\":{{\"addresses\":[{{\"type\":\"IPAddress\",\"value\":\"{address}\"}}]}}}}");

    // ════════════════════════════════════════════════════════════════
    //  ApplyL4RouteAsync — what lands on the cluster
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyL4RouteAsync_AppliesTheTcpRouteItself()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync();

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        result.IsSuccess.Should().BeTrue(result.Error);

        // The TCPRoute goes to the backend's namespace, named service-protocol-port, pointing at
        // the dedicated L4 gateway's listener for that port.
        applied.Should().ContainSingle(m => m.Contains(Document("TCPRoute")))
            .Which.Should().Contain("name: billing-db-tcp-5432")
            .And.Contain("namespace: billing-ns")
            .And.Contain($"name: {ExternalRouteService.L4GatewayName}")
            .And.Contain("sectionName: tcp-5432")
            .And.Contain("port: 5432");
    }

    [Fact]
    public async Task ApplyL4RouteAsync_ForTcp_StandsDownGatewayMtlsToTheBackend()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync(L4Protocol.Tcp);

        await sut.ApplyL4RouteAsync(route.Id);

        // The ingress gateway would otherwise force mTLS to a sidecar-less pod. Both halves are
        // needed: PERMISSIVE on what the pod accepts, DISABLE on what the gateway sends.
        applied.Should().ContainSingle(m => m.Contains(Document("PeerAuthentication")))
            .Which.Should().Contain("namespace: billing-ns")
            .And.Contain("mode: PERMISSIVE");

        applied.Should().ContainSingle(m => m.Contains(Document("DestinationRule")))
            .Which.Should().Contain("host: billing-db.billing-ns.svc.cluster.local")
            .And.Contain("mode: DISABLE");
    }

    [Fact]
    public async Task ApplyL4RouteAsync_ForUdp_LeavesMtlsAlone()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync(L4Protocol.Udp, externalPort: 53);

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        result.IsSuccess.Should().BeTrue(result.Error);

        // Istio mTLS does not apply to UDP, so neither resource should be sent.
        applied.Should().NotContain(m => m.Contains(Document("PeerAuthentication")));
        applied.Should().NotContain(m => m.Contains(Document("DestinationRule")));
        applied.Should().ContainSingle(m => m.Contains(Document("UDPRoute")))
            .Which.Should().Contain("sectionName: udp-53");
    }

    [Fact]
    public async Task ApplyL4RouteAsync_WithoutTheRouteCrd_SendsNothingAtAll()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync();

        // The TCPRoute/UDPRoute CRDs ship only in the Gateway API experimental channel.
        k8s.Setup(x => x.ResourceExistsAsync("crd", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("tcproutes.gateway.networking.k8s.io")
            .And.Contain("experimental channel");

        // Refusing early matters: a half-applied gateway would publish a port nothing serves.
        applied.Should().BeEmpty();
        deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyL4RouteAsync_AnUnreachableClusterReadsAsCrdMissing()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync();

        k8s.Setup(x => x.ResourceExistsAsync("crd", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): connection refused"));

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        // As it did when this ran kubectl directly: a read that fails is not evidence the CRD is
        // there, so the apply is refused rather than attempted.
        result.IsSuccess.Should().BeFalse();
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyL4RouteAsync_ReportsTheGatewaysOwnAddressAsTheEndpoint()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync();
        GatewayReportsAddress("203.0.113.40");

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Data.Should().Contain("203.0.113.40:5432/TCP");
    }

    [Fact]
    public async Task ApplyL4RouteAsync_WithNoAddressYet_SaysPendingRatherThanFailing()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync();
        // A LoadBalancer that has not been assigned an address yet: the field is simply absent.
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"status\":{\"addresses\":[]}}");

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Data.Should().Contain("pending");
    }

    [Fact]
    public async Task ApplyL4RouteAsync_ObservedOnlyRoute_SendsNothing()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync(managed: false);

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        result.IsSuccess.Should().BeFalse();
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyL4RouteAsync_WithoutAStoredKubeconfig_SendsNothing()
    {
        await SeedAsync(withKubeconfig: false);
        AppL4Route route = await SeedL4RouteAsync();

        KubernetesOperationResult<string> result = await sut.ApplyL4RouteAsync(route.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
        applied.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════════
    //  DeleteL4RouteFromClusterAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteL4RouteFromClusterAsync_DeletesTheRouteUnderItsOwnResourceType()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync();

        await sut.DeleteL4RouteFromClusterAsync(route.Id);

        // The fully-qualified resource type matters: "tcproute" alone is ambiguous with any other
        // group that has registered one.
        deleted.Should().Contain(
            ("tcproute.gateway.networking.k8s.io", "billing-db-tcp-5432", "billing-ns"));
    }

    [Fact]
    public async Task DeleteL4RouteFromClusterAsync_WhenItWasTheLastRoute_RemovesTheGatewayToo()
    {
        await SeedAsync();
        AppL4Route route = await SeedL4RouteAsync();

        await sut.DeleteL4RouteFromClusterAsync(route.Id);

        // No L4 ports left, so the dedicated Gateway goes with it — that is what releases its
        // auto-provisioned LoadBalancer. Regenerating it instead would keep billing for nothing.
        deleted.Should().Contain(
            ("gateway.gateway.networking.k8s.io", ExternalRouteService.L4GatewayName, "istio-system"));
        applied.Should().NotContain(m => m.Contains(Document("Gateway")));
    }

    [Fact]
    public async Task DeleteL4RouteFromClusterAsync_WithRoutesRemaining_RegeneratesTheGateway()
    {
        await SeedAsync();
        AppL4Route going = await SeedL4RouteAsync(externalPort: 5432);
        await SeedL4RouteAsync(externalPort: 6379);

        await sut.DeleteL4RouteFromClusterAsync(going.Id);

        // The surviving route keeps its listener; the Gateway is re-applied, not deleted.
        applied.Should().ContainSingle(m => m.Contains(Document("Gateway")))
            .Which.Should().Contain("tcp-6379").And.NotContain("tcp-5432");
        deleted.Should().NotContain(
            ("gateway.gateway.networking.k8s.io", ExternalRouteService.L4GatewayName, "istio-system"));
    }

    // ════════════════════════════════════════════════════════════════
    //  GetL4EndpointAddressAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetL4EndpointAddressAsync_ReadsTheAddressFromTheGateway()
    {
        await SeedAsync();
        GatewayReportsAddress("198.51.100.7");

        string? address = await sut.GetL4EndpointAddressAsync(clusterId);

        address.Should().Be("198.51.100.7");
    }

    [Fact]
    public async Task GetL4EndpointAddressAsync_ReturnsNullWhenTheGatewayIsNotThere()
    {
        await SeedAsync();
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): NotFound"));

        string? address = await sut.GetL4EndpointAddressAsync(clusterId);

        address.Should().BeNull();
    }

    [Fact]
    public async Task GetL4EndpointAddressAsync_ReturnsNullForAClusterThatIsNotThere()
    {
        await SeedAsync();
        GatewayReportsAddress("198.51.100.7");

        string? address = await sut.GetL4EndpointAddressAsync(Guid.NewGuid());

        address.Should().BeNull();
    }

    /// <summary>
    /// Worth saying plainly, because the cluster client's whole point is that it resolves a
    /// credential by (tenant, cluster) together and refuses the pair it was not given: this method
    /// takes only a cluster id, so the tenant it checks against is the one read off that very row.
    /// That is not a tenancy check, and no test here can pretend otherwise — a cross-tenant call
    /// cannot even be expressed against this signature. Closing it means giving the method a
    /// caller-supplied tenant, which is a change to its callers, not to this service.
    /// </summary>
    [Fact]
    public async Task GetL4EndpointAddressAsync_DerivesTheTenantFromTheClusterItWasAskedAbout()
    {
        await SeedAsync();
        GatewayReportsAddress("198.51.100.7");

        string? address = await sut.GetL4EndpointAddressAsync(clusterId);

        // Reached with no tenant in hand at all, and it answers.
        address.Should().Be("198.51.100.7");
    }

    // ════════════════════════════════════════════════════════════════
    //  ApplyMeshMtlsAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyMeshMtlsAsync_AppliesThePeerAuthenticationForTheNamespace()
    {
        await SeedAsync();

        KubernetesOperationResult<string> result = await sut.ApplyMeshMtlsAsync(clusterId, "billing-ns");

        result.IsSuccess.Should().BeTrue(result.Error);
        applied.Should().ContainSingle(m => m.Contains(Document("PeerAuthentication")))
            .Which.Should().Contain("namespace: billing-ns");
    }

    [Fact]
    public async Task ApplyMeshMtlsAsync_WithoutAStoredKubeconfig_SendsNothing()
    {
        await SeedAsync(withKubeconfig: false);

        KubernetesOperationResult<string> result = await sut.ApplyMeshMtlsAsync(clusterId, "billing-ns");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
        applied.Should().BeEmpty();
    }
}
