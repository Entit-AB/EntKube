using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The four status-poll sweeps that reach a cluster through a <c>ReconcileAllAsync</c> on a service
/// rather than from the hosted service itself — and so looked exempt from §4.0.1.2 while none of
/// them acted as a tenant.
///
/// <para><b>What this is and is not.</b> For the three data-service polls the conversion changes
/// no reachability: a mis-stamped row's <c>(tenant, cluster)</c> pair was already refused at the
/// seam, which is the point of <c>ForAsync</c>. What changes is where the tenant comes from — the
/// tenant table rather than the row being acted on — which is what lets a tenant's cycle be run,
/// audited and failed in isolation, and what stops the next edit inside one of these loops quietly
/// depending on a row-derived tenant.</para>
///
/// <para><b>The exception is the certificate distributor</b>, whose loop carried a comment saying
/// it already worked this way. It selected every distribution in the installation, took the tenant
/// off each row, and re-read the row with <c>d.Id == distributionId &amp;&amp; d.TenantId ==
/// tenantId</c> — a predicate comparing a row to itself, which can never fail. It preserved the
/// pair, which is why it read as converted.</para>
///
/// <para>These pin the selections, including the status rules that had no coverage at all: a
/// status poll that stops looking at a broker it once marked Failed can never see it recover.</para>
/// </summary>
public sealed class ReconcileSweepTenancyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;

    /// <summary>
    /// The reconcile predicates take DataServices' own context now, so the assertions ask through
    /// it. Seeding still goes through ApplicationDbContext — one database, two views of it, which
    /// is the whole point of the module contexts.
    /// </summary>
    private DataServicesDbContext Scoped =>
        ((IDbContextFactory<DataServicesDbContext>)factory).CreateDbContext();

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid ourK8s = Guid.NewGuid();
    private readonly Guid theirK8s = Guid.NewGuid();

    public ReconcileSweepTenancyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (ApplicationDbContext seed = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            seed.Database.EnsureCreated();
        }
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();

        foreach ((Guid tenantId, Guid clusterId, string label) in
                 new[] { (ours, ourK8s, "ours"), (theirs, theirK8s, "theirs") })
        {
            Guid envId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
            db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = label });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
                Name = $"{label}-k8s", ApiServerUrl = $"https://{label}.example",
            });
        }
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public void A_broker_poll_sees_only_the_asking_tenants_brokers()
    {
        db.RabbitMQClusters.Add(NewRabbit(ours, ourK8s, "ours-broker", RabbitMQClusterStatus.Running));
        db.RabbitMQClusters.Add(NewRabbit(theirs, theirK8s, "theirs-broker", RabbitMQClusterStatus.Running));
        db.SaveChanges();

        RabbitMQService.ReconcilableFor(Scoped, ours).Select(c => c.Name).Should().Equal(["ours-broker"]);
        RabbitMQService.ReconcilableFor(Scoped, theirs).Select(c => c.Name).Should().Equal(["theirs-broker"]);
    }

    /// <summary>
    /// Failed is a verdict from a previous poll, not a terminal state: a broker marked Failed by a
    /// transient blip must still be looked at, or its badge stays wrong until someone clicks
    /// Refresh. Only a broker mid-teardown is left alone. Nothing asserted this before.
    /// </summary>
    [Fact]
    public void A_broker_poll_still_looks_at_a_failed_broker_but_not_one_being_deleted()
    {
        db.RabbitMQClusters.Add(NewRabbit(ours, ourK8s, "failed", RabbitMQClusterStatus.Failed));
        db.RabbitMQClusters.Add(NewRabbit(ours, ourK8s, "deleting", RabbitMQClusterStatus.Deleting));
        db.SaveChanges();

        RabbitMQService.ReconcilableFor(Scoped, ours).Select(c => c.Name).Should().Equal(["failed"]);
    }

    [Fact]
    public void A_kafka_poll_sees_only_the_asking_tenants_clusters_and_skips_failed()
    {
        db.KafkaClusters.Add(NewKafka(ours, ourK8s, "ours-events", KafkaClusterStatus.Running));
        db.KafkaClusters.Add(NewKafka(ours, ourK8s, "ours-failed", KafkaClusterStatus.Failed));
        db.KafkaClusters.Add(NewKafka(theirs, theirK8s, "theirs-events", KafkaClusterStatus.Running));
        db.SaveChanges();

        KafkaService.ReconcilableFor(Scoped, ours).Select(c => c.Name).Should().Equal(["ours-events"]);
        KafkaService.ReconcilableFor(Scoped, theirs).Select(c => c.Name).Should().Equal(["theirs-events"]);
    }

    [Fact]
    public void An_elasticsearch_poll_sees_only_the_asking_tenants_clusters_and_skips_failed()
    {
        db.ElasticsearchClusters.Add(NewElastic(ours, ourK8s, "ours-search", ElasticsearchClusterStatus.Running));
        db.ElasticsearchClusters.Add(NewElastic(ours, ourK8s, "ours-failed", ElasticsearchClusterStatus.Failed));
        db.ElasticsearchClusters.Add(NewElastic(theirs, theirK8s, "theirs-search", ElasticsearchClusterStatus.Running));
        db.SaveChanges();

        ElasticsearchService.ReconcilableFor(Scoped, ours).Select(c => c.Name).Should().Equal(["ours-search"]);
        ElasticsearchService.ReconcilableFor(Scoped, theirs).Select(c => c.Name).Should().Equal(["theirs-search"]);
    }

    /// <summary>
    /// The one with a behavioural edge: this sweep writes a Secret — a certificate and often its
    /// private key — into namespaces on a cluster, and its previous "tenant check" compared a row
    /// to itself.
    /// </summary>
    [Fact]
    public void A_certificate_reconcile_sees_only_the_asking_tenants_distributions()
    {
        db.Set<CertificateDistribution>().Add(NewDistribution(ours, ourK8s, "ours-tls"));
        db.Set<CertificateDistribution>().Add(NewDistribution(theirs, theirK8s, "theirs-tls"));
        db.SaveChanges();

        CertificateDistributionService.DistributionsFor(db, ours)
            .Select(d => d.Name).Should().Equal(["ours-tls"]);
        CertificateDistributionService.DistributionsFor(db, theirs)
            .Select(d => d.Name).Should().Equal(["theirs-tls"]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static RabbitMQCluster NewRabbit(Guid tenantId, Guid k8sId, string name, RabbitMQClusterStatus status) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, KubernetesClusterId = k8sId,
        Name = name, Namespace = "messaging", RabbitMQVersion = "3.13", StorageSize = "10Gi",
        Status = status,
    };

    private static KafkaCluster NewKafka(Guid tenantId, Guid k8sId, string name, KafkaClusterStatus status) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, KubernetesClusterId = k8sId,
        Name = name, Namespace = "messaging", KafkaVersion = "3.7.0", StorageSize = "10Gi", Status = status,
    };

    private static ElasticsearchCluster NewElastic(Guid tenantId, Guid k8sId, string name, ElasticsearchClusterStatus status) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, KubernetesClusterId = k8sId,
        Name = name, Namespace = "search", Status = status,
    };

    private static CertificateDistribution NewDistribution(Guid tenantId, Guid clusterId, string name) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, ClusterId = clusterId,
        Name = name, VaultSecretId = Guid.NewGuid(), TargetSecretName = name,
        Scope = TrustDistributionScope.TenantNamespaces,
    };
}
