using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// One operator action, one acknowledgment — for switching headscale to TLS passthrough, the last
/// of the multi-dialog buttons.
///
/// <para>The switch is six changes to the cluster: the certificate copied out of the cert-manager
/// namespace, the ConfigMap patched with the TLS paths, the Deployment re-applied, a rollout
/// forced so the frozen ConfigMap mount is re-read, the Service moved to 443, and the old
/// HTTPRoute deleted so the TLSRoute can take its name — then the routes re-applied at the end.
/// One press raised a dialog for each.</para>
///
/// <para><b>Everything the switch reads is faked here and nothing it writes is.</b> The reads are
/// branched on the resource being asked for rather than on call order, because the order is an
/// implementation detail of the switch and not something a fixture should pin.</para>
/// </summary>
public class HeadscaleTlsSwitchOneAckTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
        (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();

    private int gatedChanges;

    private Guid tenantId, clusterId, envId, componentId;

    public HeadscaleTlsSwitchOneAckTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        // Reads, never gated. Branching on the resource asked for: the cert the switch copies, and
        // a ConfigMap that does NOT already carry tls_cert_path, so the patch really runs.
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string resource, string ns, string kubeconfig, string _, CancellationToken _) =>
            {
                if (resource.StartsWith("secret/", StringComparison.Ordinal))
                {
                    // base64 of "cert" and "key" — the switch only moves these across, never reads them.
                    return Task.FromResult("""{"data":{"tls.crt":"Y2VydA==","tls.key":"a2V5"}}""");
                }

                if (resource.StartsWith("configmap/", StringComparison.Ordinal))
                {
                    return Task.FromResult("""{"data":{"config.yaml":"server_url: https://vpn.example.com\n"}}""");
                }

                return Task.FromResult("{}");
            });
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
    /// Makes the mocked factory gate its writes the way the real one does. The gate lives inside
    /// the real <see cref="KubernetesClientFactory"/>, so without this the action reaches the gate
    /// zero times and a dialog count proves nothing.
    /// </summary>
    private void GateEveryWrite()
    {
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = manifest,
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });

        k8s.Setup(x => x.PatchStrategicAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string kind, string name, string ns, string patch, string kubeconfig,
                            CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Patch,
                    Kind = kind, Name = name, Namespace = ns, Patch = patch,
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });

        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string kind, string name, string ns, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Delete,
                    // Composite rather than kind/name, so the gate's delete diff does not shell
                    // out to a real `kubectl get` and skip it as a no-op. The collapsing is what
                    // is under test, not the gate's own no-op detection.
                    Manifest = $"# delete {kind}/{name} in {ns}\n",
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });
    }

    private HeadscaleService NewService() =>
        new(testDb.Factory, k8s.Object, vault,
            new ExternalRouteService(testDb.Factory,
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
                NullLogger<ExternalRouteService>.Instance),
            // Real: the switch finishes by re-applying the routes through it, and that apply is
            // inside the same operation. A null here would throw at the last step.
            TestServices.BuildLifecycle(testDb, k8s.Object),
            httpClientFactory: null!,
            catalog: new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            clusterClients: new ClusterClientFactory(testDb.Factory, k8s.Object),
            gate: gate);

    private async Task SeedAsync()
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        envId = Guid.NewGuid();
        componentId = Guid.NewGuid();

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
            Id = componentId, ClusterId = clusterId, Name = "headscale",
            ComponentType = "Manifest", HelmChartName = "headscale",
            ReleaseName = "headscale", Namespace = "headscale",
            Status = ComponentStatus.Installed,
        });
        await db.SaveChangesAsync();

        // The route the switch moves to Passthrough, and whose old HTTPRoute it deletes first.
        db.Set<ExternalRoute>().Add(new ExternalRoute
        {
            Id = Guid.NewGuid(), ComponentId = componentId,
            Hostname = "vpn.example.com", ServiceName = "headscale", ServicePort = 8080,
            TlsMode = TlsMode.ClusterIssuer, ClusterIssuerName = "letsencrypt",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await vault.SetComponentSecretAsync(tenantId, componentId, "server-url", "https://vpn.example.com");
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    [Fact]
    public async Task Switching_to_tls_passthrough_asks_the_operator_exactly_once()
    {
        await SeedAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        await NewService().SwitchToTlsPassthroughAsync(tenantId, clusterId);

        gatedChanges.Should().BeGreaterThan(2,
            "the certificate, the ConfigMap, the Deployment, the rollout, the Service and the old "
            + "HTTPRoute are all changes to the cluster");
        sink.Calls.Should().Be(1, "one press, one dialog");
    }

    /// <summary>
    /// And the window closes with the action. Without this a fix that suppressed the question for
    /// the rest of the circuit would pass the test above while retiring the gate for everything
    /// the operator did next.
    /// </summary>
    [Fact]
    public async Task A_second_switch_asks_again()
    {
        await SeedAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        HeadscaleService sut = NewService();

        await sut.SwitchToTlsPassthroughAsync(tenantId, clusterId);
        sink.Calls.Should().Be(1);

        await sut.SwitchToTlsPassthroughAsync(tenantId, clusterId);
        sink.Calls.Should().Be(2, "a second press is a second decision");
    }
}
