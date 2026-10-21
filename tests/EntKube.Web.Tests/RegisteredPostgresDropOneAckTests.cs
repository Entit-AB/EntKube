using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// One operator action, one acknowledgment — for dropping a registered-Postgres database.
///
/// <para>This is another case whose dialog count grows with the DATA: the instance's own
/// credential Secret is one gated change, and every app bound to the database is one more as its
/// keys are removed from that app's Secret. Dropping a database used by three apps asked four
/// times for a single press of Drop.</para>
///
/// <para>The SQL half is expected to fail here and is caught by the service on purpose ("continue
/// with local cleanup so the user isn't stuck"), which is convenient: it means the cleanup this
/// test is about runs exactly as it does when a pod is unreachable in production.</para>
/// </summary>
public class RegisteredPostgresDropOneAckTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
        (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();

    private int gatedChanges;

    private Guid tenantId, clusterId, envId, instanceId, databaseId;

    public RegisteredPostgresDropOneAckTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        // The SQL never reaches a real pod. The service catches this and carries on with the
        // cleanup, which is the half under test.
        k8s.Setup(x => x.ExecuteSqlOnPodAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no pod here"));
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class CountingSink : EntKube.Web.Services.ClusterChanges.IClusterChangeAckSink
    {
        public int Calls { get; private set; }

        public Task<EntKube.Web.Services.ClusterChanges.ClusterChangeDecision> RequestAsync(
            EntKube.Web.Services.ClusterChanges.PlannedClusterChange change,
            EntKube.Web.Services.ClusterChanges.ClusterChangeDiff diff,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(
                EntKube.Web.Services.ClusterChanges.ClusterChangeDecision.Acknowledged);
        }
    }

    /// <summary>
    /// Makes the mocked factory gate its writes the way the real one does — without this the
    /// action reaches the gate zero times and a dialog count proves nothing.
    ///
    /// <para><c>DeleteManifestAsync</c> is raised as a COMPOSITE change (a manifest rather than a
    /// kind/name pair) so the gate's delete diff does not shell out to a real <c>kubectl get</c>
    /// and decide the delete is a no-op. What is under test here is the collapsing, not the
    /// gate's own no-op detection.</para>
    /// </summary>
    private void GateEveryWrite()
    {
        k8s.Setup(x => x.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string kind, string name, string ns, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Delete,
                    Manifest = $"# delete {kind}/{name} in {ns}\n",
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });

        k8s.Setup(x => x.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string name, string ns, IReadOnlyDictionary<string, string?> keys,
                            IReadOnlyDictionary<string, string>? annotations, string kubeconfig,
                            string? summary, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: {name}\n",
                    Kubeconfig = kubeconfig,
                    Summary = summary,
                }, ct);
                return string.Empty;
            });

        // The Secret has to carry the ownership annotation, or there is nothing recorded as this
        // writer's and RemoveAsync correctly deletes nothing — which would leave the action with
        // a single gated change and make the test pass for the wrong reason.
        string annotation = EntKube.Web.Services.Clusters.SharedSecretKeys.AnnotationFor(
            EntKube.Web.Services.Clusters.SharedSecretKeys.Owners.Database);

        k8s.Setup(x => x.GetResourceJsonAsync("secret", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HelmExecutionResult
            {
                Success = true,
                Output = "{\"metadata\":{\"annotations\":{\""
                       + annotation + "\":\"DB_HOST,DB_PASSWORD\"}}}",
            });
    }

    private RegisteredPostgresService NewService() =>
        new(testDb.Factory, vault,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new StorageBrowserService(null!),
            NullLogger<RegisteredPostgresService>.Instance,
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
            gate);

    /// <summary>An instance with one database and <paramref name="boundApps"/> apps bound to it.</summary>
    private async Task SeedAsync(int boundApps)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        envId = Guid.NewGuid();
        instanceId = Guid.NewGuid();
        databaseId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod", ApiServerUrl = "https://k8s.example.com",
        });
        db.Set<RegisteredPostgresInstance>().Add(new RegisteredPostgresInstance
        {
            Id = instanceId, TenantId = tenantId, KubernetesClusterId = clusterId,
            Name = "legacy-pg", Namespace = "databases", ServiceName = "legacy-pg",
            AdminPodName = "legacy-pg-0",
        });
        db.Set<RegisteredPostgresDatabase>().Add(new RegisteredPostgresDatabase
        {
            Id = databaseId, RegisteredPostgresInstanceId = instanceId,
            Name = "orders", Owner = "orders_app",
        });
        await db.SaveChangesAsync();

        Guid customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "customer" });
        await db.SaveChangesAsync();

        for (int i = 0; i < boundApps; i++)
        {
            Guid appId = Guid.NewGuid(), deploymentId = Guid.NewGuid();
            db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = $"app-{i}" });
            await db.SaveChangesAsync();
            db.AppDeployments.Add(new AppDeployment
            {
                Id = deploymentId, AppId = appId, Name = $"deploy-{i}",
                Type = DeploymentType.Manual, ClusterId = clusterId, EnvironmentId = envId,
                Namespace = $"app-{i}-ns",
            });
            await db.SaveChangesAsync();
            db.Set<DatabaseBinding>().Add(new DatabaseBinding
            {
                Id = Guid.NewGuid(), RegisteredPostgresDatabaseId = databaseId,
                AppDeploymentId = deploymentId, KubernetesSecretName = $"app-{i}-env",
            });
            await db.SaveChangesAsync();
        }

        await vault.InitializeVaultAsync(tenantId);
        await vault.SetRegisteredPostgresAdminPasswordAsync(tenantId, instanceId, "admin-pw");
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    [Fact]
    public async Task Dropping_a_database_used_by_several_apps_asks_the_operator_exactly_once()
    {
        await SeedAsync(boundApps: 2);

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        await NewService().DropDatabaseAsync(tenantId, instanceId, databaseId);

        // The instance's own credential Secret, plus one key-removal per bound app.
        gatedChanges.Should().BeGreaterThan(2,
            "the count grows with the number of apps bound to the database, which is the point");
        sink.Calls.Should().Be(1, "one press of Drop, one dialog");
    }

    /// <summary>
    /// And the window closes with the action. Without this a fix that suppressed the question for
    /// the rest of the circuit would pass the test above while retiring the gate for everything
    /// the operator did next.
    /// </summary>
    [Fact]
    public async Task A_second_drop_asks_again()
    {
        await SeedAsync(boundApps: 1);

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        RegisteredPostgresService sut = NewService();

        await sut.DropDatabaseAsync(tenantId, instanceId, databaseId);
        sink.Calls.Should().Be(1);

        // A second database on the same instance: a separate decision.
        Guid secondId = Guid.NewGuid();
        await using (ApplicationDbContext write = testDb.CreateContext())
        {
            write.Set<RegisteredPostgresDatabase>().Add(new RegisteredPostgresDatabase
            {
                Id = secondId, RegisteredPostgresInstanceId = instanceId,
                Name = "invoices", Owner = "invoices_app",
            });
            await write.SaveChangesAsync();
        }

        await sut.DropDatabaseAsync(tenantId, instanceId, secondId);
        sink.Calls.Should().Be(2, "a second drop is a second decision");
    }
}
