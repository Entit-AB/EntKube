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

    /// <summary>
    /// Every key-scoped write asked of a cluster. App secrets go through this now, because the
    /// Secret they land in can have other writers — see <see cref="SharedSecretKeysTests"/>.
    /// </summary>
    private readonly List<(string Name, string Namespace,
        IReadOnlyDictionary<string, string?> Keys,
        IReadOnlyDictionary<string, string>? Annotations)> merged = [];

    /// <summary>What the live Secret reports as the vault's keys, for the removal case.</summary>
    private string? ownedKeysAnnotation;

    /// <summary>Every namespace the sync ensured exists.</summary>
    private readonly List<string> namespaces = [];

    private Guid ours, theirs, ourCluster, theirCluster, appId, prodEnv, testEnv;

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

        k8s.Setup(x => x.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string name, string ns, IReadOnlyDictionary<string, string?> keys,
                       IReadOnlyDictionary<string, string>? annotations, string _, string? _,
                       CancellationToken _) => merged.Add((name, ns, keys, annotations)))
            .ReturnsAsync("secret/patched");

        k8s.Setup(x => x.GetResourceJsonAsync("secret", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HelmExecutionResult
            {
                Success = true,
                Output = ownedKeysAnnotation is null
                    ? """{"metadata":{"name":"app-secrets"}}"""
                    : "{\"metadata\":{\"annotations\":{\"entkube.io/keys.vault\":\""
                      + ownedKeysAnnotation + "\"}}}",
            });

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

        (ourCluster, appId, prodEnv) = await SeedTenantAsync(ours, "ours");
        (theirCluster, _, _) = await SeedTenantAsync(theirs, "theirs");
    }

    /// <summary>
    /// A second environment for our tenant, with no cluster of its own — which is the shape
    /// that matters here. A sync scoped to it still reaches the production cluster, because a
    /// shared secret may target any of the app's clusters.
    /// </summary>
    private async Task GivenASecondEnvironmentAsync()
    {
        testEnv = Guid.NewGuid();

        await using ApplicationDbContext write = testDb.CreateContext();
        write.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = testEnv, TenantId = ours, Name = "test",
        });
        await write.SaveChangesAsync();
    }

    private async Task<(Guid ClusterId, Guid AppId, Guid EnvironmentId)> SeedTenantAsync(
        Guid tenantId, string label)
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

        return (clusterId, app, envId);
    }

    /// <summary>
    /// Stores an app secret and marks it for sync to <paramref name="clusterId"/>. A null
    /// environment is a <em>shared</em> secret, which is the case the cross-environment safety
    /// check does not cover.
    /// </summary>
    private async Task GivenAnAppSecretAsync(
        Guid tenantId, Guid app, Guid clusterId, string name, string value,
        string k8sName = "app-secrets", string ns = "billing", Guid? environmentId = null)
    {
        VaultSecret stored = await vault.SetAppSecretAsync(
            tenantId, app, name, value, environmentId: environmentId);

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
    /// The write names exactly the keys the vault holds — and <b>only</b> those, because the
    /// Secret it lands in may also hold the keys of every service the app is bound to. Pointing
    /// a Redis or RabbitMQ binding at the app's own Secret is intended.
    ///
    /// <para>⚠️ This used to be a <em>replace</em>, deliberately, so a key removed from the vault
    /// could not survive in the cluster. That is still the property — see
    /// <c>A_key_the_vault_no_longer_holds_is_deleted_by_name</c> below — but delete-and-recreate
    /// is the wrong mechanism for a shared Secret: it dropped the other writers' keys, and so
    /// did switching to a plain apply, because apply deletes whatever the object's last-applied
    /// annotation named and that annotation belongs to the object.</para>
    /// </summary>
    [Fact]
    public async Task The_write_holds_exactly_the_keys_the_vault_holds()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");

        HelmExecutionResult result = await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        result.Success.Should().BeTrue(result.Output);

        (string name, string ns, IReadOnlyDictionary<string, string?> keys, _) =
            merged.Should().ContainSingle().Subject;

        name.Should().Be("app-secrets");
        ns.Should().Be("billing");
        namespaces.Should().Contain("billing", "the namespace is ensured before the secret lands");

        keys.Keys.Should().BeEquivalentTo(["DB_PASSWORD", "API_KEY"]);
        keys["DB_PASSWORD"].Should().Be("hunter2");
        keys["API_KEY"].Should().Be("sk-live-1234");
    }

    /// <summary>
    /// The property delete-and-recreate existed for, kept without it: a credential that has been
    /// revoked must not survive in the cluster. The keys the vault owned last time are recorded
    /// on the Secret, so the ones it no longer holds are deleted <em>by name</em>.
    /// </summary>
    [Fact]
    public async Task A_key_the_vault_no_longer_holds_is_deleted_by_name()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2");

        // The last sync also wrote API_KEY, which has since been removed from the vault.
        ownedKeysAnnotation = "API_KEY,DB_PASSWORD";

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        IReadOnlyDictionary<string, string?> keys = merged.Should().ContainSingle().Subject.Keys;

        keys["DB_PASSWORD"].Should().Be("hunter2");
        keys["API_KEY"].Should().BeNull("a revoked credential must not survive in the cluster");
    }

    /// <summary>
    /// And the keys it owns are recorded, so the next sync can do the above.
    /// </summary>
    [Fact]
    public async Task The_vault_records_which_keys_are_its_own()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        merged.Should().ContainSingle().Subject
            .Annotations!["entkube.io/keys.vault"].Should().Be("API_KEY,DB_PASSWORD");
    }

    /// <summary>
    /// The managed-by labels are in the manifest rather than stamped by a follow-up
    /// <c>kubectl label</c>. That call was best-effort, so a Secret could be written and left
    /// unlabelled — and the deployment importer uses exactly that label to avoid re-adopting
    /// EntKube's own Secrets back into the vault.
    /// </summary>
    [Fact]
    public async Task The_managed_by_labels_are_part_of_every_write()
    {
        await SeedAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2");

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        // Through the real patch builder, from what the sync actually asked for: the labels are
        // added there, so asserting them anywhere else would prove only that the test knows
        // the constants.
        (_, _, IReadOnlyDictionary<string, string?> keys,
         IReadOnlyDictionary<string, string>? annotations) = merged.Should().ContainSingle().Subject;

        string patch = KubernetesClientFactory.BuildSecretKeyPatch(keys, annotations);

        patch.Should().Contain($"\"{VaultService.ManagedByLabelKey}\":\"{VaultService.ManagedByLabelValue}\"");
        patch.Should().Contain("\"entkube.io/managed\":\"true\"");
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
    /// a mocked factory: a mutant that drops the delete turns the replace into a plain apply and
    /// lets a removed key survive. <c>--ignore-not-found</c> is what makes the first sync work,
    /// when there is nothing to remove yet.</para>
    ///
    /// <para><b>Still the right verb for the Secrets that keep it:</b> a certificate and an OAuth
    /// client each get a Secret of their own, with one writer and one logical value, so there is
    /// nothing in the object for a replace to destroy. App secrets moved off it because their
    /// Secret is shared.</para>
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

        // Both write paths: `replaced` alone would pass whatever this method did, now that app
        // secrets go through the key-scoped one.
        replaced.Should().BeEmpty("nothing may be written to a cluster that is not this tenant's");
        merged.Should().BeEmpty("nor through the key-scoped path");
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
    //  A sync that can only see one environment's keys
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The defect. An operator saves a secret on the <b>test</b> environment page; the sync that
    /// fires is scoped to test, and a production key disappears from the production cluster.
    ///
    /// <para><b>How a test-scoped sync reaches production at all.</b> A shared secret — one with
    /// no environment binding — is visible from every environment and may target any of the app's
    /// clusters, so it is written from the test page to whichever cluster its sync config names.
    /// The cross-environment guard does not cover it, because it has no environment to compare.
    /// </para>
    ///
    /// <para><b>Why the key was deleted.</b> This writer deletes the keys it owned last time and
    /// no longer holds, which is how a revoked credential stops being readable in the cluster.
    /// The environment filter was applied in the query, so the scoped sync held a partial picture
    /// of its own keys — and production's key, which it is not allowed to read, was
    /// indistinguishable from one that had been revoked.</para>
    /// </summary>
    [Fact]
    public async Task A_sync_scoped_to_one_environment_does_not_delete_another_environments_keys()
    {
        await SeedAsync();
        await GivenASecondEnvironmentAsync();

        // Both feed the same Secret on the production cluster.
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2",
            environmentId: prodEnv);

        // Both are in the cluster already, recorded as the vault's.
        ownedKeysAnnotation = "API_KEY,DB_PASSWORD";

        HelmExecutionResult result =
            await vault.SyncAppSecretsToKubernetesAsync(ours, appId, environmentId: testEnv);

        result.Success.Should().BeTrue(result.Output);

        IReadOnlyDictionary<string, string?> keys = merged.Should().ContainSingle().Subject.Keys;

        keys["API_KEY"].Should().Be("sk-live-1234");
        keys.Should().NotContainKey("DB_PASSWORD",
            "a sync that may not read production's key must not mistake it for a revoked one");
    }

    /// <summary>
    /// And it is retained by <em>name</em>. The whole point of the environment scope is that the
    /// test view never handles production's values, so the fix may not be "load them too".
    /// </summary>
    [Fact]
    public async Task A_key_left_to_another_environment_is_never_decrypted()
    {
        await SeedAsync();
        await GivenASecondEnvironmentAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2",
            environmentId: prodEnv);
        ownedKeysAnnotation = "API_KEY,DB_PASSWORD";

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId, environmentId: testEnv);

        // Through the real patch builder, because that is what reaches the cluster: the value
        // is base64 there, so asserting on the dictionary alone would miss an encoded leak.
        (_, _, IReadOnlyDictionary<string, string?> keys,
         IReadOnlyDictionary<string, string>? annotations) = merged.Should().ContainSingle().Subject;

        string patch = KubernetesClientFactory.BuildSecretKeyPatch(keys, annotations);

        patch.Should().NotContain("hunter2")
            .And.NotContain(Convert.ToBase64String("hunter2"u8.ToArray()));
    }

    /// <summary>
    /// The retained key stays recorded as the vault's. Dropping it from the annotation deletes
    /// nothing today, which is why it would pass the test above — and orphans it: the vault would
    /// no longer own it, so no later sync would ever remove it, and a revoked credential would
    /// outlive its revocation.
    /// </summary>
    [Fact]
    public async Task A_key_left_to_another_environment_is_still_claimed_by_the_vault()
    {
        await SeedAsync();
        await GivenASecondEnvironmentAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2",
            environmentId: prodEnv);
        ownedKeysAnnotation = "API_KEY,DB_PASSWORD";

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId, environmentId: testEnv);

        merged.Should().ContainSingle().Subject
            .Annotations!["entkube.io/keys.vault"].Should().Be("API_KEY,DB_PASSWORD");
    }

    /// <summary>
    /// The same defect wearing different clothes, and the reason the fix is not simply "do not
    /// delete". An environment-bound secret overrides a shared one of the same name — that is the
    /// documented precedence. Decide it over only the keys this call can see and the shared value
    /// wins by default, so a test-scoped sync overwrites production's override with it.
    ///
    /// <para>Here every key in the target belongs to another environment, so the right answer is
    /// to leave the Secret alone entirely — and not to ensure its namespace either, which would
    /// be a cluster change for a sync that writes nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_shared_value_does_not_overwrite_another_environments_override()
    {
        await SeedAsync();
        await GivenASecondEnvironmentAsync();

        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "shared-value");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "production-value",
            environmentId: prodEnv);
        ownedKeysAnnotation = "DB_PASSWORD";

        HelmExecutionResult result =
            await vault.SyncAppSecretsToKubernetesAsync(ours, appId, environmentId: testEnv);

        result.Success.Should().BeTrue(result.Output);
        merged.Should().BeEmpty("production holds the override for the only key in this Secret");
        replaced.Should().BeEmpty();
        namespaces.Should().BeEmpty("a sync that writes nothing has no namespace to ensure");
        result.Output.Should().Contain("another environment");
    }

    /// <summary>
    /// The guard against an over-broad fix. "A scoped sync deletes nothing" would pass every test
    /// above and silently retire the revocation property for every environment-scoped save — which
    /// is the path the UI actually uses. A key that no environment holds any more is still deleted.
    /// </summary>
    [Fact]
    public async Task A_scoped_sync_still_deletes_a_key_no_environment_holds()
    {
        await SeedAsync();
        await GivenASecondEnvironmentAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2",
            environmentId: prodEnv);

        // OLD_TOKEN was the vault's and is now nobody's.
        ownedKeysAnnotation = "API_KEY,DB_PASSWORD,OLD_TOKEN";

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId, environmentId: testEnv);

        IReadOnlyDictionary<string, string?> keys = merged.Should().ContainSingle().Subject.Keys;

        keys["OLD_TOKEN"].Should().BeNull("a revoked credential must not survive in the cluster");
        keys.Should().NotContainKey("DB_PASSWORD", "but production's key is not revoked");
    }

    /// <summary>
    /// Retaining is matched to a <b>target</b>, not to an environment. A production key that
    /// lands somewhere else — another Secret, another namespace, another cluster — has no
    /// standing in this one, and the vault owning it there is no reason to stop revoking it here.
    ///
    /// <para>Three keys, one per clause of that match, because a match that drops any one of them
    /// reads as "production owns this name somewhere, so leave it" and quietly retires the
    /// revocation for it.</para>
    /// </summary>
    [Fact]
    public async Task A_key_aimed_at_a_different_target_is_not_retained_in_this_one()
    {
        await SeedAsync();
        await GivenASecondEnvironmentAsync();

        Guid otherCluster = Guid.NewGuid();
        await using (ApplicationDbContext write = testDb.CreateContext())
        {
            write.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = otherCluster, TenantId = ours, EnvironmentId = prodEnv,
                Name = "ours-second-cluster", ApiServerUrl = "https://k8s2.example.com",
            });
            await write.SaveChangesAsync();
        }

        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");

        await GivenAnAppSecretAsync(ours, appId, ourCluster, "OTHER_SECRET", "a",
            k8sName: "other-secrets", environmentId: prodEnv);
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "OTHER_NAMESPACE", "b",
            ns: "other-ns", environmentId: prodEnv);
        await GivenAnAppSecretAsync(ours, appId, otherCluster, "OTHER_CLUSTER", "c",
            environmentId: prodEnv);

        // All three were the vault's in THIS Secret and are not any more.
        ownedKeysAnnotation = "API_KEY,OTHER_SECRET,OTHER_NAMESPACE,OTHER_CLUSTER";

        await vault.SyncAppSecretsToKubernetesAsync(ours, appId, environmentId: testEnv);

        IReadOnlyDictionary<string, string?> keys = merged.Should()
            .ContainSingle("only the in-scope secret's target is written to").Subject.Keys;

        keys["API_KEY"].Should().Be("sk-live-1234");
        keys["OTHER_SECRET"].Should().BeNull("it lives in another Secret now");
        keys["OTHER_NAMESPACE"].Should().BeNull("it lives in another namespace now");
        keys["OTHER_CLUSTER"].Should().BeNull("it lives on another cluster now");
    }

    /// <summary>
    /// An unscoped sync sees every environment's keys, so it retains nothing and behaves exactly
    /// as it did: it writes both values and deletes what the vault has dropped.
    /// </summary>
    [Fact]
    public async Task A_sync_with_no_environment_scope_writes_every_environments_keys()
    {
        await SeedAsync();
        await GivenASecondEnvironmentAsync();
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "API_KEY", "sk-live-1234");
        await GivenAnAppSecretAsync(ours, appId, ourCluster, "DB_PASSWORD", "hunter2",
            environmentId: prodEnv);
        ownedKeysAnnotation = "API_KEY,DB_PASSWORD,OLD_TOKEN";

        HelmExecutionResult result = await vault.SyncAppSecretsToKubernetesAsync(ours, appId);

        result.Success.Should().BeTrue(result.Output);

        (_, _, IReadOnlyDictionary<string, string?> keys,
         IReadOnlyDictionary<string, string>? annotations) = merged.Should().ContainSingle().Subject;

        keys["API_KEY"].Should().Be("sk-live-1234");
        keys["DB_PASSWORD"].Should().Be("hunter2");
        keys["OLD_TOKEN"].Should().BeNull();
        annotations!["entkube.io/keys.vault"].Should().Be("API_KEY,DB_PASSWORD");
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
