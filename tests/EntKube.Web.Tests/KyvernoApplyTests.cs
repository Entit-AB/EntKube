using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What applying Kyverno policies sends to a cluster, and whose cluster it is willing to send to.
///
/// <para><b>Why this is worth pinning.</b> Both entry points used to take a
/// <c>KubernetesCluster</c> and read <c>cluster.Kubeconfig</c> off the row they were handed, so the
/// caller's choice of entity <em>was</em> the authorization. These are admission policies: aimed at
/// the wrong cluster they either enforce one tenant's deny-list on another's workloads, or remove a
/// ClusterPolicy that was the only thing covering them. Deployment tests cover the rendering;
/// nothing covered who the apply acts as.</para>
/// </summary>
public class KyvernoApplyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly KyvernoPolicyService sut;

    private readonly List<(string Manifest, string Namespace, string? Summary)> applied = [];
    private readonly List<(string Kind, string Name, string Namespace)> deleted = [];
    private readonly Dictionary<(string Resource, string Name), string> live = [];

    public KyvernoApplyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string ns, string _, string? s, CancellationToken _) => applied.Add((m, ns, s)))
            .ReturnsAsync("policy.kyverno.io/require-labels configured\n");

        k8s.Setup(x => x.DeleteManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string kind, string name, string ns, string _, CancellationToken _)
                => deleted.Add((kind, name, ns)))
            .Returns(Task.CompletedTask);

        // A Kyverno from before the CEL kinds, unless a test says otherwise.
        k8s.Setup(x => x.GetResourceJsonAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string resource, string name, string _, string _, CancellationToken _) =>
                live.TryGetValue((resource, name), out string? json)
                    ? new HelmExecutionResult { Success = true, Output = json }
                    : new HelmExecutionResult { Success = false, Output = $"Error from server (NotFound): \"{name}\" not found" });

        sut = new KyvernoPolicyService(
            testDb.Factory,
            k8s.Object,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new NoAckGate(),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<KyvernoPolicyService>.Instance)
        {
            ReadyTimeout = TimeSpan.Zero,
            ReadyPollInterval = TimeSpan.Zero,
        };
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid tenantId;
    private Guid environmentId;

    private async Task<KubernetesCluster> SeedClusterAsync(bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        environmentId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "production",
        });
        KubernetesCluster cluster = new()
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }

        return cluster;
    }

    private static List<KyvernoPolicy> OnePolicy() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            PolicyType = KyvernoPolicyType.DisallowPrivilegedContainers,
            ValidationFailureAction = KyvernoValidationFailureAction.Audit,
        },
    ];

    [Fact]
    public async Task The_policies_land_in_the_apps_namespace()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeTrue(output);

        (string manifest, string ns, string? summary) = applied.Should().ContainSingle().Subject;

        ns.Should().Be("billing-prod");
        summary.Should().Be("Apply Kyverno policies to billing-prod");
        manifest.Should().Contain("kind: Policy");
    }

    /// <summary>
    /// The case the old signature could not express. The cluster belongs to somebody else, and the
    /// caller names it anyway — which is what a deployment row pointing at a foreign cluster would
    /// do. Nothing may reach that cluster.
    /// </summary>
    [Fact]
    public async Task Another_tenants_cluster_is_refused_and_nothing_is_applied()
    {
        KubernetesCluster theirs = await SeedClusterAsync();
        Guid stranger = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"stranger-{stranger:N}" });
        await db.SaveChangesAsync();
        await vault.InitializeVaultAsync(stranger);

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), stranger, theirs.Id, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("not this tenant's");
        applied.Should().BeEmpty("an admission policy applied to a cluster the asker does not own "
            + "enforces their rules on somebody else's workloads");
    }

    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_is_refused_without_a_cluster_call()
    {
        KubernetesCluster cluster = await SeedClusterAsync(withKubeconfig: false);

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("no kubeconfig");
        applied.Should().BeEmpty();
    }

    /// <summary>
    /// With nothing enabled the cluster-scoped policy must be <em>removed</em>, not left behind —
    /// and the removal is as tenant-scoped as the apply. A stranger must not be able to delete the
    /// ClusterPolicy that is covering this tenant's namespaces.
    /// </summary>
    [Fact]
    public async Task Reconcile_removes_the_cluster_policy_when_nothing_is_enabled()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        (bool success, _) = await sut.ReconcileClusterRbacPolicyAsync(tenantId, cluster.Id);

        success.Should().BeTrue();
        (string kind, string name, string ns) = deleted.Should().ContainSingle().Subject;
        kind.Should().Be("clusterpolicy");
        ns.Should().BeEmpty("a ClusterPolicy is cluster-scoped");
        name.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reconcile_for_another_tenants_cluster_deletes_nothing()
    {
        KubernetesCluster theirs = await SeedClusterAsync();
        Guid stranger = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"stranger-{stranger:N}" });
        await db.SaveChangesAsync();

        (bool success, string output) = await sut.ReconcileClusterRbacPolicyAsync(stranger, theirs.Id);

        success.Should().BeFalse();
        output.Should().Contain("not this tenant's");
        deleted.Should().BeEmpty("removing a ClusterPolicy uncovers every namespace it protected");
    }

    // ---- Moving to the CEL kinds ---------------------------------------------------------

    private const string Crd = "customresourcedefinitions.apiextensions.k8s.io";

    /// <summary>A Kyverno serving the CEL kinds, with these legacy policies in every namespace asked about.</summary>
    private void ServeCel(string chart, params string[] legacyPolicies)
    {
        live[(Crd, "namespacedvalidatingpolicies.policies.kyverno.io")] =
            """{"spec":{"versions":[{"name":"v1","served":true},{"name":"v1beta1","served":true}]}}""";
        live[(Crd, "policies.kyverno.io")] =
            """{"metadata":{"labels":{"app.kubernetes.io/version":"CHART"}}}""".Replace("CHART", chart);

        k8s.Setup(x => x.GetJsonAsync("policies.kyverno.io", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[" + string.Join(",", legacyPolicies.Select(n => "{\"metadata\":{\"name\":\"" + n + "\"}}")) + "]}");
    }

    /// <summary>
    /// Enforced, and not "ready": Kyverno's own status for a policy over a kind its reports controller may
    /// not read, which is what the chart ships for Roles.
    /// </summary>
    private void Enforced(string name, string resource = "namespacedvalidatingpolicies.policies.kyverno.io") =>
        live[(resource, name)] = """
            {"status":{"conditionStatus":{"ready":false,"conditions":[
              {"type":"RBACPermissionsGranted","status":"False"},
              {"type":"WebhookConfigured","status":"True"}]}}}
            """;

    [Fact]
    public async Task An_enforced_replacement_retires_the_policy_it_replaces()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        ServeCel("3.8.1", "disallow-privileged-containers");
        Enforced("disallow-privileged-containers");

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeTrue(output);
        applied.Should().ContainSingle().Which.Manifest.Should().Contain("kind: NamespacedValidatingPolicy");
        deleted.Should().Equal(("policies.kyverno.io", "disallow-privileged-containers", "billing-prod"));
    }

    /// <summary>Deleting first, or without asking, leaves the namespace with neither policy.</summary>
    [Fact]
    public async Task A_replacement_not_yet_enforced_leaves_the_old_policy_enforcing()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        ServeCel("3.8.1", "disallow-privileged-containers");
        // "Ready" before the webhook has said anything about it.
        live[("namespacedvalidatingpolicies.policies.kyverno.io", "disallow-privileged-containers")] =
            """{"status":{"conditionStatus":{"ready":true,"conditions":[{"type":"RBACPermissionsGranted","status":"True"}]}}}""";

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("still enforced").And.Contain("disallow-privileged-containers");
        deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_legacy_policy_with_no_replacement_is_left_alone()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        ServeCel("3.8.1", "disallow-host-path", "somebody-elses");
        Enforced("disallow-host-path");

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeTrue(output);
        deleted.Should().BeEmpty("neither was applied, so removing either would drop an enforced rule");
    }

    /// <summary>Read as "old Kyverno", this would write policies a 1.20 cluster accepts and ignores.</summary>
    [Fact]
    public async Task A_cluster_that_cannot_be_asked_is_not_written_to()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        k8s.Setup(x => x.GetResourceJsonAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HelmExecutionResult { Success = false, Output = "Unable to connect to the server" });

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(OnePolicy(), tenantId, cluster.Id, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("Unable to connect");
        applied.Should().BeEmpty();
    }

    [Theory]
    [InlineData("3.9.2", true)]
    [InlineData("3.10.0", false)]
    public async Task A_legacy_custom_policy_is_withheld_only_where_it_would_do_nothing(string chart, bool appliedThere)
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        ServeCel(chart);
        List<KyvernoPolicy> policies =
        [
            .. OnePolicy(),
            new()
            {
                Id = Guid.NewGuid(), PolicyType = KyvernoPolicyType.Custom, Name = "mine",
                CustomYaml = "apiVersion: kyverno.io/v1\nkind: Policy\nmetadata:\n  name: mine\n",
            },
        ];

        (bool success, string output) =
            await sut.ApplyToNamespaceAsync(policies, tenantId, cluster.Id, "billing-prod");

        string manifest = applied.Should().ContainSingle().Subject.Manifest;
        manifest.Should().Contain("disallow-privileged-containers", "the rest still apply");
        manifest.Contains("name: mine").Should().Be(appliedThere);
        success.Should().Be(appliedThere);
        if (!appliedThere) output.Should().Contain("mine").And.Contain("3.10.0");
    }

    /// <summary>A gate that asks nobody, for tests that are not about the gate.</summary>
    private sealed class NoAckGate : EntKube.Web.Services.ClusterChanges.IClusterChangeGate
    {
        public Task AcknowledgeAsync(
            EntKube.Web.Services.ClusterChanges.PlannedClusterChange change,
            CancellationToken ct = default) => Task.CompletedTask;

        public IDisposable RegisterSink(EntKube.Web.Services.ClusterChanges.IClusterChangeAckSink sink)
            => new Nothing();

        public IDisposable DeclareUnattended(string reason) => new Nothing();

        public IDisposable DeclareConsequence(string reason) => new Nothing();

        public IDisposable BeginOperation(string what) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>
    /// A second tenant with an environment of its own. Both rows are needed: KyvernoPolicy
    /// has FKs to Tenant and Environment, so a policy with fabricated ids cannot be inserted
    /// at all — which is also the check that the crossing being tested is constructable.
    /// </summary>
    private async Task<(Guid Tenant, Guid Environment)> SeedStrangerAsync()
    {
        Guid strangerTenant = Guid.NewGuid();
        Guid strangerEnv = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = strangerTenant, Name = "Annan", Slug = $"annan-{strangerTenant:N}",
        });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = strangerEnv, TenantId = strangerTenant, Name = "production",
        });
        await db.SaveChangesAsync();

        return (strangerTenant, strangerEnv);
    }

    // ---- Somebody else's policy ----------------------------------------------------------

    /// <summary>
    /// <b>The enforcement mode is the policy.</b> Moving a policy from Enforce to Audit leaves
    /// it listed, named and reporting — and stops it blocking anything. That is the difference
    /// between a privileged pod being refused and being admitted with a warning nobody reads,
    /// and by id alone it could be done to another tenant's environment.
    /// </summary>
    [Fact]
    public async Task Another_tenants_policy_cannot_be_moved_to_audit()
    {
        await SeedClusterAsync();
        (Guid strangerTenant, Guid strangerEnv) = await SeedStrangerAsync();

        KyvernoPolicy theirs = new()
        {
            Id = Guid.NewGuid(),
            TenantId = strangerTenant,
            EnvironmentId = strangerEnv,
            PolicyType = KyvernoPolicyType.DisallowPrivilegedContainers,
            ValidationFailureAction = KyvernoValidationFailureAction.Enforce,
        };
        db.Set<KyvernoPolicy>().Add(theirs);
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.UpdatePolicyAsync(
            tenantId, theirs.Id, KyvernoValidationFailureAction.Audit, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Kyverno policy not found, or it is not this tenant's.");

        KyvernoPolicy stored = await db.Set<KyvernoPolicy>().AsNoTracking()
            .FirstAsync(p => p.Id == theirs.Id);

        stored.ValidationFailureAction.Should().Be(KyvernoValidationFailureAction.Enforce);
    }

    /// <summary>
    /// Deleting removes the admission rule outright on the next apply. The owner's own delete
    /// still works, or the refusal above says nothing.
    /// </summary>
    [Fact]
    public async Task Another_tenants_policy_cannot_be_deleted()
    {
        await SeedClusterAsync();
        (Guid strangerTenant, Guid strangerEnv) = await SeedStrangerAsync();

        KyvernoPolicy theirs = new()
        {
            Id = Guid.NewGuid(),
            TenantId = strangerTenant,
            EnvironmentId = strangerEnv,
            PolicyType = KyvernoPolicyType.DisallowPrivilegedContainers,
            ValidationFailureAction = KyvernoValidationFailureAction.Enforce,
        };
        KyvernoPolicy ours = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            PolicyType = KyvernoPolicyType.DisallowPrivilegedContainers,
            ValidationFailureAction = KyvernoValidationFailureAction.Enforce,
        };
        db.Set<KyvernoPolicy>().AddRange(theirs, ours);
        await db.SaveChangesAsync();

        (await sut.DeletePolicyAsync(tenantId, theirs.Id)).Should().BeFalse();
        (await db.Set<KyvernoPolicy>().AsNoTracking().AnyAsync(p => p.Id == theirs.Id))
            .Should().BeTrue();

        (await sut.DeletePolicyAsync(tenantId, ours.Id)).Should().BeTrue();
        (await db.Set<KyvernoPolicy>().AsNoTracking().AnyAsync(p => p.Id == ours.Id))
            .Should().BeFalse();
    }
}
