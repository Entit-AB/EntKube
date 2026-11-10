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
/// Publishing a virtual machine's ports through the cluster's gateways.
///
/// <para>Like <see cref="RoutingClusterCallTests"/>, these assert on what is <em>sent to the cluster</em>.
/// The failure this feature has to avoid is not a wrong row: it is a VM hostname whose Gateway listener
/// quietly disappears the next time somebody applies an unrelated app route, because both Gateways are
/// regenerated wholesale from the database.</para>
/// </summary>
public class VmRouteTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb = new(TestRootKey);
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
    private readonly KubernetesOperationsService ops;
    private readonly VmRouteService routes;

    private readonly List<string> applied = [];
    private readonly List<(string Kind, string Name, string Namespace)> deleted = [];
    private readonly List<(string Resource, string Name)> patched = [];

    private Guid tenantId, clusterId, deploymentId;

    public VmRouteTests()
    {
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string manifest, string _, CancellationToken _) => applied.Add(manifest))
            .ReturnsAsync("applied");
        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string kind, string name, string ns, string _, CancellationToken _) => deleted.Add((kind, name, ns)))
            .Returns(Task.CompletedTask);
        k8s.Setup(x => x.PatchStrategicAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string res, string name, string _, string _, string _, CancellationToken _) =>
            {
                patched.Add((res, name));
                // Record the label patch in the same timeline as the applies, so order can be asserted.
                applied.Add($"#label {res}/{name}");
            })
            .Returns(Task.CompletedTask);
        k8s.Setup(x => x.ResourceExistsAsync("crd", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        VmHasLabel(true);

        ClusterClientFactory clusters = new(testDb.Factory, k8s.Object);
        EntKube.Web.Modules.Api.CatalogApi catalog = new(testDb.Factory);

        ops = new KubernetesOperationsService(
            testDb.Factory,
            new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
            new KyvernoPolicyService(testDb.Factory, k8s.Object, clusters, gate, catalog,
                NullLogger<KyvernoPolicyService>.Instance),
            gate, clusters, catalog,
            new EntKube.Web.Services.Rollouts.NoOpRolloutStarter(),
            NullLogger<KubernetesOperationsService>.Instance);

        routes = new VmRouteService(testDb.Factory, catalog);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────── Fixture ────────

    /// <summary>A tenant with one Istio cluster, and one app deployment beside the VMs.</summary>
    private async Task SeedAsync(string gateway = "istio")
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
        db.ClusterComponents.Add(gateway == "istio"
            ? new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = clusterId, Name = "istio", ComponentType = "ServiceMesh",
                Namespace = "istio-system", Status = ComponentStatus.Installed,
            }
            : new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = clusterId, Name = "traefik", ComponentType = "HelmChart",
                HelmChartName = "traefik", ReleaseName = "traefik", Namespace = "traefik", Status = ComponentStatus.Installed,
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
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    private void VmHasLabel(bool labelled) =>
        k8s.Setup(x => x.GetJsonAsync("virtualmachine.kubevirt.io/web-01", "vms",
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(labelled
                ? """{"spec":{"template":{"metadata":{"labels":{"kubevirt.io/vm":"web-01"}}}}}"""
                : """{"spec":{"template":{"metadata":{"labels":{"app":"web"}}}}}""");

    private Task<VmRoute> PublishHttpAsync(string host = "vm.example.com", int port = 80) =>
        routes.AddAsync(tenantId, clusterId, "vms", "web-01", new VmRouteRequest
        {
            Kind = VmRouteKind.Http, VmPort = port, Hostname = host, ClusterIssuerName = "letsencrypt-prod"
        });

    private Task<VmRoute> PublishTcpAsync(int external = 2222, int port = 22) =>
        routes.AddAsync(tenantId, clusterId, "vms", "web-01", new VmRouteRequest
        {
            Kind = VmRouteKind.Tcp, VmPort = port, ExternalPort = external
        });

    private static string Document(string kind) => $"\nkind: {kind}\n";

    /// <summary>
    /// Makes the mocked factory gate its applies, deletes and patches the way the real one does, so the
    /// acknowledgments an operator would see are observable. Without it the gate is never consulted.
    /// </summary>
    private void GateEveryChange()
    {
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string kubeconfig, CancellationToken ct) =>
            {
                await gate.AcknowledgeAsync(new PlannedClusterChange { Verb = ChangeVerb.Apply, Manifest = manifest, Kubeconfig = kubeconfig }, ct);
                applied.Add(manifest);
                return string.Empty;
            });
        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string kind, string name, string ns, string kubeconfig, CancellationToken ct) =>
            {
                await gate.AcknowledgeAsync(new PlannedClusterChange
                    { Verb = ChangeVerb.Delete, Resource = kind, Name = name, Namespace = ns, Kubeconfig = kubeconfig }, ct);
                deleted.Add((kind, name, ns));
            });
        k8s.Setup(x => x.PatchStrategicAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string res, string name, string ns, string patch, string kubeconfig, CancellationToken ct) =>
            {
                await gate.AcknowledgeAsync(new PlannedClusterChange
                    { Verb = ChangeVerb.Patch, Resource = res, Name = name, Namespace = ns, Patch = patch, Kubeconfig = kubeconfig }, ct);
                patched.Add((res, name));
            });
    }

    private sealed class CountingSink : IClusterChangeAckSink
    {
        public int Calls { get; private set; }

        public Task<ClusterChangeDecision> RequestAsync(PlannedClusterChange change, ClusterChangeDiff diff, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ClusterChangeDecision.Acknowledged);
        }
    }

    // ──────── HTTPS ────────

    [Fact]
    public async Task Publishing_a_hostname_sends_the_service_the_listener_and_the_route()
    {
        await SeedAsync();
        VmRoute route = await PublishHttpAsync();

        KubernetesOperationResult<string> result = await ops.ApplyVmRouteAsync(tenantId, route.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Data.Should().Contain("https://vm.example.com");

        string service = applied.Single(m => m.Contains(Document("Service")));
        service.Should().Contain("name: web-01-web").And.Contain("namespace: vms")
            .And.Contain("kubevirt.io/vm: web-01").And.Contain("name: http-80");

        applied.Should().Contain(m => m.Contains(Document("Gateway")) && m.Contains("vm.example.com"),
            "the hostname needs its own listener and certificate on the shared Gateway");
        applied.Should().Contain(m => m.Contains(Document("Certificate")) && m.Contains("letsencrypt-prod"));

        // The Gateway's own manifest carries an HTTPRoute too (the HTTP→HTTPS redirect), so the VM's is
        // the document that IS an HTTPRoute, not one that contains one.
        string httpRoute = applied.Single(m => m.StartsWith("apiVersion: gateway.networking.k8s.io/v1\nkind: HTTPRoute\n"));
        httpRoute.Should().Contain("namespace: vms").And.Contain("- vm.example.com")
            .And.Contain("name: web-01-web").And.Contain("port: 80").And.Contain("sectionName:");

        applied.Should().Contain(m => m.Contains(Document("DestinationRule")) && m.Contains("entkube-disable-mtls-web-01-web"),
            "the virt-launcher pod has no sidecar, so the gateway must not originate mesh mTLS to it");

        db.ChangeTracker.Clear();
        db.VmRoutes.Single().ClusterAppliedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_vm_namespace_is_labelled_before_the_gateway_lands()
    {
        await SeedAsync();
        VmRoute route = await PublishHttpAsync();

        await ops.ApplyVmRouteAsync(tenantId, route.Id);

        int label = applied.FindIndex(m => m == "#label namespace/vms");
        int gateway = applied.FindIndex(m => m.Contains(Document("Gateway")) && m.Contains("vm.example.com"));
        label.Should().BeGreaterThanOrEqualTo(0, "the listener admits routes by namespace label");
        label.Should().BeLessThan(gateway, "a Gateway that lands first detaches the route until the label arrives");
    }

    [Fact]
    public async Task Publishing_asks_once()
    {
        await SeedAsync();
        VmRoute route = await PublishHttpAsync();
        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryChange();

        (await ops.ApplyVmRouteAsync(tenantId, route.Id)).IsSuccess.Should().BeTrue();
        applied.Count.Should().BeGreaterThan(1, "the point is that one action makes several gated changes");
        sink.Calls.Should().Be(1, "Service, label, Gateway, DestinationRule and HTTPRoute are one published port");

        (await ops.WithdrawVmRouteAsync(tenantId, route.Id)).IsSuccess.Should().BeTrue();
        deleted.Count.Should().BeGreaterThan(1);
        sink.Calls.Should().Be(2, "and withdrawing it is one more");
    }

    /// <summary>
    /// <b>The regression this table exists to prevent.</b> Applying an app route regenerates the Gateway
    /// from the database. Before VM routes were stored and read there, that took the VM's hostname offline.
    /// </summary>
    [Fact]
    public async Task Applying_an_unrelated_app_route_keeps_the_vms_listener()
    {
        await SeedAsync();
        VmRoute vmRoute = await PublishHttpAsync();
        await ops.ApplyVmRouteAsync(tenantId, vmRoute.Id);

        AppDeployment deployment = db.AppDeployments.First(d => d.Id == deploymentId);
        AppRoute appRoute = new()
        {
            Id = Guid.NewGuid(), AppId = deployment.AppId, Hostname = "billing.example.com", IsEnabled = true, IsManaged = true,
        };
        AppDeploymentRoute dr = new()
        {
            Id = Guid.NewGuid(), AppRouteId = appRoute.Id, AppDeploymentId = deploymentId,
            ServiceName = "billing-api", ServicePort = 80, IsEnabled = true,
        };
        db.Set<AppRoute>().Add(appRoute);
        db.Set<AppDeploymentRoute>().Add(dr);
        await db.SaveChangesAsync();
        applied.Clear();

        (await ops.ApplyDeploymentRouteAsync(tenantId, dr.Id)).IsSuccess.Should().BeTrue();

        string gateway = applied.Last(m => m.Contains(Document("Gateway")));
        gateway.Should().Contain("billing.example.com").And.Contain("vm.example.com");
    }

    /// <summary>
    /// The other Gateway rebuild: re-applying a component's routes (installing Grafana, say) regenerates
    /// the same Gateway from a second place. It must keep the VM's listener and label its namespace — and
    /// must not render the VM's HTTPRoute itself, which it would put in the component's namespace.
    /// </summary>
    [Fact]
    public async Task Re_applying_component_routes_keeps_the_vms_listener_without_rendering_its_route()
    {
        await SeedAsync();
        await PublishHttpAsync();
        Guid istio = db.ClusterComponents.Single(c => c.ClusterId == clusterId).Id;
        db.ExternalRoutes.Add(new ExternalRoute
        {
            Id = Guid.NewGuid(), ComponentId = istio, Hostname = "grafana.example.com",
            ServiceName = "grafana", ServicePort = 80, ClusterIssuerName = "letsencrypt-prod",
        });
        await db.SaveChangesAsync();

        ComponentLifecycleService lifecycle = TestServices.BuildLifecycle(testDb, k8s.Object);
        HelmExecutionResult result = await lifecycle.ApplyExternalRoutesAsync(tenantId, istio);

        result.Success.Should().BeTrue(result.Output);
        string everything = string.Join("\n---\n", applied);
        applied.Last(m => m.Contains(Document("Gateway"))).Should()
            .Contain("grafana.example.com").And.Contain("vm.example.com");
        patched.Should().Contain(("namespace", "vms"));
        everything.Should().NotContain("name: web-01-web",
            "the VM's HTTPRoute belongs to ApplyVmRouteAsync, in the VM's own namespace");
    }

    [Fact]
    public async Task Withdrawing_a_hostname_removes_its_route_service_and_listener()
    {
        await SeedAsync();
        VmRoute vmRoute = await PublishHttpAsync();
        await ops.ApplyVmRouteAsync(tenantId, vmRoute.Id);

        // A second hostname, so the Gateway is regenerated rather than left as the last route.
        VmRoute other = await routes.AddAsync(tenantId, clusterId, "vms", "web-02", new VmRouteRequest
        {
            Kind = VmRouteKind.Http, VmPort = 80, Hostname = "other.example.com", ClusterIssuerName = "letsencrypt-prod"
        });
        applied.Clear();

        KubernetesOperationResult<string> result = await ops.WithdrawVmRouteAsync(tenantId, vmRoute.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        deleted.Should().Contain(("httproute.gateway.networking.k8s.io", ExternalRouteService.ToListenerName("vm.example.com") + "-route", "vms"));
        deleted.Should().Contain(("service", "web-01-web", "vms"));
        deleted.Should().Contain(("destinationrule.networking.istio.io", "entkube-disable-mtls-web-01-web", "istio-system"));

        string gateway = applied.Last(m => m.Contains(Document("Gateway")));
        gateway.Should().Contain("other.example.com").And.NotContain("vm.example.com");

        db.ChangeTracker.Clear();
        db.VmRoutes.Select(r => r.Id).Should().Equal(other.Id);
    }

    [Fact]
    public async Task A_second_port_on_the_same_vm_keeps_the_service_and_its_first_port()
    {
        await SeedAsync();
        VmRoute first = await PublishHttpAsync();
        VmRoute second = await PublishHttpAsync("admin.example.com", 8080);
        await ops.ApplyVmRouteAsync(tenantId, first.Id);
        applied.Clear();

        await ops.ApplyVmRouteAsync(tenantId, second.Id);
        applied.Single(m => m.Contains(Document("Service"))).Should().Contain("http-80").And.Contain("http-8080");

        applied.Clear();
        await ops.WithdrawVmRouteAsync(tenantId, second.Id);

        deleted.Should().NotContain(d => d.Kind == "service", "the first port still needs the Service");
        applied.Single(m => m.Contains(Document("Service"))).Should().Contain("http-80").And.NotContain("http-8080");
    }

    // ──────── TCP ────────

    [Fact]
    public async Task Publishing_a_tcp_port_adds_a_listener_and_a_tcproute_to_the_vm()
    {
        await SeedAsync();
        VmRoute route = await PublishTcpAsync();
        k8s.Setup(x => x.GetJsonAsync($"gateway/{ExternalRouteService.L4GatewayName}", It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"status":{"addresses":[{"type":"IPAddress","value":"203.0.113.9"}]}}""");

        KubernetesOperationResult<string> result = await ops.ApplyVmRouteAsync(tenantId, route.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Data.Should().Be("203.0.113.9:2222");

        applied.Should().Contain(m => m.Contains(Document("Gateway")) && m.Contains(ExternalRouteService.L4GatewayName)
            && m.Contains("port: 2222"));
        string tcpRoute = applied.Single(m => m.Contains(Document("TCPRoute")));
        tcpRoute.Should().Contain("namespace: vms").And.Contain("name: web-01-tcp").And.Contain("port: 22");
        applied.Should().Contain(m => m.Contains(Document("PeerAuthentication")) && m.Contains("namespace: vms"));
        applied.Single(m => m.Contains(Document("Service"))).Should().Contain("name: tcp-22");
    }

    [Fact]
    public async Task Deleting_the_last_app_tcp_route_keeps_the_l4_gateway_while_a_vm_uses_it()
    {
        await SeedAsync();
        VmRoute vmRoute = await PublishTcpAsync();
        await ops.ApplyVmRouteAsync(tenantId, vmRoute.Id);

        AppDeployment deployment = db.AppDeployments.First(d => d.Id == deploymentId);
        AppL4Route appRoute = new()
        {
            Id = Guid.NewGuid(), AppId = deployment.AppId, AppDeploymentId = deploymentId, Protocol = L4Protocol.Tcp,
            ExternalPort = 5432, ServiceName = "billing-db", ServicePort = 5432,
            GatewayName = ExternalRouteService.L4GatewayName, GatewayNamespace = "istio-system",
        };
        db.AppL4Routes.Add(appRoute);
        await db.SaveChangesAsync();
        applied.Clear();
        deleted.Clear();

        (await ops.DeleteL4RouteFromClusterAsync(tenantId, appRoute.Id)).IsSuccess.Should().BeTrue();

        deleted.Should().NotContain(d => d.Kind == "gateway.gateway.networking.k8s.io",
            "deleting the Gateway releases its LoadBalancer address, and the VM's SSH port is on it");
        applied.Last(m => m.Contains(Document("Gateway"))).Should().Contain("port: 2222").And.NotContain("port: 5432");
    }

    [Fact]
    public async Task Tcp_ports_are_refused_on_a_traefik_cluster_and_nothing_is_sent()
    {
        await SeedAsync(gateway: "traefik");
        VmRoute route = await PublishTcpAsync();

        KubernetesOperationResult<string> result = await ops.ApplyVmRouteAsync(tenantId, route.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Istio");
        applied.Should().BeEmpty();
    }

    // ──────── Refusals ────────

    [Fact]
    public async Task A_vm_without_the_selector_label_is_refused_before_anything_is_sent()
    {
        await SeedAsync();
        VmHasLabel(false);
        VmRoute route = await PublishHttpAsync();

        KubernetesOperationResult<string> result = await ops.ApplyVmRouteAsync(tenantId, route.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubevirt.io/vm: web-01");
        applied.Should().BeEmpty("a Service that selects nothing would publish a port that answers nothing");
    }

    [Fact]
    public async Task Another_tenant_can_neither_publish_nor_withdraw()
    {
        await SeedAsync();
        Guid owner = tenantId;
        VmRoute route = await PublishHttpAsync();
        await SeedAsync();   // a second tenant, with a cluster of its own

        (await ops.ApplyVmRouteAsync(tenantId, route.Id)).IsSuccess.Should().BeFalse();
        (await ops.WithdrawVmRouteAsync(tenantId, route.Id)).IsSuccess.Should().BeFalse();

        applied.Should().BeEmpty();
        deleted.Should().BeEmpty();
        db.ChangeTracker.Clear();
        db.VmRoutes.Should().ContainSingle(r => r.TenantId == owner);
    }

    [Fact]
    public async Task A_route_cannot_be_added_on_another_tenants_cluster()
    {
        await SeedAsync();
        Guid ownersCluster = clusterId;
        await SeedAsync();

        Func<Task> add = () => routes.AddAsync(tenantId, ownersCluster, "vms", "web-01", new VmRouteRequest
        {
            Kind = VmRouteKind.Tcp, VmPort = 22, ExternalPort = 2222
        });

        await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not this tenant's*");
    }

    // ──────── Who owns a hostname or a port ────────

    [Fact]
    public async Task A_hostname_already_served_on_the_cluster_is_refused_whoever_serves_it()
    {
        await SeedAsync();
        await PublishHttpAsync("taken.example.com");

        AppDeployment deployment = db.AppDeployments.First(d => d.Id == deploymentId);
        AppRoute appRoute = new()
        {
            Id = Guid.NewGuid(), AppId = deployment.AppId, Hostname = "app.example.com", IsEnabled = true, IsManaged = true,
        };
        db.Set<AppRoute>().Add(appRoute);
        db.Set<AppDeploymentRoute>().Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(), AppRouteId = appRoute.Id, AppDeploymentId = deploymentId,
            ServiceName = "billing-api", ServicePort = 80, IsEnabled = true,
        });
        Guid istio = db.ClusterComponents.Single(c => c.ClusterId == clusterId).Id;
        db.ExternalRoutes.Add(new ExternalRoute { Id = Guid.NewGuid(), ComponentId = istio, Hostname = "grafana.example.com" });
        await db.SaveChangesAsync();

        foreach (string host in new[] { "taken.example.com", "app.example.com", "GRAFANA.example.com" })
        {
            Func<Task> add = () => PublishHttpAsync(host);
            await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already in use*", host);
        }
    }

    [Fact]
    public async Task A_tcp_port_is_one_listener_whether_an_app_or_a_vm_claims_it()
    {
        await SeedAsync();
        await PublishTcpAsync(external: 2222);

        Func<Task> vmTwice = () => PublishTcpAsync(external: 2222, port: 3389);
        await vmTwice.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2222*");

        AppDeployment deployment = db.AppDeployments.First(d => d.Id == deploymentId);
        AppL4RouteService appL4 = new(testDb.Factory, new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<AppL4RouteService>.Instance);
        Func<Task> appClaims = () => appL4.AddRouteAsync(tenantId, deployment.AppId, deploymentId,
            new AppL4RouteRequest { Protocol = L4Protocol.Tcp, ExternalPort = 2222, ServiceName = "billing-db", ServicePort = 5432 });

        await appClaims.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2222*",
            "the reverse check: an app may not take a port a VM already publishes");
    }

    private AppRouteService AppRoutes() =>
        new(testDb.Factory, new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory), NullLogger<AppRouteService>.Instance);

    [Fact]
    public async Task An_app_route_may_not_bring_a_vms_hostname_onto_its_cluster()
    {
        await SeedAsync();
        await PublishHttpAsync("vm.example.com");
        AppDeployment deployment = db.AppDeployments.First(d => d.Id == deploymentId);

        // The app route on its own is fine: it belongs to no cluster yet.
        AppRoute appRoute = await AppRoutes().AddRouteAsync(tenantId, deployment.AppId,
            new AppRouteRequest { Hostname = "vm.example.com", ClusterIssuerName = "letsencrypt-prod" });

        Func<Task> link = () => AppRoutes().AddDeploymentRouteAsync(tenantId, appRoute.Id, deploymentId,
            new AppDeploymentRouteRequest { ServiceName = "billing-api" });

        await link.Should().ThrowAsync<InvalidOperationException>().WithMessage("*published by a virtual machine*");
    }

    [Fact]
    public async Task A_linked_app_route_may_not_be_renamed_onto_a_vms_hostname()
    {
        await SeedAsync();
        await PublishHttpAsync("vm.example.com");
        AppDeployment deployment = db.AppDeployments.First(d => d.Id == deploymentId);
        AppRoute appRoute = await AppRoutes().AddRouteAsync(tenantId, deployment.AppId,
            new AppRouteRequest { Hostname = "billing.example.com", ClusterIssuerName = "letsencrypt-prod" });
        await AppRoutes().AddDeploymentRouteAsync(tenantId, appRoute.Id, deploymentId,
            new AppDeploymentRouteRequest { ServiceName = "billing-api" });

        Func<Task> rename = () => AppRoutes().UpdateRouteAsync(tenantId, appRoute.Id,
            new AppRouteRequest { Hostname = "vm.example.com", ClusterIssuerName = "letsencrypt-prod" });

        await rename.Should().ThrowAsync<InvalidOperationException>().WithMessage("*published by a virtual machine*");

        // And an edit that keeps the hostname is not refused by it.
        await AppRoutes().UpdateRouteAsync(tenantId, appRoute.Id,
            new AppRouteRequest { Hostname = "billing.example.com", ClusterIssuerName = "letsencrypt-prod" });
    }

    [Fact]
    public async Task A_component_route_may_not_take_a_vms_hostname()
    {
        await SeedAsync();
        await PublishHttpAsync("vm.example.com");
        Guid istio = db.ClusterComponents.Single(c => c.ClusterId == clusterId).Id;

        ExternalRouteService external = new(testDb.Factory, new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<ExternalRouteService>.Instance);
        Func<Task> add = () => external.AddRouteAsync(tenantId, istio,
            new ExternalRouteRequest { Hostname = "vm.example.com", ClusterIssuerName = "letsencrypt-prod" });

        await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already in use*");
    }

    [Theory]
    [InlineData(VmRouteKind.Http, 80, "not a host", "letsencrypt", null, "hostname")]
    [InlineData(VmRouteKind.Http, 80, "vm.example.com", "", null, "ClusterIssuer")]
    [InlineData(VmRouteKind.Http, 0, "vm.example.com", "letsencrypt", null, "VM port")]
    [InlineData(VmRouteKind.Tcp, 22, null, null, 15021, "Istio gateway itself")]
    [InlineData(VmRouteKind.Tcp, 22, null, null, 70000, "external port")]
    public void A_request_the_gateway_could_not_serve_is_refused_with_a_sentence(
        VmRouteKind kind, int vmPort, string? host, string? issuer, int? external, string mentions)
    {
        Action validate = () => VmRouteService.Validate(new VmRouteRequest
        {
            Kind = kind, VmPort = vmPort, Hostname = host, ClusterIssuerName = issuer, ExternalPort = external
        });

        validate.Should().Throw<ArgumentException>().WithMessage($"*{mentions}*");
    }
}
