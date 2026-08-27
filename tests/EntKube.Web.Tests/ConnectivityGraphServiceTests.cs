using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for ConnectivityGraphService — the least-privilege connectivity model:
/// inference of exposed ports/edges from existing config, the pre-apply analyzer
/// that predicts breakage, and CRUD for declared intent.
/// </summary>
public class ConnectivityGraphServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    /// <summary>An empty `kubectl get services -o json` payload — the cluster has no Services.</summary>
    private const string NoLiveServices = """{"items":[]}""";

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly IDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly ConnectivityGraphService sut;

    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid envId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();

    public ConnectivityGraphServiceTests()
    {
        // Mirrors production: the graph resolves cluster.Kubeconfig from the vault via the
        // interceptor, which is what lets it verify routed Services against the live cluster.
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        dbFactory = testDb.Factory;

        // By default the cluster answers "no Services" — tests that need one say so.
        SetLiveServices(NoLiveServices);

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "ConnCo", Slug = "connco" };
        db.Tenants.Add(tenant);
        db.Environments.Add(new Data.Environment { Id = envId, TenantId = tenant.Id, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId,
            TenantId = tenant.Id,
            EnvironmentId = envId,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com"
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenant.Id, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });
        db.SaveChanges();

        VaultService vault = testDb.CreateVaultService();
        vault.InitializeVaultAsync(tenant.Id).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, tenant.Id, clusterId, TestKubeconfig.Valid).GetAwaiter().GetResult();

        sut = new ConnectivityGraphService(dbFactory, k8s.Object, NullLogger<ConnectivityGraphService>.Instance);
    }

    /// <summary>Makes `kubectl get services` return <paramref name="json"/> for any namespace.</summary>
    private void SetLiveServices(string json) =>
        k8s.Setup(f => f.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

    private AppDeployment SeedDeployment(string ns = "billing")
    {
        AppDeployment dep = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Name = "billing-deploy",
            Type = DeploymentType.Yaml,
            EnvironmentId = envId,
            ClusterId = clusterId,
            Namespace = ns
        };
        db.AppDeployments.Add(dep);
        db.SaveChanges();
        return dep;
    }

    private void SeedServiceManifest(Guid deploymentId, string name, int port, string? appProtocol = null)
    {
        string protoLine = appProtocol is null ? "" : $"\n    appProtocol: {appProtocol}";
        db.DeploymentManifests.Add(new DeploymentManifest
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Kind = "Service",
            Name = name,
            SortOrder = 40,
            YamlContent = $"""
                apiVersion: v1
                kind: Service
                metadata:
                  name: {name}
                  namespace: billing
                spec:
                  selector:
                    app: {name}
                  ports:
                  - name: http
                    port: {port}
                    targetPort: 8080{protoLine}
                """
        });
        db.SaveChanges();
    }

    private void SeedRoute(Guid deploymentId, string serviceName, int servicePort, bool enabled = true)
    {
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = $"{serviceName}.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = deploymentId,
            ServiceName = serviceName,
            ServicePort = servicePort,
            IsEnabled = enabled
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task BuildGraph_InfersExposedPortFromServiceManifest()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80, appProtocol: "http");

        ConnectivityGraph graph = await sut.BuildGraphAsync(appId, envId);

        graph.ExposedPorts.Should().ContainSingle();
        ServicePortNode p = graph.ExposedPorts[0];
        p.ServiceName.Should().Be("billing-svc");
        p.Port.Should().Be(80);
        p.TargetPort.Should().Be(8080);
        p.AppProtocol.Should().Be("http");
        p.Source.Should().Be(ConnectivitySource.Inferred);
    }

    [Fact]
    public async Task Analyze_RouteToUnexposedService_FlagsBrokenDependencyError()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);

        // A route that targets a service/port that is NOT exposed.
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = "billing.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = dep.Id,
            ServiceName = "does-not-exist",
            ServicePort = 9000
        });
        db.SaveChanges();

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(appId, envId);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Error &&
            f.Category == "Broken dependency" &&
            f.Title.Contains("does-not-exist:9000"));
    }

    [Fact]
    public async Task Analyze_DenyAllWithDatabaseBinding_FlagsEgressPolicyConflict()
    {
        AppDeployment dep = SeedDeployment();
        db.DatabaseBindings.Add(new DatabaseBinding
        {
            Id = Guid.NewGuid(),
            AppDeploymentId = dep.Id,
            KubernetesSecretName = "billing-db"
        });
        db.AppNetworkPolicies.Add(new AppNetworkPolicy
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            EnvironmentId = envId,
            Name = "deny-all",
            PolicyType = AppNetworkPolicyType.DenyAll
        });
        db.SaveChanges();

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(appId, envId);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Error &&
            f.Category == "Policy conflict" &&
            f.Detail.Contains("PostgreSQL"));
    }

    [Fact]
    public async Task Analyze_ConsistentConfig_ReturnsNoErrors()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);

        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = "billing.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = dep.Id,
            ServiceName = "billing-svc",
            ServicePort = 80
        });
        db.SaveChanges();

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(appId, envId);

        findings.Should().NotContain(f => f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public async Task AddRule_ThenGet_RoundTripsAsDeclared()
    {
        Guid peerAppId = Guid.NewGuid();
        db.Apps.Add(new App { Id = peerAppId, CustomerId = customerId, Name = "worker" });
        db.SaveChanges();

        await sut.AddRuleAsync(new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            Direction = ConnectivityDirection.Egress,
            PeerType = ConnectivityPeerType.App,
            PeerAppId = peerAppId,
            Port = 8080,
            Protocol = L4Protocol.Tcp
        });

        List<ConnectivityRule> rules = await sut.GetRulesAsync(appId, envId);

        rules.Should().ContainSingle();
        rules[0].Source.Should().Be(ConnectivitySource.Declared);
        rules[0].PeerApp!.Name.Should().Be("worker");
    }

    [Fact]
    public async Task GenerateNetworkPolicies_ProducesDefaultDenyDnsAndIngressAllow()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = "billing.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = dep.Id,
            ServiceName = "billing-svc",
            ServicePort = 80
        });
        db.SaveChanges();

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(appId, envId);

        plans.Should().ContainSingle();
        ConnectivityPolicyPlan plan = plans[0];
        plan.Namespace.Should().Be("billing");
        plan.Policies.Select(p => p.Name).Should().Contain(new[]
        {
            "billing-api-default-deny", "billing-api-allow-dns", "billing-api-allow-ingress"
        });

        string denyYaml = plan.Policies.Single(p => p.Name.EndsWith("default-deny")).Yaml;
        denyYaml.Should().Contain("policyTypes:").And.Contain("- Ingress").And.Contain("- Egress");
        denyYaml.Should().Contain("podSelector: {}");

        string ingressYaml = plan.Policies.Single(p => p.Name.EndsWith("allow-ingress")).Yaml;
        ingressYaml.Should().Contain("kubernetes.io/metadata.name: istio-system");
        // The Service is 80 → targetPort 8080; NetworkPolicy must match the pod's port (8080), not 80.
        ingressYaml.Should().Contain("port: 8080");
        ingressYaml.Should().NotContain("port: 80\n");

        string dnsYaml = plan.Policies.Single(p => p.Name.EndsWith("allow-dns")).Yaml;
        dnsYaml.Should().Contain("port: 53");
    }

    [Fact]
    public async Task GenerateNetworkPolicies_ExternalDependency_ProducesUnenforceableWarning()
    {
        SeedDeployment();
        await sut.AddExternalDependencyAsync(new ExternalDependency
        {
            AppId = appId,
            EnvironmentId = envId,
            Host = "api.stripe.com",
            Port = 443,
            Tls = true
        });

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(appId, envId);

        plans.Should().ContainSingle();
        plans[0].Warnings.Should().Contain(w => w.Contains("api.stripe.com"));
    }

    [Fact]
    public async Task GenerateNetworkPolicies_ExternalDep_WithIstio_ProducesServiceEntryNotWarning()
    {
        SeedDeployment();
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = "istio-base",
            ComponentType = "HelmChart",
            Status = ComponentStatus.Installed
        });
        db.SaveChanges();
        await sut.AddExternalDependencyAsync(new ExternalDependency
        {
            AppId = appId,
            EnvironmentId = envId,
            Host = "api.stripe.com",
            Port = 443,
            Tls = true
        });

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(appId, envId);

        ConnectivityPolicyPlan plan = plans.Single();
        plan.HasIstio.Should().BeTrue();
        GeneratedPolicy se = plan.Policies.Single(p => p.Kind == "ServiceEntry");
        se.Yaml.Should().Contain("kind: ServiceEntry")
            .And.Contain("- api.stripe.com")
            .And.Contain("resolution: DNS")
            .And.Contain("protocol: TLS");
        plan.Warnings.Should().NotContain(w => w.Contains("api.stripe.com"));
    }

    [Fact]
    public async Task GenerateNetworkPolicies_WithIstioAndRoute_ProducesAuthorizationPolicy()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = "istiod",
            ComponentType = "HelmChart",
            Status = ComponentStatus.Installed
        });
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = "billing.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = dep.Id,
            ServiceName = "billing-svc",
            ServicePort = 80
        });
        db.SaveChanges();

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(appId, envId);

        ConnectivityPolicyPlan plan = plans.Single();
        GeneratedPolicy authz = plan.Policies.Single(p => p.Kind == "AuthorizationPolicy");
        authz.Yaml.Should().Contain("kind: AuthorizationPolicy")
            .And.Contain("action: ALLOW")
            .And.Contain("namespaces:")
            .And.Contain("- istio-system")
            // AuthZ matches the pod's container port (targetPort 8080), not the Service port 80.
            .And.Contain("- \"8080\"")
            // workload selector taken from the Service's spec.selector (app: billing-svc)
            .And.Contain("app: billing-svc");
        plan.Notes.Should().Contain(n => n.Contains("sidecar"));
    }

    [Fact]
    public async Task GenerateNetworkPolicies_WithoutIstio_NoAuthorizationPolicy()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = "billing.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = dep.Id,
            ServiceName = "billing-svc",
            ServicePort = 80
        });
        db.SaveChanges();

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(appId, envId);

        plans.Single().Policies.Should().NotContain(p => p.Kind == "AuthorizationPolicy");
    }

    [Fact]
    public async Task GenerateNetworkPolicies_DeclaredCidrEgress_ProducesIpBlock()
    {
        SeedDeployment();
        await sut.AddRuleAsync(new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            Direction = ConnectivityDirection.Egress,
            PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "10.20.0.0/16",
            Port = 5432,
            Protocol = L4Protocol.Tcp
        });

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(appId, envId);

        string egressYaml = plans[0].Policies.Single(p => p.Name.EndsWith("allow-egress")).Yaml;
        egressYaml.Should().Contain("ipBlock:").And.Contain("cidr: 10.20.0.0/16");
        egressYaml.Should().Contain("port: 5432");
    }

    [Fact]
    public async Task AddRule_AppPeerWithoutPeerApp_Throws()
    {
        Func<Task> act = () => sut.AddRuleAsync(new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            PeerType = ConnectivityPeerType.App,
            PeerAppId = null
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeletingApp_ReferencedAsPeer_SucceedsOnlyAfterClearingRules()
    {
        Guid peerAppId = Guid.NewGuid();
        db.Apps.Add(new App { Id = peerAppId, CustomerId = customerId, Name = "worker" });
        db.SaveChanges();
        await sut.AddRuleAsync(new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            PeerType = ConnectivityPeerType.App,
            PeerAppId = peerAppId
        });

        // Removing the peer app directly violates the Restrict FK on ConnectivityRule.PeerApp.
        using (ApplicationDbContext c1 = dbFactory.CreateDbContext())
        {
            c1.Apps.Remove((await c1.Apps.FindAsync(peerAppId))!);
            Func<Task> bad = () => c1.SaveChangesAsync();
            await bad.Should().ThrowAsync<DbUpdateException>();
        }

        // What DeleteAppAsync now does first: clear rules referencing the app as a peer.
        using (ApplicationDbContext c2 = dbFactory.CreateDbContext())
        {
            await c2.ConnectivityRules.Where(r => r.PeerAppId == peerAppId).ExecuteDeleteAsync();
            c2.Apps.Remove((await c2.Apps.FindAsync(peerAppId))!);
            Func<Task> ok = () => c2.SaveChangesAsync();
            await ok.Should().NotThrowAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public async Task AddRule_OutOfRangePort_Throws(int port)
    {
        Func<Task> act = () => sut.AddRuleAsync(new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            PeerType = ConnectivityPeerType.Namespace,
            PeerNamespace = "other",
            Port = port
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddRule_InvalidNamespaceOrSelector_Throws()
    {
        // A namespace value that would break/inject YAML.
        Func<Task> badNs = () => sut.AddRuleAsync(new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            PeerType = ConnectivityPeerType.Namespace,
            PeerNamespace = "foo: bar"
        });
        await badNs.Should().ThrowAsync<InvalidOperationException>();

        // A selector value containing a newline (injection attempt).
        Func<Task> badSel = () => sut.AddRuleAsync(new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            PeerType = ConnectivityPeerType.Selector,
            PeerSelector = "{\"app\":\"api\\n        - namespaceSelector: {}\"}"
        });
        await badSel.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddExternalDependency_InvalidHost_Throws()
    {
        Func<Task> act = () => sut.AddExternalDependencyAsync(new ExternalDependency
        {
            AppId = appId,
            EnvironmentId = envId,
            Host = "bad host\nwith newline",
            Port = 443
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task BuildGraph_InClusterDatabaseBinding_IsInternalEgress()
    {
        AppDeployment dep = SeedDeployment();
        db.DatabaseBindings.Add(new DatabaseBinding
        {
            Id = Guid.NewGuid(),
            AppDeploymentId = dep.Id,
            KubernetesSecretName = "billing-db" // no registered id → in-cluster (CNPG/default)
        });
        db.SaveChanges();

        ConnectivityGraph graph = await sut.BuildGraphAsync(appId, envId);

        graph.InternalEgress.Should().Contain(e => e.PeerLabel == "PostgreSQL");
        graph.ExternalEgress.Should().NotContain(e => e.PeerLabel == "PostgreSQL");
    }

    [Fact]
    public async Task Analyze_DisabledRoute_NotFlaggedAsBroken()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = "billing.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = dep.Id,
            ServiceName = "does-not-exist",
            ServicePort = 9000,
            IsEnabled = false // disabled → generator ignores it, so analyzer must too
        });
        db.SaveChanges();

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(appId, envId);

        findings.Should().NotContain(f => f.Category == "Broken dependency");
    }

    [Fact]
    public async Task GenerateNetworkPolicies_SelectorlessServiceWithIstio_SkipsAuthzWithWarning()
    {
        AppDeployment dep = SeedDeployment();
        // A Service with ports but NO spec.selector (e.g. headless/manual endpoints).
        db.DeploymentManifests.Add(new DeploymentManifest
        {
            Id = Guid.NewGuid(),
            DeploymentId = dep.Id,
            Kind = "Service",
            Name = "billing-svc",
            SortOrder = 40,
            YamlContent = """
                apiVersion: v1
                kind: Service
                metadata:
                  name: billing-svc
                  namespace: billing
                spec:
                  ports:
                  - name: http
                    port: 80
                    targetPort: 8080
                """
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = "istiod",
            ComponentType = "HelmChart",
            Status = ComponentStatus.Installed
        });
        AppRoute route = new() { Id = Guid.NewGuid(), AppId = appId, Hostname = "billing.example.com" };
        db.AppRoutes.Add(route);
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = Guid.NewGuid(),
            AppRouteId = route.Id,
            AppDeploymentId = dep.Id,
            ServiceName = "billing-svc",
            ServicePort = 80
        });
        db.SaveChanges();

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(appId, envId);

        ConnectivityPolicyPlan plan = plans.Single();
        plan.Policies.Should().NotContain(p => p.Kind == "AuthorizationPolicy");
        plan.Warnings.Should().Contain(w => w.Contains("no pod selector"));
    }

    [Fact]
    public async Task BuildGraph_RoutedServiceWithoutManifest_IsResolvedFromTheLiveCluster()
    {
        // A Helm/Manual/Git deployment: the Service exists in the cluster but no
        // DeploymentManifest row declares it.
        AppDeployment dep = SeedDeployment();
        SeedRoute(dep.Id, "billing-svc", 80);
        SetLiveServices("""
            {
              "items": [
                {
                  "metadata": { "name": "billing-svc", "namespace": "billing" },
                  "spec": {
                    "selector": { "app": "billing" },
                    "ports": [
                      { "name": "http", "port": 80, "targetPort": 8080, "protocol": "TCP" }
                    ]
                  }
                }
              ]
            }
            """);

        ConnectivityGraph graph = await sut.BuildGraphAsync(appId, envId);

        ServicePortNode p = graph.ExposedPorts.Should().ContainSingle().Subject;
        p.ServiceName.Should().Be("billing-svc");
        p.Port.Should().Be(80);
        p.TargetPort.Should().Be(8080);
        p.AppProtocol.Should().Be("http");
        p.Selector.Should().Contain("app", "billing");
        p.FromCluster.Should().BeTrue();
        graph.LiveLookupFailed.Should().BeFalse();

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(graph, appId, envId);
        findings.Should().NotContain(f => f.Category == "Broken dependency");
    }

    [Fact]
    public async Task Analyze_ClusterUnreachable_DowngradesUnknownBackendToWarning()
    {
        AppDeployment dep = SeedDeployment();
        SeedRoute(dep.Id, "billing-svc", 80);
        k8s.Setup(f => f.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dial tcp: i/o timeout"));

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(appId, envId);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Warning &&
            f.Category == "Broken dependency" &&
            f.Title.Contains("can't be verified"));
        findings.Should().NotContain(f => f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public async Task BuildGraph_ServiceManifestPresent_DoesNotQueryTheCluster()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);
        SeedRoute(dep.Id, "billing-svc", 80);

        await sut.BuildGraphAsync(appId, envId);

        k8s.Verify(f => f.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }
}
