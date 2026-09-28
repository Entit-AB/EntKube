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
    private readonly VaultService vault;
    private readonly AuditService audit;
    private readonly ElasticsearchService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid k8sClusterId = Guid.NewGuid();

    public ElasticsearchServiceTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        k8s = new Mock<IKubernetesClientFactory>();
        vault = testDb.CreateVaultService();
        audit = new AuditService(testDb.Factory);
        sut = new ElasticsearchService(
            testDb.Factory, k8s.Object, vault, audit, NullLogger<ElasticsearchService>.Instance);
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
        string manifest = ElasticsearchService.BuildElasticsearchJobManifest(c, "search-ilm-apply-1", "search-entkube-ilm-apply");

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

    private async Task<(ElasticsearchCluster Cluster, ElasticsearchIlmPolicy Policy)> SeedClusterWithPolicyAsync()
    {
        await SeedClusterAsync();

        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        c.WarmCount = 2;
        c.ColdCount = 2;
        db.ElasticsearchClusters.Add(c);

        ElasticsearchIlmPolicy p = SamplePolicy();
        p.Id = Guid.NewGuid();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        db.ElasticsearchIlmPolicies.Add(p);

        await db.SaveChangesAsync();
        return (c, p);
    }

    [Fact]
    public async Task AJobStillRunning_IsNotRecordedAsAFailure()
    {
        (_, ElasticsearchIlmPolicy policy) = await SeedClusterWithPolicyAsync();

        // The job's own wait for a forming cluster is longer than this method holds a page open, so
        // "not finished yet" must leave the policies alone rather than marking them broken.
        k8s.Setup(x => x.GetJsonAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"status":{"active":1}}""");

        sut.JobPollInterval = TimeSpan.Zero;
        sut.JobPollAttempts = 2;

        Func<Task> act = () => sut.ApplyPoliciesAsync(tenantId, policy.ElasticsearchClusterId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*still running*");

        ElasticsearchIlmPolicy after = await db.ElasticsearchIlmPolicies.AsNoTracking().SingleAsync();
        after.LastError.Should().BeNull();
        after.LastAppliedAt.Should().BeNull();
    }

    [Fact]
    public async Task AFailedJob_RecordsWhatElasticsearchSaid()
    {
        (_, ElasticsearchIlmPolicy policy) = await SeedClusterWithPolicyAsync();

        k8s.Setup(x => x.GetJsonAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"status":{"failed":1}}""");
        k8s.Setup(x => x.GetPodLogsAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("PUT /_ilm/policy/app-logs -> HTTP 400\nillegal_argument_exception");

        sut.JobPollInterval = TimeSpan.Zero;

        Func<Task> act = () => sut.ApplyPoliciesAsync(tenantId, policy.ElasticsearchClusterId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*illegal_argument_exception*");

        ElasticsearchIlmPolicy after = await db.ElasticsearchIlmPolicies.AsNoTracking().SingleAsync();
        after.LastError.Should().Contain("HTTP 400");
        after.LastAppliedAt.Should().BeNull();
    }

    [Fact]
    public async Task ASucceededJob_StampsEveryPolicyAsApplied()
    {
        (_, ElasticsearchIlmPolicy policy) = await SeedClusterWithPolicyAsync();

        k8s.Setup(x => x.GetJsonAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"status":{"succeeded":1}}""");
        k8s.Setup(x => x.GetPodLogsAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("all policies and templates applied");

        sut.JobPollInterval = TimeSpan.Zero;

        await sut.ApplyPoliciesAsync(tenantId, policy.ElasticsearchClusterId);

        ElasticsearchIlmPolicy after = await db.ElasticsearchIlmPolicies.AsNoTracking().SingleAsync();
        after.LastAppliedAt.Should().NotBeNull();
        after.LastError.Should().BeNull();
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

    // ──────── Snapshots ────────

    private async Task<(ElasticsearchCluster Cluster, StorageLink Link)> SeedClusterWithStorageAsync(
        StorageProvider provider = StorageProvider.MinIO,
        string endpoint = "http://minio.minio.svc.cluster.local:9000",
        bool withCredentials = true)
    {
        await SeedClusterAsync();

        Guid envId = await db.Set<Data.Environment>().Select(e => e.Id).FirstAsync();

        StorageLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = envId,
            Provider = provider,
            Name = "Backups",
            Endpoint = endpoint,
            BucketName = "es-snapshots"
        };
        db.StorageLinks.Add(link);

        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        db.ElasticsearchClusters.Add(c);
        await db.SaveChangesAsync();

        if (withCredentials)
        {
            await vault.SetStorageLinkSecretAsync(tenantId, link.Id, "ACCESS_KEY", "AKIAEXAMPLE");
            await vault.SetStorageLinkSecretAsync(tenantId, link.Id, "SECRET_KEY", "s3cr3t");
        }

        return (c, link);
    }

    private void ArrangeSucceedingJob()
    {
        k8s.Setup(x => x.GetJsonAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"status":{"succeeded":1}}""");
        k8s.Setup(x => x.GetPodLogsAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("repository registered and verified");
        sut.JobPollInterval = TimeSpan.Zero;
    }

    [Theory]
    [InlineData("http://minio.minio.svc.cluster.local:9000", "minio.minio.svc.cluster.local:9000", "http")]
    [InlineData("https://s3.eu-west-1.amazonaws.com", "s3.eu-west-1.amazonaws.com", "https")]
    [InlineData("s3.example.com", "s3.example.com", "https")]
    public void AnEndpointUrlIsSplitTheWayElasticsearchWantsIt(string url, string endpoint, string protocol)
    {
        // Elasticsearch takes host[:port] and the scheme as two separate client settings.
        ElasticsearchS3Settings.SplitEndpoint(url).Should().Be((endpoint, protocol));
    }

    [Fact]
    public async Task ConfiguringSnapshots_PutsTheKeysInTheKeystoreSecret_NotInAManifest()
    {
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync();
        ArrangeSucceedingJob();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        await sut.ConfigureSnapshotsAsync(tenantId, c.Id, link.Id, null, "0 30 1 * * ?", 30, 5, 50);

        string secret = applied.Single(m => m.Contains("kind: Secret"));
        secret.Should().Contain("s3.client.default.access_key");
        secret.Should().Contain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("AKIAEXAMPLE")));

        // The key must not appear in plaintext anywhere, least of all in the Elasticsearch CR.
        applied.Should().NotContain(m => m.Contains("AKIAEXAMPLE"));

        ElasticsearchCluster stored = await db.ElasticsearchClusters.AsNoTracking().SingleAsync();
        stored.SnapshotsEnabled.Should().BeTrue();
        stored.SnapshotBasePath.Should().Be("search");   // defaults to the cluster name
    }

    [Fact]
    public async Task TheClusterLearnsWhereTheBucketIs_OnEveryTier()
    {
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync();
        ArrangeSucceedingJob();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        await sut.ConfigureSnapshotsAsync(tenantId, c.Id, link.Id, "prod", "0 30 1 * * ?", 30, 5, 50);

        string manifest = applied.Single(m => m.Contains("kind: Elasticsearch"));
        YamlMappingNode root = Parse(manifest);

        // The keystore Secret is referenced once, for the whole cluster.
        ((YamlScalarNode)((YamlMappingNode)((YamlSequenceNode)((YamlMappingNode)root["spec"])["secureSettings"])[0])["secretName"])
            .Value.Should().Be("search-es-snapshot-s3");

        // The client settings are per-node: a repository registered on one node is used by all.
        foreach (string tier in new[] { "master", "hot" })
        {
            YamlMappingNode config = (YamlMappingNode)NodeSet(manifest, tier)["config"];
            ((YamlScalarNode)config["s3.client.default.endpoint"]).Value.Should().Be("minio.minio.svc.cluster.local:9000");
            ((YamlScalarNode)config["s3.client.default.protocol"]).Value.Should().Be("http");
            ((YamlScalarNode)config["s3.client.default.path_style_access"]).Value.Should().Be("true");
        }
    }

    [Fact]
    public async Task AwsGetsVirtualHostAddressing_MinioGetsPathStyle()
    {
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync(
            StorageProvider.AwsS3, "https://s3.eu-west-1.amazonaws.com");
        ArrangeSucceedingJob();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        await sut.ConfigureSnapshotsAsync(tenantId, c.Id, link.Id, null, "0 30 1 * * ?", 30, 5, 50);

        // Path-style against AWS is deprecated; virtual-host style against MinIO resolves to a
        // hostname that does not exist. The provider is what decides.
        applied.Single(m => m.Contains("kind: Elasticsearch")).Should().NotContain("path_style_access");
    }

    [Fact]
    public async Task SnapshotsWithoutCredentialsInTheVault_AreRefusedWithWhereToPutThem()
    {
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync(withCredentials: false);
        ArrangeSucceedingJob();

        Func<Task> act = () => sut.ConfigureSnapshotsAsync(tenantId, c.Id, link.Id, null, "0 30 1 * * ?", 30, 5, 50);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ACCESS_KEY/SECRET_KEY*");
    }

    [Fact]
    public void TheSetupScript_RetriesTheRepository_BecauseTheKeystoreArrivesLate()
    {
        ElasticsearchCluster c = SampleCluster();
        c.SnapshotExpireAfterDays = 14;
        c.SnapshotMinCount = 3;
        c.SnapshotMaxCount = 20;

        string script = ElasticsearchService.BuildSnapshotSetupScript(c, new ElasticsearchS3Settings(
            "minio:9000", "http", true, null, "es-snapshots", "search"));

        script.Should().Contain("_snapshot/entkube-s3?verify=true");
        script.Should().Contain("for i in $(seq 1 10)");
        script.Should().Contain("_slm/policy/search-entkube-snapshots");
        script.Should().Contain("\"expire_after\": \"14d\"");
        script.Should().Contain("\"min_count\": 3");
        // The cluster state carries the templates and ILM policies the data needs to be usable.
        script.Should().Contain("\"include_global_state\": true");
    }

    [Fact]
    public void StoppingSnapshots_LeavesTheRepositoryAlone()
    {
        string script = ElasticsearchService.BuildSnapshotDisableScript(SampleCluster());

        script.Should().Contain("-X DELETE");
        script.Should().Contain("_slm/policy/search-entkube-snapshots");
        // Deleting the repository is how you lose the backups you turned this off while still having.
        script.Should().NotContain("-X DELETE \"$ES/_snapshot");
    }

    [Fact]
    public void SlmStatusIsReadBackFromTheJobLog()
    {
        string log = """
            waiting for cluster
            ---ENTKUBE-SLM---
            {"search-entkube-snapshots":{"version":1,"policy":{},"last_success":{"snapshot_name":"search-snap-2026.09.28","time":1790000000000}}}
            """;

        ElasticsearchService.SnapshotStatus status =
            ElasticsearchService.ParseSnapshotStatus(log, "search-entkube-snapshots");

        status.LastSuccessName.Should().Be("search-snap-2026.09.28");
        status.LastSuccessAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790000000000).UtcDateTime);
        status.LastFailure.Should().BeNull();
    }

    [Fact]
    public void AFailureOlderThanTheLastSuccess_IsNotReported()
    {
        string log = """
            ---ENTKUBE-SLM---
            {"p":{"last_success":{"snapshot_name":"snap-2","time":1790000000000},
                  "last_failure":{"snapshot_name":"snap-1","time":1780000000000,"details":"old news"}}}
            """;

        // SLM keeps both forever; reporting the stale one leaves a healthy cluster looking broken.
        ElasticsearchService.ParseSnapshotStatus(log, "p").LastFailure.Should().BeNull();
    }

    [Fact]
    public void AFailureNewerThanTheLastSuccess_IsReported()
    {
        string log = """
            ---ENTKUBE-SLM---
            {"p":{"last_success":{"snapshot_name":"snap-1","time":1780000000000},
                  "last_failure":{"snapshot_name":"snap-2","time":1790000000000,"details":"repository_missing_exception"}}}
            """;

        ElasticsearchService.ParseSnapshotStatus(log, "p").LastFailure.Should().Be("repository_missing_exception");
    }

    [Fact]
    public void ALogWithoutTheMarker_ReadsAsNoStatusRatherThanThrowing()
    {
        ElasticsearchService.ParseSnapshotStatus("connection refused", "p")
            .Should().Be(new ElasticsearchService.SnapshotStatus(null, null, null));
    }

    [Fact]
    public async Task AClusterWhoseStorageLinkWentAway_StillApplies()
    {
        // No vaulted credentials here: they hold an FK to the link, and this test is about the link
        // itself going away.
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync(withCredentials: false);
        ArrangeCluster(TwoNodes, NoPods);
        ArrangeSucceedingJob();

        c.SnapshotsEnabled = true;
        c.SnapshotStorageLinkId = link.Id;
        db.StorageLinks.Remove(link);
        await db.SaveChangesAsync();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        ElasticsearchCluster edited = SampleCluster();
        edited.Id = c.Id;
        edited.HotCount = 3;

        // A storage link deleted out from under a cluster must not make every later apply fail.
        await sut.UpdateClusterAsync(tenantId, edited);

        applied.Single(m => m.Contains("kind: Elasticsearch")).Should().NotContain("secureSettings");
    }
    // ──────── Listing and restoring ────────

    private const string RepositoryListing = """
        waiting for cluster
        ---ENTKUBE-SNAPSHOTS---
        {"snapshots":[
          {"snapshot":"search-snap-2026.09.27","state":"SUCCESS","start_time_in_millis":1790000000000,
           "end_time_in_millis":1790000060000,"indices":["logs-app-000001","logs-app-000002"],
           "shards":{"total":4,"failed":0,"successful":4}},
          {"snapshot":"search-snap-2026.09.28","state":"PARTIAL","start_time_in_millis":1790086400000,
           "end_time_in_millis":0,"indices":["logs-app-000003"],"shards":{"total":2,"failed":1,"successful":1}}
        ]}
        """;

    [Fact]
    public void TheRepositoryListingIsReadBackFromTheJobLog()
    {
        List<ElasticsearchService.ElasticsearchSnapshotInfo> snaps =
            ElasticsearchService.ParseSnapshotList(RepositoryListing);

        // Newest first: the one somebody is most likely to want is the one they reach for.
        snaps.Select(x => x.Name).Should().Equal("search-snap-2026.09.28", "search-snap-2026.09.27");

        snaps[1].Complete.Should().BeTrue();
        snaps[1].IndexCount.Should().Be(2);
        snaps[1].EndedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790000060000).UtcDateTime);

        // A partial snapshot restores, but not all of it — it must not read as a clean backup.
        snaps[0].Complete.Should().BeFalse();
        snaps[0].FailedShards.Should().Be(1);
        // end_time_in_millis is 0 while a snapshot is still running, which is not "finished in 1970".
        snaps[0].EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task ListingSnapshotsOnAClusterWithNoRepository_SaysSo()
    {
        (ElasticsearchCluster c, _) = await SeedClusterWithStorageAsync(withCredentials: false);

        Func<Task> act = () => sut.ListSnapshotsAsync(tenantId, c.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no snapshot repository*");
    }

    [Fact]
    public void ASideBySideRestore_RenamesAndLeavesTheLiveIndicesAlone()
    {
        string script = ElasticsearchService.BuildRestoreScript(
            SampleCluster(), "search-snap-2026.09.28", "logs-app-*",
            ElasticsearchService.RestoreMode.SideBySide, "restored-", includeGlobalState: false);

        script.Should().Contain("\"rename_replacement\": \"restored-$1\"");
        script.Should().Contain("_snapshot/entkube-s3/search-snap-2026.09.28/_restore?wait_for_completion=true");
        // Nothing live is closed, and aliases stay with the indices they currently point at.
        script.Should().NotContain("_close");
        script.Should().Contain("\"include_aliases\": false");
    }

    [Fact]
    public void AnInPlaceRestore_ClosesTheTargetIndicesFirst()
    {
        string script = ElasticsearchService.BuildRestoreScript(
            SampleCluster(), "search-snap-2026.09.28", "logs-app-*",
            ElasticsearchService.RestoreMode.InPlace, "", includeGlobalState: true);

        // Without the close, Elasticsearch refuses the restore and the backup looks broken.
        script.Should().Contain("/logs-app-*/_close?ignore_unavailable=true");
        script.Should().Contain("\"include_global_state\": true");
        script.Should().NotContain("rename_pattern");
    }

    [Fact]
    public async Task ASideBySideRestoreOfTheGlobalState_IsRefused()
    {
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await sut.ConfigureSnapshotsAsync(tenantId, c.Id, link.Id, null, "0 30 1 * * ?", 30, 5, 50);

        // There is only one set of cluster settings, so "alongside" is not a thing it can be.
        Func<Task> act = () => sut.RestoreSnapshotAsync(
            tenantId, c.Id, "snap-1", "*", ElasticsearchService.RestoreMode.SideBySide,
            "restored-", includeGlobalState: true, performedBy: "nils");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot be restored side by side*");
    }

    [Fact]
    public async Task ASideBySideRestoreWithoutAPrefix_IsRefused()
    {
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await sut.ConfigureSnapshotsAsync(tenantId, c.Id, link.Id, null, "0 30 1 * * ?", 30, 5, 50);

        Func<Task> act = () => sut.RestoreSnapshotAsync(
            tenantId, c.Id, "snap-1", "*", ElasticsearchService.RestoreMode.SideBySide,
            "", includeGlobalState: false, performedBy: "nils");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a prefix*");
    }

    [Fact]
    public async Task ARestoreIsWrittenToTheAuditLog_WithAName()
    {
        (ElasticsearchCluster c, StorageLink link) = await SeedClusterWithStorageAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await sut.ConfigureSnapshotsAsync(tenantId, c.Id, link.Id, null, "0 30 1 * * ?", 30, 5, 50);

        await sut.RestoreSnapshotAsync(
            tenantId, c.Id, "search-snap-2026.09.28", "logs-app-*",
            ElasticsearchService.RestoreMode.InPlace, "", includeGlobalState: false, performedBy: "nils");

        AuditEvent recorded = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.ResourceKind == "Elasticsearch");

        recorded.Action.Should().Be("elasticsearch.restore.in-place");
        recorded.PerformedBy.Should().Be("nils");
        recorded.ResourceName.Should().Be("search/search");
        recorded.Details.Should().Contain("search-snap-2026.09.28").And.Contain("over the live indices");
    }

    // ──────── Application users and bindings ────────

    private async Task<ElasticsearchCluster> SeedPlainClusterAsync()
    {
        await SeedClusterAsync();
        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        db.ElasticsearchClusters.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Theory]
    [InlineData(ElasticsearchAccess.Viewer, "read", "write")]
    [InlineData(ElasticsearchAccess.Writer, "write", "manage")]
    [InlineData(ElasticsearchAccess.Manager, "all", "nothing-else")]
    public void EachAccessLevelGrantsWhatItSays(ElasticsearchAccess access, string granted, string notGranted)
    {
        IReadOnlyList<string> privileges = ElasticsearchService.PrivilegesFor(access);

        privileges.Should().Contain(granted);
        privileges.Should().NotContain(notGranted);
    }

    [Fact]
    public void ARoleIsScopedToOneIndexPattern_AndNoClusterPrivilegesUnlessItManages()
    {
        ElasticsearchUser writer = new() { Username = "orders", IndexPattern = "logs-orders-*", Access = ElasticsearchAccess.Writer };
        string script = ElasticsearchService.BuildUserApplyScript(SampleCluster(), writer);

        script.Should().Contain("\"logs-orders-*\"");
        script.Should().Contain("_security/role/entkube-orders");
        script.Should().Contain("_security/user/orders");
        // auto_configure is what lets a writer add a field to a data stream's mapping.
        script.Should().Contain("auto_configure");
        script.Should().Contain("\"cluster\": []");
    }

    [Fact]
    public void ThePasswordReachesElasticsearchThroughTheEnvironment_NeverTheScript()
    {
        ElasticsearchUser user = new() { Username = "orders", IndexPattern = "logs-*", Access = ElasticsearchAccess.Writer };
        string script = ElasticsearchService.BuildUserApplyScript(SampleCluster(), user);

        // The user document is built with an expanding heredoc so only the password interpolates;
        // the role document above it stays literal.
        script.Should().Contain("cat > /tmp/user.json <<ENTKUBE_EOF");
        script.Should().Contain("\"password\": \"${ES_USER_PASSWORD}\"");
        script.Should().Contain("cat > /tmp/role.json <<'ENTKUBE_EOF'");
        // And it is removed rather than left in the container's /tmp.
        script.Should().Contain("rm -f /tmp/user.json");
    }

    [Fact]
    public void TheApplyJobIsGivenTheUserPassword_FromItsOwnSecret()
    {
        ElasticsearchCluster c = SampleCluster();
        string manifest = ElasticsearchService.BuildElasticsearchJobManifest(c, "job-1", "cm-1", "es-user-orders");

        manifest.Should().Contain("ES_USER_PASSWORD");
        manifest.Should().Contain("name: es-user-orders");
        // The elastic superuser credential is still how the job authenticates to make the change.
        manifest.Should().Contain("ELASTIC_PASSWORD");
    }

    [Theory]
    [InlineData("Orders_API")]
    [InlineData("-orders")]
    [InlineData("orders.api")]
    public async Task UsernamesThatWouldNotSurviveBeingASecretName_AreRefused(string username)
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();

        Func<Task> act = () => sut.CreateUserAsync(tenantId, c.Id, username, "logs-*", ElasticsearchAccess.Writer);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AUserWithNoIndexPattern_IsRefused()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();

        // A user with access to everything is the superuser this whole thing exists to avoid.
        Func<Task> act = () => sut.CreateUserAsync(tenantId, c.Id, "orders", "  ", ElasticsearchAccess.Writer);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*index pattern is required*");
    }

    [Fact]
    public async Task CreatingAUser_WritesThePasswordToASecretAndNeverToTheDatabase()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        ElasticsearchUser user = await sut.CreateUserAsync(
            tenantId, c.Id, "orders", "logs-orders-*", ElasticsearchAccess.Writer);

        string secret = applied.First(m => m.Contains("kind: Secret"));
        secret.Should().Contain("name: es-user-orders");
        secret.Should().Contain("password:");

        ElasticsearchUser stored = await db.ElasticsearchUsers.AsNoTracking().SingleAsync();
        stored.LastAppliedAt.Should().NotBeNull();
        stored.LastError.Should().BeNull();

        // Nothing on the entity can hold a password, which is the point of reading it back from the
        // cluster when a binding needs it.
        typeof(ElasticsearchUser).GetProperties().Select(pr => pr.Name)
            .Should().NotContain(n => n.Contains("Password", StringComparison.OrdinalIgnoreCase));
        user.CredentialsSecretName.Should().Be("es-user-orders");
    }

    [Fact]
    public async Task ABindingWritesTheEndpoint_CredentialsAndCa_IntoTheAppsNamespace()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ElasticsearchUser user = await sut.CreateUserAsync(
            tenantId, c.Id, "orders", "logs-orders-*", ElasticsearchAccess.Writer);

        AppDeployment deployment = await SeedDeploymentAsync();

        k8s.Setup(x => x.GetSecretValueAsync("es-user-orders", "password", "search",
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("the-password");
        k8s.Setup(x => x.GetSecretValueAsync("search-es-http-certs-public", "ca.crt", "search",
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("-----BEGIN CERTIFICATE-----");

        List<(string Manifest, string Kubeconfig)> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, kc, _) => applied.Add((m, kc)))
            .Returns(Task.CompletedTask);

        await sut.CreateBindingAsync(tenantId, c.Id, deployment.Id, user.Id, "elasticsearch");

        string secret = applied.Select(a => a.Manifest).Single(m => m.Contains("kind: Secret"));
        secret.Should().Contain("namespace: orders");
        secret.Should().Contain($"ELASTICSEARCH_PASSWORD: {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("the-password"))}");
        secret.Should().Contain("ELASTICSEARCH_USERNAME:");
        // Without the CA the app either fails or is told to skip verification.
        secret.Should().Contain("ELASTICSEARCH_CA_CRT:");

        ElasticsearchBinding stored = await db.ElasticsearchBindings.AsNoTracking().SingleAsync();
        stored.LastSyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ABindingWhosePasswordSecretIsGone_SaysWhatToDoAboutIt()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ElasticsearchUser user = await sut.CreateUserAsync(
            tenantId, c.Id, "orders", "logs-*", ElasticsearchAccess.Writer);
        AppDeployment deployment = await SeedDeploymentAsync();

        k8s.Setup(x => x.GetSecretValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        Func<Task> act = () => sut.CreateBindingAsync(tenantId, c.Id, deployment.Id, user.Id, "elasticsearch");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Re-apply the user*");
    }

    [Fact]
    public async Task AUserAnApplicationStillUses_CannotBeDeleted()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8s.Setup(x => x.GetSecretValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("pw");

        ElasticsearchUser user = await sut.CreateUserAsync(
            tenantId, c.Id, "orders", "logs-*", ElasticsearchAccess.Writer);
        AppDeployment deployment = await SeedDeploymentAsync();
        await sut.CreateBindingAsync(tenantId, c.Id, deployment.Id, user.Id, "elasticsearch");

        // Deleting it would leave the app holding credentials that quietly stop working.
        Func<Task> act = () => sut.DeleteUserAsync(tenantId, user.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*binding(s) still use*");
        (await db.ElasticsearchUsers.CountAsync()).Should().Be(1);
    }

    private async Task<AppDeployment> SeedDeploymentAsync()
    {
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Acme" };
        db.Customers.Add(customer);

        Data.App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "orders" };
        db.Apps.Add(app);

        Guid envId = await db.Set<Data.Environment>().Select(e => e.Id).FirstAsync();

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            ClusterId = k8sClusterId,
            EnvironmentId = envId,
            Name = "prod",
            Namespace = "orders"
        };
        db.AppDeployments.Add(deployment);
        await db.SaveChangesAsync();
        return deployment;
    }
}
