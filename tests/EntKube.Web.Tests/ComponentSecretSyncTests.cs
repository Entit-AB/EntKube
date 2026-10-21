using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Pushing a catalog component's configured secrets into its namespace — the last path that was
/// still building Secrets with <c>--from-file</c>, and another that had no test.
///
/// <para><b>Why the old form was kept so long, and why it could go.</b> Its comment said "NEVER
/// --from-literal", and gave three reasons: <c>RunProcessAsync</c> passed one argument string that
/// .NET split itself, so a value with a space became several arguments; a value with a double
/// quote had the quote eaten and the rest of the command line swallowed into it, producing a
/// Secret that applies cleanly while holding the wrong password; and process arguments are
/// world-readable in <c>ps</c>. Every one of those is about the argument list, and base64 in a
/// manifest the seam writes 0600 satisfies all three better — base64 contains no spaces and no
/// quotes. The prohibition on <c>--from-literal</c> stands; it was never a prohibition on a
/// manifest.</para>
///
/// <para><b>What had to survive.</b> The bcrypt transform, below, is the behaviour a rewrite of
/// this method would most easily drop: the vault keeps the plaintext so an operator can reveal it,
/// and the cluster must get the hash. Dropping it would write a plaintext password into a Secret
/// whose key is literally named <c>PASSWORD_HASH</c>, and wg-easy would reject every login.</para>
/// </summary>
public class ComponentSecretSyncTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly ComponentLifecycleService sut;

    private readonly List<(string Name, string Namespace, string Manifest)> replaced = [];
    private readonly List<string> namespaces = [];

    private Guid tenantId, clusterId, componentId;

    public ComponentSecretSyncTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ReplaceSecretAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback((string name, string ns, string manifest, string _, string? _, CancellationToken _) =>
                replaced.Add((name, ns, manifest)))
            .ReturnsAsync("secret/wg-easy-env configured");

        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback((string ns, string _, CancellationToken _) => namespaces.Add(ns))
            .Returns(Task.CompletedTask);

        sut = TestServices.BuildLifecycle(testDb, k8s.Object);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A wg-easy component, because its <c>PASSWORD_HASH</c> field is the one in the catalog that
    /// sets <c>BcryptOnSync</c> — so this is the real configuration rather than a contrived one.
    /// </summary>
    private async Task SeedAsync(bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        componentId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "wg-easy",
            ComponentType = "Helm", HelmChartName = "wg-easy", Namespace = "wg-easy",
            Status = ComponentStatus.Installed,
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }
    }

    private async Task GivenAComponentSecretAsync(
        string name, string value, string k8sName = "wg-easy-env", string ns = "wg-easy")
    {
        VaultSecret stored = await vault.SetComponentSecretAsync(tenantId, componentId, name, value);

        await using ApplicationDbContext write = testDb.CreateContext();
        VaultSecret row = write.Set<VaultSecret>().First(s => s.Id == stored.Id);
        row.SyncToKubernetes = true;
        row.KubernetesSecretName = k8sName;
        row.KubernetesNamespace = ns;
        await write.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_configured_secret_lands_in_the_components_namespace()
    {
        await SeedAsync();
        await GivenAComponentSecretAsync("WG_HOST", "vpn.example.com");

        HelmExecutionResult result = await sut.SyncComponentSecretsAsync(tenantId, componentId);

        result.Success.Should().BeTrue(result.Output);

        (string name, string ns, string manifest) = replaced.Should().ContainSingle().Subject;

        name.Should().Be("wg-easy-env");
        ns.Should().Be("wg-easy");
        namespaces.Should().Contain("wg-easy");

        manifest.Should().Contain("\"WG_HOST\":")
            .And.Contain(Convert.ToBase64String("vpn.example.com"u8.ToArray()));
    }

    /// <summary>
    /// The transform a rewrite would most easily lose. The vault keeps the plaintext so the UI can
    /// reveal it; the cluster must get the bcrypt hash. Writing the plaintext into a key named
    /// <c>PASSWORD_HASH</c> would leave wg-easy rejecting every login — and the Secret would apply
    /// cleanly, so nothing would say why.
    /// </summary>
    [Fact]
    public async Task A_bcrypt_field_reaches_the_cluster_hashed_and_not_in_plaintext()
    {
        await SeedAsync();
        await GivenAComponentSecretAsync("PASSWORD_HASH", "hunter2");

        await sut.SyncComponentSecretsAsync(tenantId, componentId);

        string manifest = replaced.Should().ContainSingle().Subject.Manifest;

        manifest.Should().NotContain(Convert.ToBase64String("hunter2"u8.ToArray()),
            "the plaintext must not reach the cluster");

        string line = manifest.Split('\n').Single(l => l.Contains("\"PASSWORD_HASH\"", StringComparison.Ordinal));
        string encoded = line.Split(':', 2)[1].Trim();
        string hash = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        hash.Should().StartWith("$2", "bcrypt");
        BCrypt.Net.BCrypt.Verify("hunter2", hash).Should().BeTrue(
            "the hash has to be of the password the operator typed");
    }

    /// <summary>
    /// And the vault keeps the plaintext. The hash is a transformation on the way out, not a
    /// replacement of what is stored — the help text promises the operator can reveal it.
    /// </summary>
    [Fact]
    public async Task The_vault_still_holds_the_plaintext_after_a_bcrypt_sync()
    {
        await SeedAsync();
        await GivenAComponentSecretAsync("PASSWORD_HASH", "hunter2");

        await sut.SyncComponentSecretsAsync(tenantId, componentId);

        string? stored = await vault.GetComponentSecretValueAsync(tenantId, componentId, "PASSWORD_HASH");

        stored.Should().Be("hunter2");
    }

    [Fact]
    public async Task The_managed_by_labels_are_part_of_the_manifest()
    {
        await SeedAsync();
        await GivenAComponentSecretAsync("WG_HOST", "vpn.example.com");

        await sut.SyncComponentSecretsAsync(tenantId, componentId);

        string manifest = replaced.Single().Manifest;

        manifest.Should().Contain($"{VaultService.ManagedByLabelKey}: {VaultService.ManagedByLabelValue}");
        manifest.Should().Contain("entkube.io/managed: \"true\"",
            "this is what stops the deployment importer re-adopting EntKube's own Secret");
    }

    [Fact]
    public async Task Several_keys_land_in_one_secret()
    {
        await SeedAsync();
        await GivenAComponentSecretAsync("WG_HOST", "vpn.example.com");
        await GivenAComponentSecretAsync("WG_PORT", "51820");

        await sut.SyncComponentSecretsAsync(tenantId, componentId);

        string manifest = replaced.Should().ContainSingle(
            "both keys target the same K8s Secret, so there is one replace").Subject.Manifest;

        manifest.Should().Contain("\"WG_HOST\":").And.Contain("\"WG_PORT\":");
    }

    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_is_refused()
    {
        await SeedAsync(withKubeconfig: false);
        await GivenAComponentSecretAsync("WG_HOST", "vpn.example.com");

        HelmExecutionResult result = await sut.SyncComponentSecretsAsync(tenantId, componentId);

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("No kubeconfig");
        replaced.Should().BeEmpty();
        namespaces.Should().BeEmpty();
    }

    [Fact]
    public async Task A_component_with_nothing_marked_for_sync_touches_no_cluster()
    {
        await SeedAsync();

        HelmExecutionResult result = await sut.SyncComponentSecretsAsync(tenantId, componentId);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("No secrets marked");
        replaced.Should().BeEmpty();
    }

    // ──────── The refusal the tenant parameter buys ────────

    /// <summary>
    /// A stranger cannot sync this component's secrets into its cluster.
    ///
    /// <para>This was the last tautological seam call in the codebase, and it hid behind a local
    /// alias: the method read <c>Guid tenantId = component.Cluster.TenantId</c> and then called
    /// <c>ForAsync(tenantId, component.ClusterId)</c>, which reads exactly like every converted
    /// site. The comment above it explained that the check had to be vacuous because the method
    /// is "reached by component id by design" — all five callers had a tenant.</para>
    ///
    /// <para>Asserted on what reached the cluster: these are Secrets, so a refusal that still
    /// wrote them would be the whole of the problem.</para>
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_sync_this_components_secrets()
    {
        await SeedAsync();
        await GivenAComponentSecretAsync("ADMIN_PASSWORD", "s3cret");

        HelmExecutionResult result = await sut.SyncComponentSecretsAsync(Guid.NewGuid(), componentId);

        result.Success.Should().BeFalse();
        replaced.Should().BeEmpty("no Secret may be written to a cluster this tenant does not own");
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
    /// Syncing a component's secrets is the acknowledgment case that scales with the DATA rather
    /// than with the code: every target Secret is a namespace to ensure and a Secret to write, so
    /// a component with four of them asked eight times for one press of Sync. A fixed "two or
    /// three" is bad; a number the operator cannot predict is worse.
    ///
    /// <para>The mock has to acknowledge, because the gate lives inside the real
    /// <see cref="KubernetesClientFactory"/> and this one is mocked — without that the action
    /// reaches the gate zero times and the count below is trivially satisfied.</para>
    /// </summary>
    [Fact]
    public async Task Syncing_several_secrets_asks_the_operator_exactly_once()
    {
        await SeedAsync();

        // Two distinct target Secrets in two namespaces: four gated changes, one press.
        await GivenAComponentSecretAsync("WG_HOST", "vpn.example.com", "wg-easy-env", "wg-easy");
        await GivenAComponentSecretAsync("API_KEY", "s3cret", "wg-easy-api", "wg-easy-other");

        EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
            (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();
        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        int gatedChanges = 0;

        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
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

        ComponentLifecycleService gated = TestServices.BuildLifecycle(testDb, k8s.Object, gate: gate);

        HelmExecutionResult result = await gated.SyncComponentSecretsAsync(tenantId, componentId);

        result.Success.Should().BeTrue();
        gatedChanges.Should().BeGreaterThan(2,
            "two target Secrets means two namespaces ensured and two Secrets written — the point "
            + "of this case is that the number grows with the data");
        sink.Calls.Should().Be(1, "one press of Sync, one dialog");
    }

    /// <summary>
    /// And the window closes with the action: pressing Sync again asks again. Without this, a fix
    /// that suppressed the question for the rest of the circuit would pass the test above while
    /// quietly retiring the gate for everything the operator did next.
    /// </summary>
    [Fact]
    public async Task Pressing_sync_a_second_time_asks_again()
    {
        await SeedAsync();
        await GivenAComponentSecretAsync("WG_HOST", "vpn.example.com");

        EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
            (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();
        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string ns, string kubeconfig, CancellationToken ct) =>
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n",
                    Kubeconfig = kubeconfig,
                }, ct));

        k8s.Setup(x => x.ReplaceSecretAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string name, string ns, string manifest, string kubeconfig,
                            string? summary, CancellationToken ct) =>
            {
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = manifest,
                    Kubeconfig = kubeconfig,
                    Summary = summary,
                }, ct);
                return "secret configured";
            });

        ComponentLifecycleService gated = TestServices.BuildLifecycle(testDb, k8s.Object, gate: gate);

        await gated.SyncComponentSecretsAsync(tenantId, componentId);
        sink.Calls.Should().Be(1);

        await gated.SyncComponentSecretsAsync(tenantId, componentId);
        sink.Calls.Should().Be(2, "a second press is a second decision");
    }

    /// <summary>
    /// Applying the external routes is the other acknowledgment case in this service whose count
    /// grows with the data: every route namespace is a label patch, every route renamed out from
    /// under its old name is a delete, and the Gateway with all the listeners on it is one more
    /// apply. One press of "apply routes", as many dialogs as the cluster had hostnames.
    /// </summary>
    [Fact]
    public async Task Applying_the_external_routes_asks_the_operator_exactly_once()
    {
        await SeedAsync();

        await using (ApplicationDbContext write = testDb.CreateContext())
        {
            write.Set<ExternalRoute>().Add(new ExternalRoute
            {
                Id = Guid.NewGuid(), ComponentId = componentId,
                Hostname = "vpn.example.com", ServiceName = "wg-easy", ServicePort = 51821,
                TlsMode = TlsMode.ClusterIssuer, ClusterIssuerName = "letsencrypt",
            });
            await write.SaveChangesAsync();
        }

        EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
            (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();
        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        int gatedChanges = 0;

        // Reading the backend's ports is a READ and must never be gated; it is mocked only so the
        // Service lookup has something to parse.
        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        k8s.Setup(x => x.PatchStrategicAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string kind, string name, string ns, string patch, string kubeconfig,
                            CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Patch,
                    Kind = kind, Name = name, Namespace = ns, Patch = patch,
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });

        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
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

        ComponentLifecycleService gated = TestServices.BuildLifecycle(testDb, k8s.Object, gate: gate);

        await gated.ApplyExternalRoutesAsync(tenantId, componentId);

        gatedChanges.Should().BeGreaterThan(1,
            "labelling the route namespace and applying the Gateway are both gated changes");
        sink.Calls.Should().Be(1, "one press of apply, one dialog");
    }
}
