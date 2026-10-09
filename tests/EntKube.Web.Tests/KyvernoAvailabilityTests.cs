using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether Kyverno is available to an environment — the question the Governance tab asks before it
/// offers to author a policy.
///
/// <para><b>It had no test at all</b>, which is why it is here: the check moved from a SQL
/// predicate on Catalog's table to <c>ICatalogApi</c> plus the same predicate in memory, and an
/// untested boolean is the easiest thing in the world to invert while the suite stays green. False
/// where it should be true hides the feature; true where it should be false offers to write
/// policies that no admission controller will ever enforce, which is worse — the operator believes
/// the cluster is governed.</para>
///
/// <para>The environment scoping is the part worth pinning. The contract read is tenant-wide, so
/// the filter down to this environment's clusters is now the caller's, and losing it would report
/// Kyverno as available everywhere in the tenant the moment it was installed anywhere.</para>
/// </summary>
public class KyvernoAvailabilityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KyvernoPolicyService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid envA = Guid.NewGuid();
    private readonly Guid envB = Guid.NewGuid();
    private Guid clusterA, clusterB;

    public KyvernoAvailabilityTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        TestDbContextFactory dbFactory = new(connection);

        sut = new KyvernoPolicyService(
            dbFactory,
            new Mock<IKubernetesClientFactory>().Object,
            new EntKube.Web.Services.ClusterChanges.ClusterChangeGate(
                new ConfigurationBuilder().Build(),
                NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance),
            new EntKube.Web.Modules.Api.CatalogApi(dbFactory),
            NullLogger<KyvernoPolicyService>.Instance);

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Gov", Slug = $"gov-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envA, TenantId = tenantId, Name = "production" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envB, TenantId = tenantId, Name = "staging" });

        clusterA = AddCluster(envA, "prod-cluster");
        clusterB = AddCluster(envB, "staging-cluster");

        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddCluster(Guid environmentId, string name)
    {
        Guid id = Guid.NewGuid();

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = id, TenantId = tenantId, EnvironmentId = environmentId, Name = name,
            ApiServerUrl = "https://k8s.example.com",
        });

        return id;
    }

    private void Install(Guid clusterId, string? name = null, string? release = null, string? chart = null,
        ComponentStatus status = ComponentStatus.Installed)
    {
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = name ?? "something",
            ComponentType = "helm", ReleaseName = release, HelmChartName = chart,
            Status = status, Namespace = "kyverno",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task No_clusters_in_the_environment_means_not_available()
    {
        (await sut.IsKyvernoAvailableAsync(tenantId, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task A_cluster_without_kyverno_means_not_available()
    {
        Install(clusterA, name: "cert-manager");

        (await sut.IsKyvernoAvailableAsync(tenantId, envA)).Should().BeFalse();
    }

    [Theory]
    [InlineData("kyverno", null, null)]
    [InlineData("renamed", "kyverno", null)]
    [InlineData("renamed", null, "kyverno")]
    public async Task Kyverno_is_found_by_its_name_its_release_or_its_chart(
        string name, string? release, string? chart)
    {
        Install(clusterA, name: name, release: release, chart: chart);

        (await sut.IsKyvernoAvailableAsync(tenantId, envA)).Should().BeTrue();
    }

    [Fact]
    public async Task An_installation_that_has_not_finished_does_not_count()
    {
        Install(clusterA, name: "kyverno", status: ComponentStatus.Installing);

        (await sut.IsKyvernoAvailableAsync(tenantId, envA)).Should().BeFalse(
            "offering to author policies against a half-installed admission controller tells the "
            + "operator the cluster is governed when nothing is enforcing anything yet");
    }

    /// <summary>
    /// The scoping the conversion made the caller's responsibility. The contract read is
    /// tenant-wide, so dropping the cluster filter would report Kyverno available in every
    /// environment the tenant has.
    /// </summary>
    [Fact]
    public async Task Kyverno_in_another_environment_does_not_make_it_available_here()
    {
        Install(clusterB, name: "kyverno");

        (await sut.IsKyvernoAvailableAsync(tenantId, envB)).Should().BeTrue("it is installed there");
        (await sut.IsKyvernoAvailableAsync(tenantId, envA)).Should().BeFalse("but not here");
    }

    /// <summary>
    /// And the tenant scoping the contract itself applies, which is the reason to ask it rather
    /// than the table: another tenant's Kyverno is not ours, even on an environment id we pass.
    /// </summary>
    [Fact]
    public async Task Another_tenants_kyverno_is_not_available_to_us()
    {
        Guid otherTenant = Guid.NewGuid();
        Guid otherEnv = Guid.NewGuid();
        Guid otherCluster = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = otherTenant, Name = "Other", Slug = $"other-{otherTenant:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = otherEnv, TenantId = otherTenant, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = otherCluster, TenantId = otherTenant, EnvironmentId = otherEnv,
            Name = "their-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.SaveChanges();

        Install(otherCluster, name: "kyverno");

        (await sut.IsKyvernoAvailableAsync(tenantId, otherEnv)).Should().BeFalse();
    }
}
