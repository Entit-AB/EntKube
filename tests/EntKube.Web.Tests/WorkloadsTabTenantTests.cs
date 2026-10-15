using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The workloads view resolves a cluster when it is mounted on a cluster page.
///
/// <para><b>The bug this covers was reported from a running instance.</b> Every workload view on a
/// cluster page said <c>"Cluster has no kubeconfig configured."</c> about clusters whose credential
/// was fine and that every other tab on the same page could reach. The cause was not a credential
/// at all: <c>ClusterDetail</c> mounted this tab with a cluster id and <em>no tenant</em>, the tab
/// defaulted its tenant to <c>Guid.Empty</c>, and <c>IClusterClientFactory.ForAsync</c> answers
/// "no" for a cluster that is not the asking tenant's — which this reported as a missing
/// kubeconfig.</para>
///
/// <para><b>Why a render test and not a static one.</b> I tried a check over the Razor sources that
/// every mount site supplies each <c>[EditorRequired]</c> parameter. All thirteen hits were false
/// positives: <c>builder.OpenComponent&lt;WorkloadsTab&gt;(0)</c> matches as a tag, and an
/// attribute like <c>ValueChanged="@(v => x = v ?? "")"</c> contains quotes that desync any
/// regex-based attribute scan. A guard needing thirteen exemptions would hide the fourteenth real
/// one, so the check is here, where the component is actually rendered.</para>
/// </summary>
public class WorkloadsTabTenantTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public WorkloadsTabTenantTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();

        Guid envId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "primary", ApiServerUrl = "https://c:6443",
        });
        db.SaveChanges();

        VaultService vault = testDb.CreateVaultService();
        vault.InitializeVaultAsync(tenantId).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid)
            .GetAwaiter().GetResult();

        // Empty cluster: the point is which cluster is resolved, not what is running on it.
        k8s.Setup(x => x.GetJsonAllNamespacesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");

        Services.AddSingleton(testDb.Factory);
        Services.AddSingleton(new WorkloadService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            NullLogger<WorkloadService>.Instance));
        Services.AddSingleton(new TenantService(
            testDb.Factory, vault, new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory)));
    }

    public new void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Mounted the way <c>ClusterDetail</c> mounts it. Before the fix this rendered the credential
    /// message; the assertion is deliberately on the <em>absence</em> of that text, because that is
    /// the symptom somebody reported.
    /// </summary>
    [Fact]
    public void A_cluster_page_mount_does_not_report_a_missing_kubeconfig()
    {
        IRenderedComponent<WorkloadsTab> tab = Render<WorkloadsTab>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.ClusterId, clusterId));

        tab.Markup.Should().NotContain("no kubeconfig",
            "the cluster's credential is stored and every other tab on that page can reach it");
        tab.Markup.Should().NotContain("not this tenant's");
    }

    /// <summary>
    /// A mount that forgets the tenant says so, instead of blaming the cluster. This is the whole
    /// remedy: failing closed was right, reporting it as a credential problem was not.
    /// </summary>
    [Fact]
    public void A_mount_with_no_tenant_names_the_wiring_mistake()
    {
        IRenderedComponent<WorkloadsTab> tab = Render<WorkloadsTab>(p => p
            .Add(c => c.ClusterId, clusterId));

        tab.Markup.Should().Contain("without a TenantId");
        tab.Markup.Should().Contain("not a problem with the cluster");
        tab.Markup.Should().NotContain("no kubeconfig",
            "naming the wrong cause is what sent somebody to look at their cluster credentials");
    }
}
