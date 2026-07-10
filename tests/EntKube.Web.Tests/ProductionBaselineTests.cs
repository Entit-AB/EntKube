using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The production baseline the New Cluster wizard installs after provisioning, and the
/// one-shot blueprint authoring helper it uses to launch a provision+bootstrap run.
/// </summary>
public sealed class ProductionBaselineTests
{
    [Fact]
    public void AllBaselineSteps_ReferenceExistingCatalogEntries()
    {
        foreach (BaselineCapability cap in ProductionBaseline.Capabilities)
            foreach (BaselineStep step in cap.Steps)
                ComponentCatalog.GetByKey(step.CatalogKey)
                    .Should().NotBeNull($"baseline references catalog key '{step.CatalogKey}'");
    }

    [Fact]
    public void BaselineNeverIncludesAutoInjectedPlatformComponents()
    {
        // provision → ccm → cinder-csi (and the CNI) are prepended to the run automatically;
        // a baseline must never list them or they'd install twice.
        IEnumerable<string> keys = ProductionBaseline.Capabilities.SelectMany(c => c.Steps).Select(s => s.CatalogKey);
        keys.Should().NotContain(new[] { "cilium", "openstack-ccm", "openstack-cinder-csi" });
    }

    [Fact]
    public void BuildSteps_PreservesCapabilityOrder_AndFiltersUnknown()
    {
        IReadOnlyList<BaselineStep> steps = ProductionBaseline.BuildSteps(
            ["node-autoscaling", "ingress-tls", "not-a-real-key"]);

        // ingress-tls precedes node-autoscaling in the declaration, so cert-manager (its first
        // step) must come before cluster-autoscaler regardless of selection order.
        List<string> keys = steps.Select(s => s.CatalogKey).ToList();
        keys.Should().Contain("cert-manager");
        keys.Should().Contain("cluster-autoscaler");
        keys.IndexOf("cert-manager").Should().BeLessThan(keys.IndexOf("cluster-autoscaler"));
        keys.Should().NotContain("not-a-real-key");
    }

    [Fact]
    public void BaselineStep_ResolvesReleaseAndNamespaceFromCatalog()
    {
        BaselineStep step = new("cert-manager");
        CatalogEntry entry = ComponentCatalog.GetByKey("cert-manager")!;

        step.ResolvedReleaseName.Should().Be(entry.DefaultReleaseName);
        step.ResolvedNamespace.Should().Be(entry.DefaultNamespace);
    }

    [Fact]
    public async Task CreateProvisioningBlueprintAsync_PersistsConfigAndBaselineStepsInOrder()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        IDbContextFactory<ApplicationDbContext> dbFactory = new TestDbContextFactory(connection);
        ClusterBlueprintService sut = new(dbFactory);

        Guid tenantId = Guid.NewGuid();
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
            db.SaveChanges();
        }

        OpenStackProvisioningConfig config = new()
        {
            OpenStackConnectionId = Guid.NewGuid(),
            ClusterName = "prod-eu-1",
            NodeImageName = "ubuntu-kube",
            ControlPlaneFlavor = "b.4c8gb",
            WorkerPools = [new WorkerPool { Count = 3, Flavor = "b.8c16gb" }],
            ExternalNetworkId = "ext",
            BootstrapImageName = "ubuntu",
            BootstrapFlavor = "b.2c4gb",
            BootstrapNetworkId = "tenant-net",
        };
        IReadOnlyList<BaselineStep> baseline = ProductionBaseline.BuildSteps(["ingress-tls", "storage"]);

        ClusterBlueprint bp = await sut.CreateProvisioningBlueprintAsync(tenantId, "prod-eu-1-baseline", config, baseline);

        ClusterBlueprint reloaded = (await sut.GetBlueprintAsync(bp.Id))!;
        reloaded.ProvisioningProvider.Should().Be("openstack");
        reloaded.ProvisioningConfig.Should().NotBeNullOrEmpty();
        OpenStackProvisioningConfig storedConfig = OpenStackProvisioningConfig.FromJson(reloaded.ProvisioningConfig!);
        storedConfig.ClusterName.Should().Be("prod-eu-1");

        // Steps are appended in baseline order, all Component-typed.
        List<BlueprintStep> steps = reloaded.Steps.OrderBy(s => s.Order).ToList();
        steps.Select(s => s.Key).Should().ContainInOrder(baseline.Select(b => b.CatalogKey));
        steps.Should().OnlyContain(s => s.StepType == BlueprintStepType.Component);
    }

    [Fact]
    public async Task CreateProvisioningBlueprintAsync_RejectsInvalidConfig()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        IDbContextFactory<ApplicationDbContext> dbFactory = new TestDbContextFactory(connection);
        ClusterBlueprintService sut = new(dbFactory);
        using (ApplicationDbContext db = dbFactory.CreateDbContext()) { db.Database.EnsureCreated(); }

        Func<Task> act = () => sut.CreateProvisioningBlueprintAsync(
            Guid.NewGuid(), "bad", new OpenStackProvisioningConfig(), []);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
