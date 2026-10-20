using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose node inventory a method acts on.
///
/// <para>A <see cref="ClusterServer"/> row is how a cluster's hardware is written down: the
/// node's address, its <b>management address</b>, the SSH user and port, and the jump host it
/// is reached through. These are records rather than live connection settings — the screen
/// says so when it offers to remove one — so what is at stake on the read is knowing another
/// tenant's hardware and how it is reached, and on the write their records being correct.</para>
///
/// <para>ClusterServer carries no TenantId; the cluster it belongs to does, so every check
/// here is a traversal. The page that calls these three already passed TenantId to every
/// other method on the service.</para>
/// </summary>
public class NodeInventoryTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly FleetDbContext db;
    private readonly NodeManagementService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid ourClusterId = Guid.NewGuid();

    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid theirClusterId = Guid.NewGuid();
    private readonly Guid theirServerId = Guid.NewGuid();

    public NodeInventoryTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        TestDbContextFactory factory = new(connection);
        db = ((IDbContextFactory<FleetDbContext>)factory).CreateDbContext();
        db.Database.EnsureCreated();

        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Tenants.AddRange(
                new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
                new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });

            Guid ourEnv = Guid.NewGuid();
            Guid theirEnv = Guid.NewGuid();
            seed.Set<Data.Environment>().AddRange(
                new Data.Environment { Id = ourEnv, TenantId = tenantId, Name = "production" },
                new Data.Environment { Id = theirEnv, TenantId = otherTenantId, Name = "production" });

            seed.KubernetesClusters.AddRange(
                new KubernetesCluster
                {
                    Id = ourClusterId, TenantId = tenantId, EnvironmentId = ourEnv,
                    Name = "entit-prod", ApiServerUrl = "https://k8s.entit.example",
                },
                new KubernetesCluster
                {
                    Id = theirClusterId, TenantId = otherTenantId, EnvironmentId = theirEnv,
                    Name = "annan-prod", ApiServerUrl = "https://k8s.annan.example",
                });
            seed.SaveChanges();
        }

        db.Set<ClusterServer>().Add(new ClusterServer
        {
            Id = theirServerId,
            ClusterId = theirClusterId,
            DisplayName = "annan-node-1",
            IpAddress = "10.90.0.11",
            ManagementIpAddress = "10.91.0.11",
            SshUser = "annan-ops",
            JumpHost = "bastion.annan.example",
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        sut = new NodeManagementService(
            factory, new AuditService(factory, new EntKube.Web.Modules.Api.DeliveryApi(factory)), null!,
            NullLogger<NodeManagementService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Another_tenants_node_inventory_is_not_listable()
    {
        // A positive first, so the empty below is a refusal and not an empty table.
        await sut.SaveServerAsync(tenantId, new ClusterServer
        {
            ClusterId = ourClusterId, DisplayName = "entit-node-1", IpAddress = "10.1.0.11",
        });

        (await sut.GetServersAsync(tenantId, ourClusterId)).Should().ContainSingle();
        (await sut.GetServersAsync(tenantId, theirClusterId)).Should().BeEmpty();
    }

    /// <summary>
    /// A record can be created only against a cluster that is ours — the ClusterId arrives on
    /// the entity, so nothing else says which cluster the row is filed under.
    /// </summary>
    [Fact]
    public async Task A_server_record_cannot_be_added_to_another_tenants_cluster()
    {
        Func<Task> act = () => sut.SaveServerAsync(tenantId, new ClusterServer
        {
            ClusterId = theirClusterId, DisplayName = "planted", IpAddress = "10.0.0.1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cluster not found, or it is not this tenant's.");

        (await db.Set<ClusterServer>().AsNoTracking()
            .CountAsync(s => s.ClusterId == theirClusterId)).Should().Be(1);
    }

    /// <summary>
    /// ⚠️ Editing theirs names OUR cluster on the entity, so the cluster check passes and the
    /// row check is what has to refuse. Naming their cluster instead would be caught by the
    /// earlier guard, and the test would pass without the row check existing at all.
    /// </summary>
    [Fact]
    public async Task Another_tenants_server_record_cannot_be_rewritten()
    {
        Func<Task> act = () => sut.SaveServerAsync(tenantId, new ClusterServer
        {
            Id = theirServerId,
            ClusterId = ourClusterId,
            DisplayName = "taken",
            IpAddress = "10.0.0.1",
            SshUser = "root",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Server not found, or it is not this tenant's.");

        ClusterServer stored = await db.Set<ClusterServer>().AsNoTracking()
            .FirstAsync(s => s.Id == theirServerId);

        stored.DisplayName.Should().Be("annan-node-1");
        stored.SshUser.Should().Be("annan-ops");
    }

    [Fact]
    public async Task Another_tenants_server_record_cannot_be_deleted()
    {
        await sut.DeleteServerAsync(tenantId, theirServerId);

        (await db.Set<ClusterServer>().AsNoTracking().AnyAsync(s => s.Id == theirServerId))
            .Should().BeTrue();

        // Ours does go, or the survival above proves nothing.
        ClusterServer ours = await sut.SaveServerAsync(tenantId, new ClusterServer
        {
            ClusterId = ourClusterId, DisplayName = "entit-node-2",
        });

        await sut.DeleteServerAsync(tenantId, ours.Id);
        (await db.Set<ClusterServer>().AsNoTracking().AnyAsync(s => s.Id == ours.Id))
            .Should().BeFalse();
    }
}
