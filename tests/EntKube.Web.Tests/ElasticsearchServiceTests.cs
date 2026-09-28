using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for ElasticsearchService — the ECK-backed search stack.
///
/// Three things are worth holding still here, and they are what these tests pin:
/// the node roles each tier gets (a hot tier without <c>data_content</c> means Kibana never
/// starts), the arithmetic that keeps a cluster from being asked for more than it has, and the
/// lifecycle policies, whose phases Elasticsearch rejects outright if they are out of order.
/// </summary>
public class ElasticsearchServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly Mock<IKubernetesClientFactory> k8s;
    private readonly ElasticsearchService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid k8sClusterId = Guid.NewGuid();

    public ElasticsearchServiceTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        k8s = new Mock<IKubernetesClientFactory>();
        sut = new ElasticsearchService(testDb.Factory, k8s.Object, NullLogger<ElasticsearchService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }

    // ──────── Helpers ────────

    private static ElasticsearchCluster SampleCluster() => new()
    {
        Id = Guid.NewGuid(),
        Name = "search",
        Namespace = "search",
        Version = "9.5.0",
        MasterCount = 3, MasterCpuRequest = "500m", MasterMemory = "2Gi", MasterStorageSize = "5Gi",
        HotCount = 2, HotCpuRequest = "1", HotMemory = "4Gi", HotStorageSize = "100Gi"
    };

    private async Task SeedClusterAsync()
    {
        Tenant tenant = new() { Id = tenantId, Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production" };
        db.Set<Data.Environment>().Add(env);

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = k8sClusterId,
            TenantId = tenantId,
            EnvironmentId = env.Id,
            Name = "prod",
            ApiServerUrl = "https://k8s.example.com"
        });

        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(testDb.CreateVaultService(), tenantId, k8sClusterId,
            TestKubeconfig.Valid);
    }

    private static YamlMappingNode Parse(string yaml)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode NodeSet(string manifest, string name) =>
        ((YamlSequenceNode)((YamlMappingNode)Parse(manifest)["spec"])["nodeSets"])
        .Children
        .Cast<YamlMappingNode>()
        .Single(n => ((YamlScalarNode)n["name"]).Value == name);

    private static string[] Roles(YamlMappingNode nodeSet) =>
        [.. ((YamlSequenceNode)((YamlMappingNode)nodeSet["config"])["node.roles"])
            .Children.Cast<YamlScalarNode>().Select(v => v.Value!)];

    // ──────── Manifest: topology ────────

    [Fact]
    public void Manifest_IsValidYaml_AndTargetsTheEckApi()
    {
        YamlMappingNode root = Parse(ElasticsearchService.BuildElasticsearchManifest(SampleCluster()));

        ((YamlScalarNode)root["apiVersion"]).Value.Should().Be("elasticsearch.k8s.elastic.co/v1");
        ((YamlScalarNode)root["kind"]).Value.Should().Be("Elasticsearch");
        ((YamlScalarNode)((YamlMappingNode)root["metadata"])["namespace"]).Value.Should().Be("search");
    }

    [Fact]
    public void Masters_HoldOnlyTheMasterRole()
    {
        string manifest = ElasticsearchService.BuildElasticsearchManifest(SampleCluster());

        // A master that also holds shards is the failure this whole topology exists to avoid.
        Roles(NodeSet(manifest, "master")).Should().Equal("master");
    }

    [Fact]
    public void HotTier_CarriesContentAndIngest_WhenThereIsNoDedicatedIngestTier()
    {
        string manifest = ElasticsearchService.BuildElasticsearchManifest(SampleCluster());

        // data_content is what Kibana's own indices land on; without it Kibana never goes green.
        Roles(NodeSet(manifest, "hot")).Should().BeEquivalentTo("data_hot", "data_content", "ingest");
    }

    [Fact]
    public void DedicatedIngestTier_TakesTheIngestRoleOffTheHotTier()
    {
        ElasticsearchCluster c = SampleCluster();
        c.IngestCount = 2;

        string manifest = ElasticsearchService.BuildElasticsearchManifest(c);

        Roles(NodeSet(manifest, "hot")).Should().BeEquivalentTo("data_hot", "data_content");
        Roles(NodeSet(manifest, "ingest")).Should().Equal("ingest");
    }

    [Fact]
    public void WarmAndColdTiers_AppearOnlyWhenTheyHaveNodes()
    {
        ElasticsearchCluster c = SampleCluster();
        c.WarmCount = 2;

        string manifest = ElasticsearchService.BuildElasticsearchManifest(c);
        YamlSequenceNode sets = (YamlSequenceNode)((YamlMappingNode)Parse(manifest)["spec"])["nodeSets"];

        sets.Children.Cast<YamlMappingNode>().Select(n => ((YamlScalarNode)n["name"]).Value)
            .Should().BeEquivalentTo("master", "hot", "warm");
        Roles(NodeSet(manifest, "warm")).Should().Equal("data_warm");
    }

    [Fact]
    public void SingleNode_GetsEveryRole_ByNotNamingAny()
    {
        ElasticsearchCluster c = SampleCluster();
        c.MasterCount = 1;
        c.HotCount = 0;
        c.MasterStorageSize = "50Gi";

        string manifest = ElasticsearchService.BuildElasticsearchManifest(c);
        YamlMappingNode only = NodeSet(manifest, "all");

        // Leaving node.roles unset is what makes one node a working cluster; naming ["master"]
        // there would give a node that can hold no data.
        only.Children.Should().NotContainKey(new YamlScalarNode("config"));
        ((YamlScalarNode)only["count"]).Value.Should().Be("1");
    }

    // ──────── Manifest: resources ────────

    [Fact]
    public void EveryTier_GetsAHeapOfHalfItsMemory()
    {
        string manifest = ElasticsearchService.BuildElasticsearchManifest(SampleCluster());
        YamlMappingNode hot = NodeSet(manifest, "hot");

        YamlMappingNode container = (YamlMappingNode)((YamlSequenceNode)
            ((YamlMappingNode)((YamlMappingNode)hot["podTemplate"])["spec"])["containers"])[0];
        YamlMappingNode env = (YamlMappingNode)((YamlSequenceNode)container["env"])[0];

        ((YamlScalarNode)env["name"]).Value.Should().Be("ES_JAVA_OPTS");
        ((YamlScalarNode)env["value"]).Value.Should().Be("-Xms2048m -Xmx2048m");
    }

    [Fact]
    public void MemoryRequestAndLimitAreEqual_SoTheHeapCannotOutgrowTheCgroup()
    {
        string manifest = ElasticsearchService.BuildElasticsearchManifest(SampleCluster());
        YamlMappingNode container = (YamlMappingNode)((YamlSequenceNode)
            ((YamlMappingNode)((YamlMappingNode)NodeSet(manifest, "hot")["podTemplate"])["spec"])["containers"])[0];
        YamlMappingNode resources = (YamlMappingNode)container["resources"];

        ((YamlScalarNode)((YamlMappingNode)resources["requests"])["memory"]).Value.Should().Be("4Gi");
        ((YamlScalarNode)((YamlMappingNode)resources["limits"])["memory"]).Value.Should().Be("4Gi");

        // No CPU limit: throttling a JVM mid-GC turns a slow query into a timing-out one.
        ((YamlMappingNode)resources["limits"]).Children.Should().NotContainKey(new YamlScalarNode("cpu"));
    }

    [Theory]
    [InlineData("1Gi", 512)]      // floor, not 512 exactly by halving — a 1Gi node halves to 512
    [InlineData("4Gi", 2048)]
    [InlineData("64Gi", 31 * 1024)]  // capped below the compressed-oops boundary
    [InlineData("512Mi", 512)]       // never below the floor
    public void Heap_IsHalfTheMemory_CappedAt31Gi(string memory, int expectedMb)
    {
        ElasticsearchService.HeapMbFor(memory).Should().Be(expectedMb);
    }

    [Fact]
    public void MmapCanBeTurnedOff_ForNodesWhereVmMaxMapCountCannotBeRaised()
    {
        ElasticsearchCluster c = SampleCluster();
        c.AllowMmap = false;

        string manifest = ElasticsearchService.BuildElasticsearchManifest(c);
        YamlMappingNode config = (YamlMappingNode)NodeSet(manifest, "hot")["config"];

        ((YamlScalarNode)config["node.store.allow_mmap"]).Value.Should().Be("false");
    }

    [Fact]
    public void StorageClassAppliesToEveryTier_WhenSet()
    {
        ElasticsearchCluster c = SampleCluster();
        c.StorageClass = "fast-ssd";
        c.WarmCount = 1;

        string manifest = ElasticsearchService.BuildElasticsearchManifest(c);

        foreach (string tier in new[] { "master", "hot", "warm" })
        {
            YamlMappingNode claim = (YamlMappingNode)((YamlSequenceNode)NodeSet(manifest, tier)["volumeClaimTemplates"])[0];
            ((YamlScalarNode)((YamlMappingNode)claim["spec"])["storageClassName"]).Value.Should().Be("fast-ssd");
        }
    }

    [Fact]
    public void Kibana_PinsNodeOldSpaceBelowItsContainerLimit()
    {
        ElasticsearchCluster c = SampleCluster();
        c.KibanaMemory = "1Gi";

        YamlMappingNode root = Parse(ElasticsearchService.BuildKibanaManifest(c));
        YamlMappingNode container = (YamlMappingNode)((YamlSequenceNode)
            ((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)root["spec"])["podTemplate"])["spec"])["containers"])[0];
        YamlMappingNode env = (YamlMappingNode)((YamlSequenceNode)container["env"])[0];

        ((YamlScalarNode)env["name"]).Value.Should().Be("NODE_OPTIONS");
        ((YamlScalarNode)env["value"]).Value.Should().Be("--max-old-space-size=768");
        ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)root["spec"])["elasticsearchRef"])["name"]).Value
            .Should().Be("search");
    }

    // ──────── Validation ────────

    [Fact]
    public void EvenMasterCounts_AreRefused()
    {
        ElasticsearchCluster c = SampleCluster();
        c.MasterCount = 2;

        Action act = () => ElasticsearchService.ValidateTopology(c);

        act.Should().Throw<InvalidOperationException>().WithMessage("*even quorum*");
    }

    [Fact]
    public void WarmTierWithoutAHotTier_IsRefused()
    {
        ElasticsearchCluster c = SampleCluster();
        c.HotCount = 0;
        c.WarmCount = 2;

        Action act = () => ElasticsearchService.ValidateTopology(c);

        act.Should().Throw<InvalidOperationException>().WithMessage("*hot tier is required*");
    }

    [Fact]
    public void MultipleMastersWithNoDataTier_IsRefused()
    {
        ElasticsearchCluster c = SampleCluster();
        c.HotCount = 0;

        Action act = () => ElasticsearchService.ValidateTopology(c);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no data tier*");
    }

    [Fact]
    public void TiersBelowOneGigabyte_AreRefused()
    {
        ElasticsearchCluster c = SampleCluster();
        c.HotMemory = "512Mi";

        Action act = () => ElasticsearchService.ValidateTopology(c);

        act.Should().Throw<InvalidOperationException>().WithMessage("*garbage collecting*");
    }

    [Fact]
    public void NamesLongerThanEckCanSuffix_AreRefused()
    {
        ElasticsearchCluster c = SampleCluster();
        c.Name = new string('a', 40);

        Action act = () => ElasticsearchService.ValidateTopology(c);

        act.Should().Throw<InvalidOperationException>().WithMessage("*36 characters*");
    }

    // ──────── Quantities and footprint ────────

    [Theory]
    [InlineData("2Gi", 2L * 1024 * 1024 * 1024)]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("1G", 1_000_000_000L)]
    [InlineData("1Ti", 1024L * 1024 * 1024 * 1024)]
    [InlineData("1048576", 1048576L)]
    public void MemoryQuantitiesParse(string quantity, long expected) =>
        ElasticsearchService.ParseMemoryBytes(quantity).Should().Be(expected);

    [Theory]
    [InlineData("500m", 500)]
    [InlineData("2", 2000)]
    [InlineData("1.5", 1500)]
    public void CpuQuantitiesParse(string quantity, long expected) =>
        ElasticsearchService.ParseCpuMilli(quantity).Should().Be(expected);

    [Fact]
    public void Footprint_SumsEveryTierAndKibana()
    {
        ElasticsearchCluster c = SampleCluster();
        c.KibanaEnabled = true;
        c.KibanaCount = 1;
        c.KibanaMemory = "1Gi";
        c.KibanaCpuRequest = "500m";

        ElasticsearchFootprint fp = ElasticsearchService.Footprint(c);

        fp.PodCount.Should().Be(6);                       // 3 masters + 2 hot + 1 kibana
        fp.MemoryBytesTotal.Should().Be(15L * 1024 * 1024 * 1024);   // 3×2 + 2×4 + 1
        fp.CpuMilliTotal.Should().Be(500 * 3 + 1000 * 2 + 500);
        fp.StorageBytesTotal.Should().Be((3L * 5 + 2L * 100) * 1024 * 1024 * 1024);
        fp.LargestPodMemoryBytes.Should().Be(4L * 1024 * 1024 * 1024);
    }

    // ──────── Capacity ────────

    private const string TwoNodes = """
        {"items":[
          {"metadata":{"name":"node-a"},"status":{"allocatable":{"cpu":"4","memory":"16Gi"}}},
          {"metadata":{"name":"node-b"},"status":{"allocatable":{"cpu":"4","memory":"16Gi"}}}
        ]}
        """;

    private const string NoPods = """{"items":[]}""";

    private void ArrangeCluster(string nodesJson, string podsJson)
    {
        k8s.Setup(x => x.GetJsonAllNamespacesAsync("nodes", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodesJson);
        k8s.Setup(x => x.GetJsonAllNamespacesAsync("pods", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(podsJson);
    }

    [Fact]
    public async Task ATopologyThatFits_IsNotBlocked()
    {
        await SeedClusterAsync();
        ArrangeCluster(TwoNodes, NoPods);

        ElasticsearchCapacityCheck check = await sut.CheckCapacityAsync(k8sClusterId, SampleCluster());

        check.CapacityKnown.Should().BeTrue();
        check.NodeCount.Should().Be(2);
        check.Fits.Should().BeTrue();
        check.AllocatableMemoryBytes.Should().Be(32L * 1024 * 1024 * 1024);
    }

    [Fact]
    public async Task ATopologyLargerThanTheCluster_IsBlockedWithTheNumbers()
    {
        await SeedClusterAsync();
        ArrangeCluster(TwoNodes, NoPods);

        ElasticsearchCluster c = SampleCluster();
        c.HotCount = 8;
        c.HotMemory = "8Gi";

        ElasticsearchCapacityCheck check = await sut.CheckCapacityAsync(k8sClusterId, c);

        check.Fits.Should().BeFalse();
        check.Blocking.Should().ContainSingle(b => b.Contains("more memory than the cluster has unallocated"));
    }

    [Fact]
    public async Task APodLargerThanAnyNode_IsBlockedEvenWhenTheTotalsLookFine()
    {
        await SeedClusterAsync();
        ArrangeCluster(TwoNodes, NoPods);

        // 24Gi across two 16Gi nodes: the sum fits the cluster, one pod fits no node.
        ElasticsearchCluster c = SampleCluster();
        c.MasterCount = 1;
        c.HotCount = 1;
        c.HotMemory = "24Gi";

        ElasticsearchCapacityCheck check = await sut.CheckCapacityAsync(k8sClusterId, c);

        check.Fits.Should().BeFalse();
        check.Blocking.Should().ContainSingle(b => b.Contains("would stay Pending"));
    }

    [Fact]
    public async Task ExistingPodsCountAgainstFreeCapacity()
    {
        await SeedClusterAsync();
        ArrangeCluster(TwoNodes, """
            {"items":[
              {"spec":{"nodeName":"node-a","containers":[{"resources":{"requests":{"cpu":"1","memory":"12Gi"}}}]},"status":{"phase":"Running"}},
              {"spec":{"nodeName":"node-b","containers":[{"resources":{"requests":{"cpu":"1","memory":"12Gi"}}}]},"status":{"phase":"Running"}},
              {"spec":{"nodeName":"node-b","containers":[{"resources":{"requests":{"memory":"4Gi"}}}]},"status":{"phase":"Succeeded"}}
            ]}
            """);

        ElasticsearchCapacityCheck check = await sut.CheckCapacityAsync(k8sClusterId, SampleCluster());

        // A finished pod holds nothing, so only the two running ones are charged.
        check.CommittedMemoryBytes.Should().Be(24L * 1024 * 1024 * 1024);
        check.FreeMemoryBytes.Should().Be(8L * 1024 * 1024 * 1024);
        check.Fits.Should().BeFalse();
    }

    [Fact]
    public async Task AnUpdateIsChargedOnlyForWhatItAdds()
    {
        await SeedClusterAsync();
        ArrangeCluster(TwoNodes, NoPods);

        ElasticsearchCluster current = SampleCluster();
        ElasticsearchCluster planned = SampleCluster();
        planned.HotCount = 3;   // one more 4Gi node

        ElasticsearchCapacityCheck check = await sut.CheckCapacityAsync(k8sClusterId, planned, current);

        check.DeltaMemoryBytes.Should().Be(4L * 1024 * 1024 * 1024);
        check.Fits.Should().BeTrue();
    }

    [Fact]
    public async Task CordonedNodesDoNotCount_TheirRoomIsNotSchedulable()
    {
        await SeedClusterAsync();
        ArrangeCluster("""
            {"items":[
              {"metadata":{"name":"node-a"},"spec":{"unschedulable":true},"status":{"allocatable":{"cpu":"4","memory":"16Gi"}}},
              {"metadata":{"name":"node-b"},"status":{"allocatable":{"cpu":"4","memory":"16Gi"}}}
            ]}
            """, NoPods);

        ElasticsearchCapacityCheck check = await sut.CheckCapacityAsync(k8sClusterId, SampleCluster());

        check.NodeCount.Should().Be(1);
        check.AllocatableMemoryBytes.Should().Be(16L * 1024 * 1024 * 1024);
    }

    [Fact]
    public async Task AnUnreachableCluster_WarnsRatherThanBlocks()
    {
        await SeedClusterAsync();
        k8s.Setup(x => x.GetJsonAllNamespacesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));

        ElasticsearchCapacityCheck check = await sut.CheckCapacityAsync(k8sClusterId, SampleCluster());

        check.Fits.Should().BeTrue();
        check.CapacityKnown.Should().BeFalse();
        check.Warnings.Should().ContainSingle(w => w.Contains("connection refused"));
    }

    [Fact]
    public async Task CreateRefusesATopologyThatCannotSchedule_UnlessAcknowledged()
    {
        await SeedClusterAsync();
        ArrangeCluster(TwoNodes, NoPods);

        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        c.HotCount = 8;
        c.HotMemory = "8Gi";

        Func<Task> act = () => sut.CreateClusterAsync(c);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*more memory than the cluster has unallocated*");
        (await db.ElasticsearchClusters.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateAppliesBothCrs_AndTracksTheCluster()
    {
        await SeedClusterAsync();
        ArrangeCluster(TwoNodes, NoPods);

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;

        await sut.CreateClusterAsync(c);

        applied.Should().HaveCount(2);
        applied[0].Should().Contain("kind: Elasticsearch");
        applied[1].Should().Contain("kind: Kibana");

        ElasticsearchCluster stored = await db.ElasticsearchClusters.SingleAsync();
        stored.Status.Should().Be(ElasticsearchClusterStatus.Creating);
        stored.LastError.Should().BeNull();
        k8s.Verify(x => x.EnsureNamespaceAsync("search", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────── Lifecycle policies ────────

    private static ElasticsearchIlmPolicy SamplePolicy() => new()
    {
        Name = "app-logs",
        IndexPattern = "logs-app-*",
        RolloverMaxPrimaryShardGb = 50,
        RolloverMaxAgeDays = 7,
        WarmAfterDays = 7,
        ColdAfterDays = 30,
        DeleteAfterDays = 90
    };

    [Fact]
    public void IlmPolicy_RendersEveryPhaseItWasGiven()
    {
        string json = ElasticsearchService.BuildIlmPolicyJson(SamplePolicy());
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement phases = doc.RootElement.GetProperty("policy").GetProperty("phases");

        phases.GetProperty("hot").GetProperty("actions").GetProperty("rollover")
            .GetProperty("max_primary_shard_size").GetString().Should().Be("50gb");
        phases.GetProperty("warm").GetProperty("min_age").GetString().Should().Be("7d");
        phases.GetProperty("cold").GetProperty("min_age").GetString().Should().Be("30d");
        phases.GetProperty("delete").GetProperty("min_age").GetString().Should().Be("90d");
    }

    [Fact]
    public void IlmPolicy_OmitsPhasesThatWereNotAskedFor()
    {
        ElasticsearchIlmPolicy p = SamplePolicy();
        p.WarmAfterDays = null;
        p.ColdAfterDays = null;
        p.DeleteAfterDays = null;

        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildIlmPolicyJson(p));
        System.Text.Json.JsonElement phases = doc.RootElement.GetProperty("policy").GetProperty("phases");

        phases.TryGetProperty("warm", out _).Should().BeFalse();
        phases.TryGetProperty("delete", out _).Should().BeFalse();
        phases.TryGetProperty("hot", out _).Should().BeTrue();
    }

    [Fact]
    public void IndexTemplate_ForADataStream_NamesThePolicyAndTheShape()
    {
        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildIndexTemplateJson(SamplePolicy()));
        System.Text.Json.JsonElement root = doc.RootElement;

        root.GetProperty("index_patterns")[0].GetString().Should().Be("logs-app-*");
        root.TryGetProperty("data_stream", out _).Should().BeTrue();
        root.GetProperty("template").GetProperty("settings")
            .GetProperty("index.lifecycle.name").GetString().Should().Be("app-logs");
    }

    [Fact]
    public void IndexTemplate_WithoutADataStream_CarriesARolloverAlias()
    {
        ElasticsearchIlmPolicy p = SamplePolicy();
        p.UseDataStream = false;

        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildIndexTemplateJson(p));
        System.Text.Json.JsonElement settings = doc.RootElement.GetProperty("template").GetProperty("settings");

        // Without an alias ILM has nothing to roll over, and the policy parks in the hot phase.
        settings.GetProperty("index.lifecycle.rollover_alias").GetString().Should().Be("app-logs");
        doc.RootElement.TryGetProperty("data_stream", out _).Should().BeFalse();
    }

    [Fact]
    public async Task APolicyAgingIntoATierTheClusterDoesNotHave_IsRefused()
    {
        await SeedClusterAsync();

        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        db.ElasticsearchClusters.Add(c);
        await db.SaveChangesAsync();

        ElasticsearchIlmPolicy p = SamplePolicy();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        p.ColdAfterDays = null;

        Func<Task> act = () => sut.CreatePolicyAsync(p);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*warm tier the cluster does not have*");
    }

    [Fact]
    public async Task APolicyWhosePhasesAreOutOfOrder_IsRefused()
    {
        await SeedClusterAsync();

        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        c.WarmCount = 2;
        c.ColdCount = 2;
        db.ElasticsearchClusters.Add(c);
        await db.SaveChangesAsync();

        ElasticsearchIlmPolicy p = SamplePolicy();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        p.WarmAfterDays = 30;
        p.ColdAfterDays = 7;

        Func<Task> act = () => sut.CreatePolicyAsync(p);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cold phase must come after*");
    }

    // ──────── The apply Job ────────

    [Fact]
    public void ApplyJob_ReadsThePasswordFromTheSecret_AndNeverCarriesIt()
    {
        ElasticsearchCluster c = SampleCluster();
        string manifest = ElasticsearchService.BuildIlmJobManifest(c, "search-ilm-apply-1", "search-entkube-ilm-apply");

        YamlMappingNode container = (YamlMappingNode)((YamlSequenceNode)
            ((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)Parse(manifest)["spec"])["template"])["spec"])["containers"])[0];
        YamlMappingNode env = (YamlMappingNode)((YamlSequenceNode)container["env"])[0];

        ((YamlScalarNode)env["name"]).Value.Should().Be("ELASTIC_PASSWORD");
        ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)env["valueFrom"])["secretKeyRef"])["name"]).Value
            .Should().Be("search-es-elastic-user");

        // The verification CA is mounted rather than skipped, and the job runs the stack's own image.
        manifest.Should().Contain("search-es-http-certs-public");
        manifest.Should().Contain("docker.elastic.co/elasticsearch/elasticsearch:9.5.0");
    }

    [Fact]
    public void ApplyScript_VerifiesTheCertificate_AndFailsOnAnErrorResponse()
    {
        ElasticsearchCluster c = SampleCluster();
        string configMap = ElasticsearchService.BuildIlmConfigMapManifest(c, [SamplePolicy()]);

        YamlMappingNode data = (YamlMappingNode)Parse(configMap)["data"];
        string script = ((YamlScalarNode)data["apply.sh"]).Value!;

        script.Should().Contain("--cacert /es-ca/ca.crt");
        script.Should().NotContain("-k ");
        script.Should().Contain("/_ilm/policy/app-logs");
        script.Should().Contain("/_index_template/app-logs");
        // An HTTP error must fail the job — curl alone would exit 0 on a 400.
        script.Should().Contain("if [ \"$code\" -ge 300 ]");

        data.Children.Should().ContainKey(new YamlScalarNode("app-logs.policy.json"));
        data.Children.Should().ContainKey(new YamlScalarNode("app-logs.template.json"));
    }

    [Theory]
    [InlineData("""{"status":{"succeeded":1}}""", true, true)]
    [InlineData("""{"status":{"failed":1}}""", true, false)]
    [InlineData("""{"status":{"active":1}}""", false, false)]
    public void JobCompletionIsReadFromTheJobStatus(string json, bool finished, bool succeeded)
    {
        ElasticsearchService.ParseJobCompletion(json).Should().Be((finished, succeeded));
    }

    // ──────── Status ────────

    [Fact]
    public void ElasticsearchStatusIsReadFromTheCr()
    {
        (string? health, string? phase, int nodes) = ElasticsearchService.ParseElasticsearchStatus(
            """{"status":{"health":"green","phase":"Ready","availableNodes":5}}""");

        health.Should().Be("green");
        phase.Should().Be("Ready");
        nodes.Should().Be(5);
    }

    [Fact]
    public void PodTierIsReadFromTheStatefulSetLabel()
    {
        List<ElasticsearchPodInfo> pods = ElasticsearchService.ParsePodList("""
            {"items":[
              {"metadata":{"name":"search-es-hot-0","labels":{"elasticsearch.k8s.elastic.co/statefulset-name":"search-es-hot"}},
               "spec":{"nodeName":"node-a"},
               "status":{"phase":"Running","containerStatuses":[{"ready":true,"restartCount":2}]}}
            ]}
            """);

        pods.Should().ContainSingle();
        pods[0].Tier.Should().Be("hot");
        pods[0].Ready.Should().BeTrue();
        pods[0].Restarts.Should().Be(2);
        pods[0].Node.Should().Be("node-a");
    }
}
