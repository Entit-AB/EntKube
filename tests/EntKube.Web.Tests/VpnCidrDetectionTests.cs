using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// CIDR auto-detection, after it stopped writing a kubeconfig to a temp file and running
/// <c>kubectl get … -o jsonpath=…</c> six times.
///
/// <para>The jsonpath expressions were the reason this had no tests: every source was a format
/// string handed to a process, so the only thing assertable was the string. The parsing is in
/// C# now, which is what these exercise — plus the thing that matters more, that a tenant who
/// does not own the cluster reaches it at all.</para>
/// </summary>
public class VpnCidrDetectionTests : IDisposable
{
    private static readonly byte[] RootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb = new(RootKey);
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly Guid ourTenant = Guid.NewGuid();
    private Guid ourCluster;

    public VpnCidrDetectionTests()
    {
        VaultService vault = testDb.CreateVaultService();
        using ApplicationDbContext db = testDb.CreateContext();

        db.Tenants.Add(new Tenant { Id = ourTenant, Name = "vpnco", Slug = $"vpnco-{ourTenant:N}" });
        Guid environmentId = Guid.NewGuid();
        ourCluster = Guid.NewGuid();
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = ourTenant, Name = "prod" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = ourCluster, TenantId = ourTenant, EnvironmentId = environmentId,
            Name = "vpn-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.SaveChanges();

        testDb.SeedKubeconfigAsync(vault, ourTenant, ourCluster, TestKubeconfig.Valid)
            .GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private VpnService Build()
        => new(testDb.Factory, testDb.CreateVaultService(),
            TestServices.BuildLifecycle(testDb, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object));

    // ── Authorization ─────────────────────────────────────────────────────────

    /// <summary>
    /// The page used to hand this method a <c>KubernetesCluster</c> it had found with a local
    /// <c>FirstOrDefault</c> over the rows it happened to be rendering, and the method read the
    /// credential off whatever arrived. It takes an id now, so a stranger reaches nothing.
    /// </summary>
    [Fact]
    public async Task Another_tenant_detects_nothing_and_does_not_reach_the_cluster()
    {
        (string? pod, string? service) =
            await Build().DetectClusterCidrsAsync(Guid.NewGuid(), ourCluster);

        pod.Should().BeNull();
        service.Should().BeNull();
        k8s.Verify(f => f.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The owner reads — or the refusal above proves nothing.</summary>
    [Fact]
    public async Task The_owner_reads_the_control_plane_flags()
    {
        k8s.Setup(f => f.GetJsonAsync("pods", "kube-system", It.IsAny<string>(),
                "component=kube-controller-manager", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PodWithCommand("--cluster-cidr=10.244.0.0/16"));
        k8s.Setup(f => f.GetJsonAsync("pods", "kube-system", It.IsAny<string>(),
                "component=kube-apiserver", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PodWithCommand("--service-cluster-ip-range=10.96.0.0/12"));

        (string? pod, string? service) =
            await Build().DetectClusterCidrsAsync(ourTenant, ourCluster);

        pod.Should().Be("10.244.0.0/16");
        service.Should().Be("10.96.0.0/12");

        // The credential the service never saw is the one the cluster is reached with.
        k8s.Verify(f => f.GetJsonAsync("pods", "kube-system", TestKubeconfig.Valid,
            "component=kube-controller-manager", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A cluster that answers nothing useful still returns a pair rather than throwing — the
    /// operator types the CIDRs in, which is what every <c>catch { }</c> in the method is for.
    /// </summary>
    [Fact]
    public async Task A_cluster_that_answers_nothing_detects_nothing()
    {
        k8s.Setup(f => f.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dial tcp: i/o timeout"));

        (string? pod, string? service) =
            await Build().DetectClusterCidrsAsync(ourTenant, ourCluster);

        pod.Should().BeNull();
        service.Should().BeNull();
    }

    private static string PodWithCommand(params string[] args)
    {
        string command = string.Join(",", args.Prepend("kube-binary").Select(a => $"\"{a}\""));
        return "{\"items\":[{\"spec\":{\"containers\":[{\"command\":[" + command + "]}]}}]}";
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_flag_is_read_whether_it_carries_its_value_or_precedes_it()
    {
        VpnService.ReadFlagFromPodCommand(
                PodWithCommand("--cluster-cidr=10.42.0.0/16"), "--cluster-cidr")
            .Should().Be("10.42.0.0/16");

        VpnService.ReadFlagFromPodCommand(
                PodWithCommand("--cluster-cidr", "10.42.0.0/16"), "--cluster-cidr")
            .Should().Be("10.42.0.0/16");
    }

    /// <summary>
    /// A selector that matches no pod is the normal case on a managed cluster, where the control
    /// plane is not visible at all. Indexing <c>items[0]</c> on the empty array it returns throws
    /// <c>ArgumentOutOfRangeException</c>, which no null-conditional chain catches.
    /// </summary>
    [Fact]
    public void An_empty_pod_list_is_not_an_exception()
    {
        VpnService.ReadFlagFromPodCommand("""{"items":[]}""", "--cluster-cidr").Should().BeNull();
        VpnService.ReadFlagFromPodCommand("""{"items":[{"spec":{"containers":[]}}]}""", "--cluster-cidr")
            .Should().BeNull();
        VpnService.ReadFlagFromPodCommand("", "--cluster-cidr").Should().BeNull();
    }

    [Fact]
    public void A_flag_that_is_not_present_reads_as_null()
    {
        VpnService.ReadFlagFromPodCommand(PodWithCommand("--bind-address=0.0.0.0"), "--cluster-cidr")
            .Should().BeNull();
    }

    [Fact]
    public void A_config_map_key_is_read_from_data()
    {
        const string json = """{"data":{"config.conf":"clusterCIDR: 10.42.0.0/16\n"}}""";

        VpnService.ReadConfigMapKey(json, "config.conf").Should().Contain("10.42.0.0/16");
        VpnService.ReadConfigMapKey(json, "ClusterConfiguration").Should().BeNull();
    }

    /// <summary>
    /// Sources 5 and 6 come out of one node list now; the jsonpath version fetched the same list
    /// twice to take one field out of it each time.
    /// </summary>
    [Fact]
    public void The_node_list_yields_the_pod_supernet_and_k3s_service_cidr()
    {
        const string nodes = """
            {"items":[
              {"metadata":{"annotations":{"k3s.io/node-args":"[\"server\",\"--service-cidr\",\"10.43.0.0/16\"]"}},
               "spec":{"podCIDR":"10.42.0.0/24"}},
              {"metadata":{"annotations":{}},"spec":{"podCIDR":"10.42.1.0/24"}},
              {"metadata":{"annotations":{}},"spec":{"podCIDR":"10.42.2.0/24"}}
            ]}
            """;

        (string? pod, string? service) = VpnService.ReadCidrsFromNodes(nodes);

        pod.Should().Be("10.42.0.0/22", "the minimum CIDR covering all three assigned node subnets");
        service.Should().Be("10.43.0.0/16");
    }

    /// <summary>
    /// ⚠️ The spelling this used to be unable to read. The old regex matched
    /// <c>--service-cidr</c> followed by one of <c>= \ "</c>, and in a JSON-encoded arg array the
    /// separator is <c>","</c> — so the standard form was invisible and only the <c>=</c> form
    /// ever matched. Both are asserted here so neither can regress to the other's cost.
    /// </summary>
    [Fact]
    public void K3s_server_args_are_read_in_both_spellings()
    {
        const string separate = """
            {"items":[{"metadata":{"annotations":
              {"k3s.io/node-args":"[\"server\",\"--service-cidr\",\"10.43.0.0/16\"]"}},"spec":{}}]}
            """;
        const string joined = """
            {"items":[{"metadata":{"annotations":
              {"k3s.io/node-args":"[\"server\",\"--service-cidr=10.43.0.0/16\"]"}},"spec":{}}]}
            """;

        VpnService.ReadCidrsFromNodes(separate).ServiceCidr.Should().Be("10.43.0.0/16");
        VpnService.ReadCidrsFromNodes(joined).ServiceCidr.Should().Be("10.43.0.0/16");
    }

    /// <summary>
    /// Nodes with no assigned subnet are the state of a cluster whose CNI has not finished
    /// starting, and every field here is optional in the API.
    /// </summary>
    [Fact]
    public void A_node_list_with_nothing_assigned_yields_nothing()
    {
        VpnService.ReadCidrsFromNodes("""{"items":[{"metadata":{},"spec":{}}]}""")
            .Should().Be((null, null));
        VpnService.ReadCidrsFromNodes("""{"items":[]}""").Should().Be((null, null));
        VpnService.ReadCidrsFromNodes("{}").Should().Be((null, null));
        VpnService.ReadCidrsFromNodes("").Should().Be((null, null));
    }
}
