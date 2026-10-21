using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// One operator action, one acknowledgment — for applying the Stalwart mail configuration.
///
/// <para>This is the longest action in the product: the credential mirror writes a Secret per
/// namespace, the StatefulSet is re-applied, the configuration plan runs as a Job, and the routes
/// are applied at the end. One press of Apply raised a dialog for each.</para>
///
/// <para><b>The test stops at the credential mismatch, deliberately.</b> The apply refuses, by
/// design, when the recovery credential and the CLI credential on the cluster disagree — before
/// anything restarts. That refusal happens AFTER the credential sync, which is where the first
/// gated changes are, so it is enough to show the collapsing while costing none of what comes
/// after it: a five-minute rollout poll, a Job that has to be watched, and several live resource
/// shapes to fake. The operation is scoped to the whole method, so what follows the refusal is
/// inside the same window by construction.</para>
/// </summary>
public class StalwartApplyOneAckTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
        (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();

    private int gatedChanges;

    private Guid tenantId, clusterId, envId, componentId;

    public StalwartApplyOneAckTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        // Reads: never gated, and shaped only enough for the apply to get as far as the
        // credential check. The recovery entry comes back empty, which is the refusal this
        // test rides to keep the rollout and the Job out of the fixture.
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        k8s.Setup(x => x.GetSecretValueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
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

    private void GateEveryWrite()
    {
        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        k8s.Setup(x => x.ReplaceSecretAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
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
                return "secret configured";
            });

        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = manifest,
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });
    }

    private StalwartService NewService() =>
        new(testDb.Factory, vault,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            // Not reached before the credential refusal, and null rather than a half-built graph
            // so a test that strays past it fails loudly instead of passing for a bad reason.
            routeService: null!, keycloakService: null!,
            lifecycleService: TestServices.BuildLifecycle(testDb, k8s.Object),
            redisService: null!,
            catalog: new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            logger: NullLogger<StalwartService>.Instance,
            gate: gate);

    private async Task SeedAsync()
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        envId = Guid.NewGuid();
        componentId = Guid.NewGuid();

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
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = StalwartService.CatalogKey,
            ComponentType = "Manifest", HelmChartName = "stalwart",
            ReleaseName = "stalwart", Namespace = "mail",
            Status = ComponentStatus.Installed,
        });
        Guid configId = Guid.NewGuid();
        db.Set<StalwartComponentConfig>().Add(new StalwartComponentConfig
        {
            Id = configId, TenantId = tenantId, ClusterComponentId = componentId,
            Hostname = "mail.example.com", AdminUsername = "admin",
            // Internal rather than the LDAP default: an LDAP configuration needs a directory
            // connection to get past the preflight, and a directory is nothing to do with what
            // this test is about.
            AuthMode = StalwartAuthMode.Internal,
        });
        await db.SaveChangesAsync();

        // The apply runs a preflight BEFORE it touches anything, and refuses a configuration
        // that would produce "a mail server nobody can log in to". A domain and an account get
        // past it — without them the action makes no cluster changes at all and the dialog count
        // would be measuring a refusal rather than a collapse.
        Guid domainId = Guid.NewGuid();
        db.Set<StalwartMailDomain>().Add(new StalwartMailDomain
        {
            Id = domainId, ConfigId = configId, Name = "example.com", IsPrimary = true,
        });
        await db.SaveChangesAsync();
        db.Set<StalwartMailAccount>().Add(new StalwartMailAccount
        {
            // Matches AdminUsername: the preflight refuses an administrator who is not an account
            // this server serves.
            Id = Guid.NewGuid(), ConfigId = configId, DomainId = domainId, LocalPart = "admin",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        // Internal auth needs a stored administrator password, or the preflight refuses.
        await vault.SetComponentSecretAsync(
            tenantId, componentId, StalwartManifestBuilder.AdminPasswordSecretName, "admin-pw");

        // Two secrets bound for two different namespaces, so the credential mirror alone makes
        // four gated changes — a namespace and a Secret each. Without this the sync writes
        // nothing and "one dialog" would be true of an action that changed nothing.
        await GivenASyncedSecretAsync("STALWART_ADMIN", "pw", "stalwart-credentials", "mail");
        await GivenASyncedSecretAsync("STALWART_EXTRA", "x", "stalwart-extra", "mail-aux");
    }

    private async Task GivenASyncedSecretAsync(string name, string value, string k8sName, string ns)
    {
        VaultSecret stored = await vault.SetComponentSecretAsync(tenantId, componentId, name, value);

        await using ApplicationDbContext write = testDb.CreateContext();
        VaultSecret row = write.Set<VaultSecret>().First(s => s.Id == stored.Id);
        row.SyncToKubernetes = true;
        row.KubernetesSecretName = k8sName;
        row.KubernetesNamespace = ns;
        await write.SaveChangesAsync();
    }

    [Fact]
    public async Task Applying_the_configuration_asks_the_operator_exactly_once()
    {
        await SeedAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        HelmExecutionResult result = await NewService().ApplyConfigurationAsync(tenantId, componentId);

        // The apply refuses at the credential check — which is the point: everything up to it
        // already happened, and it happened under one dialog.
        result.Success.Should().BeFalse("the recovery credential is deliberately absent");
        result.Output.Should().Contain("recovery", "it should reach the credential check: " + result.Output);

        gatedChanges.Should().BeGreaterThan(1,
            "the credential mirror alone writes a namespace and a Secret per target");
        sink.Calls.Should().Be(1, "one press of Apply, one dialog");
    }

    /// <summary>
    /// And the window closes with the action. Without this a fix that suppressed the question for
    /// the rest of the circuit would pass the test above while retiring the gate for everything
    /// the operator did next.
    /// </summary>
    [Fact]
    public async Task A_second_apply_asks_again()
    {
        await SeedAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        StalwartService sut = NewService();

        await sut.ApplyConfigurationAsync(tenantId, componentId);
        sink.Calls.Should().Be(1);

        await sut.ApplyConfigurationAsync(tenantId, componentId);
        sink.Calls.Should().Be(2, "a second press is a second decision");
    }
}
