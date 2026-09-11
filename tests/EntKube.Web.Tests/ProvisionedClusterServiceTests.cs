using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The cluster spec is what day-2 edits, so the things asserted here are the ones that decide
/// whether an edit is safe: that the generation moves when the shape changes, that a pool cannot
/// be removed out from under the last workload, and that the projection into provisioning carries
/// every pool rather than the first.
/// </summary>
public class ProvisionedClusterServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly ProvisionedClusterService sut;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid environmentId = Guid.NewGuid();

    public ProvisionedClusterServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = "testco" });
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = "production" });
        db.SaveChanges();

        sut = new ProvisionedClusterService(new TestDbContextFactory(connection), NullLogger<ProvisionedClusterService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private ProvisionedCluster Spec(string name = "prod-eu-1") => new()
    {
        TenantId = tenantId,
        EnvironmentId = environmentId,
        OpenStackConnectionId = Guid.NewGuid(),
        Name = name,
        ControlPlaneFlavor = "b.4c8gb",
        ExternalNetworkId = "ext-net",
        BaseImageName = "ubuntu-22.04",
        BootstrapFlavor = "b.2c4gb",
        BootstrapNetworkId = "tenant-net"
    };

    private static ProvisionedWorkerPool Pool(string name, string flavor = "b.4c8gb", int count = 3) =>
        new() { Name = name, Flavor = flavor, Count = count };

    // ── Generation ──

    [Fact]
    public async Task A_new_spec_starts_unobserved()
    {
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general")]);

        created.Generation.Should().Be(1);
        created.ObservedGeneration.Should().Be(0, "nothing has reconciled it yet");
    }

    [Fact]
    public async Task Changing_the_shape_moves_the_generation_past_what_was_observed()
    {
        // This is how "has the cluster caught up" stays answerable without diffing everything.
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general")]);
        await sut.RecordObservationAsync(created.Id, observedGeneration: 1, observedStateJson: "{}", error: null);

        await sut.UpdateAsync(tenantId, created.Id, c => c.ControlPlaneFlavor = "b.8c16gb");

        ProvisionedCluster reloaded = (await sut.GetAsync(tenantId, created.Id))!;
        reloaded.Generation.Should().Be(2);
        reloaded.ObservedGeneration.Should().Be(1, "the reconciler has not caught up with the edit");
    }

    [Fact]
    public async Task Adding_a_pool_counts_as_a_change_of_shape()
    {
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general")]);

        await sut.AddPoolAsync(tenantId, created.Id, Pool("memory", "b.8c32gb", 2));

        ProvisionedCluster reloaded = (await sut.GetAsync(tenantId, created.Id))!;
        reloaded.Generation.Should().Be(2);
        reloaded.WorkerPools.Should().HaveCount(2);
    }

    [Fact]
    public async Task Recording_an_observation_does_not_move_the_generation()
    {
        // Otherwise the reconciler would chase its own tail: every observation would look like a
        // new edit needing another pass.
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general")]);

        await sut.RecordObservationAsync(created.Id, 1, "{\"ready\":true}", null);

        ProvisionedCluster reloaded = (await sut.GetAsync(tenantId, created.Id))!;
        reloaded.Generation.Should().Be(1);
        reloaded.ObservedGeneration.Should().Be(1);
        reloaded.LastReconciledAt.Should().NotBeNull();
    }

    // ── Pools ──

    [Fact]
    public async Task A_duplicate_pool_name_is_refused()
    {
        // Pool names become CAPI resource names, so two of them collapse into one — the exact
        // failure the authored manifests exist to remove.
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general")]);

        Func<Task> add = () => sut.AddPoolAsync(tenantId, created.Id, Pool("General"));

        await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already has a pool*");
    }

    [Fact]
    public async Task The_last_worker_pool_cannot_be_removed()
    {
        // A cluster with no workers has nowhere to run anything, and finding that out by watching
        // the machines disappear is not the moment to notice.
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general")]);
        Guid poolId = created.WorkerPools.Single().Id;

        Func<Task> remove = () => sut.RemovePoolAsync(tenantId, created.Id, poolId);

        await remove.Should().ThrowAsync<InvalidOperationException>().WithMessage("*only worker pool*");
    }

    [Fact]
    public async Task A_pool_can_be_removed_while_another_remains()
    {
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general"), Pool("memory", "b.8c32gb", 2)]);
        Guid memoryId = created.WorkerPools.Single(p => p.Name == "memory").Id;

        await sut.RemovePoolAsync(tenantId, created.Id, memoryId);

        ProvisionedCluster reloaded = (await sut.GetAsync(tenantId, created.Id))!;
        reloaded.WorkerPools.Should().ContainSingle().Which.Name.Should().Be("general");
        reloaded.Generation.Should().Be(2);
    }

    // ── Tenancy ──

    [Fact]
    public async Task Another_tenants_spec_is_not_reachable()
    {
        ProvisionedCluster created = await sut.CreateAsync(Spec(), [Pool("general")]);

        ProvisionedCluster? stolen = await sut.GetAsync(Guid.NewGuid(), created.Id);

        stolen.Should().BeNull();
    }

    // ── Projection ──

    [Fact]
    public void Every_pool_survives_the_projection_into_provisioning()
    {
        // The whole point of the spec being rows: what the operator asked for is what gets built.
        ProvisionedCluster spec = Spec();
        spec.WorkerPools =
        [
            new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 3 },
            new ProvisionedWorkerPool { Name = "memory", Flavor = "b.8c32gb", Count = 2, DiskGb = 200 }
        ];

        OpenStackProvisioningConfig config = ProvisionedClusterService.ToConfig(spec);

        config.WorkerPools.Should().HaveCount(2);
        config.WorkerPools.Single(p => p.Name == "memory").Flavor.Should().Be("b.8c32gb");
        config.WorkerPools.Single(p => p.Name == "memory").DiskGb.Should().Be(200);
    }

    [Fact]
    public void A_managed_network_projects_as_no_network_so_capo_creates_one()
    {
        ProvisionedCluster spec = Spec();
        spec.NetworkMode = ClusterNetworkMode.Managed;
        spec.NodeNetworkId = "left-over-from-an-earlier-edit";

        OpenStackProvisioningConfig config = ProvisionedClusterService.ToConfig(spec);

        // A stale id must not leak through, or CAPO attaches nodes to a network the operator
        // stopped choosing.
        config.NodeNetworkId.Should().BeNull();
    }

    [Fact]
    public void Labels_and_taints_round_trip_through_the_stored_json()
    {
        OpenStackProvisioningConfig config = new()
        {
            OpenStackConnectionId = Guid.NewGuid(),
            ClusterName = "prod-eu-1",
            ControlPlaneFlavor = "b.4c8gb",
            ExternalNetworkId = "ext-net",
            BaseImageName = "ubuntu-22.04",
            BootstrapFlavor = "b.2c4gb",
            BootstrapNetworkId = "tenant-net",
            WorkerPools =
            [
                new WorkerPool
                {
                    Name = "gpu",
                    Flavor = "g.8c32gb",
                    Count = 2,
                    Labels = new Dictionary<string, string> { ["workload"] = "gpu" },
                    Taints = [new NodeTaint { Key = "nvidia.com/gpu", Value = "true", Effect = "NoSchedule" }]
                }
            ]
        };

        (ProvisionedCluster spec, List<ProvisionedWorkerPool> pools) =
            ProvisionedClusterService.FromConfig(Guid.NewGuid(), Guid.NewGuid(), config);
        spec.WorkerPools = pools;

        WorkerPool restored = ProvisionedClusterService.ToConfig(spec).WorkerPools.Single();

        restored.Labels.Should().ContainKey("workload").WhoseValue.Should().Be("gpu");
        restored.Taints.Should().ContainSingle().Which.Key.Should().Be("nvidia.com/gpu");
    }

    [Fact]
    public void A_blueprint_config_with_no_network_becomes_a_managed_network_spec()
    {
        OpenStackProvisioningConfig config = new()
        {
            OpenStackConnectionId = Guid.NewGuid(),
            ClusterName = "prod-eu-1",
            NodeNetworkId = null,
            WorkerPools = [new WorkerPool { Name = "general", Flavor = "b.4c8gb" }]
        };

        (ProvisionedCluster spec, _) = ProvisionedClusterService.FromConfig(Guid.NewGuid(), Guid.NewGuid(), config);

        spec.NetworkMode.Should().Be(ClusterNetworkMode.Managed);
    }

    // ── Validation ──

    [Fact]
    public void An_even_control_plane_is_rejected_before_any_machine_exists()
    {
        ProvisionedCluster spec = Spec();
        spec.ControlPlaneCount = 2;

        ProvisionedClusterService.Validate(spec, [new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb" }])
            .Should().Contain(e => e.Contains("quorum"));
    }

    [Fact]
    public void Half_configured_autoscaling_is_reported_rather_than_silently_ignored()
    {
        // One bound without the other produces a MachineDeployment the autoscaler will not touch,
        // and an operator who believes the pool scales.
        ProvisionedWorkerPool pool = new() { Name = "general", Flavor = "b.4c8gb", MinCount = 2 };

        ProvisionedClusterService.Validate(Spec(), [pool])
            .Should().Contain(e => e.Contains("both a minimum and a maximum"));
    }

    [Fact]
    public void A_maximum_below_the_minimum_is_caught()
    {
        ProvisionedWorkerPool pool = new() { Name = "general", Flavor = "b.4c8gb", MinCount = 5, MaxCount = 2 };

        ProvisionedClusterService.Validate(Spec(), [pool])
            .Should().Contain(e => e.Contains("maximum below its minimum"));
    }

    [Fact]
    public void A_complete_spec_has_nothing_to_report()
    {
        ProvisionedClusterService.Validate(Spec(), [new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb" }])
            .Should().BeEmpty();
    }
}
