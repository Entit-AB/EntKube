using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.Extensions.DependencyInjection;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The reconciler is the only thing in this feature that changes a production cluster with nobody
/// watching, so what it is willing to do is the thing to pin down. It corrects two kinds of
/// difference and leaves everything else to a person — and both halves of that are tested, because
/// the expensive failure here is not "it did not fix something", it is "it fixed something on its
/// own that it should have asked about".
/// </summary>
public class ClusterReconcilerConvergeTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly string connectionString;
    private readonly IDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IDbContextFactory<ApplicationDbContext> interceptedFactory;
    private readonly Mock<IKubernetesClientFactory> k8s = new(MockBehavior.Loose);
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    /// <summary>Has to carry clusters/contexts/users, or the vault refuses to store it.</summary>
    private const string Kubeconfig = """
        apiVersion: v1
        clusters:
        - cluster:
            server: https://api.example:6443
          name: prod-eu-1
        contexts:
        - context:
            cluster: prod-eu-1
            user: admin
          name: prod-eu-1
        current-context: prod-eu-1
        users:
        - name: admin
          user:
            token: test-token
        """;

    public ClusterReconcilerConvergeTests()
    {
        // A named, shared-cache database rather than a bare ":memory:" one, because the kubeconfig
        // resolver opens its own context while the outer query is still being materialized. Over a
        // single shared connection SQLite cannot have two readers at once, and the resolver — which
        // swallows failures by design — would quietly hand back no kubeconfig. In production each
        // context has its own connection, so this makes the test match rather than paper over it.
        connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        connection = new SqliteConnection(connectionString);
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        dbFactory = new ConnectionStringDbContextFactory(connectionString);

        Guid environmentId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = "testco" });
        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = environmentId,
            Name = "prod-eu-1",
            ApiServerUrl = "https://api.example"
        });
        db.SaveChanges();

        // The kubeconfig is not a column — it lives in the vault and is put back onto the entity by
        // the materialization interceptor. Seeding it any other way would test a path production
        // does not have, and this class exists partly because that difference is easy to miss.
        VaultEncryptionService encryption = new(Convert.FromBase64String(
            "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg="));
        VaultService vault = new(dbFactory, encryption);
        vault.InitializeVaultAsync(tenantId).GetAwaiter().GetResult();
        (bool stored, string? storeError, _) = vault.SetClusterKubeconfigAsync(tenantId, clusterId,
            new KubeconfigBundle { ConfigYaml = Kubeconfig }, "test")
            .GetAwaiter().GetResult();

        // Asserted rather than ignored: the store validates the document, and a rejected seed would
        // leave every test in this class exercising the "no kubeconfig" path instead of the one
        // being tested — which is exactly what happened while writing them.
        if (!stored) throw new InvalidOperationException($"Test kubeconfig was rejected: {storeError}");

        interceptedFactory = new InterceptingDbContextFactory(connectionString, encryption, dbFactory);
    }

    /// <summary>Opens a connection of its own per context, as the application's factory does.</summary>
    private sealed class ConnectionStringDbContextFactory(string connectionString)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options);
    }

    /// <summary>A context factory that materializes kubeconfigs the way the application does.</summary>
    private sealed class InterceptingDbContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly string connectionString;

        // Built once. A fresh interceptor per context makes EF construct a new internal service
        // provider each time, which it rightly complains about after twenty of them.
        private readonly KubeconfigMaterializationInterceptor interceptor;

        public InterceptingDbContextFactory(
            string connectionString,
            VaultEncryptionService encryption,
            IDbContextFactory<ApplicationDbContext> inner)
        {
            this.connectionString = connectionString;

            ServiceCollection services = new();
            services.AddSingleton(inner);
            interceptor = new KubeconfigMaterializationInterceptor(
                new KubeconfigResolver(services.BuildServiceProvider(), encryption));
        }

        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private Guid SeedSpec(Action<ProvisionedCluster>? tweak = null, params ProvisionedWorkerPool[] pools)
    {
        ProvisionedCluster spec = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = clusterId,
            EnvironmentId = db.Environments.First().Id,
            OpenStackConnectionId = Guid.NewGuid(),
            Name = "prod-eu-1",
            ControlPlaneFlavor = "b.4c8gb",
            ExternalNetworkId = "ext-net",
            NodeImageName = "entkube-k8s-v1.31.4-1",
            BootstrapFlavor = "b.2c4gb",
            BootstrapNetworkId = "tenant-net"
        };
        tweak?.Invoke(spec);

        db.ProvisionedClusters.Add(spec);
        foreach (ProvisionedWorkerPool pool in pools)
        {
            pool.Id = Guid.NewGuid();
            pool.ProvisionedClusterId = spec.Id;
            db.ProvisionedWorkerPools.Add(pool);
        }
        db.SaveChanges();
        return spec.Id;
    }

    /// <summary>
    /// Stubs what the cluster returns rather than what the reader concludes, so the parsing the
    /// reconciler depends on is exercised too — a converge decision made on a misread status field
    /// is the failure this whole class is guarding against.
    /// </summary>
    private ClusterReconciler ReconcilerSeeing(
        int controlPlaneReady = 3,
        int controlPlaneUpdated = 3,
        PoolObservation[]? pools = null,
        string[]? failedMachines = null,
        bool unreachable = false)
    {
        void Stub(string resource, string json)
        {
            var setup = k8s.Setup(x => x.GetJsonAsync(resource, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()));

            if (unreachable) setup.ThrowsAsync(new HttpRequestException("connection refused"));
            else setup.ReturnsAsync(json);
        }

        // Serialized rather than hand-written: JSON is all braces, and a raw string literal treats
        // those as interpolation syntax — the same trap that has bitten three times in this feature.
        Stub("kubeadmcontrolplanes.controlplane.cluster.x-k8s.io", JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new
                {
                    metadata = new { name = "prod-eu-1-control-plane" },
                    spec = new { replicas = 3, version = "v1.31.4", clusterName = "prod-eu-1" },
                    status = new { readyReplicas = controlPlaneReady, updatedReplicas = controlPlaneUpdated }
                }
            }
        }));

        Stub("machinedeployments.cluster.x-k8s.io", JsonSerializer.Serialize(new
        {
            items = (pools ?? []).Select(p => new
            {
                metadata = new { name = $"prod-eu-1-{p.Name}" },
                spec = new
                {
                    replicas = p.Desired,
                    clusterName = "prod-eu-1",
                    template = new { spec = new { version = "v1.31.4" } }
                },
                status = new { replicas = p.Current, readyReplicas = p.Ready, updatedReplicas = p.Updated }
            })
        }));

        Stub("machines.cluster.x-k8s.io", JsonSerializer.Serialize(new
        {
            items = (failedMachines ?? []).Select(m => new
            {
                metadata = new { name = m },
                spec = new { clusterName = "prod-eu-1" },
                status = new { phase = "Failed" }
            })
        }));

        return new ClusterReconciler(
            interceptedFactory,
            new ProvisionedClusterService(interceptedFactory, NullLogger<ProvisionedClusterService>.Instance),
            new ClusterStateReader(k8s.Object, NullLogger<ClusterStateReader>.Instance),
            k8s.Object,
            NullLogger<ClusterReconciler>.Instance);
    }

    private static PoolObservation Seen(string name, int desired, int ready) =>
        new(name, desired, desired, ready, desired, "v1.31.4");

    // ── What it corrects ──

    [Fact]
    public async Task A_replica_count_that_drifted_from_the_spec_is_put_back()
    {
        // One number, idempotent, and the difference between an edit that was made and one that
        // took. This is the case worth acting on unattended.
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 5 });

        await ReconcilerSeeing(pools: [Seen("general", 3, 3)]).ReconcileAsync(specId);

        k8s.Verify(x => x.PatchStrategicAsync(
            "machinedeployments.cluster.x-k8s.io", "prod-eu-1-general", It.IsAny<string>(),
            It.Is<string>(p => p.Contains("\"replicas\":5")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_pool_in_the_spec_that_the_cluster_does_not_have_is_created()
    {
        // Additive, and exactly the retry a person would perform after an edit that failed partway.
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "memory", Flavor = "b.8c32gb", Count = 2 });

        await ReconcilerSeeing(pools: []).ReconcileAsync(specId);

        k8s.Verify(x => x.ApplyManifestAsync(
            It.Is<string>(m => m.Contains("prod-eu-1-memory")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_pool_that_already_matches_is_left_alone()
    {
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 3 });

        await ReconcilerSeeing(pools: [Seen("general", 3, 3)]).ReconcileAsync(specId);

        k8s.Verify(x => x.PatchStrategicAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── What it refuses to touch ──

    [Fact]
    public async Task An_autoscaled_pool_keeps_the_count_the_autoscaler_gave_it()
    {
        // The autoscaler owns this number. Correcting it back to the spec every five minutes would
        // mean the two fight, and the cluster oscillates.
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool
        {
            Name = "general", Flavor = "b.4c8gb", Count = 3, MinCount = 2, MaxCount = 10
        });

        await ReconcilerSeeing(pools: [Seen("general", 7, 7)]).ReconcileAsync(specId);

        k8s.Verify(x => x.PatchStrategicAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_pool_on_the_cluster_that_is_not_in_the_spec_is_never_deleted()
    {
        // It looks like a removal that did not finish. It also looks exactly like a pool somebody
        // added by hand, and getting that wrong destroys machines nobody asked to lose.
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 3 });

        await ReconcilerSeeing(pools: [Seen("general", 3, 3), Seen("someone-elses", 2, 2)]).ReconcileAsync(specId);

        k8s.Verify(x => x.DeleteManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nothing_is_applied_while_a_rollout_is_in_flight()
    {
        // Same reason day-2 refuses: a second change during a rollout replaces more machines than
        // either intended.
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 5 });

        // updatedReplicas short of replicas is what a rollout looks like on the wire.
        await ReconcilerSeeing(controlPlaneUpdated: 1, pools: [Seen("general", 3, 3)]).ReconcileAsync(specId);

        k8s.Verify(x => x.PatchStrategicAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nothing_is_applied_to_a_degraded_cluster()
    {
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 5 });

        await ReconcilerSeeing(pools: [Seen("general", 3, 3)], failedMachines: ["dead"]).ReconcileAsync(specId);

        k8s.Verify(x => x.PatchStrategicAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_paused_cluster_is_not_touched_or_even_observed()
    {
        // Paused means somebody is working on it. A controller changing things underneath them is
        // the whole reason the state exists.
        Guid specId = SeedSpec(
            c => c.DesiredState = ProvisionedClusterState.Paused,
            new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 5 });

        ClusterObservation result = await ReconcilerSeeing(pools: []).ReconcileAsync(specId);

        result.Health.Should().Be(ClusterHealth.Unreachable);
        result.Summary.Should().Contain("paused");
        k8s.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unreachable_cluster_is_not_acted_on()
    {
        // Nothing is known about it. Applying a spec to a cluster we cannot see is how a network
        // problem becomes a cluster problem.
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 5 });

        await ReconcilerSeeing(unreachable: true).ReconcileAsync(specId);

        // Reading is fine and is how it found out. Writing is the thing that must not happen.
        k8s.Verify(x => x.PatchStrategicAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        k8s.Verify(x => x.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unreachable_cluster_does_not_claim_the_spec_was_applied()
    {
        Guid specId = SeedSpec(pools: new ProvisionedWorkerPool { Name = "general", Flavor = "b.4c8gb", Count = 3 });

        await ReconcilerSeeing(unreachable: true).ReconcileAsync(specId);

        ProvisionedCluster reloaded = db.ProvisionedClusters.AsNoTracking().First(c => c.Id == specId);
        reloaded.ObservedGeneration.Should().Be(0, "nothing was confirmed against the cluster");
        reloaded.LastError.Should().Contain("did not answer");
    }
}
