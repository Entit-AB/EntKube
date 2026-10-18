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

    private readonly Guid tenantId = Guid.NewGuid();
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

        Tenant tenant = new() { Id = tenantId, Name = "ConnCo", Slug = "connco" };
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

        // The real factory over the mocked kubectl wrapper: the service names a cluster by id and
        // the seam resolves the credential, so the tenant check these tests rely on is the
        // production one rather than a stub's.
        sut = new ConnectivityGraphService(
            dbFactory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<ConnectivityGraphService>.Instance);
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

        ConnectivityGraph graph = await sut.BuildGraphAsync(tenantId, appId, envId);

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

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(tenantId, appId, envId);

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

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(tenantId, appId, envId);

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

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(tenantId, appId, envId);

        findings.Should().NotContain(f => f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public async Task AddRule_ThenGet_RoundTripsAsDeclared()
    {
        Guid peerAppId = Guid.NewGuid();
        db.Apps.Add(new App { Id = peerAppId, CustomerId = customerId, Name = "worker" });
        db.SaveChanges();

        await sut.AddRuleAsync(tenantId, new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            Direction = ConnectivityDirection.Egress,
            PeerType = ConnectivityPeerType.App,
            PeerAppId = peerAppId,
            Port = 8080,
            Protocol = L4Protocol.Tcp
        });

        List<ConnectivityRule> rules = await sut.GetRulesAsync(tenantId, appId, envId);

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

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(tenantId, appId, envId);

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
        await sut.AddExternalDependencyAsync(tenantId, new ExternalDependency
        {
            AppId = appId,
            EnvironmentId = envId,
            Host = "api.stripe.com",
            Port = 443,
            Tls = true
        });

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(tenantId, appId, envId);

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
        await sut.AddExternalDependencyAsync(tenantId, new ExternalDependency
        {
            AppId = appId,
            EnvironmentId = envId,
            Host = "api.stripe.com",
            Port = 443,
            Tls = true
        });

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(tenantId, appId, envId);

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

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(tenantId, appId, envId);

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

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(tenantId, appId, envId);

        plans.Single().Policies.Should().NotContain(p => p.Kind == "AuthorizationPolicy");
    }

    [Fact]
    public async Task GenerateNetworkPolicies_DeclaredCidrEgress_ProducesIpBlock()
    {
        SeedDeployment();
        await sut.AddRuleAsync(tenantId, new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            Direction = ConnectivityDirection.Egress,
            PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "10.20.0.0/16",
            Port = 5432,
            Protocol = L4Protocol.Tcp
        });

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(tenantId, appId, envId);

        string egressYaml = plans[0].Policies.Single(p => p.Name.EndsWith("allow-egress")).Yaml;
        egressYaml.Should().Contain("ipBlock:").And.Contain("cidr: 10.20.0.0/16");
        egressYaml.Should().Contain("port: 5432");
    }

    [Fact]
    public async Task AddRule_AppPeerWithoutPeerApp_Throws()
    {
        Func<Task> act = () => sut.AddRuleAsync(tenantId, new ConnectivityRule
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
        await sut.AddRuleAsync(tenantId, new ConnectivityRule
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
        Func<Task> act = () => sut.AddRuleAsync(tenantId, new ConnectivityRule
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
        Func<Task> badNs = () => sut.AddRuleAsync(tenantId, new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            PeerType = ConnectivityPeerType.Namespace,
            PeerNamespace = "foo: bar"
        });
        await badNs.Should().ThrowAsync<InvalidOperationException>();

        // A selector value containing a newline (injection attempt).
        Func<Task> badSel = () => sut.AddRuleAsync(tenantId, new ConnectivityRule
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
        Func<Task> act = () => sut.AddExternalDependencyAsync(tenantId, new ExternalDependency
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

        ConnectivityGraph graph = await sut.BuildGraphAsync(tenantId, appId, envId);

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

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(tenantId, appId, envId);

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

        List<ConnectivityPolicyPlan> plans = await sut.GenerateNetworkPoliciesAsync(tenantId, appId, envId);

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

        ConnectivityGraph graph = await sut.BuildGraphAsync(tenantId, appId, envId);

        ServicePortNode p = graph.ExposedPorts.Should().ContainSingle().Subject;
        p.ServiceName.Should().Be("billing-svc");
        p.Port.Should().Be(80);
        p.TargetPort.Should().Be(8080);
        p.AppProtocol.Should().Be("http");
        p.Selector.Should().Contain("app", "billing");
        p.FromCluster.Should().BeTrue();
        graph.LiveLookupFailed.Should().BeFalse();

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(tenantId, graph, appId, envId);
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

        List<ConnectivityFinding> findings = await sut.AnalyzeAsync(tenantId, appId, envId);

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

        await sut.BuildGraphAsync(tenantId, appId, envId);

        k8s.Verify(f => f.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The graph's live half is read <em>as the asking tenant</em>. A stranger naming this app's
    /// cluster reaches nothing, and the graph says the live lookup failed rather than reporting the
    /// cluster has no Services — which would read as a fact about the cluster.
    /// </summary>
    [Fact]
    public async Task BuildGraph_AnotherTenant_DoesNotReachTheCluster()
    {
        AppDeployment dep = SeedDeployment();
        SeedRoute(dep.Id, "billing-svc", 80);

        ConnectivityGraph graph = await sut.BuildGraphAsync(Guid.NewGuid(), appId, envId);

        graph.LiveLookupFailed.Should().BeTrue();
        graph.ExposedPorts.Should().NotContain(p => p.FromCluster);
        k8s.Verify(f => f.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a tenant that does not own the cluster should not get as far as kubectl");
    }

    /// <summary>
    /// Applying is a change, so the tenant matters more here than anywhere else in this file. It
    /// used to be derived from the app and so was never a check at all.
    /// </summary>
    [Fact]
    public async Task ApplyNetworkPolicies_AnotherTenant_AppliesNothing()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);

        List<(string Target, bool Success, string Output)> results =
            await sut.ApplyNetworkPoliciesAsync(Guid.NewGuid(), appId, envId);

        results.Should().NotBeEmpty();
        results.Should().OnlyContain(r => !r.Success);
        results.Should().OnlyContain(r => r.Output.Contains("not this tenant's"));
        k8s.Verify(f => f.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The owner, by contrast, applies — or the refusal above proves nothing.</summary>
    [Fact]
    public async Task ApplyNetworkPolicies_TheOwner_Applies()
    {
        AppDeployment dep = SeedDeployment();
        SeedServiceManifest(dep.Id, "billing-svc", 80);

        List<(string Target, bool Success, string Output)> results =
            await sut.ApplyNetworkPoliciesAsync(tenantId, appId, envId);

        results.Should().OnlyContain(r => r.Success);
        results.Should().OnlyContain(r => r.Target == "prod-cluster/billing");
        // The credential the service never saw is the one the cluster is reached with.
        k8s.Verify(f => f.ApplyManifestAsync(It.IsAny<string>(), TestKubeconfig.Valid,
            It.IsAny<CancellationToken>()), Times.Once);
    }


    // ---- Somebody else's application ---------------------------------------------------

    /// <summary>
    /// A second tenant with their own customer, application, environment and one declared
    /// rule and external dependency. The returned ids are all a caller would need to have
    /// held to reach them.
    /// </summary>
    private (Guid Tenant, Guid App, Guid Env, Guid Rule, Guid Dependency) Stranger()
    {
        Guid t = Guid.NewGuid();
        Guid customer = Guid.NewGuid();
        Guid app = Guid.NewGuid();
        Guid sibling = Guid.NewGuid();
        Guid env = Guid.NewGuid();
        Guid cluster = Guid.NewGuid();
        Guid rule = Guid.NewGuid();
        Guid dep = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = t, Name = "Annan AB", Slug = $"annan-{t:N}" });
        db.Environments.Add(new Data.Environment { Id = env, TenantId = t, Name = "production" });
        db.Customers.Add(new Customer { Id = customer, TenantId = t, Name = "Annan" });
        db.Apps.Add(new App { Id = app, CustomerId = customer, Name = "journal-api" });

        // A SIBLING app under the same customer, or GetCandidatePeerAppsAsync returns nothing
        // even unscoped (it excludes the app itself) and the assertion passes against the bug.
        db.Apps.Add(new App { Id = sibling, CustomerId = customer, Name = "journal-worker" });

        // A cluster and a DEPLOYMENT, or BuildGraph has no namespaces and
        // GenerateNetworkPoliciesAsync bails at `namespaces.Count == 0` — so all three of those
        // assertions would be empty for want of data rather than for want of permission.
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster,
            TenantId = t,
            EnvironmentId = env,
            Name = "annan-cluster",
            ApiServerUrl = "https://annan.example.com",
        });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(),
            AppId = app,
            Name = "journal-deploy",
            Type = DeploymentType.Yaml,
            EnvironmentId = env,
            ClusterId = cluster,
            Namespace = "journal",
        });
        db.ConnectivityRules.Add(new ConnectivityRule
        {
            Id = rule,
            AppId = app,
            EnvironmentId = env,
            Direction = ConnectivityDirection.Ingress,
            PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "10.90.0.0/16",
            Port = 8443,
            Protocol = L4Protocol.Tcp,
            Source = ConnectivitySource.Declared,
        });
        db.ExternalDependencies.Add(new ExternalDependency
        {
            Id = dep,
            AppId = app,
            EnvironmentId = env,
            Host = "journal.inera.se",
            Port = 443,
            Tls = true,
            Source = ConnectivitySource.Declared,
        });
        db.SaveChanges();

        return (t, app, env, rule, dep);
    }

    /// <summary>
    /// Nothing about another tenant's application in another tenant's environment is
    /// readable by naming the pair: not the declared rules, not the external hosts it
    /// reaches, not its sibling applications' names, and not a generated policy set.
    /// </summary>
    [Fact]
    public async Task Another_tenants_connectivity_is_not_readable()
    {
        (Guid _, Guid theirApp, Guid theirEnv, Guid _, Guid _) = Stranger();

        // A positive first, so that every BeEmpty below is about the tenant and not about an
        // empty database.
        await sut.AddRuleAsync(tenantId, new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            Direction = ConnectivityDirection.Egress,
            PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "10.1.0.0/16",
            Port = 5432,
            Protocol = L4Protocol.Tcp,
        });
        (await sut.GetRulesAsync(tenantId, appId, envId)).Should().ContainSingle();

        (await sut.GetRulesAsync(tenantId, theirApp, theirEnv)).Should().BeEmpty();
        (await sut.GetExternalDependenciesAsync(tenantId, theirApp, theirEnv)).Should().BeEmpty();
        // Their customer has a sibling app, so an unscoped picker WOULD list "journal-worker".
        (await sut.GetCandidatePeerAppsAsync(tenantId, theirApp)).Should().BeEmpty();

        // They have a deployment in namespace "journal" on their own cluster, so an unscoped
        // graph WOULD carry a namespace and the generator WOULD get past namespaces.Count == 0.
        ConnectivityGraph graph = await sut.BuildGraphAsync(tenantId, theirApp, theirEnv);
        //
        // Namespaces is the assertion that carries this: without the guard BuildGraph reads
        // their AppDeployments and the list becomes ["journal"].
        graph.Namespaces.Should().BeEmpty();
        //
        // LiveLookupFailed, by contrast, is a SHAPE assertion and not proof the guard fired —
        // without the guard the cluster seam would refuse the live lookup on its own and set
        // this too. It is here because an empty graph that does not say so reads as a fact
        // about their application rather than as us not being allowed to look.
        graph.LiveLookupFailed.Should().BeTrue();

        (await sut.AnalyzeAsync(tenantId, theirApp, theirEnv)).Should().BeEmpty();
        (await sut.AnalyzeAsync(tenantId, graph, theirApp, theirEnv)).Should().BeEmpty();
        (await sut.GenerateNetworkPoliciesAsync(tenantId, theirApp, theirEnv)).Should().BeEmpty();
    }

    /// <summary>
    /// The scope is a <i>pair</i>, and each half has to be checked: a rule names an
    /// application and an environment, and a generated NetworkPolicy lands in the namespace
    /// the environment decides. One crossing per half, so neither can be deleted and leave
    /// the other covering for it.
    /// </summary>
    [Fact]
    public async Task The_scope_is_an_app_and_an_environment_and_both_are_checked()
    {
        (Guid _, Guid theirApp, Guid theirEnv, Guid _, Guid _) = Stranger();

        // A rule in EACH crossing, so each half of the guard has something it alone can refuse.
        // Without these the lists come back empty for want of data and both halves could be
        // deleted with the test still green.
        db.ConnectivityRules.Add(new ConnectivityRule
        {
            Id = Guid.NewGuid(), AppId = appId, EnvironmentId = theirEnv,
            Direction = ConnectivityDirection.Ingress, PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "10.70.0.0/16", Port = 1234, Protocol = L4Protocol.Tcp,
        });
        db.ConnectivityRules.Add(new ConnectivityRule
        {
            Id = Guid.NewGuid(), AppId = theirApp, EnvironmentId = envId,
            Direction = ConnectivityDirection.Ingress, PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "10.80.0.0/16", Port = 5678, Protocol = L4Protocol.Tcp,
        });
        db.SaveChanges();

        // Our application, their environment — only the environment half catches this.
        (await sut.GetRulesAsync(tenantId, appId, theirEnv)).Should().BeEmpty();

        // Their application, our environment — only the application half catches this.
        (await sut.GetRulesAsync(tenantId, theirApp, envId)).Should().BeEmpty();

        // And our own pair still works, or the two above prove nothing.
        (await sut.GetRulesAsync(tenantId, appId, envId)).Should().BeEmpty();
        db.ConnectivityRules.Add(new ConnectivityRule
        {
            Id = Guid.NewGuid(), AppId = appId, EnvironmentId = envId,
            Direction = ConnectivityDirection.Ingress, PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "10.1.0.0/16", Port = 9999, Protocol = L4Protocol.Tcp,
        });
        db.SaveChanges();
        (await sut.GetRulesAsync(tenantId, appId, envId)).Should().ContainSingle();
    }

    /// <summary>
    /// <b>A rule is an allow.</b> It is generated into a NetworkPolicy and, for an app peer,
    /// an AuthorizationPolicy — so writing one against an application by Id alone was opening
    /// a hole in whichever application the entity named, and deleting one was closing a hole
    /// somebody else was relying on.
    /// </summary>
    [Fact]
    public async Task Another_tenants_rule_cannot_be_added_or_deleted()
    {
        (Guid _, Guid theirApp, Guid theirEnv, Guid theirRule, Guid _) = Stranger();

        Func<Task> add = () => sut.AddRuleAsync(tenantId, new ConnectivityRule
        {
            AppId = theirApp,
            EnvironmentId = theirEnv,
            Direction = ConnectivityDirection.Ingress,
            PeerType = ConnectivityPeerType.Cidr,
            PeerCidr = "0.0.0.0/0",
            Port = 8443,
            Protocol = L4Protocol.Tcp,
        });

        await add.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("App or environment not found, or it is not this tenant's.");

        await sut.DeleteRuleAsync(tenantId, theirRule);

        using ApplicationDbContext check = dbFactory.CreateDbContext();
        (await check.ConnectivityRules.AnyAsync(r => r.Id == theirRule)).Should().BeTrue();
        (await check.ConnectivityRules.CountAsync(r => r.AppId == theirApp)).Should().Be(1);
    }

    /// <summary>
    /// <b>The peer, not only the subject.</b> An app-to-app rule names a second application
    /// and generates an AuthorizationPolicy principal for it, so a peer belonging to another
    /// tenant is an allow for their workload into ours. The picker only ever offers
    /// applications of the same customer, which is not a check — it is a convenience.
    /// </summary>
    [Fact]
    public async Task A_peer_app_from_another_tenant_is_refused()
    {
        (Guid _, Guid theirApp, Guid _, Guid _, Guid _) = Stranger();

        Func<Task> act = () => sut.AddRuleAsync(tenantId, new ConnectivityRule
        {
            AppId = appId,
            EnvironmentId = envId,
            Direction = ConnectivityDirection.Ingress,
            PeerType = ConnectivityPeerType.App,
            PeerAppId = theirApp,
            Port = 8080,
            Protocol = L4Protocol.Tcp,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Peer app not found, or it is not this tenant's.");

        (await sut.GetRulesAsync(tenantId, appId, envId)).Should().BeEmpty();
    }

    /// <summary>
    /// <b>An external dependency is an egress allow to a host of the caller's choosing</b> —
    /// an Istio ServiceEntry and a NetworkPolicy egress rule. Writing one against somebody
    /// else's application granted that application a route out to a host they never named.
    /// </summary>
    [Fact]
    public async Task Another_tenants_external_dependency_cannot_be_added_or_deleted()
    {
        (Guid _, Guid theirApp, Guid theirEnv, Guid _, Guid theirDep) = Stranger();

        Func<Task> add = () => sut.AddExternalDependencyAsync(tenantId, new ExternalDependency
        {
            AppId = theirApp,
            EnvironmentId = theirEnv,
            Host = "collect.example.net",
            Port = 443,
            Tls = true,
        });

        await add.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("App or environment not found, or it is not this tenant's.");

        await sut.DeleteExternalDependencyAsync(tenantId, theirDep);

        using ApplicationDbContext check = dbFactory.CreateDbContext();
        ExternalDependency theirs = await check.ExternalDependencies
            .AsNoTracking().FirstAsync(d => d.Id == theirDep);

        theirs.Host.Should().Be("journal.inera.se");
    }


    /// <summary>
    /// <b>Analyzing an already-built graph still has to check the pair it is handed.</b>
    ///
    /// <para>This overload exists so the topology view can analyse the graph it already has
    /// instead of rebuilding it — so it takes the graph as data and the application and
    /// environment as ids, and then reads <c>AppNetworkPolicies</c> and
    /// <c>ConnectivityRules</c> for that pair. Handing it our graph and somebody else's ids
    /// made it analyse their policy rows.</para>
    ///
    /// <para>⚠️ My first version of this passed the <em>refused</em> (empty) graph, and every
    /// finding needs graph content combined with their rows — so it came back empty whether
    /// the guard was there or not, and the clause sweep reported the guard as uncovered. The
    /// graph has to carry an edge for the crossing to be visible at all.</para>
    /// </summary>
    [Fact]
    public async Task Analyzing_an_already_built_graph_still_checks_the_pair_it_is_given()
    {
        (Guid _, Guid theirApp, Guid theirEnv, Guid _, Guid _) = Stranger();

        // Our graph, carrying an internal egress edge (a managed database binding).
        AppDeployment dep = SeedDeployment();
        db.DatabaseBindings.Add(new DatabaseBinding
        {
            Id = Guid.NewGuid(),
            AppDeploymentId = dep.Id,
            KubernetesSecretName = "billing-db",
        });

        // A deny-all policy on each pair. Against an egress edge that is a policy conflict,
        // which is the finding that shows whose rows were read.
        db.AppNetworkPolicies.Add(new AppNetworkPolicy
        {
            Id = Guid.NewGuid(), AppId = appId, EnvironmentId = envId,
            Name = "deny-all", PolicyType = AppNetworkPolicyType.DenyAll,
        });
        db.AppNetworkPolicies.Add(new AppNetworkPolicy
        {
            Id = Guid.NewGuid(), AppId = theirApp, EnvironmentId = theirEnv,
            Name = "deny-all", PolicyType = AppNetworkPolicyType.DenyAll,
        });
        db.SaveChanges();

        ConnectivityGraph ours = await sut.BuildGraphAsync(tenantId, appId, envId);
        ours.InternalEgress.Should().NotBeEmpty("the crossing is invisible without an edge");

        // Our own pair reports the conflict, so the refusal below is about the tenant and not
        // about a finding that cannot fire.
        (await sut.AnalyzeAsync(tenantId, ours, appId, envId))
            .Should().Contain(f => f.Category == "Policy conflict");

        // Their pair, our graph: nothing, because their policy rows are not ours to read.
        (await sut.AnalyzeAsync(tenantId, ours, theirApp, theirEnv))
            .Should().NotContain(f => f.Category == "Policy conflict");
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }
}
