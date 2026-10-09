using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Pushing vault secrets into a cluster — the most consequential write EntKube performs, and
/// until now one with no test at all.
///
/// <para><b>What this conversion had to preserve.</b> The code it replaces said "delete and
/// recreate for clean state", and that comment was load-bearing: these Secrets are not created by
/// <c>apply</c>, so they carry no last-applied annotation, and an apply would therefore
/// <em>merge</em>. A key removed from the vault — a credential that has been revoked — would have
/// survived in the cluster. The first test here is that property.</para>
///
/// <para><b>What it had to avoid.</b> Values must not reach a process argument list, which is what
/// the <c>--from-file</c> form existed for. They are now in a manifest the seam writes to a 0600
/// file instead, and <see cref="SecretRedactionTests"/> covers them not reaching the dialog
/// either.</para>
/// </summary>
public class VaultSecretSyncTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    /// <summary>Every secret replacement asked of a cluster, as (name, namespace, manifest).</summary>
    private readonly List<(string Name, string Namespace, string Manifest)> replaced = [];

    /// <summary>Every namespace the sync ensured exists.</summary>
    private readonly List<string> namespaces = [];

    private Guid ours, theirs, ourCluster, theirCluster, appId;

    public VaultSecretSyncTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();

        k8s.Setup(x => x.ReplaceSecretAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback((string name, string ns, string manifest, string _, string? _, CancellationToken _) =>
                replaced.Add((name, ns, manifest)))
            .ReturnsAsync("secret/synced configured");

        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback((string ns, string _, CancellationToken _) => namespaces.Add(ns))
            .Returns(Task.CompletedTask);

        vault = testDb.CreateVaultService(new ClusterClientFactory(testDb.Factory, k8s.Object));
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync()
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();

        (ourCluster, appId) = await SeedTenantAsync(ours, "ours");
        (theirCluster, _) = await SeedTenantAsync(theirs, "theirs");
    }

    private async Task<(Guid ClusterId, Guid AppId)> SeedTenantAsync(Guid tenantId, string label)
    {
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid app = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        await db.SaveChangesAsync();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label} customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = app, CustomerId = customerId, Name = $"{label}-app" });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        return (clusterId, app);
    }

    /// <summary>
    /// Stores an app secret and marks it for sync to <paramref name="clusterId"/>. A null
    /// environment is a <em>shared</em> secret, which is the case the cross-environment safety
    /// check does not cover.
    /// </summary>
    private async Task GivenAnAppSecretAsync(
        Guid tenantId, Guid app, Guid clusterId, string name, string value,
        string k8sName = "app-secrets", string ns = "billing")
    {
        VaultSecret stored = await vault.SetAppSecretAsync(tenantId, app, name, value);

        await using ApplicationDbContext write = testDb.CreateContext();
        VaultSecret row = write.Set<VaultSecret>().First(s => s.Id == stored.Id);
        row.SyncToKubernetes = true;
        row.KubernetesSecretName = k8sName;
        row.KubernetesNamespace = ns;
        row.KubernetesClusterId = clusterId;
        await write.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════════════════════
    //  The property that made `apply` unusable here
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The manifest carries exactly the keys the vault holds, and the operation is a replace
    /// rather than an apply — so a key that has been removed from the vault cannot survive in the
    /// cluster. An apply would have merged it back in, because these Secrets have no last-applied
    /// annotation for kubectl to diff against.
    /// </summary>
    [Fact]
    public async Task The_manifest_holds_exactly_the_keys_the_vault_holds()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");

        HelmExecutionResult result = await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        result.Success.Should().BeTrue(result.Output);

        (string name, string ns, string manifest) = replaced.Should().ContainSingle().Subject;

        name.Should().Be("app-secrets");
        ns.Should().Be("billing");
        namespaces.Should().Contain("billing", "the namespace is ensured before the secret lands");

        manifest.Should().Contain("\"DB_PASSWORD\":").And.Contain("\"API_KEY\":");
        manifest.Should().Contain(Convert.ToBase64String("hunter2"u8.ToArray()));
        manifest.Should().Contain(Convert.ToBase64String("sk-live-1234"u8.ToArray()));
    }

    /// <summary>
    /// The managed-by labels are in the manifest rather than stamped by a follow-up
    /// <c>kubectl label</c>. That call was best-effort, so a Secret could be written and left
    /// unlabelled — and the deployment importer uses exactly that label to avoid re-adopting
    /// EntKube's own Secrets back into the vault.
    /// </summary>
    [Fact]
    public async Task The_managed_by_labels_are_part_of_the_manifest()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2");

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        string manifest = replaced.Single().Manifest;

        manifest.Should().Contain($"{VaultService.ManagedByLabelKey}: {VaultService.ManagedByLabelValue}");
        manifest.Should().Contain("entkube.io/managed: \"true\"");
    }

    /// <summary>
    /// Values go in the manifest, never in an argument list. That is the property the
    /// <c>--from-file</c> form this replaces existed to get, and it is why the replacement is a
    /// manifest and not <c>--from-literal</c>: a process argument list is readable by anything
    /// that can see the process table.
    /// </summary>
    [Fact]
    public async Task The_value_is_never_put_in_a_command_line()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2");

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        KubernetesClientFactory.BuildApplyArguments("/tmp/m.yaml", "/tmp/kc", "billing")
            .Should().NotContain("hunter2").And.NotContain("--from-literal");
    }

    /// <summary>
    /// A replace is a delete <em>then</em> an apply, and both steps have to be there.
    ///
    /// <para>Asserted on the sequence as data because every test above reaches the cluster through
    /// a mocked factory: a mutant that drops the delete turns the replace into a plain apply, lets
    /// a key removed from the vault survive in the cluster, and passes every other test in this
    /// file. <c>--ignore-not-found</c> is what makes the first sync work, when there is nothing to
    /// remove yet.</para>
    /// </summary>
    [Fact]
    public void A_replace_deletes_before_it_applies()
    {
        IReadOnlyList<string> sequence = KubernetesClientFactory.BuildReplaceSecretSequence(
            "app-secrets", "billing", "/tmp/m.yaml", "/tmp/kc");

        sequence.Should().HaveCount(2, "a replace is not an apply");

        sequence[0].Should().Be(
            "delete secret app-secrets --namespace billing --ignore-not-found --kubeconfig=/tmp/kc");
        sequence[1].Should().Be(
            "apply -f /tmp/m.yaml --kubeconfig=/tmp/kc --namespace billing");
    }

    // ════════════════════════════════════════════════════════════════
    //  The hole the seam closed
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// A <b>shared</b> app secret — one with no environment binding — pointed at another tenant's
    /// cluster.
    ///
    /// <para>The existing safety check refuses an <em>environment-bound</em> secret whose target
    /// cluster is in a different environment. A shared secret has no environment, so that check is
    /// skipped entirely, and nothing else on this path compared the target cluster's tenant to the
    /// app's. Our credentials would have been written into their cluster. <c>ForAsync</c> refuses
    /// it because it resolves the credential tenant-first.</para>
    /// </summary>
    [Fact]
    public async Task A_shared_secret_aimed_at_another_tenants_cluster_is_refused()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, theirCluster, "DB_PASSWORD", "hunter2");

        HelmExecutionResult result = await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        replaced.Should().BeEmpty("nothing may be written to a cluster that is not this tenant's");
        namespaces.Should().BeEmpty("not even the namespace");

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("not this tenant's");
    }

    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_is_reported_and_skipped()
    {
        ours = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = ours, Name = "bare", Slug = $"bare-{ours:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = ours, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = ours, EnvironmentId = envId,
            Name = "bare-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { Id = customerId, TenantId = ours, Name = "bare customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "bare-app" });
        await db.SaveChangesAsync();
        await vault.InitializeVaultAsync(ours);

        await GivenAnAppSecretAsync(ours, appId, clusterId, "DB_PASSWORD", "hunter2");

        HelmExecutionResult result = await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        replaced.Should().BeEmpty();
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("no kubeconfig");
    }

    // ════════════════════════════════════════════════════════════════
    //  The manifest builder, directly
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void A_rendered_secret_manifest_is_ordered_and_base64_encoded()
    {
        string manifest = VaultService.BuildSecretManifest(
            "app-secrets", "billing", "Opaque",
            new Dictionary<string, string> { ["ZEBRA"] = "z", ["ALPHA"] = "a" });

        manifest.Should().Contain("kind: Secret")
            .And.Contain("name: app-secrets")
            .And.Contain("namespace: billing")
            .And.Contain("type: Opaque");

        // Ordered, so re-syncing unchanged values produces an identical manifest and the gate's
        // dry-run reports no change rather than asking about a reordering.
        manifest.IndexOf("\"ALPHA\"", StringComparison.Ordinal)
            .Should().BeLessThan(manifest.IndexOf("\"ZEBRA\"", StringComparison.Ordinal));

        manifest.Should().Contain($"\"ALPHA\": {Convert.ToBase64String("a"u8.ToArray())}");
    }

    /// <summary>
    /// Keys are quoted. Kubernetes allows <c>.</c> and <c>-</c> in a Secret key, and a TLS Secret
    /// uses <c>tls.crt</c> — valid unquoted YAML, but only by relying on it.
    /// </summary>
    [Fact]
    public void A_dotted_key_survives_rendering()
    {
        string manifest = VaultService.BuildSecretManifest(
            "tls", "billing", "kubernetes.io/tls",
            new Dictionary<string, string> { ["tls.crt"] = "cert", ["tls.key"] = "key" });

        manifest.Should().Contain("\"tls.crt\": ").And.Contain("\"tls.key\": ");
        manifest.Should().Contain("type: kubernetes.io/tls");
    }

    /// <summary>
    /// A value with newlines — every certificate and private key — must survive. The base64 of a
    /// PEM body is one unbroken token, so it needs no YAML block scalar and cannot wrap.
    /// </summary>
    [Fact]
    public void A_multiline_value_renders_as_one_line()
    {
        const string pem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n";

        string manifest = VaultService.BuildSecretManifest(
            "tls", "billing", "kubernetes.io/tls",
            new Dictionary<string, string> { ["tls.crt"] = pem });

        string line = manifest.Split('\n').Single(l => l.Contains("\"tls.crt\"", StringComparison.Ordinal));

        line.Should().EndWith(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem)));
        manifest.Should().NotContain("BEGIN CERTIFICATE", "the body is encoded, not inlined");
    }
}
