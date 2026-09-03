using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for WorkloadService — the read-only Pods/Deployments/ReplicaSets/
/// StatefulSets/DaemonSets browser. kubectl is mocked through
/// IKubernetesClientFactory, so these cover the JSON→WorkloadView mapping:
/// status wording, health roll-up, namespace scoping and partial failures.
/// </summary>
public class WorkloadServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8s;
    private readonly WorkloadService sut;

    public WorkloadServiceTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vaultService = testDb.CreateVaultService();
        k8s = new Mock<IKubernetesClientFactory>();

        // Default: every kind returns an empty list, so each test only sets up what it cares about.
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");
        k8s.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");

        sut = new WorkloadService(testDb.Factory, k8s.Object, NullLogger<WorkloadService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────── Helpers ────────

    private async Task<KubernetesCluster> SeedClusterAsync(bool withKubeconfig = true)
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
        if (withKubeconfig)
            await testDb.SeedKubeconfigAsync(vaultService, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        return cluster;
    }

    private void SetupAllNamespaces(string resource, string json) =>
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                resource, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

    private void SetupNamespaced(string resource, string ns, string json) =>
        k8s.Setup(f => f.GetJsonAsync(
                resource, ns, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

    private static string Wrap(params string[] items) =>
        $$"""{"items":[{{string.Join(",", items)}}]}""";

    /// <summary>A healthy single-container pod owned by a ReplicaSet.</summary>
    private static string RunningPod(string name = "api-abc123", string ns = "apps") =>
        $$"""
        {
          "metadata": {
            "name": "{{name}}", "namespace": "{{ns}}",
            "creationTimestamp": "2026-08-01T10:00:00Z",
            "ownerReferences": [{ "kind": "ReplicaSet", "name": "api-7d9", "controller": true }]
          },
          "spec": { "nodeName": "worker-1", "containers": [{ "name": "api", "image": "ghcr.io/acme/api:1.4" }] },
          "status": {
            "phase": "Running",
            "podIP": "10.42.0.7",
            "containerStatuses": [{
              "name": "api", "image": "ghcr.io/acme/api:1.4", "ready": true,
              "restartCount": 0, "state": { "running": { "startedAt": "2026-08-01T10:00:05Z" } }
            }]
          }
        }
        """;

    /// <summary>A pod whose only container is stuck restarting.</summary>
    private static string CrashLoopPod(string name = "worker-xyz", string ns = "apps") =>
        $$"""
        {
          "metadata": { "name": "{{name}}", "namespace": "{{ns}}" },
          "spec": { "containers": [{ "name": "worker", "image": "ghcr.io/acme/worker:2.0" }] },
          "status": {
            "phase": "Running",
            "containerStatuses": [{
              "name": "worker", "image": "ghcr.io/acme/worker:2.0", "ready": false, "restartCount": 7,
              "state": { "waiting": { "reason": "CrashLoopBackOff", "message": "back-off 5m0s restarting failed container" } }
            }]
          }
        }
        """;

    private static string DeploymentJson(string name, string ns, int? replicas, int ready, int updated = 0) =>
        $$"""
        {
          "metadata": { "name": "{{name}}", "namespace": "{{ns}}", "creationTimestamp": "2026-07-01T08:00:00Z" },
          "spec": {
            {{(replicas is null ? "" : $"\"replicas\": {replicas},")}}
            "template": { "spec": { "containers": [{ "name": "app", "image": "ghcr.io/acme/{{name}}:1.0" }] } }
          },
          "status": { "readyReplicas": {{ready}}, "updatedReplicas": {{updated}} }
        }
        """;

    // ──────── Cluster preconditions ────────

    [Fact]
    public async Task LoadAsync_UnknownCluster_ReturnsError()
    {
        WorkloadSnapshot snapshot = await sut.LoadAsync(Guid.NewGuid());

        snapshot.IsSuccess.Should().BeFalse();
        snapshot.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task LoadAsync_ClusterWithoutKubeconfig_ReturnsError()
    {
        KubernetesCluster cluster = await SeedClusterAsync(withKubeconfig: false);

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        snapshot.IsSuccess.Should().BeFalse();
        snapshot.Error.Should().Contain("kubeconfig");
    }

    // ──────── Pods ────────

    [Fact]
    public async Task LoadAsync_RunningPod_IsHealthyWithNodeAndOwner()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap(RunningPod()));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView pod = snapshot.Workloads.Should().ContainSingle(w => w.Kind == WorkloadKind.Pod).Subject;
        pod.Name.Should().Be("api-abc123");
        pod.Namespace.Should().Be("apps");
        pod.Health.Should().Be(HealthStatus.Healthy);
        pod.StatusText.Should().Be("Running");
        pod.Ready.Should().Be(1);
        pod.Desired.Should().Be(1);
        pod.Node.Should().Be("worker-1");
        pod.PodIP.Should().Be("10.42.0.7");
        pod.OwnerKind.Should().Be("ReplicaSet");
        pod.OwnerName.Should().Be("api-7d9");
        pod.Images.Should().ContainSingle().Which.Should().Be("ghcr.io/acme/api:1.4");
        pod.CreatedAt.Should().Be(new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task LoadAsync_CrashLoopingPod_SurfacesReasonAndRestartsAsDegraded()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap(CrashLoopPod()));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView pod = snapshot.Workloads.Single();
        pod.Health.Should().Be(HealthStatus.Degraded);
        pod.StatusText.Should().Be("CrashLoopBackOff");
        pod.Restarts.Should().Be(7);
        pod.Ready.Should().Be(0);
        pod.Message.Should().Contain("back-off");
    }

    [Fact]
    public async Task LoadAsync_PodBeingCreated_IsProgressingNotDegraded()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap("""
        {
          "metadata": { "name": "api-new", "namespace": "apps" },
          "spec": { "containers": [{ "name": "api", "image": "ghcr.io/acme/api:1.5" }] },
          "status": {
            "phase": "Pending",
            "containerStatuses": [{
              "name": "api", "image": "ghcr.io/acme/api:1.5", "ready": false, "restartCount": 0,
              "state": { "waiting": { "reason": "ContainerCreating" } }
            }]
          }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView pod = snapshot.Workloads.Single();
        pod.Health.Should().Be(HealthStatus.Progressing);
        pod.StatusText.Should().Be("Pending");
    }

    [Fact]
    public async Task LoadAsync_CompletedJobPod_IsSuspendedAndReportsCompleted()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap("""
        {
          "metadata": { "name": "backup-1", "namespace": "ops" },
          "spec": { "containers": [{ "name": "backup", "image": "ghcr.io/acme/backup:3" }] },
          "status": {
            "phase": "Succeeded",
            "containerStatuses": [{
              "name": "backup", "image": "ghcr.io/acme/backup:3", "ready": false, "restartCount": 0,
              "state": { "terminated": { "reason": "Completed", "exitCode": 0 } }
            }]
          }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView pod = snapshot.Workloads.Single();
        pod.Health.Should().Be(HealthStatus.Suspended);
        pod.StatusText.Should().Be("Completed");
    }

    [Fact]
    public async Task LoadAsync_TerminatingPod_ReportsTerminating()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap("""
        {
          "metadata": { "name": "api-old", "namespace": "apps", "deletionTimestamp": "2026-08-16T09:00:00Z" },
          "spec": { "containers": [{ "name": "api", "image": "ghcr.io/acme/api:1.4" }] },
          "status": {
            "phase": "Running",
            "containerStatuses": [{
              "name": "api", "image": "ghcr.io/acme/api:1.4", "ready": true,
              "restartCount": 0, "state": { "running": {} }
            }]
          }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        snapshot.Workloads.Single().StatusText.Should().Be("Terminating");
        snapshot.Workloads.Single().Health.Should().Be(HealthStatus.Progressing);
    }

    [Fact]
    public async Task LoadAsync_PodWithNoContainerStatuses_CountsContainersFromSpec()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap("""
        {
          "metadata": { "name": "unscheduled", "namespace": "apps" },
          "spec": { "containers": [{ "name": "a", "image": "a:1" }, { "name": "b", "image": "b:1" }] },
          "status": { "phase": "Pending" }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView pod = snapshot.Workloads.Single();
        // "0/2", not a misleading "0/0" that would render as fully ready.
        pod.Ready.Should().Be(0);
        pod.Desired.Should().Be(2);
        pod.Health.Should().Be(HealthStatus.Progressing);
    }

    [Fact]
    public async Task LoadAsync_FailingInitContainer_WinsOverAppContainerState()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap("""
        {
          "metadata": { "name": "api-init", "namespace": "apps" },
          "spec": { "containers": [{ "name": "api", "image": "api:1" }] },
          "status": {
            "phase": "Pending",
            "initContainerStatuses": [{
              "name": "migrate", "image": "migrate:1", "ready": false, "restartCount": 3,
              "state": { "waiting": { "reason": "ImagePullBackOff" } }
            }],
            "containerStatuses": [{
              "name": "api", "image": "api:1", "ready": false, "restartCount": 0,
              "state": { "waiting": { "reason": "PodInitializing" } }
            }]
          }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView pod = snapshot.Workloads.Single();
        pod.StatusText.Should().Be("ImagePullBackOff");
        pod.Health.Should().Be(HealthStatus.Degraded);
        pod.Restarts.Should().Be(3);
        pod.Containers.Should().HaveCount(2);
    }

    // ──────── Controllers ────────

    [Fact]
    public async Task LoadAsync_Deployments_MapHealthFromReplicaCounts()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("deployments", Wrap(
            DeploymentJson("full", "apps", replicas: 3, ready: 3, updated: 3),
            DeploymentJson("partial", "apps", replicas: 3, ready: 1),
            DeploymentJson("rolling", "apps", replicas: 3, ready: 0),
            DeploymentJson("scaled-down", "apps", replicas: 0, ready: 0)));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        Dictionary<string, WorkloadView> byName = snapshot.Workloads.ToDictionary(w => w.Name);
        byName["full"].Health.Should().Be(HealthStatus.Healthy);
        byName["full"].StatusText.Should().Be("3/3 ready");
        byName["full"].Updated.Should().Be(3);
        byName["partial"].Health.Should().Be(HealthStatus.Degraded);
        byName["rolling"].Health.Should().Be(HealthStatus.Progressing);
        byName["scaled-down"].Health.Should().Be(HealthStatus.Suspended);
        byName["scaled-down"].StatusText.Should().Be("Scaled to zero");
        snapshot.Workloads.Should().OnlyContain(w => w.Kind == WorkloadKind.Deployment);
    }

    [Fact]
    public async Task LoadAsync_DeploymentWithoutReplicas_DefaultsToOne()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("deployments", Wrap(DeploymentJson("implicit", "apps", replicas: null, ready: 1)));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView deployment = snapshot.Workloads.Single();
        deployment.Desired.Should().Be(1);
        deployment.Health.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task LoadAsync_SupersededReplicaSet_IsMarkedInactive()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("replicasets", Wrap("""
        {
          "metadata": {
            "name": "api-7d9", "namespace": "apps",
            "ownerReferences": [{ "kind": "Deployment", "name": "api", "controller": true }]
          },
          "spec": { "replicas": 0 },
          "status": { "readyReplicas": 0 }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView rs = snapshot.Workloads.Single();
        rs.Kind.Should().Be(WorkloadKind.ReplicaSet);
        rs.IsInactive.Should().BeTrue();
        rs.OwnerName.Should().Be("api");
    }

    [Fact]
    public async Task LoadAsync_StatefulSets_AreListedWithTemplateImages()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("statefulsets", Wrap("""
        {
          "metadata": { "name": "mq", "namespace": "messaging" },
          "spec": {
            "replicas": 3,
            "template": { "spec": { "containers": [{ "name": "rabbitmq", "image": "rabbitmq:3.13" }] } }
          },
          "status": { "readyReplicas": 3 }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView sts = snapshot.Workloads.Single();
        sts.Kind.Should().Be(WorkloadKind.StatefulSet);
        sts.Health.Should().Be(HealthStatus.Healthy);
        sts.Images.Should().ContainSingle().Which.Should().Be("rabbitmq:3.13");
    }

    [Fact]
    public async Task LoadAsync_DaemonSet_UsesNodeSchedulingCounts()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("daemonsets", Wrap("""
        {
          "metadata": { "name": "node-exporter", "namespace": "monitoring" },
          "spec": { "template": { "spec": { "containers": [{ "name": "exporter", "image": "quay.io/node-exporter:1.8" }] } } },
          "status": {
            "desiredNumberScheduled": 5, "numberReady": 4,
            "updatedNumberScheduled": 5, "numberMisscheduled": 1
          }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        WorkloadView ds = snapshot.Workloads.Single();
        ds.Kind.Should().Be(WorkloadKind.DaemonSet);
        ds.Desired.Should().Be(5);
        ds.Ready.Should().Be(4);
        ds.Health.Should().Be(HealthStatus.Degraded);
        ds.StatusText.Should().Be("4/5 ready");
        ds.Message.Should().Contain("no longer match");
    }

    // ──────── Scoping, namespaces, resilience ────────

    [Fact]
    public async Task LoadAsync_WithNamespace_QueriesThatNamespaceOnly()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupNamespaced("pods", "apps", Wrap(RunningPod()));
        SetupNamespaced("pods", "other", Wrap(RunningPod("other-pod", "other")));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id, "apps");

        snapshot.Workloads.Should().ContainSingle().Which.Namespace.Should().Be("apps");
        k8s.Verify(f => f.GetJsonAsync("pods", "apps", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        k8s.Verify(f => f.GetJsonAllNamespacesAsync("pods", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadAsync_ListsEveryNamespaceForTheFilter()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("namespaces", Wrap(
            """{ "metadata": { "name": "kube-system" } }""",
            """{ "metadata": { "name": "apps" } }"""));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        // Sorted, and independent of which namespaces actually hold workloads.
        snapshot.Namespaces.Should().Equal("apps", "kube-system");
    }

    [Fact]
    public async Task LoadAsync_OneKindFailing_WarnsButKeepsTheRest()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap(RunningPod()));
        k8s.Setup(f => f.GetJsonAllNamespacesAsync(
                "daemonsets", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Error from server (Forbidden): daemonsets is forbidden\nmore detail"));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        snapshot.IsSuccess.Should().BeTrue();
        snapshot.Workloads.Should().ContainSingle(w => w.Kind == WorkloadKind.Pod);
        snapshot.Warnings.Should().ContainSingle()
            .Which.Should().Contain("daemonsets").And.NotContain("more detail");
    }

    [Fact]
    public async Task LoadAsync_UnparseableOutput_YieldsNoRowsRatherThanThrowing()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", "not json at all");

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        snapshot.IsSuccess.Should().BeTrue();
        snapshot.Workloads.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_MixedKinds_AreSortedByNamespaceThenKindThenName()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        SetupAllNamespaces("pods", Wrap(RunningPod("zeta", "apps"), RunningPod("alpha", "apps")));
        SetupAllNamespaces("deployments", Wrap(DeploymentJson("api", "apps", 1, 1)));
        SetupAllNamespaces("daemonsets", Wrap("""
        {
          "metadata": { "name": "agent", "namespace": "kube-system" },
          "spec": {}, "status": { "desiredNumberScheduled": 2, "numberReady": 2 }
        }
        """));

        WorkloadSnapshot snapshot = await sut.LoadAsync(cluster.Id);

        snapshot.Workloads.Select(w => $"{w.Namespace}/{w.Kind}/{w.Name}").Should().Equal(
            "apps/Pod/alpha",
            "apps/Pod/zeta",
            "apps/Deployment/api",
            "kube-system/DaemonSet/agent");
    }
}
