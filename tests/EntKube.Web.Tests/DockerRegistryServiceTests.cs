using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for DockerRegistryService environment scoping — registry credentials are scoped by
/// app + environment (null environment = shared), like app secrets, including the cross-environment
/// sync guard. Uses the intercepting test DB so the cluster kubeconfig resolves from the vault.
/// </summary>
public class DockerRegistryServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly DockerRegistryService sut;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    /// <summary>Namespaces the sync ensured, in order.</summary>
    private readonly List<string> namespacesEnsured = [];

    /// <summary>Secrets the sync replaced, as (name, namespace, manifest).</summary>
    private readonly List<(string Name, string Ns, string Manifest)> secretsReplaced = [];

    public DockerRegistryServiceTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(f => f.EnsureNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ns, _, _) => namespacesEnsured.Add(ns))
            .Returns(Task.CompletedTask);

        k8s.Setup(f => f.ReplaceSecretAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string?, CancellationToken>(
                (name, ns, manifest, _, _, _) => secretsReplaced.Add((name, ns, manifest)))
            .ReturnsAsync("secret/registry-creds replaced");
        // The real factory over the intercepting test database: the credential reaches the cluster
        // the way production resolves it, and a cross-tenant ask refuses here rather than in a mock.
        sut = new DockerRegistryService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            testDb.Encryption);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }

    private async Task<(Tenant tenant, App app, Data.Environment prod, Data.Environment test)> SeedAsync()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Co", Slug = $"co-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Cust" };
        db.Customers.Add(customer);
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "App" };
        db.Apps.Add(app);
        Data.Environment prod = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "prod" };
        Data.Environment test = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "test" };
        db.Set<Data.Environment>().AddRange(prod, test);
        await db.SaveChangesAsync();
        await vault.InitializeVaultAsync(tenant.Id);
        return (tenant, app, prod, test);
    }

    private Task<DockerRegistryCredential> CreateAsync(Guid tenantId, Guid? appId, string name, Guid? envId) =>
        sut.CreateAsync(tenantId, appId, name, DockerRegistryType.Generic,
            "registry.example.com", "user", "pass", email: null, environmentId: envId);

    [Fact]
    public async Task CreateAsync_AppScoped_StoresEnvironmentBinding()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();

        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-acr", prod.Id);

        cred.AppId.Should().Be(app.Id);
        cred.EnvironmentId.Should().Be(prod.Id);
    }

    [Fact]
    public async Task CreateAsync_TenantWide_IgnoresEnvironment()
    {
        (Tenant tenant, _, Data.Environment prod, _) = await SeedAsync();

        // No app → an environment binding makes no sense and is dropped.
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, appId: null, "shared", prod.Id);

        cred.AppId.Should().BeNull();
        cred.EnvironmentId.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithEnvironmentFilter_ReturnsSharedAndThatEnvironmentOnly()
    {
        (Tenant tenant, App app, Data.Environment prod, Data.Environment test) = await SeedAsync();
        await CreateAsync(tenant.Id, app.Id, "shared-cred", null);
        await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await CreateAsync(tenant.Id, app.Id, "test-cred", test.Id);

        List<DockerRegistryCredential> visibleInProd = await sut.GetAsync(tenant.Id, app.Id, prod.Id);

        visibleInProd.Select(c => c.Name).Should().BeEquivalentTo("shared-cred", "prod-cred");
        visibleInProd.Should().NotContain(c => c.Name == "test-cred");
    }

    [Fact]
    public async Task ChangeScopeAsync_BindsAndUnbinds()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "cred", null);

        await sut.ChangeScopeAsync(tenant.Id, cred.Id, prod.Id);
        (await db.Set<DockerRegistryCredential>().AsNoTracking().FirstAsync(c => c.Id == cred.Id))
            .EnvironmentId.Should().Be(prod.Id);

        await sut.ChangeScopeAsync(tenant.Id, cred.Id, null);
        (await db.Set<DockerRegistryCredential>().AsNoTracking().FirstAsync(c => c.Id == cred.Id))
            .EnvironmentId.Should().BeNull();
    }

    [Fact]
    public async Task ChangeScopeAsync_NoOpForTenantWide()
    {
        (Tenant tenant, _, Data.Environment prod, _) = await SeedAsync();
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, appId: null, "shared", null);

        await sut.ChangeScopeAsync(tenant.Id, cred.Id, prod.Id);

        (await db.Set<DockerRegistryCredential>().AsNoTracking().FirstAsync(c => c.Id == cred.Id))
            .EnvironmentId.Should().BeNull();
    }

    [Fact]
    public async Task SyncToKubernetesAsync_EnvBoundCredToOtherEnvironmentCluster_Fails()
    {
        (Tenant tenant, App app, Data.Environment prod, Data.Environment test) = await SeedAsync();

        // A cluster in the TEST environment, with a kubeconfig stored in the vault.
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = test.Id,
            Name = "test-cluster",
            ApiServerUrl = "https://k8s",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vault, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        // A credential bound to PROD, configured to sync into the TEST cluster.
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await sut.ConfigureSyncAsync(tenant.Id, cred.Id, cluster.Id, "registry-creds", "app");

        KubernetesOperationResult<string> result = await sut.SyncToKubernetesAsync(tenant.Id, cred.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("different environment");

        secretsReplaced.Should().BeEmpty("the guard runs before anything reaches the cluster");
    }

    /// <summary>
    /// The sync writes a registry password into a namespace, so it is exactly the kind of change
    /// that must not happen without the gate seeing it. Through the seam it is
    /// <c>EnsureNamespaceAsync</c> + <c>ReplaceSecretAsync</c>; it used to be <c>kubectl apply -f</c>
    /// run as its own process with <c>--kubeconfig</c>, which the gate cannot see at all.
    /// </summary>
    [Fact]
    public async Task SyncToKubernetesAsync_WritesTheSecretThroughTheSeam()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
            Name = "prod-cluster", ApiServerUrl = "https://k8s",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vault, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await sut.ConfigureSyncAsync(tenant.Id, cred.Id, cluster.Id, "registry-creds", "app");

        (await sut.SyncToKubernetesAsync(tenant.Id, cred.Id)).IsSuccess.Should().BeTrue();

        namespacesEnsured.Should().Equal(["app"]);
        secretsReplaced.Should().ContainSingle()
            .Which.Should().Match<(string Name, string Ns, string Manifest)>(s =>
                s.Name == "registry-creds" && s.Ns == "app"
                && s.Manifest.Contains("kubernetes.io/dockerconfigjson")
                && !s.Manifest.Contains("kind: Namespace"));
    }

    /// <summary>
    /// And another tenant cannot use it to write into a cluster that is not theirs: the seam
    /// refuses, so nothing is ensured and nothing is written.
    /// </summary>
    [Fact]
    public async Task SyncToKubernetesAsync_RefusesAnotherTenantsCluster()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
            Name = "prod-cluster", ApiServerUrl = "https://k8s",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vault, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await sut.ConfigureSyncAsync(tenant.Id, cred.Id, cluster.Id, "registry-creds", "app");

        KubernetesOperationResult<string> result =
            await sut.SyncToKubernetesAsync(Guid.NewGuid(), cred.Id);

        result.IsSuccess.Should().BeFalse();
        secretsReplaced.Should().BeEmpty("nothing should reach anyone's cluster");
        namespacesEnsured.Should().BeEmpty();
    }

    // ──────── Tenancy on the row, where there is no cluster to refuse ────────

    /// <summary>
    /// ⚠️ <c>GetPasswordAsync</c> is the sharpest shape in this service: it <b>returns the
    /// registry password in cleartext</b> and took a bare credential id. There is no cluster, no
    /// manifest and no seam between that id and the secret, so nothing downstream could have
    /// refused — unlike <c>SyncToKubernetesAsync</c> above, whose tenant was already load-bearing
    /// because the cluster seam refuses a foreign cluster.
    ///
    /// <para><c>DockerRegistryCredential</c> has no <c>TenantId</c> of its own; it lives in a
    /// vault, and the vault has one, so every predicate here runs <c>c.Vault.TenantId</c>.</para>
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_read_or_rewrite_a_registry_credential()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();
        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-acr", prod.Id);

        Guid stranger = Guid.NewGuid();

        (await sut.GetPasswordAsync(stranger, cred.Id)).Should().BeNull(
            "the credential exists and its password is retrievable, so this is a refusal");
        (await sut.GetPasswordAsync(tenant.Id, cred.Id)).Should().Be("pass",
            "the owner's own read still returns it, or the null above proves nothing");

        await sut.UpdateAsync(stranger, cred.Id, "attacker", "attacker-pass", "a@example.com");
        await sut.ChangeScopeAsync(stranger, cred.Id, null);
        (await sut.DeleteAsync(stranger, cred.Id)).Should().BeFalse();

        using ApplicationDbContext after = testDb.CreateContext();
        DockerRegistryCredential intact =
            after.Set<DockerRegistryCredential>().Single(c => c.Id == cred.Id);
        intact.Username.Should().Be("user", "rewriting it would break every pull that uses it");
        intact.EnvironmentId.Should().Be(prod.Id, "and unscoping it would widen who may pull");
        (await sut.GetPasswordAsync(tenant.Id, cred.Id)).Should().Be("pass");

        // The owner can still do all three.
        await sut.UpdateAsync(tenant.Id, cred.Id, "renamed", null, null);
        (await sut.GetAsync(tenant.Id, app.Id, prod.Id))
            .Should().ContainSingle().Which.Username.Should().Be("renamed");
        (await sut.DeleteAsync(tenant.Id, cred.Id)).Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════
    //  One action, one acknowledgment
    // ════════════════════════════════════════════════════════════════

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
    /// Writing a pull secret ensures the namespace and then replaces the Secret: two gated
    /// changes, one press.
    ///
    /// <para>The mock has to acknowledge, because the gate lives inside the real
    /// <see cref="KubernetesClientFactory"/> and this one is mocked — without that the action
    /// reaches the gate zero times and "one dialog" is satisfied by an action that asked
    /// nobody.</para>
    /// </summary>
    [Fact]
    public async Task SyncToKubernetesAsync_AsksTheOperatorExactlyOnce()
    {
        (Tenant tenant, App app, Data.Environment prod, _) = await SeedAsync();

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = prod.Id,
            Name = "prod-cluster", ApiServerUrl = "https://k8s",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vault, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        DockerRegistryCredential cred = await CreateAsync(tenant.Id, app.Id, "prod-cred", prod.Id);
        await sut.ConfigureSyncAsync(tenant.Id, cred.Id, cluster.Id, "registry-creds", "app");

        EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
            (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();
        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        int gatedChanges = 0;

        k8s.Setup(f => f.EnsureNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string ns, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n",
                    Kubeconfig = kubeconfig,
                }, ct);
            });

        k8s.Setup(f => f.ReplaceSecretAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string name, string ns, string manifest, string kubeconfig,
                            string? summary, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = manifest,
                    Kubeconfig = kubeconfig,
                    Summary = summary,
                }, ct);
                return "secret replaced";
            });

        DockerRegistryService gated = new(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            testDb.Encryption,
            gate);

        (await gated.SyncToKubernetesAsync(tenant.Id, cred.Id)).IsSuccess.Should().BeTrue();

        gatedChanges.Should().BeGreaterThan(1, "the namespace and the Secret are both gated changes");
        sink.Calls.Should().Be(1, "one action, one dialog");

        // The window closes with the action: syncing again is a new decision.
        (await gated.SyncToKubernetesAsync(tenant.Id, cred.Id)).IsSuccess.Should().BeTrue();
        sink.Calls.Should().Be(2, "a second press asks again");
    }
}
