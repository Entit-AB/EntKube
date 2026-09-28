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
        // cluster when a binding needs it. A timestamp saying when one was last set is fine — what
        // must never appear is somewhere to put the password itself.
        typeof(ElasticsearchUser).GetProperties()
            .Where(pr => pr.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(pr => pr.PropertyType != typeof(string));
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

    // ──────── Live reads: disk, indices and lifecycle state ────────

    [Fact]
    public void TheQueryRunsOnLoopbackAndTakesThePasswordFromStdin()
    {
        string command = ElasticsearchService.BuildQueryCommand("/_cluster/health");

        // The password is read from stdin, so it is never in an argument list a "ps" would show.
        command.Should().StartWith("read -r ES_PW;");
        command.Should().Contain("https://localhost:9200/_cluster/health");
        command.Should().Contain("--cacert");
        command.Should().NotContain("ELASTIC_PASSWORD=");
    }

    [Fact]
    public void OnlyAReadyPodIsAsked()
    {
        string? pod = ElasticsearchService.FirstReadyPodName("""
            {"items":[
              {"metadata":{"name":"search-es-master-0"},"status":{"containerStatuses":[{"ready":false}]}},
              {"metadata":{"name":"search-es-hot-0"},"status":{"containerStatuses":[{"ready":true}]}}
            ]}
            """);

        // Asking a pod that is still starting gets a connection refused, not an answer.
        pod.Should().Be("search-es-hot-0");
    }

    [Fact]
    public void NoReadyPodReadsAsNothingToAsk()
    {
        ElasticsearchService.FirstReadyPodName("""{"items":[]}""").Should().BeNull();
    }

    [Fact]
    public void ClusterHealthCarriesTheUnassignedShardCount()
    {
        (string? status, int unassigned) = ElasticsearchService.ParseClusterHealth(
            """{"status":"yellow","number_of_nodes":5,"unassigned_shards":3}""");

        status.Should().Be("yellow");
        unassigned.Should().Be(3);
    }

    [Fact]
    public void AllocationIgnoresTheUnassignedRow_AndPutsTheFullestNodeFirst()
    {
        List<ElasticsearchService.ElasticsearchNodeDisk> nodes = ElasticsearchService.ParseAllocation("""
            [
              {"shards":"12","disk.used":"50","disk.total":"100","node":"search-es-hot-0"},
              {"shards":"40","disk.used":"91","disk.total":"100","node":"search-es-hot-1"},
              {"shards":"3","disk.used":null,"disk.total":null,"node":"UNASSIGNED"}
            ]
            """);

        // The UNASSIGNED row has no disk figures; counting it as a node at 0% would drag the
        // fleet's worst disk down and hide exactly the problem this is read for.
        nodes.Select(n => n.Node).Should().Equal("search-es-hot-1", "search-es-hot-0");
        nodes[0].PercentUsed.Should().Be(91);
        nodes[0].Shards.Should().Be(40);
    }

    [Fact]
    public void IndicesComeBackBiggestFirst_WithTheirLifecyclePhase()
    {
        string cat = """
            [
              {"index":"logs-app-000001","health":"green","status":"open","pri":"1","rep":"1","docs.count":"1000","store.size":"500"},
              {"index":"logs-app-000002","health":"green","status":"open","pri":"1","rep":"1","docs.count":"9000","store.size":"9000"}
            ]
            """;
        string ilm = """
            {"indices":{
              "logs-app-000001":{"managed":true,"phase":"warm","step":"complete"},
              "logs-app-000002":{"managed":true,"phase":"hot","step":"check-rollover-ready"}
            }}
            """;

        List<ElasticsearchService.ElasticsearchIndexInfo> indices = ElasticsearchService.ParseIndices(cat, ilm);

        // The question behind opening this list is nearly always "what is eating the disk".
        indices.Select(i => i.Name).Should().Equal("logs-app-000002", "logs-app-000001");
        indices[0].IlmPhase.Should().Be("hot");
        indices[0].DocCount.Should().Be(9000);
        indices[1].IlmPhase.Should().Be("warm");
        indices.Should().OnlyContain(i => i.IlmError == null);
    }

    [Fact]
    public void AStalledLifecyclePolicyIsSurfacedWithItsReason()
    {
        Dictionary<string, (string? Phase, string? Error)> explained = ElasticsearchService.ParseIlmExplain("""
            {"indices":{
              "logs-app-000003":{"managed":true,"phase":"warm","step":"ERROR",
                "step_info":{"type":"illegal_argument_exception","reason":"no shrink node available"}},
              "logs-app-000004":{"managed":true,"phase":"hot","step":"rollover"},
              "kibana-internal":{"managed":false}
            }}
            """);

        // An ILM policy that has stopped is the failure mode tiers are most likely to hit, and it
        // is invisible anywhere else: the index simply stays where it is.
        explained["logs-app-000003"].Error.Should().Be("no shrink node available");
        explained["logs-app-000004"].Error.Should().BeNull();
        explained.Should().NotContainKey("kibana-internal");
    }

    [Fact]
    public void IndicesStillListWhenIlmCannotBeRead()
    {
        // The explain call is best-effort — an older cluster, or one still starting, must not cost
        // the index list it was asked for.
        List<ElasticsearchService.ElasticsearchIndexInfo> indices = ElasticsearchService.ParseIndices(
            """[{"index":"logs-app-000001","health":"green","status":"open","pri":"1","rep":"0","docs.count":"1","store.size":"10"}]""",
            "");

        indices.Should().ContainSingle();
        indices[0].IlmPhase.Should().BeNull();
    }

    [Fact]
    public async Task ARefreshedInsightIsStored_SoTheAdvisorNeedNotAskTheCluster()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();

        k8s.Setup(x => x.GetJsonAsync("pods", "search", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[{"metadata":{"name":"search-es-hot-0"},"status":{"containerStatuses":[{"ready":true}]}}]}""");
        k8s.Setup(x => x.GetSecretValueAsync("search-es-elastic-user", "elastic", "search",
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("pw");

        k8s.Setup(x => x.RunCommandOnPodWithStdinAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(cmd => cmd[2].Contains("_cluster/health")),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("""{"status":"yellow","unassigned_shards":2}""");
        k8s.Setup(x => x.RunCommandOnPodWithStdinAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(cmd => cmd[2].Contains("_cat/allocation")),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("""[{"shards":"9","disk.used":"88","disk.total":"100","node":"search-es-hot-0"}]""");

        await sut.RefreshInsightAsync(c.Id);

        ElasticsearchCluster stored = await db.ElasticsearchClusters.AsNoTracking().SingleAsync();
        stored.Health.Should().Be("yellow");
        stored.UnassignedShards.Should().Be(2);
        stored.HighestNodeDiskPercent.Should().Be(88);
        stored.InsightCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AClusterThatCannotBeAsked_KeepsItsLastReadingAndItsStaleness()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        c.HighestNodeDiskPercent = 42;
        c.InsightCheckedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        k8s.Setup(x => x.GetJsonAsync("pods", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));

        // Not throwing matters — this runs in the background poller — and so does not moving
        // InsightCheckedAt, which is how the advisor tells stale figures from good news.
        await sut.RefreshInsightAsync(c.Id);

        ElasticsearchCluster stored = await db.ElasticsearchClusters.AsNoTracking().SingleAsync();
        stored.HighestNodeDiskPercent.Should().Be(42);
        stored.InsightCheckedAt.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TheWatermarksAreElasticsearchsOwn()
    {
        // Not opinions: Elasticsearch stops allocating at 85 and turns indices read-only at 95.
        ElasticsearchService.DiskHighWatermarkPercent.Should().Be(85);
        ElasticsearchService.DiskFloodStagePercent.Should().Be(95);
    }

    // ──────── Version upgrades ────────

    private static ElasticsearchCluster UpgradeableCluster(string version = "9.5.0")
    {
        ElasticsearchCluster c = SampleCluster();
        c.Version = version;
        c.Status = ElasticsearchClusterStatus.Running;
        c.Health = "green";
        c.SnapshotsEnabled = true;
        c.SnapshotStorageLinkId = Guid.NewGuid();
        c.SnapshotLastSuccessAt = DateTime.UtcNow.AddHours(-3);
        c.HighestNodeDiskPercent = 40;
        return c;
    }

    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("9.5.0", 9, 5, 0)]
    [InlineData("10.0.1", 10, 0, 1)]
    [InlineData("9.5.0-SNAPSHOT", 9, 5, 0)]
    public void VersionsParse(string version, int major, int minor, int patch)
    {
        ElasticsearchService.TryParseVersion(version, out (int Major, int Minor, int Patch) parsed).Should().BeTrue();
        parsed.Should().Be((major, minor, patch));
    }

    [Theory]
    [InlineData("9.5")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("9.x.0")]
    public void ThingsThatAreNotVersionsDoNotParse(string version)
    {
        ElasticsearchService.TryParseVersion(version, out _).Should().BeFalse();
    }

    [Fact]
    public void APatchUpgradeOnAHealthyBackedUpClusterIsAllowed()
    {
        ElasticsearchService.ElasticsearchUpgradePlan plan =
            ElasticsearchService.PlanUpgrade(UpgradeableCluster(), "9.5.1", Now);

        plan.CanProceed.Should().BeTrue();
        plan.Warnings.Should().BeEmpty();
        plan.Notes.Should().Contain(n => n.Contains("one at a time"));
    }

    [Fact]
    public void ADowngradeIsRefused_BecauseTheDataDirectoryHasAlreadyMoved()
    {
        ElasticsearchService.ElasticsearchUpgradePlan plan =
            ElasticsearchService.PlanUpgrade(UpgradeableCluster("9.5.0"), "9.4.0", Now);

        plan.CanProceed.Should().BeFalse();
        plan.Blockers.Should().ContainSingle(b => b.Contains("cannot be downgraded"));
    }

    [Fact]
    public void SkippingAMajorIsRefused()
    {
        ElasticsearchService.ElasticsearchUpgradePlan plan =
            ElasticsearchService.PlanUpgrade(UpgradeableCluster("9.5.0"), "11.0.0", Now);

        plan.CanProceed.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("one major at a time"));
    }

    [Fact]
    public void OneMajorIsAllowed_ButSaysWhereItHasToStartFrom()
    {
        ElasticsearchService.ElasticsearchUpgradePlan plan =
            ElasticsearchService.PlanUpgrade(UpgradeableCluster("9.5.0"), "10.0.0", Now);

        plan.CanProceed.Should().BeTrue();
        plan.Notes.Should().Contain(n => n.Contains("final minor"));
        plan.Warnings.Should().Contain(w => w.Contains("breaking changes"));
    }

    [Fact]
    public void UpgradingToTheVersionAlreadyRunningIsRefused()
    {
        ElasticsearchService.ElasticsearchUpgradePlan plan =
            ElasticsearchService.PlanUpgrade(UpgradeableCluster("9.5.0"), "9.5.0", Now);

        plan.CanProceed.Should().BeFalse();
        plan.Blockers.Should().ContainSingle(b => b.Contains("already runs"));
    }

    [Fact]
    public void ARedClusterIsNotUpgraded()
    {
        ElasticsearchCluster c = UpgradeableCluster();
        c.Health = "red";

        ElasticsearchService.ElasticsearchUpgradePlan plan = ElasticsearchService.PlanUpgrade(c, "9.5.1", Now);

        // Restarting every node in turn while shards are already unavailable is how a recoverable
        // problem becomes a lost index.
        plan.CanProceed.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("red"));
    }

    [Fact]
    public void AYellowClusterIsWarnedAboutRatherThanBlocked()
    {
        ElasticsearchCluster c = UpgradeableCluster();
        c.Health = "yellow";

        ElasticsearchService.ElasticsearchUpgradePlan plan = ElasticsearchService.PlanUpgrade(c, "9.5.1", Now);

        plan.CanProceed.Should().BeTrue();
        plan.Warnings.Should().Contain(w => w.Contains("no replica"));
    }

    [Fact]
    public void AClusterWithNoSnapshotsIsNotUpgraded()
    {
        ElasticsearchCluster c = UpgradeableCluster();
        c.SnapshotsEnabled = false;

        ElasticsearchService.ElasticsearchUpgradePlan plan = ElasticsearchService.PlanUpgrade(c, "9.5.1", Now);

        // An upgrade cannot be undone; the only way back is a restore.
        plan.CanProceed.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("cannot be undone"));
    }

    [Fact]
    public void SnapshotsConfiguredButNeverTaken_CountAsNoWayBack()
    {
        ElasticsearchCluster c = UpgradeableCluster();
        c.SnapshotLastSuccessAt = null;

        ElasticsearchService.ElasticsearchUpgradePlan plan = ElasticsearchService.PlanUpgrade(c, "9.5.1", Now);

        plan.CanProceed.Should().BeFalse();
        plan.Blockers.Should().Contain(b => b.Contains("nothing to go back to"));
    }

    [Fact]
    public void AnAgeingSnapshotIsAWarning()
    {
        ElasticsearchCluster c = UpgradeableCluster();
        c.SnapshotLastSuccessAt = Now.AddDays(-5);

        ElasticsearchService.ElasticsearchUpgradePlan plan = ElasticsearchService.PlanUpgrade(c, "9.5.1", Now);

        plan.CanProceed.Should().BeTrue();
        plan.Warnings.Should().Contain(w => w.Contains("5 days ago"));
    }

    [Fact]
    public void AFullDiskIsAWarning_BecauseShardsHaveNowhereToMove()
    {
        ElasticsearchCluster c = UpgradeableCluster();
        c.HighestNodeDiskPercent = 90;

        ElasticsearchService.ElasticsearchUpgradePlan plan = ElasticsearchService.PlanUpgrade(c, "9.5.1", Now);

        plan.CanProceed.Should().BeTrue();
        plan.Warnings.Should().Contain(w => w.Contains("90% full"));
    }

    [Fact]
    public void ASingleNodeIsToldItWillBeDown_NotRolled()
    {
        ElasticsearchCluster c = UpgradeableCluster();
        c.MasterCount = 1;
        c.HotCount = 0;

        ElasticsearchService.ElasticsearchUpgradePlan plan = ElasticsearchService.PlanUpgrade(c, "9.5.1", Now);

        plan.Notes.Should().Contain(n => n.Contains("down for the duration"));
    }

    [Fact]
    public async Task AnUpgradeWithBlockersIsRefusedUnlessAccepted()
    {
        await SeedClusterAsync();
        ElasticsearchCluster c = UpgradeableCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        c.Health = "red";
        db.ElasticsearchClusters.Add(c);
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.UpgradeAsync(tenantId, c.Id, "9.5.1", "nils");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*red*");
        (await db.ElasticsearchClusters.AsNoTracking().SingleAsync()).Version.Should().Be("9.5.0");
    }

    [Fact]
    public async Task AnAcceptedUpgradeAppliesBothCrs_AndSaysWhoAcceptedWhat()
    {
        await SeedClusterAsync();
        ElasticsearchCluster c = UpgradeableCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        c.Health = "red";
        db.ElasticsearchClusters.Add(c);
        await db.SaveChangesAsync();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        await sut.UpgradeAsync(tenantId, c.Id, "9.5.1", "nils", acceptBlockers: true);

        applied.Should().HaveCount(2);
        applied[0].Should().Contain("version: 9.5.1").And.Contain("kind: Elasticsearch");
        applied[1].Should().Contain("kind: Kibana").And.Contain("version: 9.5.1");

        (await db.ElasticsearchClusters.AsNoTracking().SingleAsync()).Version.Should().Be("9.5.1");

        AuditEvent recorded = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "elasticsearch.upgrade");
        recorded.PerformedBy.Should().Be("nils");
        recorded.Details.Should().Contain("9.5.0 → 9.5.1").And.Contain("accepted blockers");
    }

    // ──────── Kibana sign-in and password rotation ────────

    [Fact]
    public void AnApplicationUserGetsNoKibanaPrivileges()
    {
        ElasticsearchUser app = new()
        {
            Username = "orders", IndexPattern = "logs-orders-*",
            Access = ElasticsearchAccess.Writer, KibanaAccess = ElasticsearchKibanaAccess.None
        };

        string script = ElasticsearchService.BuildUserApplyScript(SampleCluster(), app);

        script.Should().NotContain("applications");
        script.Should().NotContain("kibana-.kibana");
    }

    [Theory]
    [InlineData(ElasticsearchKibanaAccess.Read, "read")]
    [InlineData(ElasticsearchKibanaAccess.All, "all")]
    public void APersonGetsKibanaAsAnApplicationPrivilegeOnTheirOwnRole(
        ElasticsearchKibanaAccess access, string privilege)
    {
        ElasticsearchUser person = new()
        {
            Username = "nils", IndexPattern = "logs-orders-*",
            Access = ElasticsearchAccess.Viewer, KibanaAccess = access
        };

        string script = ElasticsearchService.BuildUserApplyScript(SampleCluster(), person);

        script.Should().Contain("\"application\": \"kibana-.kibana\"");
        script.Should().Contain($"\"{privilege}\"");

        // Not the built-in viewer/editor roles: those carry read (or write) on every index, which
        // would undo the index scoping in the same breath as granting the login.
        script.Should().NotContain("\"roles\": [\"viewer\"]");
        script.Should().NotContain("\"roles\": [\"editor\"]");
        script.Should().Contain("\"logs-orders-*\"");
    }

    [Fact]
    public void ThePasswordResetTouchesThePasswordAndNothingElse()
    {
        ElasticsearchUser user = new() { Username = "nils", IndexPattern = "logs-*" };

        string script = ElasticsearchService.BuildPasswordResetScript(SampleCluster(), user);

        // The dedicated endpoint, so the account's roles are left as they are rather than
        // re-asserted from a stale copy of them here.
        script.Should().Contain("_security/user/nils/_password");
        script.Should().NotContain("_security/role/");
        script.Should().Contain("\"password\": \"${ES_USER_PASSWORD}\"");
        script.Should().Contain("rm -f /tmp/pw.json");
    }

    [Fact]
    public async Task AResetPushesTheNewPasswordToEveryBoundApplication()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        k8s.Setup(x => x.GetSecretValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("whatever-is-in-the-secret");

        ElasticsearchUser user = await sut.CreateUserAsync(
            tenantId, c.Id, "orders", "logs-*", ElasticsearchAccess.Writer);
        AppDeployment deployment = await SeedDeploymentAsync();
        await sut.CreateBindingAsync(tenantId, c.Id, deployment.Id, user.Id, "elasticsearch");

        DateTime? before = (await db.ElasticsearchBindings.AsNoTracking().SingleAsync()).LastSyncedAt;
        await Task.Delay(10);

        string password = await sut.ResetPasswordAsync(tenantId, user.Id, "nils");

        // A bound application holds the old password from the moment it changes, so the re-sync is
        // part of the reset rather than something to remember afterwards.
        password.Should().NotBeNullOrWhiteSpace();
        ElasticsearchBinding binding = await db.ElasticsearchBindings.AsNoTracking().SingleAsync();
        binding.LastSyncedAt.Should().BeAfter(before!.Value);

        ElasticsearchUser stored = await db.ElasticsearchUsers.AsNoTracking().SingleAsync();
        stored.PasswordSetAt.Should().NotBeNull();

        AuditEvent recorded = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == "elasticsearch.user.password-reset");
        recorded.PerformedBy.Should().Be("nils");
        recorded.Details.Should().Contain("orders");
    }

    [Fact]
    public async Task AFailedResetLeavesTheOldPasswordInForce_AndSaysSo()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ElasticsearchUser user = await sut.CreateUserAsync(
            tenantId, c.Id, "orders", "logs-*", ElasticsearchAccess.Writer);

        k8s.Setup(x => x.GetJsonAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"status":{"failed":1}}""");
        k8s.Setup(x => x.GetPodLogsAsync(It.IsRegex("^job/"), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("password change -> HTTP 400");

        Func<Task> act = () => sut.ResetPasswordAsync(tenantId, user.Id, "nils");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*old one is still in force*");
    }

    [Fact]
    public async Task AccessCanBeChangedWithoutTouchingThePassword()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        ElasticsearchUser user = await sut.CreateUserAsync(
            tenantId, c.Id, "nils", "logs-*", ElasticsearchAccess.Viewer, ElasticsearchKibanaAccess.Read);
        DateTime? passwordSet = (await db.ElasticsearchUsers.AsNoTracking().SingleAsync()).PasswordSetAt;
        applied.Clear();

        await sut.UpdateUserAccessAsync(
            tenantId, user.Id, "logs-orders-*", ElasticsearchAccess.Viewer, ElasticsearchKibanaAccess.All);

        ElasticsearchUser stored = await db.ElasticsearchUsers.AsNoTracking().SingleAsync();
        stored.KibanaAccess.Should().Be(ElasticsearchKibanaAccess.All);
        stored.IndexPattern.Should().Be("logs-orders-*");
        // Promoting somebody from reading Kibana to building in it must not log them out of it.
        stored.PasswordSetAt.Should().Be(passwordSet);
        applied.Should().NotContain(m => m.Contains("kind: Secret"));
    }

    // ──────── Metrics ────────

    [Fact]
    public void TheExporterAccountCanOnlyRead()
    {
        string script = ElasticsearchService.BuildExporterUserScript(SampleCluster());

        script.Should().Contain("_security/role/entkube-exporter");
        script.Should().Contain("_security/user/search-exporter");
        script.Should().Contain("\"monitor\"");
        // This credential sits in a pod for years — nothing it holds may write.
        script.Should().NotContain("\"all\"");
        script.Should().NotContain("\"write\"");
        script.Should().NotContain("manage");
    }

    [Fact]
    public void TheExporterVerifiesTheClusterCertificate_AndIsBounded()
    {
        string manifest = ElasticsearchService.BuildExporterDeployment(SampleCluster());
        YamlMappingNode root = Parse(manifest);

        YamlMappingNode container = (YamlMappingNode)((YamlSequenceNode)
            ((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)root["spec"])["template"])["spec"])["containers"])[0];

        string[] args = [.. ((YamlSequenceNode)container["args"]).Children.Cast<YamlScalarNode>().Select(a => a.Value!)];
        args.Should().Contain("--es.ca=/es-ca/ca.crt");
        args.Should().Contain("--es.uri=https://search-es-http.search.svc:9200");

        // A metrics sidecar that competes with the data nodes it measures is worse than none.
        YamlMappingNode resources = (YamlMappingNode)container["resources"];
        ((YamlScalarNode)((YamlMappingNode)resources["requests"])["memory"]).Value.Should().Be("128Mi");
        ((YamlScalarNode)((YamlMappingNode)resources["limits"])["memory"]).Value.Should().Be("128Mi");

        // The password comes from the Secret, never from an arg or a plain value.
        string manifestText = manifest;
        manifestText.Should().Contain("secretKeyRef");
        manifestText.Should().Contain("name: search-es-exporter");
    }

    [Fact]
    public void PerIndexMetricsAreOptIn()
    {
        ElasticsearchCluster off = SampleCluster();
        ElasticsearchCluster on = SampleCluster();
        on.MonitoringIndexMetrics = true;

        // Each scrape would otherwise ask the masters for every index's stats, and every index
        // becomes its own set of series in Prometheus.
        ElasticsearchService.BuildExporterDeployment(off).Should().NotContain("--es.indices");
        ElasticsearchService.BuildExporterDeployment(on).Should().Contain("--es.indices");
    }

    [Fact]
    public void TheServiceMonitorCarriesWhateverPrometheusSelectsOn()
    {
        string manifest = ElasticsearchService.BuildExporterServiceMonitor(
            SampleCluster(), new Dictionary<string, string> { ["release"] = "kube-prometheus-stack" });

        YamlMappingNode labels = (YamlMappingNode)((YamlMappingNode)Parse(manifest)["metadata"])["labels"];

        // A monitor Prometheus does not select is not an error anywhere — it is simply never
        // scraped, and the metrics never appear.
        ((YamlScalarNode)labels["release"]).Value.Should().Be("kube-prometheus-stack");
    }

    [Fact]
    public void ThePrometheusSelectorIsReadFromTheLiveResource()
    {
        ElasticsearchService.PrometheusSelector selector = ElasticsearchService.ParsePrometheusSelector("""
            {"items":[{"spec":{
              "serviceMonitorSelector":{"matchLabels":{"release":"kps"}},
              "serviceMonitorNamespaceSelector":{}
            }}]}
            """);

        selector.Found.Should().BeTrue();
        selector.MatchLabels.Should().ContainKey("release").WhoseValue.Should().Be("kps");
        selector.WatchesOtherNamespaces.Should().BeTrue();
    }

    [Fact]
    public void APrometheusConfinedToItsOwnNamespaceIsCalledOut()
    {
        // An absent (rather than empty) namespace selector confines Prometheus to its own
        // namespace, where an Elasticsearch exporter will never be.
        ElasticsearchService.PrometheusSelector selector = ElasticsearchService.ParsePrometheusSelector("""
            {"items":[{"spec":{"serviceMonitorSelector":{"matchLabels":{"release":"kps"}}}}]}
            """);

        selector.WatchesOtherNamespaces.Should().BeFalse();

        string note = ElasticsearchService.DescribeSelector(selector, "search");
        note.Should().Contain("only looks in its own namespace");
        note.Should().Contain("serviceMonitorNamespaceSelector");
    }

    [Fact]
    public void NoPrometheusAtAllIsSaidPlainly()
    {
        ElasticsearchService.PrometheusSelector selector =
            ElasticsearchService.ParsePrometheusSelector("""{"items":[]}""");

        selector.Found.Should().BeFalse();
        ElasticsearchService.DescribeSelector(selector, "search")
            .Should().Contain("Nothing will scrape it");
    }

    [Fact]
    public void APrometheusThatSelectsEverythingNeedsNoLabels()
    {
        ElasticsearchService.PrometheusSelector selector = ElasticsearchService.ParsePrometheusSelector("""
            {"items":[{"spec":{"serviceMonitorSelector":{},"serviceMonitorNamespaceSelector":{}}}]}
            """);

        selector.MatchLabels.Should().BeEmpty();
        ElasticsearchService.DescribeSelector(selector, "search").Should().Contain("selects every ServiceMonitor");
    }

    [Fact]
    public async Task TurningOnMetricsAppliesTheAccount_TheExporterAndTheMonitor()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();

        k8s.Setup(x => x.GetJsonAllNamespacesAsync("prometheuses.monitoring.coreos.com",
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[{"spec":{"serviceMonitorSelector":{"matchLabels":{"release":"kps"}},"serviceMonitorNamespaceSelector":{}}}]}""");

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        string note = await sut.EnableMonitoringAsync(tenantId, c.Id, indexMetrics: false);

        applied.Should().Contain(m => m.Contains("kind: Deployment"));
        applied.Should().Contain(m => m.Contains("kind: ServiceMonitor") && m.Contains("release: \"kps\""));
        note.Should().Contain("release=kps");

        ElasticsearchCluster stored = await db.ElasticsearchClusters.AsNoTracking().SingleAsync();
        stored.MonitoringEnabled.Should().BeTrue();
        stored.MonitoringIndexMetrics.Should().BeFalse();
    }

    [Fact]
    public async Task TurningOffMetricsRemovesEverythingItPutThere()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        c.MonitoringEnabled = true;
        await db.SaveChangesAsync();

        List<string> deleted = [];
        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, CancellationToken>((kind, name, _, _, _) => deleted.Add($"{kind}/{name}"))
            .Returns(Task.CompletedTask);

        await sut.DisableMonitoringAsync(tenantId, c.Id);

        deleted.Should().BeEquivalentTo(
            "prometheusrule/search-es-exporter", "servicemonitor/search-es-exporter",
            "service/search-es-exporter", "deployment/search-es-exporter",
            "secret/search-es-exporter");
        (await db.ElasticsearchClusters.AsNoTracking().SingleAsync()).MonitoringEnabled.Should().BeFalse();
    }

    // ──────── Kibana spaces ────────

    [Fact]
    public void AUserWithNoSpaceSeesAllOfThem()
    {
        ElasticsearchUser person = new()
        {
            Username = "nils", IndexPattern = "logs-*",
            Access = ElasticsearchAccess.Viewer, KibanaAccess = ElasticsearchKibanaAccess.Read
        };

        ElasticsearchService.BuildUserApplyScript(SampleCluster(), person)
            .Should().Contain("\"*\"");
    }

    [Fact]
    public void AUserScopedToASpaceSeesOnlyThatOne()
    {
        ElasticsearchUser person = new()
        {
            Username = "nils", IndexPattern = "logs-orders-*",
            Access = ElasticsearchAccess.Viewer, KibanaAccess = ElasticsearchKibanaAccess.All,
            KibanaSpaceId = "orders"
        };

        string script = ElasticsearchService.BuildUserApplyScript(SampleCluster(), person);

        // The space menu shows nothing else, and neither do the dashboards.
        script.Should().Contain("\"space:orders\"");
    }

    [Fact]
    public void ASpaceIsCreatedOrUpdated_SoReapplyingIsHarmless()
    {
        ElasticsearchKibanaSpace space = new() { SpaceId = "orders", Name = "Orders team", Description = "Order pipeline" };

        string script = ElasticsearchService.BuildSpaceApplyScript(SampleCluster(), space);

        script.Should().Contain("POST \"$KB/api/spaces/space\"");
        script.Should().Contain("409");
        script.Should().Contain("PUT \"$KB/api/spaces/space/orders\"");
        // Kibana rejects any write without this header, with a message about cross-site request
        // forgery that reads as a problem with the body.
        script.Should().Contain("kbn-xsrf:true");
        // Kibana is a different service with a different certificate from Elasticsearch's.
        script.Should().Contain("--cacert /kb-ca/ca.crt");
        script.Should().Contain("$KB/api/status");
    }

    [Fact]
    public void TheAdminJobMountsKibanasCaOnlyWhenThereIsAKibana()
    {
        ElasticsearchCluster with = SampleCluster();
        ElasticsearchCluster without = SampleCluster();
        without.KibanaEnabled = false;

        ElasticsearchService.BuildElasticsearchJobManifest(with, "j", "cm")
            .Should().Contain("search-kb-http-certs-public").And.Contain("optional: true");
        ElasticsearchService.BuildElasticsearchJobManifest(without, "j", "cm")
            .Should().NotContain("kb-ca");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("Orders Team")]
    [InlineData("orders/team")]
    [InlineData("")]
    public async Task SpaceIdsKibanaWouldRejectAreRefusedHere(string spaceId)
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();

        Func<Task> act = () => sut.CreateSpaceAsync(tenantId, c.Id, spaceId, "Orders", null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ASpaceNeedsAKibanaToLiveIn()
    {
        await SeedClusterAsync();
        ElasticsearchCluster c = SampleCluster();
        c.TenantId = tenantId;
        c.KubernetesClusterId = k8sClusterId;
        c.KibanaEnabled = false;
        db.ElasticsearchClusters.Add(c);
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.CreateSpaceAsync(tenantId, c.Id, "orders", "Orders", null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nowhere to put a space*");
    }

    [Fact]
    public async Task CreatingASpaceAppliesItAndRecordsIt()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        await sut.CreateSpaceAsync(tenantId, c.Id, "orders", "Orders team", "The order pipeline");

        applied.Should().Contain(m => m.Contains("api/spaces/space"));

        ElasticsearchKibanaSpace stored = await db.ElasticsearchKibanaSpaces.AsNoTracking().SingleAsync();
        stored.SpaceId.Should().Be("orders");
        stored.LastAppliedAt.Should().NotBeNull();
        stored.LastError.Should().BeNull();
    }

    [Fact]
    public async Task ASpaceSomebodyIsConfinedToCannotBeDeleted()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ElasticsearchKibanaSpace space = await sut.CreateSpaceAsync(tenantId, c.Id, "orders", "Orders team", null);
        await sut.CreateUserAsync(tenantId, c.Id, "nils", "logs-*", ElasticsearchAccess.Viewer,
            ElasticsearchKibanaAccess.Read, "orders");

        // That account would sign in to a space that no longer exists and see nothing, with no
        // message saying why.
        Func<Task> act = () => sut.DeleteSpaceAsync(tenantId, space.Id, "nils");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*can only see this space*");
        (await db.ElasticsearchKibanaSpaces.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeletingASpaceIsAudited_BecauseItTakesTheDashboardsWithIt()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ElasticsearchKibanaSpace space = await sut.CreateSpaceAsync(tenantId, c.Id, "orders", "Orders team", null);

        await sut.DeleteSpaceAsync(tenantId, space.Id, "nils");

        (await db.ElasticsearchKibanaSpaces.CountAsync()).Should().Be(0);
        AuditEvent recorded = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == "elasticsearch.kibana-space.delete");
        recorded.PerformedBy.Should().Be("nils");
        recorded.Details.Should().Contain("orders");
    }

    // ──────── Cross-cluster search ────────

    private async Task<(ElasticsearchCluster Local, ElasticsearchCluster Remote)> SeedTwoClustersAsync(
        string localVersion = "9.5.0", string remoteVersion = "9.5.0", bool sameK8s = true)
    {
        await SeedClusterAsync();

        ElasticsearchCluster local = SampleCluster();
        local.Name = "search";
        local.TenantId = tenantId;
        local.KubernetesClusterId = k8sClusterId;
        local.Version = localVersion;

        Guid otherK8s = k8sClusterId;
        if (!sameK8s)
        {
            Guid envId = await db.Set<Data.Environment>().Select(e => e.Id).FirstAsync();
            otherK8s = Guid.NewGuid();
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = otherK8s, TenantId = tenantId, EnvironmentId = envId,
                Name = "other", ApiServerUrl = "https://other.example.com"
            });
        }

        ElasticsearchCluster remote = SampleCluster();
        remote.Name = "archive";
        remote.Namespace = "archive";
        remote.TenantId = tenantId;
        remote.KubernetesClusterId = otherK8s;
        remote.Version = remoteVersion;

        db.ElasticsearchClusters.AddRange(local, remote);
        await db.SaveChangesAsync();
        return (local, remote);
    }

    [Fact]
    public void AClusterWithNoLinksRendersNoRemoteSection()
    {
        string manifest = ElasticsearchService.BuildElasticsearchManifest(SampleCluster());

        manifest.Should().NotContain("remoteClusters");
        manifest.Should().NotContain("remoteClusterServer");
    }

    [Fact]
    public void ALinkedClusterNamesTheRemoteAndScopesTheApiKey()
    {
        string manifest = ElasticsearchService.BuildElasticsearchManifest(
            SampleCluster(), s3: null,
            remotes: [new ElasticsearchRemoteRef("prod", "archive", "archive", ["logs-*", "metrics-*"])]);

        YamlMappingNode remote = (YamlMappingNode)((YamlSequenceNode)
            ((YamlMappingNode)Parse(manifest)["spec"])["remoteClusters"])[0];

        ((YamlScalarNode)remote["name"]).Value.Should().Be("prod");
        ((YamlScalarNode)((YamlMappingNode)remote["elasticsearchRef"])["namespace"]).Value.Should().Be("archive");

        // The key grants search on exactly these and nothing else; ECK creates and rotates it.
        YamlSequenceNode names = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)
            remote["apiKey"])["access"])["search"])["names"];
        names.Children.Cast<YamlScalarNode>().Select(n => n.Value).Should().Equal("logs-*", "metrics-*");
    }

    [Fact]
    public void AClusterSomebodySearchesOpensItsRemoteClusterServer()
    {
        string manifest = ElasticsearchService.BuildElasticsearchManifest(
            SampleCluster(), s3: null, remotes: null, serveAsRemote: true);

        YamlMappingNode spec = (YamlMappingNode)Parse(manifest)["spec"];
        ((YamlScalarNode)((YamlMappingNode)spec["remoteClusterServer"])["enabled"]).Value.Should().Be("true");
    }

    [Theory]
    [InlineData("8.14.0", true)]
    [InlineData("8.13.4", false)]
    [InlineData("9.0.0", true)]
    [InlineData("7.17.0", false)]
    public void CrossClusterApiKeysNeedAtLeast814(string version, bool supported)
    {
        ElasticsearchService.IsNewEnoughForRemoteClusters(version).Should().Be(supported);
    }

    [Fact]
    public async Task AClusterCannotSearchItself()
    {
        (ElasticsearchCluster local, _) = await SeedTwoClustersAsync();

        Func<Task> act = () => sut.CreateRemoteLinkAsync(tenantId, local.Id, local.Id, "self", "*");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already can, locally*");
    }

    [Fact]
    public async Task ClustersOnDifferentKubernetesClustersCannotBeLinkedHere()
    {
        (ElasticsearchCluster local, ElasticsearchCluster remote) = await SeedTwoClustersAsync(sameK8s: false);

        // ECK can only wire a connection between clusters it manages together.
        Func<Task> act = () => sut.CreateRemoteLinkAsync(tenantId, local.Id, remote.Id, "prod", "*");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different Kubernetes clusters*");
    }

    [Fact]
    public async Task AClusterTooOldForCrossClusterApiKeysIsRefused()
    {
        (ElasticsearchCluster local, ElasticsearchCluster remote) = await SeedTwoClustersAsync(remoteVersion: "8.12.0");

        Func<Task> act = () => sut.CreateRemoteLinkAsync(tenantId, local.Id, remote.Id, "prod", "*");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*8.14 or later*");
    }

    [Theory]
    [InlineData("Prod Cluster")]
    [InlineData("prod:cluster")]
    [InlineData("")]
    public async Task AliasesThatWouldNotWorkInAQueryAreRefused(string alias)
    {
        (ElasticsearchCluster local, ElasticsearchCluster remote) = await SeedTwoClustersAsync();

        Func<Task> act = () => sut.CreateRemoteLinkAsync(tenantId, local.Id, remote.Id, alias, "*");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LinkingAppliesTheSearchedClusterFirst()
    {
        (ElasticsearchCluster local, ElasticsearchCluster remote) = await SeedTwoClustersAsync();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        string note = await sut.CreateRemoteLinkAsync(tenantId, local.Id, remote.Id, "prod", "logs-*");

        // The local cluster's connection has nothing to land on until the remote server is open.
        applied.Should().HaveCount(2);
        applied[0].Should().Contain("name: archive").And.Contain("remoteClusterServer");
        applied[1].Should().Contain("name: search").And.Contain("remoteClusters");
        note.Should().Contain("restarting");

        ElasticsearchRemoteLink stored = await db.ElasticsearchRemoteLinks.AsNoTracking().SingleAsync();
        stored.LastAppliedAt.Should().NotBeNull();
        stored.SearchIndexPatterns.Should().Be("logs-*");
    }

    [Fact]
    public async Task TwoRemotesCannotShareAnAliasOnOneCluster()
    {
        (ElasticsearchCluster local, ElasticsearchCluster remote) = await SeedTwoClustersAsync();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.CreateRemoteLinkAsync(tenantId, local.Id, remote.Id, "prod", "*");

        Func<Task> act = () => sut.CreateRemoteLinkAsync(tenantId, local.Id, remote.Id, "prod", "*");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already searches something*");
    }

    [Fact]
    public async Task RemovingALinkReappliesTheSearchingClusterWithoutIt()
    {
        (ElasticsearchCluster local, ElasticsearchCluster remote) = await SeedTwoClustersAsync();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.CreateRemoteLinkAsync(tenantId, local.Id, remote.Id, "prod", "*");
        ElasticsearchRemoteLink link = await db.ElasticsearchRemoteLinks.AsNoTracking().SingleAsync();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        await sut.DeleteRemoteLinkAsync(tenantId, link.Id);

        applied.Should().ContainSingle();
        applied[0].Should().NotContain("remoteClusters");
        (await db.ElasticsearchRemoteLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OnlyClustersThatCouldActuallyBeLinkedAreOffered()
    {
        (ElasticsearchCluster local, _) = await SeedTwoClustersAsync(remoteVersion: "8.12.0");

        List<ElasticsearchCluster> candidates = await sut.GetRemoteCandidatesAsync(tenantId, local.Id);

        // Offering a cluster the link would then refuse is worse than not offering it.
        candidates.Should().BeEmpty();
    }

    // ──────── Ingest pipelines ────────

    private static ElasticsearchIngestPipeline SamplePipeline() => new()
    {
        Name = "app-logs",
        GrokField = "message",
        GrokPattern = "%{LOGLEVEL:level} %{GREEDYDATA:msg}",
        TimestampField = "time",
        TimestampFormats = "ISO8601,UNIX_MS",
        RenameFields = "msg:message",
        RemoveFields = "agent,host.raw",
        SetFields = "env=production"
    };

    [Fact]
    public void EveryPipelineRecordsFailuresInsteadOfRejectingTheDocument()
    {
        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildPipelineJson(SamplePipeline()));

        // Without this, a grok pattern that does not match rejects the whole document and the line
        // is simply gone. With it, the line arrives with the error recorded and searchable.
        System.Text.Json.JsonElement onFailure = doc.RootElement.GetProperty("on_failure")[0];
        onFailure.GetProperty("set").GetProperty("field").GetString().Should().Be("ingest.failure");
        onFailure.GetProperty("set").GetProperty("value").GetString().Should().Contain("_ingest.on_failure_message");
    }

    [Fact]
    public void TheProcessorsComeOutInTheOrderTheyHaveToRunIn()
    {
        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildPipelineJson(SamplePipeline()));

        string[] kinds = [.. doc.RootElement.GetProperty("processors").EnumerateArray()
            .Select(p => p.EnumerateObject().First().Name)];

        // grok first (it creates the fields), then the date, then renames, constants, and removals
        // last — removing a field before something reads it is the classic way to lose it.
        kinds.Should().Equal("grok", "date", "rename", "set", "remove");
    }

    [Fact]
    public void ADateProcessorTargetsAtTimestamp_WithEveryFormatGiven()
    {
        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildPipelineJson(SamplePipeline()));

        System.Text.Json.JsonElement date = doc.RootElement.GetProperty("processors")[1].GetProperty("date");
        date.GetProperty("target_field").GetString().Should().Be("@timestamp");
        date.GetProperty("formats").EnumerateArray().Select(f => f.GetString())
            .Should().Equal("ISO8601", "UNIX_MS");
    }

    [Fact]
    public void RenamesAndRemovalsTolerateADocumentThatLacksTheField()
    {
        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildPipelineJson(SamplePipeline()));

        System.Text.Json.JsonElement processors = doc.RootElement.GetProperty("processors");
        processors[2].GetProperty("rename").GetProperty("ignore_missing").GetBoolean().Should().BeTrue();
        processors[4].GetProperty("remove").GetProperty("ignore_missing").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void CustomProcessorsAreAppendedAsJson_NotSplicedAsText()
    {
        ElasticsearchIngestPipeline p = SamplePipeline();
        p.CustomProcessorsJson = """[{"lowercase": {"field": "level"}}, {"set": {"field": "kept", "value": "[bracketed]"}}]""";

        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildPipelineJson(p));

        string[] kinds = [.. doc.RootElement.GetProperty("processors").EnumerateArray()
            .Select(x => x.EnumerateObject().First().Name)];

        kinds.Should().Equal("grok", "date", "rename", "set", "remove", "lowercase", "set");
        // A value containing brackets survives, which text splicing would not have guaranteed.
        doc.RootElement.GetProperty("processors")[6].GetProperty("set").GetProperty("value").GetString()
            .Should().Be("[bracketed]");
    }

    [Fact]
    public void AnEmptyPipelineIsStillValid()
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
            ElasticsearchService.BuildPipelineJson(new ElasticsearchIngestPipeline { Name = "passthrough" }));

        doc.RootElement.GetProperty("processors").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("description").GetString().Should().Contain("passthrough");
    }

    [Fact]
    public async Task AGrokPatternWithNoFieldToReadIsRefused()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ElasticsearchIngestPipeline p = SamplePipeline();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        p.GrokField = null;

        Func<Task> act = () => sut.SavePipelineAsync(p);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a field to read from*");
    }

    [Theory]
    [InlineData("msg-message", "*not a rename*")]
    [InlineData("a:b:c", "*not a rename*")]
    public async Task MalformedRenamePairsAreRefused(string pairs, string message)
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ElasticsearchIngestPipeline p = SamplePipeline();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        p.RenameFields = pairs;

        Func<Task> act = () => sut.SavePipelineAsync(p);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public async Task CustomProcessorsThatAreNotAJsonArrayAreRefused()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ElasticsearchIngestPipeline p = SamplePipeline();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        p.CustomProcessorsJson = """{"lowercase": {"field": "level"}}""";

        Func<Task> act = () => sut.SavePipelineAsync(p);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*must be a JSON array*");
    }

    [Fact]
    public async Task SavingAPipelineAppliesItAndRecordsIt()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        ElasticsearchIngestPipeline p = SamplePipeline();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;

        await sut.SavePipelineAsync(p);

        applied.Should().Contain(m => m.Contains("_ingest/pipeline/app-logs"));
        ElasticsearchIngestPipeline stored = await db.ElasticsearchIngestPipelines.AsNoTracking().SingleAsync();
        stored.LastAppliedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task APipelineATemplateStillUsesCannotBeDeleted()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        c.WarmCount = 2;
        c.ColdCount = 2;
        await db.SaveChangesAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ElasticsearchIngestPipeline p = SamplePipeline();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        ElasticsearchIngestPipeline saved = await sut.SavePipelineAsync(p);

        ElasticsearchIlmPolicy policy = SamplePolicy();
        policy.TenantId = tenantId;
        policy.ElasticsearchClusterId = c.Id;
        policy.DefaultPipelineName = saved.Name;
        await sut.CreatePolicyAsync(policy);

        // A template naming a pipeline that does not exist fails every write through it.
        Func<Task> act = () => sut.DeletePipelineAsync(tenantId, saved.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*still send documents through*");
    }

    [Fact]
    public void ATemplateCarriesItsDefaultPipeline()
    {
        ElasticsearchIlmPolicy p = SamplePolicy();
        p.DefaultPipelineName = "app-logs";

        using System.Text.Json.JsonDocument doc =
            System.Text.Json.JsonDocument.Parse(ElasticsearchService.BuildIndexTemplateJson(p));

        doc.RootElement.GetProperty("template").GetProperty("settings")
            .GetProperty("index.default_pipeline").GetString().Should().Be("app-logs");
    }

    [Fact]
    public void TheSimulationSendsThePipelineInline_SoAnUnsavedEditCanBeTried()
    {
        string script = ElasticsearchService.BuildPipelineSimulateScript(
            SampleCluster(), SamplePipeline(), """{"message": "WARN disk almost full"}""");

        script.Should().Contain("_ingest/pipeline/_simulate");
        script.Should().Contain("\"docs\"");
        script.Should().Contain("\"pipeline\"");
        script.Should().Contain("---ENTKUBE-SIMULATE---");
    }

    [Fact]
    public async Task ASampleThatIsNotJsonIsRefusedBeforeAJobIsStarted()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ElasticsearchIngestPipeline p = SamplePipeline();
        p.TenantId = tenantId;
        p.ElasticsearchClusterId = c.Id;
        ElasticsearchIngestPipeline saved = await sut.SavePipelineAsync(p);

        Func<Task> act = () => sut.SimulatePipelineAsync(tenantId, saved.Id, "not json at all");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not valid JSON*");
    }

    // ──────── Alerting rules ────────

    [Fact]
    public void TheAlertsAreScopedToTheirOwnCluster()
    {
        string rule = ElasticsearchService.BuildExporterPrometheusRule(
            SampleCluster(), new Dictionary<string, string>());

        YamlMappingNode group = (YamlMappingNode)((YamlSequenceNode)((YamlMappingNode)Parse(rule)["spec"])["groups"])[0];
        string[] alerts = [.. ((YamlSequenceNode)group["rules"]).Children.Cast<YamlMappingNode>()
            .Select(r => ((YamlScalarNode)r["alert"]).Value!)];

        alerts.Should().Equal(
            "ElasticsearchRed", "ElasticsearchYellow", "ElasticsearchDiskHigh",
            "ElasticsearchHeapPressure", "ElasticsearchExporterDown");

        // One Prometheus scrapes many clusters; an expression without the cluster label would fire
        // one cluster's alert for another's problem.
        foreach (YamlMappingNode r in ((YamlSequenceNode)group["rules"]).Children.Cast<YamlMappingNode>())
        {
            string expr = ((YamlScalarNode)r["expr"]).Value!;
            expr.Should().Contain("search");
        }
    }

    [Fact]
    public void EveryAlertSaysWhichClusterItIsAbout_InLabels()
    {
        string rule = ElasticsearchService.BuildExporterPrometheusRule(
            SampleCluster(), new Dictionary<string, string>());

        YamlMappingNode group = (YamlMappingNode)((YamlSequenceNode)((YamlMappingNode)Parse(rule)["spec"])["groups"])[0];

        foreach (YamlMappingNode r in ((YamlSequenceNode)group["rules"]).Children.Cast<YamlMappingNode>())
        {
            YamlMappingNode labels = (YamlMappingNode)r["labels"];
            // An incident that cannot be traced back to one cluster is a page nobody can act on.
            ((YamlScalarNode)labels["elasticsearch_cluster"]).Value.Should().Be("search");
            ((YamlScalarNode)labels["namespace"]).Value.Should().Be("search");
            labels.Children.Should().ContainKey(new YamlScalarNode("severity"));
            ((YamlMappingNode)r["annotations"]).Children.Should().ContainKey(new YamlScalarNode("summary"));
        }
    }

    [Fact]
    public void RedPagesQuickly_AndYellowWaitsOutARollingRestart()
    {
        string rule = ElasticsearchService.BuildExporterPrometheusRule(
            SampleCluster(), new Dictionary<string, string>());

        YamlSequenceNode rules = (YamlSequenceNode)((YamlMappingNode)((YamlSequenceNode)
            ((YamlMappingNode)Parse(rule)["spec"])["groups"])[0])["rules"];

        YamlMappingNode red = (YamlMappingNode)rules[0];
        YamlMappingNode yellow = (YamlMappingNode)rules[1];

        ((YamlScalarNode)red["for"]).Value.Should().Be("5m");
        ((YamlScalarNode)((YamlMappingNode)red["labels"])["severity"]).Value.Should().Be("critical");

        // Every rolling restart and every rollover turns a cluster yellow for a few minutes.
        ((YamlScalarNode)yellow["for"]).Value.Should().Be("1h");
        ((YamlScalarNode)((YamlMappingNode)yellow["labels"])["severity"]).Value.Should().Be("warning");
    }

    [Fact]
    public void TheDiskAlertUsesElasticsearchsOwnWatermark()
    {
        string rule = ElasticsearchService.BuildExporterPrometheusRule(
            SampleCluster(), new Dictionary<string, string>());

        rule.Should().Contain("> 0.85");
        rule.Should().Contain("elasticsearch_filesystem_data_available_bytes");
    }

    [Fact]
    public void SilenceFromTheExporterIsItselfAnAlert()
    {
        string rule = ElasticsearchService.BuildExporterPrometheusRule(
            SampleCluster(), new Dictionary<string, string>());

        // Otherwise every other rule here goes quiet for the wrong reason.
        rule.Should().Contain("ElasticsearchExporterDown");
        rule.Should().Contain("up{job=~\".*search-es-exporter.*\"} == 0");
    }

    [Fact]
    public void ThePrometheusRuleCarriesTheRuleSelectorsLabels_WhichNeedNotMatchTheMonitorsr()
    {
        // kube-prometheus-stack sets both from the same release value, but they are two separate
        // selectors: getting one right and the other wrong is entirely possible.
        ElasticsearchService.PrometheusSelector selector = ElasticsearchService.ParsePrometheusSelector("""
            {"items":[{"spec":{
              "serviceMonitorSelector":{"matchLabels":{"release":"kps"}},
              "ruleSelector":{"matchLabels":{"prometheus":"main","role":"alert-rules"}},
              "serviceMonitorNamespaceSelector":{}
            }}]}
            """);

        selector.RuleLabels.Should().ContainKey("role").WhoseValue.Should().Be("alert-rules");

        string rule = ElasticsearchService.BuildExporterPrometheusRule(SampleCluster(), selector.RuleLabels);
        YamlMappingNode labels = (YamlMappingNode)((YamlMappingNode)Parse(rule)["metadata"])["labels"];
        ((YamlScalarNode)labels["role"]).Value.Should().Be("alert-rules");
        ((YamlScalarNode)labels["prometheus"]).Value.Should().Be("main");
    }

    [Fact]
    public void WithNoRuleSelector_TheMonitorsLabelsAreUsed()
    {
        ElasticsearchService.PrometheusSelector selector = ElasticsearchService.ParsePrometheusSelector("""
            {"items":[{"spec":{"serviceMonitorSelector":{"matchLabels":{"release":"kps"}},"serviceMonitorNamespaceSelector":{}}}]}
            """);

        selector.RuleLabels.Should().ContainKey("release").WhoseValue.Should().Be("kps");
    }

    [Fact]
    public async Task TurningOnMetricsInstallsTheAlertsToo()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        ArrangeSucceedingJob();

        k8s.Setup(x => x.GetJsonAllNamespacesAsync("prometheuses.monitoring.coreos.com",
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[{"spec":{"serviceMonitorSelector":{"matchLabels":{"release":"kps"}},"serviceMonitorNamespaceSelector":{}}}]}""");

        List<string> applied = [];
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        string note = await sut.EnableMonitoringAsync(tenantId, c.Id, indexMetrics: false);

        applied.Should().Contain(m => m.Contains("kind: PrometheusRule") && m.Contains("release: \"kps\""));
        note.Should().Contain("Alerting rules were installed");
    }

    [Fact]
    public async Task TurningOffMetricsRemovesTheAlertsFirst()
    {
        ElasticsearchCluster c = await SeedPlainClusterAsync();
        c.MonitoringEnabled = true;
        await db.SaveChangesAsync();

        List<string> deleted = [];
        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, CancellationToken>((kind, name, _, _, _) => deleted.Add($"{kind}/{name}"))
            .Returns(Task.CompletedTask);

        await sut.DisableMonitoringAsync(tenantId, c.Id);

        // Rules first: leaving them behind would fire ExporterDown for a monitor deliberately removed.
        deleted[0].Should().Be("prometheusrule/search-es-exporter");
    }
}
