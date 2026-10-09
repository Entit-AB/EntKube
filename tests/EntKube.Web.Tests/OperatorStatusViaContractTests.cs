using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// "Is this operator installed" asked through <c>ICatalogApi</c> instead of Catalog's table.
///
/// <para><b>What is worth testing about a read.</b> Not that it returns rows — that the predicate
/// did not widen. Each of these services matches a specific list of aliases against specific
/// columns, and the lists are <em>not</em> symmetrical: Elasticsearch accepts <c>eck-operator</c>
/// as a name or a chart but only <c>elastic-operator</c> as a release; Kafka accepts one string
/// across all three. A contract method taking the keys and matching each against every column
/// would answer "installed" for components these have always rejected, and the symptom is a
/// Services page offering to create a cluster that no operator will reconcile.</para>
///
/// <para>Each test below installs a component that the predicate must <em>not</em> match, beside
/// one it must.</para>
/// </summary>
public class OperatorStatusViaContractTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private Guid ours, theirs, ourCluster, theirCluster;

    public OperatorStatusViaContractTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private EntKube.Web.Modules.Api.CatalogApi Catalog() => new(testDb.Factory);

    private EntKube.Web.Services.Clusters.ClusterClientFactory Clusters() =>
        new(testDb.Factory, k8s.Object);

    private async Task SeedAsync()
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();
        ourCluster = await SeedTenantAsync(ours, "ours");
        theirCluster = await SeedTenantAsync(theirs, "theirs");
    }

    private async Task<Guid> SeedTenantAsync(Guid tenantId, string label)
    {
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        await db.SaveChangesAsync();

        return clusterId;
    }

    private async Task InstallAsync(
        Guid clusterId, string name, string? release = null, string? chart = null,
        ComponentStatus status = ComponentStatus.Installed)
    {
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = name,
            ComponentType = "helm", ReleaseName = release, HelmChartName = chart,
            Status = status, Namespace = "operators",
        });
        await db.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════════════════════
    //  Elasticsearch: eck-operator as name or chart, elastic-operator as release
    // ════════════════════════════════════════════════════════════════

    private ElasticsearchService Elastic() =>
        new(testDb.Factory, Clusters(), testDb.CreateVaultService(),
            new AuditService(testDb.Factory), Catalog(),
            NullLogger<ElasticsearchService>.Instance);

    [Theory]
    [InlineData("eck-operator", null, null)]
    [InlineData("renamed", "elastic-operator", null)]
    [InlineData("renamed", null, "eck-operator")]
    public async Task The_eck_operator_is_found_by_each_alias_it_actually_answers_to(
        string name, string? release, string? chart)
    {
        await SeedAsync();
        await InstallAsync(ourCluster, name, release, chart);

        ElasticsearchOperatorStatus status = await Elastic().GetOperatorStatusAsync(ours);

        status.OperatorAvailable.Should().BeTrue();
        status.OperatorClusterName.Should().Be("ours-cluster",
            "the contract carries the cluster name inline, which is why it does");
    }

    /// <summary>
    /// The asymmetry. <c>elastic-operator</c> is accepted as a <em>release</em> name and never as
    /// a component name or a chart — so a uniform "match every key against every column" contract
    /// method would answer true here, and this would report an operator that is not installed.
    /// </summary>
    [Theory]
    [InlineData("elastic-operator", null, null)]
    [InlineData("renamed", null, "elastic-operator")]
    [InlineData("renamed", "eck-operator", null)]
    public async Task An_alias_in_the_wrong_column_is_not_a_match(
        string name, string? release, string? chart)
    {
        await SeedAsync();
        await InstallAsync(ourCluster, name, release, chart);

        (await Elastic().GetOperatorStatusAsync(ours)).OperatorAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task An_operator_still_installing_does_not_count()
    {
        await SeedAsync();
        await InstallAsync(ourCluster, "eck-operator", status: ComponentStatus.Installing);

        (await Elastic().GetOperatorStatusAsync(ours)).OperatorAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_operator_is_not_ours()
    {
        await SeedAsync();
        await InstallAsync(theirCluster, "eck-operator");

        (await Elastic().GetOperatorStatusAsync(ours)).OperatorAvailable.Should().BeFalse();
        (await Elastic().GetOperatorStatusAsync(theirs)).OperatorAvailable.Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════
    //  Databases: two operators, one answer each
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task The_database_status_reports_each_operator_separately()
    {
        await SeedAsync();
        await InstallAsync(ourCluster, "cloudnative-pg");

        DatabaseService databases = new(testDb.Factory, Clusters(), Catalog());

        DatabaseOperatorStatus status = await databases.GetOperatorStatusAsync(ours);

        status.CnpgAvailable.Should().BeTrue();
        status.CnpgClusterName.Should().Be("ours-cluster");
        status.MongoDbAvailable.Should().BeFalse("only CNPG is installed");
    }

    /// <summary>
    /// MongoDB's chart is only ever <c>community-operator</c>, while its two operator names are
    /// accepted as a name or a release. A component whose <em>chart</em> is named like the
    /// operator is not the operator.
    /// </summary>
    [Fact]
    public async Task A_mongodb_operator_name_in_the_chart_column_is_not_a_match()
    {
        await SeedAsync();
        await InstallAsync(ourCluster, "something-else", chart: "mongodb-operator");

        DatabaseService databases = new(testDb.Factory, Clusters(), Catalog());

        (await databases.GetOperatorStatusAsync(ours)).MongoDbAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_mongodb_operator_by_its_chart_name_is_a_match()
    {
        await SeedAsync();
        await InstallAsync(ourCluster, "renamed", chart: "community-operator");

        DatabaseService databases = new(testDb.Factory, Clusters(), Catalog());

        (await databases.GetOperatorStatusAsync(ours)).MongoDbAvailable.Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════
    //  The purge preview count, which has to be exact
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The number shown before a tenant is deleted. It was a <c>CountAsync</c> on the table and is
    /// now the length of the contract's tenant listing — which must be the same rows, because an
    /// undercount in a type-to-confirm delete dialog is the worst possible place for one.
    /// </summary>
    [Fact]
    public async Task The_purge_preview_counts_only_this_tenants_components()
    {
        await SeedAsync();
        await InstallAsync(ourCluster, "cloudnative-pg");
        await InstallAsync(ourCluster, "kyverno");
        await InstallAsync(theirCluster, "cloudnative-pg");

        TenantService tenants = new(testDb.Factory, testDb.CreateVaultService(), Catalog());

        TenantPurgePreview? preview = await tenants.GetTenantPurgePreviewAsync(ours);

        preview.Should().NotBeNull();
        preview!.Components.Should().Be(2, "the other tenant's component is not ours to delete");
    }

    /// <summary>
    /// A component that is not installed still counts for the purge: the row exists and will be
    /// deleted, which is what the preview is counting.
    /// </summary>
    [Fact]
    public async Task The_purge_preview_counts_components_whatever_their_status()
    {
        await SeedAsync();
        await InstallAsync(ourCluster, "cloudnative-pg", status: ComponentStatus.Failed);

        TenantService tenants = new(testDb.Factory, testDb.CreateVaultService(), Catalog());

        (await tenants.GetTenantPurgePreviewAsync(ours))!.Components.Should().Be(1);
    }
}
