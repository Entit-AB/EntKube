using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The refusal that stands between an operator and a Kyverno that would keep every policy and enforce
/// none of them.
/// </summary>
public class KyvernoUpgradeGuardTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly Mock<IClusterClient> cluster = new();
    private readonly KyvernoUpgradeGuard sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public KyvernoUpgradeGuardTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid envId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId, Name = "prod", ApiServerUrl = "https://k8s.example.com",
        });
        db.SaveChanges();

        cluster.Setup(c => c.GetJsonAllNamespacesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");
        cluster.Setup(c => c.GetJsonAllNamespacesAsync("policies.kyverno.io", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[{"metadata":{"name":"disallow-host-path","namespace":"entit"}},{"metadata":{"name":"theirs","namespace":"entit"}}]}""");
        cluster.Setup(c => c.GetResourceJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HelmExecutionResult { Success = false, Output = "Error from server (NotFound): not found" });

        Mock<IClusterClientFactory> clients = new();
        clients.Setup(c => c.ForAsync(tenantId, clusterId, It.IsAny<CancellationToken>())).ReturnsAsync(cluster.Object);

        sut = new KyvernoUpgradeGuard(new TestDbContextFactory(connection), clients.Object);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid Component(string? version, string chart = "kyverno", string? values = null)
    {
        Guid id = Guid.NewGuid();
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = id, ClusterId = clusterId, Name = $"{chart}-{id:N}", ComponentType = "HelmChart", HelmChartName = chart,
            HelmChartVersion = version, HelmValues = values, Status = ComponentStatus.Installed, Namespace = "kyverno",
        });
        db.SaveChanges();
        return id;
    }

    [Theory]
    [InlineData("3.8.1", false)]
    [InlineData("3.9.2", false)]
    [InlineData("3.10.0-rc.1", true)] // the release candidates carry the same engine
    [InlineData("3.10.0", true)]
    [InlineData("4.0.0", true)]
    [InlineData(null, true)] // unpinned installs the newest
    public async Task Only_a_version_that_stops_enforcing_is_refused(string? version, bool refused)
    {
        string? conflict = await sut.DescribeLegacyPolicyConflictAsync(tenantId, Component(version));

        (conflict is not null).Should().Be(refused, conflict);
    }

    [Fact]
    public async Task A_cluster_with_no_legacy_policies_is_not_refused()
    {
        cluster.Setup(c => c.GetJsonAllNamespacesAsync("policies.kyverno.io", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");

        (await sut.DescribeLegacyPolicyConflictAsync(tenantId, Component("3.10.0"))).Should().BeNull();
    }

    [Fact]
    public async Task Another_chart_is_not_asked_about()
    {
        (await sut.DescribeLegacyPolicyConflictAsync(tenantId, Component("3.10.0", chart: "harbor"))).Should().BeNull();
    }

    [Fact]
    public async Task An_operator_who_acknowledged_it_is_let_through()
    {
        Guid acknowledged = Component("3.10.0", values: "upgrade:\n  acknowledgeLegacyPoliciesNotEnforced: true\n");
        Guid commentedOut = Component("3.10.0", values: "upgrade:\n  # acknowledgeLegacyPoliciesNotEnforced: true\n");

        (await sut.DescribeLegacyPolicyConflictAsync(tenantId, acknowledged)).Should().BeNull();
        (await sut.DescribeLegacyPolicyConflictAsync(tenantId, commentedOut)).Should().NotBeNull();
    }

    [Fact]
    public async Task Another_tenants_component_is_not_described()
    {
        Guid component = Component("3.10.0");

        (await sut.DescribeLegacyPolicyConflictAsync(Guid.NewGuid(), component)).Should().BeNull();
        cluster.Verify(c => c.GetJsonAllNamespacesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void EntKubes_policies_are_told_apart_from_the_rest()
    {
        string text = KyvernoUpgradeGuard.Describe("3.10.0", new KyvernoApi(true, "3.8.1"),
        [
            ("Policy", "entit", "disallow-host-path"),
            ("Policy", "entit", "theirs"),
            ("ClusterPolicy", null, "restrict-cluster-rbac"),
            ("PolicyException", "kyverno", "disallow-host-path"),
        ]);

        string[] halves = text.Split("2 are not EntKube's");
        halves.Should().HaveCount(2, text);
        halves[0].Should().Contain("2 carry the names").And.Contain("Policy in entit: disallow-host-path")
            .And.Contain("ClusterPolicy: restrict-cluster-rbac").And.NotContain("theirs");
        halves[1].Should().Contain("Policy in entit: theirs").And.Contain("PolicyException in kyverno: disallow-host-path");
        text.Should().NotContain("acknowledgeLegacy", "the way past this is not the flag that disables the policies");
    }

    [Fact]
    public void A_kyverno_too_old_for_the_replacements_is_sent_through_the_version_that_runs_both()
    {
        KyvernoUpgradeGuard.Describe("3.10.0", new KyvernoApi(false, "3.5.2"), [("Policy", "a", "b")])
            .Should().Contain("3.5.2").And.Contain("chart 3.9.x first");
        KyvernoUpgradeGuard.Describe("3.10.0", new KyvernoApi(true, "3.8.1"), [("Policy", "a", "b")])
            .Should().NotContain("3.9.x");
    }
}
