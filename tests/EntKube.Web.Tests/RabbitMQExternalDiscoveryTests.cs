using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.Language.Flow;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for adopting RabbitMQ brokers that were installed outside EntKube — Helm charts and
/// hand-rolled StatefulSets, which have no RabbitmqCluster CR for discovery to find.
///
/// The interesting behaviour is all in the StatefulSet sweep: recognising a broker without
/// relying on any chart's naming conventions, resolving where its admin credentials actually
/// live, and refusing to touch a workload whose lifecycle belongs to Helm.
/// </summary>
public class RabbitMQExternalDiscoveryTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8s;
    private readonly RabbitMQService sut;

    public RabbitMQExternalDiscoveryTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vaultService = testDb.CreateVaultService();
        k8s = new Mock<IKubernetesClientFactory>();
        sut = new RabbitMQService(testDb.Factory, k8s.Object, vaultService);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────── Helpers ────────

    private async Task<(Tenant Tenant, KubernetesCluster Cluster)> SeedTenantWithClusterAsync()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Production" };
        db.Set<Data.Environment>().Add(env);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com"
        };

        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        await vaultService.InitializeVaultAsync(tenant.Id);
        await testDb.SeedKubeconfigAsync(vaultService, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        return (tenant, cluster);
    }

    /// <summary>No RabbitmqCluster CRD registered — the state on a cluster with no operator.</summary>
    private void SetupNoOperatorCrd() =>
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                "rabbitmqclusters.rabbitmq.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "error: the server doesn't have a resource type \"rabbitmqclusters\""));

    private void SetupStatefulSets(string json) =>
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                "statefulsets", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

    private static string Wrap(params string[] items) =>
        $$"""{"items":[{{string.Join(",", items)}}]}""";

    /// <summary>A bitnami/rabbitmq StatefulSet: username inline, password by secret reference.</summary>
    private static string BitnamiStatefulSet(string name = "mq-rabbitmq", string ns = "messaging") =>
        $$"""
        {
          "metadata": { "name": "{{name}}", "namespace": "{{ns}}" },
          "spec": {
            "replicas": 3,
            "serviceName": "{{name}}-headless",
            "template": {
              "spec": {
                "containers": [{
                  "name": "rabbitmq",
                  "image": "docker.io/bitnami/rabbitmq:3.13.7-debian-12-r0",
                  "env": [
                    { "name": "RABBITMQ_USERNAME", "value": "admin" },
                    { "name": "RABBITMQ_PASSWORD",
                      "valueFrom": { "secretKeyRef": { "name": "{{name}}", "key": "rabbitmq-password" } } }
                  ]
                }]
              }
            },
            "volumeClaimTemplates": [{
              "spec": {
                "storageClassName": "fast-ssd",
                "resources": { "requests": { "storage": "20Gi" } }
              }
            }]
          }
        }
        """;

    // ──────── Detection ────────

    [Fact]
    public async Task DiscoverClustersAsync_HelmInstalledBroker_IsAdoptedAsExternal()
    {
        (Tenant tenant, KubernetesCluster cluster) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));

        RabbitMQDiscoveryResult result = await sut.DiscoverClustersAsync(tenant.Id);

        result.External.Should().Be(1);
        result.OperatorManaged.Should().Be(0);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();
        adopted.IsOperatorManaged.Should().BeFalse();
        adopted.Name.Should().Be("mq-rabbitmq");
        adopted.Namespace.Should().Be("messaging");
        adopted.KubernetesClusterId.Should().Be(cluster.Id);
    }

    [Fact]
    public async Task DiscoverClustersAsync_ReadsReplicasStorageAndVersionFromTheStatefulSet()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));

        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();
        adopted.Replicas.Should().Be(3);
        adopted.StorageSize.Should().Be("20Gi");
        adopted.StorageClass.Should().Be("fast-ssd");
        // The distribution suffix is dropped: "3.13.7-debian-12-r0" → "3.13.7".
        adopted.RabbitMQVersion.Should().Be("3.13.7");
    }

    [Fact]
    public async Task DiscoverClustersAsync_ResolvesCredentialSecretFromContainerEnv()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));

        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        adopted.CredentialsSecretName.Should().Be("mq-rabbitmq");
        adopted.CredentialsPasswordKey.Should().Be("rabbitmq-password");
        // bitnami passes the username inline, so there is no key to read it from.
        adopted.CredentialsUsernameKey.Should().BeNull();
        adopted.AdminUsername.Should().Be("admin");
    }

    [Fact]
    public async Task DiscoverClustersAsync_CommunityChartStyleEnv_ResolvesBothKeysFromTheSecret()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap("""
        {
          "metadata": { "name": "broker", "namespace": "apps" },
          "spec": {
            "replicas": 1,
            "template": {
              "spec": {
                "containers": [{
                  "name": "rabbitmq",
                  "image": "rabbitmq:4.0.5-management",
                  "env": [
                    { "name": "RABBITMQ_DEFAULT_USER",
                      "valueFrom": { "secretKeyRef": { "name": "broker-creds", "key": "user" } } },
                    { "name": "RABBITMQ_DEFAULT_PASS",
                      "valueFrom": { "secretKeyRef": { "name": "broker-creds", "key": "pass" } } }
                  ]
                }]
              }
            }
          }
        }
        """));

        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();
        adopted.CredentialsSecretName.Should().Be("broker-creds");
        adopted.CredentialsUsernameKey.Should().Be("user");
        adopted.CredentialsPasswordKey.Should().Be("pass");
        adopted.AdminUsername.Should().BeNull();
        adopted.RabbitMQVersion.Should().Be("4.0.5");
    }

    [Fact]
    public async Task DiscoverClustersAsync_OperatorOwnedStatefulSet_IsNotAdoptedTwice()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        // The operator's own broker StatefulSet, already represented by its CR.
        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap("""
        {
          "metadata": {
            "name": "managed-server",
            "namespace": "messaging",
            "ownerReferences": [{ "kind": "RabbitmqCluster", "name": "managed" }]
          },
          "spec": {
            "replicas": 3,
            "template": {
              "spec": { "containers": [{ "name": "rabbitmq", "image": "rabbitmq:3.13-management" }] }
            }
          }
        }
        """));

        RabbitMQDiscoveryResult result = await sut.DiscoverClustersAsync(tenant.Id);

        result.Total.Should().Be(0);
        (await db.RabbitMQClusters.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DiscoverClustersAsync_NonRabbitWorkloads_AreIgnored()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap("""
        {
          "metadata": { "name": "postgres", "namespace": "db" },
          "spec": {
            "replicas": 1,
            "template": { "spec": { "containers": [{ "name": "postgres", "image": "postgres:16" }] } }
          }
        }
        """));

        RabbitMQDiscoveryResult result = await sut.DiscoverClustersAsync(tenant.Id);

        result.Total.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverClustersAsync_ExporterSidecar_DoesNotBecomeTheBrokerContainer()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap("""
        {
          "metadata": { "name": "mq", "namespace": "messaging" },
          "spec": {
            "replicas": 1,
            "template": {
              "spec": {
                "containers": [
                  { "name": "metrics", "image": "kbudde/rabbitmq-exporter:1.0.0" },
                  { "name": "rabbitmq", "image": "rabbitmq:3.13-management",
                    "env": [{ "name": "RABBITMQ_DEFAULT_PASS",
                              "valueFrom": { "secretKeyRef": { "name": "mq-secret", "key": "pw" } } }] }
                ]
              }
            }
          }
        }
        """));

        await sut.DiscoverClustersAsync(tenant.Id);

        // The exporter carries no credentials; picking it would leave these null.
        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();
        adopted.CredentialsSecretName.Should().Be("mq-secret");
        adopted.CredentialsPasswordKey.Should().Be("pw");
    }

    [Fact]
    public async Task DiscoverClustersAsync_IsIdempotent()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));

        await sut.DiscoverClustersAsync(tenant.Id);
        RabbitMQDiscoveryResult second = await sut.DiscoverClustersAsync(tenant.Id);

        second.Total.Should().Be(0);
        (await db.RabbitMQClusters.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DiscoverClustersAsync_FindsBrokersInAnyNamespace()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(
            BitnamiStatefulSet("mq-a", "team-alpha"),
            BitnamiStatefulSet("mq-b", "some-other-namespace")));

        RabbitMQDiscoveryResult result = await sut.DiscoverClustersAsync(tenant.Id);

        result.External.Should().Be(2);
        (await db.RabbitMQClusters.Select(c => c.Namespace).ToListAsync())
            .Should().BeEquivalentTo(["team-alpha", "some-other-namespace"]);
    }

    // ──────── Derived names ────────

    [Fact]
    public void PrimaryPodName_FollowsTheStatefulSetForExternalBrokers()
    {
        RabbitMQCluster external = new()
        {
            Name = "mq-rabbitmq", Namespace = "messaging", RabbitMQVersion = "3.13", StorageSize = "20Gi",
            IsOperatorManaged = false, StatefulSetName = "mq-rabbitmq", ServiceName = "mq-rabbitmq-headless"
        };

        RabbitMQCluster operatorManaged = new()
        {
            Name = "managed", Namespace = "messaging", RabbitMQVersion = "3.13", StorageSize = "20Gi"
        };

        external.PrimaryPodName.Should().Be("mq-rabbitmq-0");
        external.AmqpServiceName.Should().Be("mq-rabbitmq-headless");

        operatorManaged.PrimaryPodName.Should().Be("managed-server-0");
        operatorManaged.AmqpServiceName.Should().Be("managed-svc");
    }

    // ──────── Credentials ────────

    [Fact]
    public async Task GetAdminCredentialsAsync_External_ReadsTheChartsSecretAndInlineUsername()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));

        k8s.Setup(f => f.GetSecretValueAsync(
                "mq-rabbitmq", "rabbitmq-password", "messaging", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("s3cr3t");

        await sut.DiscoverClustersAsync(tenant.Id);
        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        (string Username, string Password)? creds =
            await sut.GetAdminCredentialsAsync(tenant.Id, adopted.Id);

        creds.Should().NotBeNull();
        creds!.Value.Username.Should().Be("admin");
        creds.Value.Password.Should().Be("s3cr3t");

        // The operator's {name}-default-user secret must never be consulted here.
        k8s.Verify(f => f.GetSecretValueAsync(
            "mq-rabbitmq-default-user", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ──────── Lifecycle is off-limits ────────

    [Fact]
    public async Task UpdateClusterAsync_External_IsRejected()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        Func<Task> act = () => sut.UpdateClusterAsync(tenant.Id, adopted.Id, "4.0", 5, "50Gi", null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Helm release*");

        k8s.Verify(f => f.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteClusterAsync_External_ForgetsTheRecordWithoutTouchingKubernetes()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        await sut.DeleteClusterAsync(tenant.Id, adopted.Id);

        (await db.RabbitMQClusters.CountAsync()).Should().Be(0);

        // Nothing in the cluster may be deleted — the broker belongs to its Helm release.
        k8s.Verify(f => f.DeleteManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ──────── Topology over rabbitmqctl ────────

    [Fact]
    public async Task GetVhostsAsync_External_ListsViaRabbitmqctlOnTheBrokerPod()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        k8s.Setup(f => f.RunCommandOnPodAsync(
                "mq-rabbitmq-0", "messaging",
                It.Is<IReadOnlyList<string>>(c => c.Contains("list_vhosts")),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync("""[{"name":"/"},{"name":"orders"}]""");

        List<RabbitMQVhostInfo> vhosts = await sut.GetVhostsAsync(tenant.Id, adopted.Id);

        vhosts.Select(v => v.VhostName).Should().BeEquivalentTo(["/", "orders"]);

        // No topology CRDs are consulted for an external broker.
        k8s.Verify(f => f.GetJsonAsync(
            "vhosts.rabbitmq.com", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetVhostsAsync_External_ToleratesPreambleBeforeTheJson()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        // rabbitmqctl commonly prints a status line ahead of the payload.
        k8s.Setup(f => f.RunCommandOnPodAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync("Listing vhosts ...\n[{\"name\":\"/\"}]\n");

        List<RabbitMQVhostInfo> vhosts = await sut.GetVhostsAsync(tenant.Id, adopted.Id);

        vhosts.Should().ContainSingle().Which.VhostName.Should().Be("/");
    }

    [Fact]
    public async Task DeleteVhostAsync_External_RoundTripsTheHandleFromTheListing()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        // The default vhost is literally "/", which a naive handle format would split apart.
        k8s.Setup(f => f.RunCommandOnPodAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(c => c.Contains("list_vhosts")),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync("""[{"name":"/"}]""");

        List<string>? deleteCommand = null;
        k8s.Setup(f => f.RunCommandOnPodAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.Is<IReadOnlyList<string>>(c => c.Contains("delete_vhost")),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback((string _, string _, IReadOnlyList<string> cmd, string _,
                       IReadOnlyDictionary<string, string>? _, CancellationToken _, int _, bool _) =>
                deleteCommand = [.. cmd])
            .ReturnsAsync("");

        RabbitMQVhostInfo vhost = (await sut.GetVhostsAsync(tenant.Id, adopted.Id)).Single();
        await sut.DeleteVhostAsync(tenant.Id, adopted.Id, vhost.K8sName);

        deleteCommand.Should().Equal(["rabbitmqctl", "delete_vhost", "/"]);
    }

    [Fact]
    public async Task CreateQueueAsync_External_ImportsAPartialDefinitionsDocument()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        string? stdin = null;
        k8s.Setup(f => f.RunCommandOnPodWithStdinAsync(
                "mq-rabbitmq-0", "messaging",
                It.Is<IReadOnlyList<string>>(c => c.Contains("import_definitions")),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, IReadOnlyList<string> _, string s, string _, CancellationToken _) =>
                stdin = s)
            .ReturnsAsync("");

        await sut.CreateQueueAsync(tenant.Id, adopted.Id, "orders", "inbox", "quorum", true, false);

        stdin.Should().NotBeNull();

        using JsonDocument doc = JsonDocument.Parse(stdin!);
        JsonElement queue = doc.RootElement.GetProperty("queues")[0];

        queue.GetProperty("name").GetString().Should().Be("inbox");
        queue.GetProperty("vhost").GetString().Should().Be("orders");
        queue.GetProperty("durable").GetBoolean().Should().BeTrue();
        queue.GetProperty("arguments").GetProperty("x-queue-type").GetString().Should().Be("quorum");

        // Only the section being created is sent, so the import stays scoped to this one queue.
        doc.RootElement.TryGetProperty("exchanges", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteExchangeAsync_External_FailsWithAnActionableMessage()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        Func<Task> act = () => sut.DeleteExchangeAsync(tenant.Id, adopted.Id, "ext:Lw==:Zm9v");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*management UI*");
    }

    [Fact]
    public async Task SetPermissionAsync_External_UsesRabbitmqctlRatherThanAPermissionCrd()
    {
        (Tenant tenant, _) = await SeedTenantWithClusterAsync();

        SetupNoOperatorCrd();
        SetupStatefulSets(Wrap(BitnamiStatefulSet()));
        await sut.DiscoverClustersAsync(tenant.Id);

        RabbitMQCluster adopted = await db.RabbitMQClusters.SingleAsync();

        List<string>? command = null;
        k8s.Setup(f => f.RunCommandOnPodAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback((string _, string _, IReadOnlyList<string> cmd, string _,
                       IReadOnlyDictionary<string, string>? _, CancellationToken _, int _, bool _) =>
                command = [.. cmd])
            .ReturnsAsync("");

        await sut.SetPermissionAsync(tenant.Id, adopted.Id, "orders", "app", ".*", ".*", ".*");

        command.Should().Equal(
            ["rabbitmqctl", "set_permissions", "--vhost", "orders", "app", ".*", ".*", ".*"]);

        k8s.Verify(f => f.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ──────── Operator-managed broker WITHOUT the Topology Operator ────────

    /// <summary>
    /// Seeds an operator-managed cluster directly (as if created by EntKube via the Cluster
    /// Operator) and controls whether the Topology Operator CRDs are registered.
    /// </summary>
    private async Task<(Guid TenantId, Guid ClusterId)> SeedOperatorManagedAsync(bool topologyCrdsPresent)
    {
        (Tenant tenant, KubernetesCluster k8sCluster) = await SeedTenantWithClusterAsync();

        RabbitMQCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = k8sCluster.Id,
            Name = "managed",
            Namespace = "messaging",
            RabbitMQVersion = "3.13",
            StorageSize = "10Gi",
            Status = RabbitMQClusterStatus.Running,
            IsOperatorManaged = true
        };
        db.RabbitMQClusters.Add(cluster);
        await db.SaveChangesAsync();

        ISetup<IKubernetesClientFactory, Task<string>> probe = k8s.Setup(f => f.GetJsonAsync(
            "crd/vhosts.rabbitmq.com", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()));

        if (topologyCrdsPresent)
        {
            probe.ReturnsAsync("""{"kind":"CustomResourceDefinition"}""");
        }
        else
        {
            // Exactly what kubectl reports when the Topology Operator was never installed.
            probe.ThrowsAsync(new InvalidOperationException(
                "kubectl failed (exit 1): error: the server doesn't have a resource type \"vhosts\""));
        }

        return (tenant.Id, cluster.Id);
    }

    [Fact]
    public async Task GetVhostsAsync_OperatorManagedWithoutTopologyOperator_FallsBackToRabbitmqctl()
    {
        (Guid tenantId, Guid clusterId) = await SeedOperatorManagedAsync(topologyCrdsPresent: false);

        k8s.Setup(f => f.RunCommandOnPodAsync(
                "managed-server-0", "messaging",
                It.Is<IReadOnlyList<string>>(c => c.Contains("list_vhosts")),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync("""[{"name":"/"},{"name":"orders"}]""");

        List<RabbitMQVhostInfo> vhosts = await sut.GetVhostsAsync(tenantId, clusterId);

        vhosts.Select(v => v.VhostName).Should().BeEquivalentTo(["/", "orders"]);

        // The CRD query that produced "the server doesn't have a resource type" must not happen.
        k8s.Verify(f => f.GetJsonAsync(
            "vhosts.rabbitmq.com", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetVhostsAsync_OperatorManagedWithTopologyOperator_StillUsesCrds()
    {
        (Guid tenantId, Guid clusterId) = await SeedOperatorManagedAsync(topologyCrdsPresent: true);

        k8s.Setup(f => f.GetJsonAsync(
                "vhosts.rabbitmq.com", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
                {"items":[{"metadata":{"name":"managed-vh-orders"},"spec":{"name":"orders"}}]}
                """);

        List<RabbitMQVhostInfo> vhosts = await sut.GetVhostsAsync(tenantId, clusterId);

        vhosts.Should().ContainSingle().Which.VhostName.Should().Be("orders");
        // CR-backed clusters keep the K8s object name — the operator owns these entries.
        vhosts[0].K8sName.Should().Be("managed-vh-orders");

        k8s.Verify(f => f.RunCommandOnPodAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CreateVhostAsync_OperatorManagedWithoutTopologyOperator_UsesCtlNotAManifest()
    {
        (Guid tenantId, Guid clusterId) = await SeedOperatorManagedAsync(topologyCrdsPresent: false);

        List<string>? command = null;
        k8s.Setup(f => f.RunCommandOnPodAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback((string _, string _, IReadOnlyList<string> cmd, string _,
                       IReadOnlyDictionary<string, string>? _, CancellationToken _, int _, bool _) =>
                command = [.. cmd])
            .ReturnsAsync("");

        await sut.CreateVhostAsync(tenantId, clusterId, "orders");

        command.Should().Equal(["rabbitmqctl", "add_vhost", "orders"]);

        // Applying a Vhost CR would fail on a cluster with no Topology Operator.
        k8s.Verify(f => f.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ──────── Status reconciliation ────────

    private void SetupCrStatus(string clusterName, bool available, bool allReady)
    {
        string avail = available ? "True" : "False";
        string ready = allReady ? "True" : "False";
        string json =
            "{\"status\":{\"conditions\":["
            + "{\"type\":\"ClusterAvailable\",\"status\":\"" + avail + "\"},"
            + "{\"type\":\"AllReplicasReady\",\"status\":\"" + ready + "\"}"
            + "]}}";

        k8s.Setup(f => f.GetJsonAsync(
                $"rabbitmqcluster/{clusterName}", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);
    }

    [Fact]
    public async Task ReconcileStatusAsync_ServingButMidRestart_StaysRunningRatherThanFailed()
    {
        (Guid tenantId, Guid clusterId) = await SeedOperatorManagedAsync(topologyCrdsPresent: true);

        // A rolling restart: the cluster serves traffic, but not every replica is ready yet.
        SetupCrStatus("managed", available: true, allReady: false);

        await sut.ReconcileStatusAsync(tenantId, clusterId);

        RabbitMQCluster after = await db.RabbitMQClusters.AsNoTracking().SingleAsync(c => c.Id == clusterId);
        after.Status.Should().Be(RabbitMQClusterStatus.Running);
        after.LastError.Should().Contain("not every replica is ready");
    }

    [Fact]
    public async Task ReconcileStatusAsync_RecoversAClusterPreviouslyMarkedFailed()
    {
        (Guid tenantId, Guid clusterId) = await SeedOperatorManagedAsync(topologyCrdsPresent: true);

        RabbitMQCluster stuck = await db.RabbitMQClusters.SingleAsync(c => c.Id == clusterId);
        stuck.Status = RabbitMQClusterStatus.Failed;
        stuck.LastError = "something transient happened";
        await db.SaveChangesAsync();

        SetupCrStatus("managed", available: true, allReady: true);

        await sut.ReconcileStatusAsync(tenantId, clusterId);

        RabbitMQCluster after = await db.RabbitMQClusters.AsNoTracking().SingleAsync(c => c.Id == clusterId);
        after.Status.Should().Be(RabbitMQClusterStatus.Running);
        after.LastError.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAllAsync_RevisitsFailedClustersSoTheyCanRecover()
    {
        (Guid tenantId, Guid clusterId) = await SeedOperatorManagedAsync(topologyCrdsPresent: true);

        RabbitMQCluster stuck = await db.RabbitMQClusters.SingleAsync(c => c.Id == clusterId);
        stuck.Status = RabbitMQClusterStatus.Failed;
        await db.SaveChangesAsync();

        SetupCrStatus("managed", available: true, allReady: true);

        // The poller, not a hand-triggered refresh — this is what used to skip Failed entirely.
        await sut.ReconcileAllAsync();

        RabbitMQCluster after = await db.RabbitMQClusters.AsNoTracking().SingleAsync(c => c.Id == clusterId);
        after.Status.Should().Be(RabbitMQClusterStatus.Running);
    }

    [Fact]
    public async Task ReconcileStatusAsync_GenuinelyUnavailable_IsStillReportedFailed()
    {
        (Guid tenantId, Guid clusterId) = await SeedOperatorManagedAsync(topologyCrdsPresent: true);

        RabbitMQCluster running = await db.RabbitMQClusters.SingleAsync(c => c.Id == clusterId);
        running.Status = RabbitMQClusterStatus.Running;
        await db.SaveChangesAsync();

        SetupCrStatus("managed", available: false, allReady: false);

        await sut.ReconcileStatusAsync(tenantId, clusterId);

        RabbitMQCluster after = await db.RabbitMQClusters.AsNoTracking().SingleAsync(c => c.Id == clusterId);
        after.Status.Should().Be(RabbitMQClusterStatus.Failed);
    }
}
