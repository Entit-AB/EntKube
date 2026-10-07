using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What <see cref="HeadscaleService"/> sends to a cluster.
///
/// <para><b>Why these, and why now.</b> This is 1,085 lines over thirty public methods with
/// <b>no tests at all</b>, and seventeen places where it takes a cluster's kubeconfig out of the
/// row. The credential-custody work (docs/decomposition.md §4.0.1) is going to rewrite every one
/// of them. The three services converted before this were safe because 24, 202 and 59 existing
/// tests caught mistakes as they were made; zero is not something to lean on, so the coverage
/// comes first — the pattern set by MongoService in #129.</para>
///
/// <para>Unlike <c>KubernetesOperationsService</c>, coverage genuinely <em>can</em> come first
/// here: this service asks <see cref="IKubernetesClientFactory"/> for everything and spawns no
/// process of its own, so a mock sees every call. That difference is worth stating because it is
/// what decides the order of the work, not the size of the file.</para>
///
/// <para>These assert on the manifest, the delete and the command — not on the database row. A
/// subnet router whose DaemonSet is no longer applied, or deleted under the wrong name, leaves
/// every row looking correct while the VPN quietly stops carrying traffic.</para>
/// </summary>
public class HeadscaleClusterCallTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly HeadscaleService sut;

    /// <summary>Every manifest handed to the cluster, in order.</summary>
    private readonly List<string> applied = [];

    /// <summary>Every delete asked of the cluster, as (kind, name, namespace).</summary>
    private readonly List<(string Kind, string Name, string Namespace)> deleted = [];

    /// <summary>Every in-pod command, as (pod, namespace, argv).</summary>
    private readonly List<(string Pod, string Namespace, string Argv)> commands = [];

    private Guid tenantId, clusterId, componentId;

    public HeadscaleClusterCallTests()
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

        sut = new HeadscaleService(
            testDb.Factory,
            k8s.Object,
            vault,
            new ExternalRouteService(testDb.Factory, NullLogger<ExternalRouteService>.Instance),
            // Not supplied: only EnsureExternalRouteAsync and SwitchToTlsPassthroughAsync reach
            // for these, and neither is exercised here. Null rather than a half-built graph, so a
            // test that strays into those paths fails loudly instead of passing for a bad reason.
            lifecycleService: null!,
            httpClientFactory: null!);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────── Fixture ────────

    /// <summary>
    /// A tenant with one cluster whose kubeconfig lives in the vault, as production stores it, and
    /// a headscale component on it.
    /// </summary>
    private async Task SeedAsync(bool withKubeconfig = true, bool istio = true)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        componentId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "headscale",
            ComponentType = "Helm", HelmChartName = "headscale", Namespace = "headscale",
            Status = Data.ComponentStatus.Installed,
        });

        // The installed ingress controller is what decides the gateway's namespace, and whether
        // the Istio-only paths run at all.
        db.ClusterComponents.Add(istio
            ? new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = clusterId, Name = "istio",
                ComponentType = "ServiceMesh", Namespace = "istio-system",
                Status = Data.ComponentStatus.Installed,
            }
            : new ClusterComponent
            {
                Id = Guid.NewGuid(), ClusterId = clusterId, Name = "traefik",
                ComponentType = "Ingress", HelmChartName = "traefik", Namespace = "traefik",
                Status = Data.ComponentStatus.Installed,
            });

        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }
    }

    /// <summary>Makes the headscale pod discoverable, as `kubectl get pods -l app=headscale` would.</summary>
    private void PodIsNamed(string podName) =>
        k8s.Setup(x => x.GetJsonAsync("pods", "headscale", It.IsAny<string>(), "app=headscale",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync($"{{\"items\":[{{\"metadata\":{{\"name\":\"{podName}\"}}}}]}}");

    // ════════════════════════════════════════════════════════════════
    //  The subnet router — what carries cluster traffic to VPN clients
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeploySubnetRouterAsync_AppliesADaemonSetAdvertisingTheServiceCidr()
    {
        await SeedAsync();

        await sut.DeploySubnetRouterAsync(clusterId, "https://vpn.example.com", "10.96.0.0/12");

        string manifest = applied.Should().ContainSingle().Subject;
        manifest.Should().Contain("kind: DaemonSet")
            .And.Contain("name: headscale-subnet-router")
            .And.Contain("namespace: headscale")
            // The CIDR and the server URL are the whole point of the router: without them it comes
            // up healthy and routes nothing.
            .And.Contain("10.96.0.0/12")
            .And.Contain("https://vpn.example.com");
    }

    [Fact]
    public async Task DeploySubnetRouterAsync_WithoutAKubeconfig_Refuses()
    {
        await SeedAsync(withKubeconfig: false);

        Func<Task> act = () => sut.DeploySubnetRouterAsync(clusterId, "https://vpn.example.com", "10.96.0.0/12");

        await act.Should().ThrowAsync<InvalidOperationException>();
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveSubnetRouterAsync_DeletesTheDaemonSetByItsOwnName()
    {
        await SeedAsync();

        await sut.RemoveSubnetRouterAsync(clusterId);

        deleted.Should().ContainSingle()
            .Which.Should().Be(("daemonset", "headscale-subnet-router", "headscale"));
    }

    [Fact]
    public async Task RemoveSubnetRouterAsync_WithoutAKubeconfig_IsSilentRatherThanThrowing()
    {
        await SeedAsync(withKubeconfig: false);

        // Deliberately unlike Deploy, which throws. Removal is called on teardown paths where a
        // cluster that can no longer be reached should not block the rest of the cleanup.
        Func<Task> act = () => sut.RemoveSubnetRouterAsync(clusterId);

        await act.Should().NotThrowAsync();
        deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task IsSubnetRouterRunningAsync_AsksForTheRoutersOwnLabel()
    {
        await SeedAsync();
        k8s.Setup(x => x.GetJsonAsync("daemonsets", "headscale", It.IsAny<string>(),
                "app=headscale-subnet-router", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[{\"metadata\":{\"name\":\"headscale-subnet-router\"}}]}");

        bool running = await sut.IsSubnetRouterRunningAsync(clusterId);

        running.Should().BeTrue();
    }

    [Fact]
    public async Task IsSubnetRouterRunningAsync_AnEmptyListIsNotRunning()
    {
        await SeedAsync();
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");

        (await sut.IsSubnetRouterRunningAsync(clusterId)).Should().BeFalse();
    }

    /// <summary>
    /// Pins a conflation rather than a feature. The method catches everything and answers false,
    /// so a cluster that cannot be reached is indistinguishable from one where the router is
    /// genuinely absent — and the UI reads that as "not running" either way.
    /// </summary>
    [Fact]
    public async Task IsSubnetRouterRunningAsync_AnUnreachableClusterAlsoReadsAsNotRunning()
    {
        await SeedAsync();
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): connection refused"));

        (await sut.IsSubnetRouterRunningAsync(clusterId)).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════
    //  Bootstrap: the API key that every other call needs
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GenerateApiKeyAsync_RunsTheKeyCommandInTheHeadscalePod()
    {
        await SeedAsync();
        PodIsNamed("headscale-7c9f8-abcde");

        k8s.Setup(x => x.RunCommandOnPodAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .Callback((string pod, string ns, IReadOnlyList<string> cmd, string _,
                       IReadOnlyDictionary<string, string>? _, CancellationToken _, int _, bool _) =>
                commands.Add((pod, ns, string.Join(' ', cmd))))
            .ReturnsAsync("abcdef0123456789");

        string key = await sut.GenerateApiKeyAsync(clusterId);

        key.Should().Be("abcdef0123456789");

        // The pod name comes from the cluster, not from a convention — a chart that names the pod
        // differently must still work.
        commands.Should().ContainSingle()
            .Which.Should().Be(("headscale-7c9f8-abcde", "headscale", "headscale apikeys create"));
    }

    [Fact]
    public async Task GenerateApiKeyAsync_IgnoresTheLogLinesHeadscaleWritesAroundTheKey()
    {
        await SeedAsync();
        PodIsNamed("headscale-1");

        // Real output: structured logs and a timestamped line on the same stream as the key.
        k8s.Setup(x => x.RunCommandOnPodAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync("""
                {"level":"info","msg":"opening database"}
                2026-10-07T09:14:22Z creating api key
                deadbeefcafe1234

                """);

        (await sut.GenerateApiKeyAsync(clusterId)).Should().Be("deadbeefcafe1234");
    }

    [Fact]
    public async Task GenerateApiKeyAsync_WithNoKeyInTheOutput_FailsLoudlyWithWhatItSaw()
    {
        await SeedAsync();
        PodIsNamed("headscale-1");

        k8s.Setup(x => x.RunCommandOnPodAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync("{\"level\":\"error\",\"msg\":\"database is locked\"}");

        // Returning "" here would store an empty API key in the vault and fail much later, on a
        // call that has nothing to do with bootstrap.
        Func<Task> act = () => sut.GenerateApiKeyAsync(clusterId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("database is locked");
    }

    [Fact]
    public async Task GenerateApiKeyAsync_WithNoHeadscalePod_SaysSoRatherThanExecNowhere()
    {
        await SeedAsync();
        k8s.Setup(x => x.GetJsonAsync("pods", "headscale", It.IsAny<string>(), "app=headscale",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");

        Func<Task> act = () => sut.GenerateApiKeyAsync(clusterId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("pod not found");
    }

    // ════════════════════════════════════════════════════════════════
    //  Making Tailscale's ts2021 handshake survive an Istio gateway
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnsureIstioPermissiveAsync_ForcesHttp11ForTheHeadscaleSniOnly()
    {
        await SeedAsync();
        await vault.SetComponentSecretAsync(tenantId, componentId, "server-url", "https://vpn.example.com");

        await sut.EnsureIstioPermissiveAsync(clusterId);

        // Tailscale's noise protocol needs an HTTP Upgrade, which HTTP/2 cannot express, so the
        // gateway must stop advertising h2 for this host.
        string alpn = applied.Should()
            .ContainSingle(m => m.Contains("entkube-headscale-h1-only")).Subject;
        alpn.Should().Contain("http/1.1").And.Contain("namespace: istio-system");

        // Scoped by SNI, which is the only thing keeping the patch off every other host on :443 —
        // the method's own comment calls this out as the blast radius.
        alpn.Should().Contain("sni: vpn.example.com");
    }

    [Fact]
    public async Task EnsureIstioPermissiveAsync_AppliesBothFiltersAndBothMeshPolicies()
    {
        await SeedAsync();
        await vault.SetComponentSecretAsync(tenantId, componentId, "server-url", "https://vpn.example.com");

        await sut.EnsureIstioPermissiveAsync(clusterId);

        // Four resources, and all four matter: either filter alone leaves the handshake broken,
        // and without the mesh pair the gateway cannot reach a sidecar-less headscale pod.
        applied.Should().HaveCount(4);
        applied.Should().Contain(m => m.Contains("entkube-headscale-h1-only"));
        applied.Should().Contain(m => m.Contains("entkube-headscale-ts2021"));
        applied.Should().Contain(m => m.Contains("kind: PeerAuthentication"));
        applied.Should().Contain(m => m.Contains("kind: DestinationRule"));
    }

    [Fact]
    public async Task EnsureIstioPermissiveAsync_OnATraefikCluster_SendsNothing()
    {
        await SeedAsync(istio: false);
        await vault.SetComponentSecretAsync(tenantId, componentId, "server-url", "https://vpn.example.com");

        await sut.EnsureIstioPermissiveAsync(clusterId);

        // EnvoyFilter and PeerAuthentication are Istio resources. Applying them where there is no
        // Istio would fail on the CRD, which is why the gateway class is checked first.
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureIstioPermissiveAsync_WithNoKnownServerUrl_SendsNothing()
    {
        await SeedAsync();

        // No server-url in the vault: there is no hostname to scope the SNI match to, and an
        // unscoped filter would strip h2 from every host on the gateway.
        await sut.EnsureIstioPermissiveAsync(clusterId);

        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureIstioPermissiveAsync_WithoutAKubeconfig_SendsNothing()
    {
        await SeedAsync(withKubeconfig: false);
        await vault.SetComponentSecretAsync(tenantId, componentId, "server-url", "https://vpn.example.com");

        await sut.EnsureIstioPermissiveAsync(clusterId);

        applied.Should().BeEmpty();
    }
}
