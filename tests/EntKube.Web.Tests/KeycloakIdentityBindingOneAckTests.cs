using System.Net;
using System.Net.Http;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Moq;
using Moq.Protected;

namespace EntKube.Web.Tests;

/// <summary>
/// One operator action, one acknowledgment — for pushing an OIDC client secret into an app's
/// namespace. Syncing an identity binding ensures the namespace and then writes the credentials
/// into it: two gated changes for one press.
///
/// <para><b>Why this one needed a harness of its own.</b> Every other service in this series
/// reaches a cluster and nothing else, so a mocked <see cref="IKubernetesClientFactory"/> was the
/// whole fixture. This one first has to ASK KEYCLOAK for the client secret — an admin token POST,
/// then a GET of the secret — before it touches the cluster at all. Without that conversation the
/// method throws before reaching the part under test, which is why the wrap shipped a PR later
/// than the rest.</para>
/// </summary>
public class KeycloakIdentityBindingOneAckTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
        (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();

    private int gatedChanges;

    private Guid tenantId, clusterId, envId, componentId, configId, realmId, bindingId;

    public KeycloakIdentityBindingOneAckTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class CountingSink : EntKube.Web.Services.ClusterChanges.IClusterChangeAckSink
    {
        public int Calls { get; private set; }

        public Task<EntKube.Web.Services.ClusterChanges.ClusterChangeDecision> RequestAsync(
            EntKube.Web.Services.ClusterChanges.PlannedClusterChange change,
            EntKube.Web.Services.ClusterChanges.ClusterChangeDiff diff,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(
                EntKube.Web.Services.ClusterChanges.ClusterChangeDecision.Acknowledged);
        }
    }

    /// <summary>
    /// A Keycloak that answers the two requests this path makes: the admin token, then the
    /// client secret. Branching on the URL rather than on call order, so the token cache
    /// changing how many times each is asked for cannot break the fixture.
    /// </summary>
    private static IHttpClientFactory KeycloakAnswering(string clientSecret)
    {
        Mock<HttpMessageHandler> handler = new();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage request, CancellationToken _) =>
            {
                string body = request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                    ? """{"access_token":"admin-token","expires_in":300}"""
                    : $$"""{"value":"{{clientSecret}}"}""";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            });

        Mock<IHttpClientFactory> factory = new();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object));
        return factory.Object;
    }

    /// <summary>
    /// Makes the mocked factory gate its writes the way the real one does — the gate lives inside
    /// the real <see cref="KubernetesClientFactory"/>, so without this the action reaches the gate
    /// zero times and a dialog count proves nothing.
    /// </summary>
    private void GateEveryWrite()
    {
        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string ns, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n",
                    Kubeconfig = kubeconfig,
                }, ct);
            });

        k8s.Setup(x => x.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string name, string ns, IReadOnlyDictionary<string, string?> keys,
                            IReadOnlyDictionary<string, string>? annotations, string kubeconfig,
                            string? summary, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: {name}\n",
                    Kubeconfig = kubeconfig,
                    Summary = summary,
                }, ct);
                return string.Empty;
            });

        k8s.Setup(x => x.GetResourceJsonAsync("secret", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HelmExecutionResult { Success = true, Output = "{\"metadata\":{}}" });
    }

    private KeycloakService NewService() =>
        new(testDb.Factory, vault, KeycloakAnswering("oidc-client-secret"),
            new CnpgService(testDb.Factory, vault,
                new ClusterClientFactory(testDb.Factory, k8s.Object),
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
                new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory), TestServices.NewGate()),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            gate);

    private async Task SeedAsync()
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        envId = Guid.NewGuid();
        componentId = Guid.NewGuid();
        configId = Guid.NewGuid();
        realmId = Guid.NewGuid();
        bindingId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "keycloakx",
            ComponentType = "HelmChart", HelmChartName = "keycloakx", Namespace = "identity",
            Status = ComponentStatus.Installed,
        });
        await db.SaveChangesAsync();

        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), deploymentId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "shop" });
        await db.SaveChangesAsync();
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "shop-prod",
            Type = DeploymentType.Manual, ClusterId = clusterId, EnvironmentId = envId,
            Namespace = "shop",
        });
        await db.SaveChangesAsync();

        db.Set<KeycloakComponentConfig>().Add(new KeycloakComponentConfig
        {
            Id = configId, TenantId = tenantId, ClusterComponentId = componentId,
            AdminUrl = "https://keycloak.example.com", AdminUsername = "admin",
        });
        db.Set<KeycloakRealm>().Add(new KeycloakRealm
        {
            Id = realmId, TenantId = tenantId, KeycloakComponentConfigId = configId,
            RealmName = "shop", DisplayName = "Shop", LinkedAppId = appId,
        });
        db.Set<IdentityBinding>().Add(new IdentityBinding
        {
            Id = bindingId, KeycloakRealmId = realmId, AppDeploymentId = deploymentId,
            ClientId = "shop-web", ClientUuid = Guid.NewGuid().ToString(),
            KubernetesSecretName = "shop-env",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await vault.SetComponentSecretAsync(tenantId, componentId, "KC_BOOTSTRAP_ADMIN_PASSWORD", "admin-pw");
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    [Fact]
    public async Task Syncing_an_identity_binding_asks_the_operator_exactly_once()
    {
        await SeedAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        await NewService().SyncIdentityBindingAsync(tenantId, bindingId);

        gatedChanges.Should().BeGreaterThan(1,
            "the namespace and the credential write are both gated changes");
        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    /// <summary>
    /// And the window closes with the action. Without this a fix that suppressed the question for
    /// the rest of the circuit would pass the test above while retiring the gate for everything
    /// the operator did next.
    /// </summary>
    [Fact]
    public async Task A_second_sync_asks_again()
    {
        await SeedAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        KeycloakService sut = NewService();

        await sut.SyncIdentityBindingAsync(tenantId, bindingId);
        sink.Calls.Should().Be(1);

        await sut.SyncIdentityBindingAsync(tenantId, bindingId);
        sink.Calls.Should().Be(2, "a second press is a second decision");
    }
}
