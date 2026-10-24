using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// One observed change is recorded once, however many instances observe it.
///
/// <para><b>The defect.</b> <c>VaultService.RefreshAppSecretFromClusterAsync</c> is the k8s→vault
/// reconcile that <see cref="ObservedSecretRefreshService"/> runs every five minutes. When the
/// live Secret differs from the vault copy it archived the old value as a version row and wrote
/// the new one, unconditionally. Two instances both read the same vault copy, both see the same
/// live change, and both append a version row: <b>one change recorded as two</b>. The secret
/// value converges — the two ciphertexts decrypt to the same plaintext and differ only by
/// nonce — so nothing is lost or corrupted. What is wrong is the record of what changed and
/// when, which for a vault is not cosmetic. See docs/decomposition.md §9.6.</para>
///
/// <para><b>How a race is made deterministic here.</b> Running the refresh twice in sequence
/// would prove nothing: the second pass reads the already-updated vault copy, finds it equal to
/// the live value and skips. The duplicate only exists when both instances read <em>before</em>
/// either writes. So the competing instance is run from inside the mocked cluster read — the
/// exact window between "this call loaded the vault row" and "this call writes" — and it is the
/// real production method, not a stand-in. ⚠️ The hook fires once, or the two instances would
/// recurse into each other forever.</para>
///
/// <para><b>Why the absence assertions are not vacuous.</b> Every "no second version row" case is
/// paired with a case that <em>must</em> produce one:
/// <see cref="One_observed_change_appends_exactly_one_version"/> proves the uncontested path
/// still archives, and <see cref="A_genuinely_second_change_is_still_recorded"/> proves the claim
/// is keyed narrowly enough that a real later change is not swallowed — the failure mode of a
/// claim keyed on the secret id alone.</para>
/// </summary>
public class ObservedSecretRefreshClaimTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private const string SecretName = "DB_PASSWORD";
    private const string K8sSecretName = "app-secrets";
    private const string Namespace = "billing";

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    /// <summary>What the live cluster Secret currently holds under <see cref="SecretName"/>.</summary>
    private string liveValue = "original";

    /// <summary>
    /// Runs once, inside the live-Secret read, to stand in for a second instance that got there
    /// first. Null for the single-instance tests.
    /// </summary>
    private Func<Task>? competingInstance;

    private Guid tenantId, clusterId, appId, secretId;

    public ObservedSecretRefreshClaimTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();

        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (competingInstance is not null)
                {
                    Func<Task> other = competingInstance;
                    competingInstance = null;
                    await other();
                }

                return LiveSecretJson(liveValue);
            });

        vault = testDb.CreateVaultService(new ClusterClientFactory(testDb.Factory, k8s.Object));
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string LiveSecretJson(string value)
        => "{\"data\":{\"" + SecretName + "\":\""
           + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "\"}}";

    /// <summary>
    /// An "observed" app secret: sync DISABLED (EntKube does not own the live Secret) but a
    /// cluster and Secret name recorded, which is exactly what the refresher sweeps.
    /// </summary>
    private async Task SeedObservedSecretAsync(string initialValue)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "acme", Slug = $"acme-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "acme-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        await db.SaveChangesAsync();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "acme customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "acme-app" });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        VaultSecret stored = await vault.SetAppSecretAsync(tenantId, appId, SecretName, initialValue);
        secretId = stored.Id;

        await using ApplicationDbContext write = testDb.CreateContext();
        VaultSecret row = write.Set<VaultSecret>().First(s => s.Id == secretId);
        row.SyncToKubernetes = false;
        row.KubernetesSecretName = K8sSecretName;
        row.KubernetesNamespace = Namespace;
        row.KubernetesClusterId = clusterId;
        await write.SaveChangesAsync();

        // A freshly created secret has no history — every version row counted below was appended
        // by a refresh, not by the seeding.
        VersionCount().Should().Be(0);
    }

    private int VersionCount()
    {
        using ApplicationDbContext read = testDb.CreateContext();
        return read.Set<VaultSecretVersion>().Count(v => v.SecretId == secretId);
    }

    private List<string> ArchivedValues()
    {
        using ApplicationDbContext read = testDb.CreateContext();
        SecretVault v = read.Set<SecretVault>().First(x => x.TenantId == tenantId);
        byte[] dek = testDb.Encryption.UnsealDataKey(v.EncryptedDataKey, v.Nonce);

        return [.. read.Set<VaultSecretVersion>()
            .Where(x => x.SecretId == secretId)
            .OrderBy(x => x.VersionNumber)
            .AsEnumerable()
            .Select(x => testDb.Encryption.Decrypt(dek, x.EncryptedValue, x.Nonce))];
    }

    // ════════════════════════════════════════════════════════════════
    //  The positive half: one instance, one change, one version row
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Uncontested, the reconcile still archives. Without this, "no second version row" would
    /// pass just as happily against a refresh that had stopped archiving at all.
    /// </summary>
    [Fact]
    public async Task One_observed_change_appends_exactly_one_version()
    {
        await SeedObservedSecretAsync("original");
        liveValue = "rotated-by-argocd";

        VaultService.ClusterRefreshResult result =
            await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);

        result.Should().Be(VaultService.ClusterRefreshResult.Updated);
        VersionCount().Should().Be(1);
        // A version row holds the value that was REPLACED, not the one that replaced it.
        ArchivedValues().Should().Equal("original");
        (await vault.GetAppSecretValueAsync(tenantId, appId, SecretName))
            .Should().Be("rotated-by-argocd");
    }

    /// <summary>
    /// A live value equal to the vault copy writes nothing at all — the no-change skip the
    /// method's own comment describes, kept intact by the claim.
    /// </summary>
    [Fact]
    public async Task An_unchanged_live_value_appends_nothing()
    {
        await SeedObservedSecretAsync("original");
        liveValue = "original";

        VaultService.ClusterRefreshResult result =
            await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);

        result.Should().Be(VaultService.ClusterRefreshResult.Unchanged);
        VersionCount().Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════════
    //  The defect: two instances, one change
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⭐ The property this change exists for. Two instances observe the same change against the
    /// same vault copy; exactly one records it.
    /// </summary>
    [Fact]
    public async Task A_second_instance_observing_the_same_change_appends_no_second_version()
    {
        await SeedObservedSecretAsync("original");
        liveValue = "rotated-by-argocd";

        // The other instance completes its whole refresh inside our window between loading the
        // vault row and writing — so we compare against, and try to claim, a copy that is already
        // superseded by the time we get to the write.
        VaultService.ClusterRefreshResult? otherResult = null;
        competingInstance = async () =>
            otherResult = await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);

        VaultService.ClusterRefreshResult result =
            await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);

        otherResult.Should().Be(VaultService.ClusterRefreshResult.Updated,
            "the instance that got there first is the one that recorded the change");
        result.Should().Be(VaultService.ClusterRefreshResult.Unchanged,
            "losing the claim is not an error — the vault copy already holds the live value");

        VersionCount().Should().Be(1, "one observed change is one version row");
        ArchivedValues().Should().Equal("original");

        // ⚠️ The point is the audit trail, not the data: the value was never at risk.
        (await vault.GetAppSecretValueAsync(tenantId, appId, SecretName))
            .Should().Be("rotated-by-argocd");
    }

    /// <summary>
    /// The loser still records that it looked. Freshness is per-instance observation and must not
    /// be suppressed by the claim, or a secret the losing instance checks every pass would read
    /// as never checked.
    /// </summary>
    [Fact]
    public async Task The_instance_that_loses_the_claim_still_records_that_it_checked()
    {
        await SeedObservedSecretAsync("original");
        liveValue = "rotated-by-argocd";

        competingInstance = async () =>
            await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);

        DateTime before = DateTime.UtcNow;
        await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);

        using ApplicationDbContext read = testDb.CreateContext();
        VaultSecret row = read.Set<VaultSecret>().First(s => s.Id == secretId);
        row.LastRefreshedFromClusterAt.Should().NotBeNull()
            .And.Subject.As<DateTime>().Should().BeOnOrAfter(before.AddSeconds(-1));
    }

    // ════════════════════════════════════════════════════════════════
    //  The other way to get it wrong: a claim that is too broad
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚠️ A claim keyed on the secret id alone — or on "refreshed within the last N minutes" —
    /// would also make the duplicate disappear, and would silently swallow the next real change.
    /// Keying on the nonce of the vault copy that was diffed means the second change is compared
    /// against, and claims, the WINNER's copy, so it is recorded on its own merits.
    /// </summary>
    [Fact]
    public async Task A_genuinely_second_change_is_still_recorded()
    {
        await SeedObservedSecretAsync("original");

        liveValue = "rotation-one";
        (await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId))
            .Should().Be(VaultService.ClusterRefreshResult.Updated);

        liveValue = "rotation-two";
        (await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId))
            .Should().Be(VaultService.ClusterRefreshResult.Updated);

        VersionCount().Should().Be(2, "two changes are two version rows");
        ArchivedValues().Should().Equal("original", "rotation-one");
        (await vault.GetAppSecretValueAsync(tenantId, appId, SecretName))
            .Should().Be("rotation-two");
    }

    /// <summary>
    /// And the two shapes composed: a contested change followed by a real second change. The
    /// claim must absorb the duplicate and then still let the next change through.
    /// </summary>
    [Fact]
    public async Task A_duplicate_then_a_real_change_is_two_rows_not_three()
    {
        await SeedObservedSecretAsync("original");

        liveValue = "rotation-one";
        competingInstance = async () =>
            await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);
        await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId);

        VersionCount().Should().Be(1);

        liveValue = "rotation-two";
        (await vault.RefreshAppSecretFromClusterAsync(tenantId, secretId))
            .Should().Be(VaultService.ClusterRefreshResult.Updated);

        VersionCount().Should().Be(2);
        ArchivedValues().Should().Equal("original", "rotation-one");
    }
}
