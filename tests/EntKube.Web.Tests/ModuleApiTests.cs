using EntKube.Contracts.Catalog;
using EntKube.Contracts.Delivery;
using EntKube.Contracts.Fleet;
using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The module contracts answer for one tenant and no other.
///
/// <para><b>Why this is the test worth writing.</b> Before the contracts, every caller wrote
/// its own tenant filter — <c>c.Cluster.TenantId == tenantId</c>, twenty-one times over, by
/// hand. One of those being forgotten would leak another customer's components, and nothing
/// would say so. Moving the filter into the contract only helps if the contract is the thing
/// that is checked, so each one is asked for a neighbour's data here and must refuse.</para>
/// </summary>
public class ModuleApiTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private Guid ourCluster, theirCluster, ourComponent, theirComponent;
    private Guid ourApp, theirApp, ourDeployment, theirDeployment;

    public ModuleApiTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        Seed(ourTenant, "ours", out ourCluster, out ourComponent, out ourApp, out ourDeployment);
        Seed(theirTenant, "theirs", out theirCluster, out theirComponent, out theirApp, out theirDeployment);
        db.SaveChanges();
    }

    private void Seed(Guid tenantId, string label, out Guid clusterId, out Guid componentId,
        out Guid appId, out Guid deploymentId)
    {
        Guid environmentId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        componentId = Guid.NewGuid();
        appId = Guid.NewGuid();
        deploymentId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = label });
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = $"{label}-env" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = $"{label}-cluster", ApiServerUrl = $"https://{label}.example",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "harbor",
            ComponentType = "Helm", Status = Data.ComponentStatus.Installed,
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label}-customer" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = $"{label}-app" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = $"{label}-deploy",
            EnvironmentId = environmentId, ClusterId = clusterId, Namespace = label,
        });
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ICatalogApi Catalog => new CatalogApi(factory);
    private IFleetApi Fleet => new FleetApi(factory);
    private IDeliveryApi Delivery => new DeliveryApi(factory);

    [Fact]
    public async Task Catalog_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        (await Catalog.GetComponentAsync(ourTenant, ourComponent)).Should().NotBeNull();
        (await Catalog.GetComponentAsync(ourTenant, theirComponent)).Should().BeNull(
            "asking for another tenant's component by id must not return it");

        (await Catalog.GetComponentsForTenantAsync(ourTenant)).Should().ContainSingle()
            .Which.ClusterName.Should().Be("ours-cluster");
        (await Catalog.GetComponentsForClusterAsync(ourTenant, theirCluster)).Should().BeEmpty();
        (await Catalog.FindComponentsAsync(ourTenant, "harbor")).Should().ContainSingle();
    }

    // ════════════════════════════════════════════════════════════════
    //  Configuring a component through the contract
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Catalog_merges_form_values_into_what_is_already_stored()
    {
        await SetHelmValuesDirectlyAsync(ourComponent, "existing:\n  keep: yes\n");

        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, ourComponent, new Dictionary<string, string> { ["added.field"] = "new" });

        // Merging, not replacing: the point of the method is that a caller patching one field
        // does not have to load, edit and write back the whole document itself.
        after.Should().NotBeNull();
        after!.HelmValues.Should().Contain("keep").And.Contain("new");
    }

    [Fact]
    public async Task Catalog_refuses_to_configure_another_tenants_component_and_writes_nothing()
    {
        await SetHelmValuesDirectlyAsync(theirComponent, "theirs: untouched\n");

        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, theirComponent, new Dictionary<string, string> { ["injected"] = "yes" });

        after.Should().BeNull("a component that is not ours must read as absent");

        // The null is only half the guarantee. A refusal that still wrote would be worse than
        // no contract at all, so the row is checked too.
        using ApplicationDbContext check = factory.CreateDbContext();
        ClusterComponent theirs = check.ClusterComponents.Single(c => c.Id == theirComponent);
        theirs.HelmValues.Should().Be("theirs: untouched\n");
    }

    [Fact]
    public async Task Catalog_set_replaces_the_values_outright()
    {
        await SetHelmValuesDirectlyAsync(ourComponent, "old: gone\n");

        InstalledComponent? after = await Catalog.SetHelmValuesAsync(
            ourTenant, ourComponent, "fresh: document\n");

        after!.HelmValues.Should().Be("fresh: document\n");
        after.HelmValues.Should().NotContain("old");
    }

    [Fact]
    public async Task Catalog_leaves_the_configuration_blob_alone_when_none_is_given()
    {
        using (ApplicationDbContext seed = factory.CreateDbContext())
        {
            ClusterComponent c = seed.ClusterComponents.Single(x => x.Id == ourComponent);
            c.Configuration = "{\"StorageLinkId\":\"keep-me\"}";
            seed.SaveChanges();
        }

        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, ourComponent, new Dictionary<string, string> { ["a"] = "b" });

        // Null means "leave it", not "clear it" — the one ambiguity the contract documents away.
        after!.Configuration.Should().Be("{\"StorageLinkId\":\"keep-me\"}");
    }

    [Fact]
    public async Task Catalog_carries_the_configuration_blob_that_callers_deserialize()
    {
        InstalledComponent? after = await Catalog.MergeHelmValuesAsync(
            ourTenant, ourComponent, new Dictionary<string, string> { ["a"] = "b" },
            "{\"StorageLinkId\":\"written\"}");

        // Nine foreign call sites read this field. The first version of the record omitted it,
        // which would have stopped every one of them adopting the contract.
        after!.Configuration.Should().Be("{\"StorageLinkId\":\"written\"}");
    }

    /// <summary>Sets a component's Helm values behind the contract's back, to arrange a test.</summary>
    private async Task SetHelmValuesDirectlyAsync(Guid componentId, string helmValues)
    {
        using ApplicationDbContext seed = factory.CreateDbContext();
        ClusterComponent c = seed.ClusterComponents.Single(x => x.Id == componentId);
        c.HelmValues = helmValues;
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Fleet_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        (await Fleet.GetClusterAsync(ourTenant, ourCluster)).Should().NotBeNull();
        (await Fleet.GetClusterAsync(ourTenant, theirCluster)).Should().BeNull();

        (await Fleet.GetClustersAsync(ourTenant)).Should().ContainSingle()
            .Which.EnvironmentName.Should().Be("ours-env");

        // Even handed the other tenant's id explicitly, the filter still applies.
        (await Fleet.GetClustersAsync(ourTenant, [ourCluster, theirCluster]))
            .Should().ContainSingle().Which.Id.Should().Be(ourCluster);
    }

    [Fact]
    public async Task Delivery_answers_for_the_asking_tenant_and_refuses_the_neighbour()
    {
        (await Delivery.GetAppAsync(ourTenant, ourApp)).Should().NotBeNull();
        (await Delivery.GetAppAsync(ourTenant, theirApp)).Should().BeNull();

        (await Delivery.GetDeploymentAsync(ourTenant, ourDeployment)).Should().NotBeNull();
        (await Delivery.GetDeploymentAsync(ourTenant, theirDeployment)).Should().BeNull(
            "tenancy reaches a deployment through its app's customer — the long way round is "
            + "exactly where a hand-written filter goes wrong");

        (await Delivery.GetDeploymentsForTenantAsync(ourTenant)).Should().ContainSingle()
            .Which.AppName.Should().Be("ours-app");
        (await Delivery.GetDeploymentsForClusterAsync(ourTenant, theirCluster)).Should().BeEmpty();
    }

    /// <summary>
    /// A cluster's kubeconfig is resolved from the vault on materialisation. Selecting it
    /// for a caller that only wanted a name would decrypt a credential for no reason, so the
    /// contract does not carry one at all.
    /// </summary>
    [Fact]
    public void A_cluster_summary_carries_no_credential()
    {
        typeof(ClusterSummary).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Kubeconfig", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }
}
