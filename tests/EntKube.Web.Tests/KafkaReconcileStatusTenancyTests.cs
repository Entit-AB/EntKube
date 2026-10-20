using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose Kafka cluster a status reconcile writes to.
///
/// <para><b>No live hole, and the PR says so.</b> Both callers of
/// <c>ReconcileStatusAsync</c> reach it with an id from a tenant-scoped query — the detail
/// read fetches on <c>c.TenantId == tenantId</c>, and the poller goes through
/// <c>ReconcilableFor(db, tenantId)</c> inside a per-tenant loop. Nothing in the application
/// could cross it.</para>
///
/// <para>What it closes is the <em>public surface</em>, which is what an API exposes whatever
/// its current callers happen to do — and the two sibling services agree about the shape:
/// <c>RabbitMQService</c> and <c>ElasticsearchService</c> both declare
/// <c>ReconcileStatusAsync(tenantId, clusterId, …)</c>. Kafka's was the only one without.</para>
///
/// <para>The method is public, so a test can cross it directly even though no caller can. That
/// is the difference between "unreachable in the app" and "unprovable" — this one is only the
/// first, so it gets an ordinary crossing rather than a plant.</para>
/// </summary>
public class KafkaReconcileStatusTenancyTests : IDisposable
{
    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly KafkaService sut;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid otherTenantId = Guid.NewGuid();

    public KafkaReconcileStatusTenancyTests()
    {
        testDb = new InterceptingTestDb(new byte[32]);
        db = testDb.CreateContext();

        db.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" },
            new Tenant { Id = otherTenantId, Name = "Annan", Slug = $"annan-{otherTenantId:N}" });
        db.SaveChanges();

        // A real VaultService over the test database rather than null!, so a later test in this
        // class that touches a credential path fails on its own merits instead of on an NRE.
        sut = new KafkaService(
            testDb.Factory, TestServices.NoClusterAccess,
            testDb.CreateVaultService(),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory));
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid SeedCluster(Guid owner, KafkaClusterStatus status)
    {
        string tag = $"{Guid.NewGuid():N}"[..8];
        Guid envId = Guid.NewGuid(), k8sId = Guid.NewGuid(), kafkaId = Guid.NewGuid();

        db.Environments.Add(new Data.Environment
        {
            Id = envId, TenantId = owner, Name = $"production-{tag}",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = k8sId, TenantId = owner, EnvironmentId = envId,
            Name = $"k8s-{tag}", ApiServerUrl = "https://k8s.example.invalid",
        });
        db.KafkaClusters.Add(new KafkaCluster
        {
            Id = kafkaId, TenantId = owner, KubernetesClusterId = k8sId,
            Name = $"events-{tag}", Namespace = "kafka", KafkaVersion = "3.7.0",
            StorageSize = "100Gi", Status = status,
        });
        db.SaveChanges();
        return kafkaId;
    }

    [Fact]
    public async Task Another_tenants_cluster_status_is_not_written()
    {
        Guid theirs = SeedCluster(otherTenantId, KafkaClusterStatus.Creating);

        await sut.ReconcileStatusAsync(tenantId, theirs, ready: true);

        (await db.KafkaClusters.AsNoTracking().FirstAsync(c => c.Id == theirs))
            .Status.Should().Be(KafkaClusterStatus.Creating);
    }

    /// <summary>
    /// The other direction of the same write: marking a healthy cluster as not ready. A status
    /// of Creating on a running broker is what the operator sees, so this is the denial half.
    /// </summary>
    [Fact]
    public async Task Another_tenants_running_cluster_is_not_marked_unready()
    {
        Guid theirs = SeedCluster(otherTenantId, KafkaClusterStatus.Running);

        await sut.ReconcileStatusAsync(tenantId, theirs, ready: false);

        (await db.KafkaClusters.AsNoTracking().FirstAsync(c => c.Id == theirs))
            .Status.Should().Be(KafkaClusterStatus.Running);
    }

    [Fact]
    public async Task Our_own_cluster_status_is_still_reconciled()
    {
        Guid ours = SeedCluster(tenantId, KafkaClusterStatus.Creating);

        await sut.ReconcileStatusAsync(tenantId, ours, ready: true);

        (await db.KafkaClusters.AsNoTracking().FirstAsync(c => c.Id == ours))
            .Status.Should().Be(KafkaClusterStatus.Running);
    }

    /// <summary>
    /// The guard must not break the rule the method exists for: a Running cluster does not
    /// regress to Creating on a transient unready blip.
    /// </summary>
    [Fact]
    public async Task Our_own_running_cluster_does_not_regress_on_a_blip()
    {
        Guid ours = SeedCluster(tenantId, KafkaClusterStatus.Running);

        await sut.ReconcileStatusAsync(tenantId, ours, ready: false);

        (await db.KafkaClusters.AsNoTracking().FirstAsync(c => c.Id == ours))
            .Status.Should().Be(KafkaClusterStatus.Running);
    }
}
