using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Per-app Redis users through <see cref="RedisService"/>, against a mocked cluster: what a binding
/// may be created against, what syncing hands the app, what deleting takes back, and what the
/// background reconcile revokes.
///
/// <para>The mocked cluster has one running leader and one running follower and remembers which
/// users each node holds, so a test can ask "does this node still know that app" rather than only
/// "was some command sent".</para>
/// </summary>
public class RedisPerAppUsersTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly RedisService sut;

    private readonly List<string> applied = [];
    private readonly List<(string Secret, IReadOnlyDictionary<string, string?> Keys)> secretWrites = [];
    private readonly List<string> stdinSent = [];
    private readonly List<IReadOnlyList<string>> commandsSent = [];

    /// <summary>
    /// Per (pod, is-the-listing) a reply that refuses the command the way redis-cli does: an error
    /// line on stdout and exit code 0. Returned before the fake changes any state, because a refused
    /// SETUSER or DELUSER does nothing.
    /// </summary>
    private readonly Dictionary<(string Pod, bool IsList), string> refusals = [];

    /// <summary>Runs once, inside the first command that changes users — "meanwhile, elsewhere".</summary>
    private Func<Task>? duringApply;

    /// <summary>The users each node holds, as the fake redis-cli sees them.</summary>
    private readonly Dictionary<string, HashSet<string>> nodeUsers = new()
    {
        ["cache-leader-0"] = ["default"],
        ["cache-follower-0"] = ["default"],
    };

    /// <summary>When set, every exec on this pod fails, as a node that refuses the change would.</summary>
    private string? failingPod;

    public RedisPerAppUsersTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        sut = new RedisService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            vault,
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory));

        FakeCluster();
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private void FakeCluster()
    {
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string _, CancellationToken _) => applied.Add(m))
            .ReturnsAsync("");

        k8s.Setup(x => x.EnsureNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        k8s.Setup(x => x.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string name, string _, IReadOnlyDictionary<string, string?> keys,
                       IReadOnlyDictionary<string, string>? _, string _, string? _, CancellationToken _) =>
                secretWrites.Add((name, keys)))
            .ReturnsAsync("");

        // Keyed on the exact namespace and selector: a lookup in the wrong namespace, or for the
        // wrong cluster's pods, finds nothing — as it would on a real cluster.
        k8s.Setup(x => x.GetJsonAsync("pods", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string ns, string _, string selector, CancellationToken _) =>
            {
                string? pod = (ns, selector) switch
                {
                    ("caching", "app=cache-leader") => "cache-leader-0",
                    ("caching", "app=cache-follower") => "cache-follower-0",
                    ("caching", "app=other-leader") => "other-leader-0",
                    ("caching", "app=other-follower") => "other-follower-0",
                    _ => null,
                };
                string container = selector["app=".Length..];
                return pod is null
                    ? """{"items":[]}"""
                    : $$$$"""{"items":[{"metadata":{"name":"{{{{pod}}}}"},"status":{"phase":"Running","containerStatuses":[{"name":"{{{{container}}}}","state":{"running":{}}},{"name":"redis-exporter","state":{"running":{}}}]}}]}""";
            });

        k8s.Setup(x => x.RunCommandOnPodWithStdinAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<EntKube.Web.Services.ClusterChanges.ChangeIntent>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Returns(async (string pod, string ns, IReadOnlyList<string> command, string stdin, string _,
                           EntKube.Web.Services.ClusterChanges.ChangeIntent _, CancellationToken _, string? container) =>
            {
                commandsSent.Add(command);
                stdinSent.Add(stdin);
                if (pod == failingPod) throw new InvalidOperationException("kubectl exec failed (exit 1): container not found");
                ns.Should().Be("caching");
                container.Should().Be(pod[..pod.LastIndexOf('-')], "the Redis container is named after its StatefulSet");

                bool isList = stdin == "ACL USERS\n";
                if (refusals.TryGetValue((pod, isList), out string? refusal)) return refusal;

                HashSet<string> users = nodeUsers[pod];
                if (isList) return string.Join('\n', users) + "\n";

                if (duringApply is { } meanwhile)
                {
                    duringApply = null;
                    await meanwhile();
                }

                List<string> replies = [];
                foreach (string line in stdin.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split(' ');
                    if (parts[1] == "SETUSER") users.Add(parts[2]);
                    if (parts[1] == "DELUSER") users.Remove(parts[2]);
                    replies.Add(parts[1] == "DELUSER" ? "1" : "OK");
                }
                return string.Join('\n', replies) + "\n";
            });
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, Guid K8sId, Guid EnvId)> SeedTenantAsync(string label)
    {
        Guid tenantId = Guid.NewGuid(), envId = Guid.NewGuid(), clusterId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = label });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-k8s", ApiServerUrl = $"https://{label}.example",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        return (tenantId, clusterId, envId);
    }

    private async Task<(Guid DeploymentId, Guid AppId)> SeedDeploymentAsync(Guid tenantId, Guid k8sId, Guid envId, string label)
    {
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), deploymentId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label} customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = $"{label}-app" });
        await db.SaveChangesAsync();
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = $"{label}-deploy",
            Type = DeploymentType.Manual, ClusterId = k8sId, EnvironmentId = envId, Namespace = $"{label}-ns",
        });
        await db.SaveChangesAsync();
        return (deploymentId, appId);
    }

    private Task<RedisCluster> CreateRedisAsync(Guid tenantId, Guid k8sId, string version = "v7.0.15") =>
        sut.CreateClusterAsync(tenantId, k8sId, "cache", "caching", 3, version, "1Gi", null, true);

    private async Task<string> AdminPasswordAsync(Guid tenantId, Guid redisId) =>
        (await sut.GetCredentialsAsync(tenantId, redisId)).password!;

    // ── New clusters ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_redis_7_cluster_starts_with_per_app_users_and_the_users_file_before_its_pods()
    {
        (Guid tenantId, Guid k8sId, _) = await SeedTenantAsync("ours");

        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);

        cluster.PerAppUsers.Should().BeTrue();
        int usersFile = applied.FindIndex(m => m.Contains("kind: ConfigMap") && m.Contains("cache-entkube-users"));
        int redisCluster = applied.FindIndex(m => m.Contains("kind: RedisCluster"));
        usersFile.Should().BeGreaterThanOrEqualTo(0);
        usersFile.Should().BeLessThan(redisCluster, "a pod whose included ConfigMap is missing never starts");
        YamlDotNet.RepresentationModel.YamlStream yaml = [];
        yaml.Load(new StringReader(applied[redisCluster]));
        var spec = (YamlDotNet.RepresentationModel.YamlMappingNode)
            ((YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode)["spec"];
        foreach (string role in new[] { "redisLeader", "redisFollower" })
        {
            var config = (YamlDotNet.RepresentationModel.YamlMappingNode)
                ((YamlDotNet.RepresentationModel.YamlMappingNode)spec[role])["redisConfig"];
            ((YamlDotNet.RepresentationModel.YamlScalarNode)config["additionalRedisConfig"]).Value
                .Should().Be("cache-entkube-users", "{0} needs the users too: a follower becomes a leader on failover", role);
        }
    }

    [Fact]
    public async Task A_redis_6_cluster_keeps_the_shared_password()
    {
        (Guid tenantId, Guid k8sId, _) = await SeedTenantAsync("ours");

        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId, "v6.2.14");

        cluster.PerAppUsers.Should().BeFalse();
        applied.Should().NotContain(m => m.Contains("additionalRedisConfig"));
    }

    // ── Creating a binding ───────────────────────────────────────────────────

    [Fact]
    public async Task A_binding_gets_its_own_user_and_never_the_admin_password()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis", "shop");

        string admin = await AdminPasswordAsync(tenantId, cluster.Id);
        IReadOnlyDictionary<string, string?> keys = secretWrites.Single().Keys;
        string username = RedisAcl.UsernameFor(binding.Id);

        keys["REDIS_USERNAME"].Should().Be(username);
        keys["REDIS_PASSWORD"].Should().NotBe(admin).And.NotBeNullOrEmpty();
        keys["REDIS_URL"].Should().Be($"redis://{username}:{keys["REDIS_PASSWORD"]}@cache-leader.caching.svc.cluster.local:6379");
        keys["REDIS_KEY_PREFIX"].Should().Be("shop:");
        keys.Values.Should().NotContain(admin);

        nodeUsers.Values.Should().AllSatisfy(u => u.Should().Contain(username),
            "every running node must accept the password before the app is handed it");
        applied.Last(m => m.Contains("cache-entkube-users")).Should().Contain($"user {username} on #");
    }

    [Fact]
    public async Task No_password_reaches_the_cluster_in_clear()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis", "shop");

        string appPassword = secretWrites.Single().Keys["REDIS_PASSWORD"]!;
        string admin = await AdminPasswordAsync(tenantId, cluster.Id);

        stdinSent.Should().NotBeEmpty().And.AllSatisfy(s => s.Should().NotContain(appPassword).And.NotContain(admin));
        applied.Where(m => m.Contains("cache-entkube-users"))
            .Should().AllSatisfy(m => m.Should().NotContain(appPassword));
        commandsSent.Should().NotBeEmpty();
        commandsSent.SelectMany(c => c).Should().AllSatisfy(a => a.Should().NotContain(admin).And.NotContain(appPassword),
            "the admin password comes from the container's own environment, inside the pod");
    }

    [Fact]
    public async Task A_binding_to_another_tenants_redis_is_refused()
    {
        (Guid ours, Guid ourK8s, Guid ourEnv) = await SeedTenantAsync("ours");
        (Guid theirs, Guid theirK8s, _) = await SeedTenantAsync("theirs");
        RedisCluster foreign = await CreateRedisAsync(theirs, theirK8s);
        (Guid deploymentId, _) = await SeedDeploymentAsync(ours, ourK8s, ourEnv, "ours");

        Func<Task> act = () => sut.CreateCacheBindingAsync(ours, foreign.Id, deploymentId, "redis");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Redis cluster not found.");
        (await db.Set<CacheBinding>().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_binding_for_another_tenants_deployment_is_refused()
    {
        (Guid ours, Guid ourK8s, _) = await SeedTenantAsync("ours");
        (Guid theirs, Guid theirK8s, Guid theirEnv) = await SeedTenantAsync("theirs");
        RedisCluster cluster = await CreateRedisAsync(ours, ourK8s);
        (Guid foreignDeployment, _) = await SeedDeploymentAsync(theirs, theirK8s, theirEnv, "theirs");

        Func<Task> act = () => sut.CreateCacheBindingAsync(ours, cluster.Id, foreignDeployment, "redis");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Deployment not found.");
        (await db.Set<CacheBinding>().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task The_environment_allow_list_decides_which_redis_an_app_may_use()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster allowedRedis = await CreateRedisAsync(tenantId, k8sId);
        RedisCluster otherRedis = await sut.CreateClusterAsync(tenantId, k8sId, "other", "caching", 3, "v7", "1Gi", null, true);
        nodeUsers["other-leader-0"] = ["default"];
        nodeUsers["other-follower-0"] = ["default"];
        (Guid deploymentId, Guid appId) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        db.Set<AppAllowedCache>().Add(new AppAllowedCache
        {
            Id = Guid.NewGuid(), AppId = appId, EnvironmentId = envId, RedisClusterId = allowedRedis.Id,
        });
        await db.SaveChangesAsync();

        Func<Task> refused = () => sut.CreateCacheBindingAsync(tenantId, otherRedis.Id, deploymentId, "redis");
        await refused.Should().ThrowAsync<InvalidOperationException>().WithMessage("*allow-list*");

        CacheBinding ok = await sut.CreateCacheBindingAsync(tenantId, allowedRedis.Id, deploymentId, "redis");
        ok.RedisClusterId.Should().Be(allowedRedis.Id);
    }

    [Fact]
    public async Task An_environment_without_an_allow_list_is_not_restricted()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");

        binding.Should().NotBeNull();
    }

    [Fact]
    public async Task Two_bindings_into_one_secret_of_one_deployment_are_refused()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");

        Func<Task> act = () => sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, " redis ");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already writes into the Secret 'redis'*");
        (await db.Set<CacheBinding>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_invalid_prefix_is_refused_before_anything_is_written()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        Func<Task> act = () => sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis", "shop*");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lowercase letters*");
        (await db.Set<CacheBinding>().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_binding_whose_first_sync_fails_is_not_left_behind()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        failingPod = "cache-follower-0";

        Func<Task> act = () => sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cache-follower-0*");
        (await db.Set<CacheBinding>().AnyAsync()).Should().BeFalse("a row that never synced looks bound to everyone");
        (await vault.GetRedisClusterSecretsAsync(tenantId, cluster.Id))
            .Should().NotContain(s => s.Name.StartsWith("REDIS_USER_", StringComparison.Ordinal));
        secretWrites.Should().BeEmpty("the app must never be handed a password a node refused");
    }

    // ── Deleting a binding ───────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_binding_revokes_its_user_on_every_node_and_drops_its_password()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        string username = RedisAcl.UsernameFor(binding.Id);

        string? warning = await sut.DeleteCacheBindingAsync(tenantId, binding.Id);

        warning.Should().BeNull("a binding created on a switched Redis never held the admin password");
        nodeUsers.Values.Should().AllSatisfy(u => u.Should().NotContain(username));
        applied.Last(m => m.Contains("cache-entkube-users")).Should().NotContain(username,
            "a node restarting later would otherwise bring the user back from the file");
        (await vault.GetRedisClusterSecretValueAsync(tenantId, cluster.Id, RedisAcl.PasswordSecretName(binding.Id)))
            .Should().BeNull();
        (await db.Set<CacheBinding>().AnyAsync(b => b.Id == binding.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_binding_whose_user_cannot_be_revoked_is_kept()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        failingPod = "cache-leader-0";
        stdinSent.Clear();

        Func<Task> act = () => sut.DeleteCacheBindingAsync(tenantId, binding.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*could not be applied*cache-leader-0*");
        stdinSent.Should().Contain(s => s.Contains($"ACL DELUSER {RedisAcl.UsernameFor(binding.Id)}"),
            "the healthy follower was still revoked; only the leader refused");
        (await db.Set<CacheBinding>().AnyAsync(b => b.Id == binding.Id)).Should().BeTrue(
            "showing the app as unbound while its credentials still work on a node would be a lie");
        (await vault.GetRedisClusterSecretValueAsync(tenantId, cluster.Id, RedisAcl.PasswordSecretName(binding.Id)))
            .Should().NotBeNull("the retry needs the same password to build the same file");
    }

    [Fact]
    public async Task A_secret_that_cannot_be_cleaned_up_is_a_warning_once_the_user_is_gone()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");

        // The Secret records the cache keys, so removal patches it — and the patch fails.
        k8s.Setup(x => x.GetResourceJsonAsync("secret", "redis", "ours-ns", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HelmExecutionResult
            {
                Success = true,
                Output = """{"metadata":{"annotations":{"entkube.io/keys.cache":"REDIS_HOST,REDIS_PASSWORD"}},"data":{}}""",
            });
        k8s.Setup(x => x.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("apiserver unreachable"));

        string? warning = await sut.DeleteCacheBindingAsync(tenantId, binding.Id);

        warning.Should().Contain("could not be removed").And.Contain("apiserver unreachable");
        (await db.Set<CacheBinding>().AnyAsync(b => b.Id == binding.Id)).Should().BeFalse();
        nodeUsers.Values.Should().AllSatisfy(u => u.Should().NotContain(RedisAcl.UsernameFor(binding.Id)));
    }

    [Fact]
    public async Task Unbinding_from_a_shared_password_redis_says_the_password_still_works()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId, "v6.2.14");
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");

        string? warning = await sut.DeleteCacheBindingAsync(tenantId, binding.Id);

        warning.Should().Contain("admin password, which stays valid");
    }

    // ── Shared-password clusters and the switch ──────────────────────────────

    [Fact]
    public async Task A_shared_password_cluster_still_hands_out_the_admin_password_as_default()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId, "v6.2.14");
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis", "shop");

        IReadOnlyDictionary<string, string?> keys = secretWrites.Single().Keys;
        keys["REDIS_USERNAME"].Should().Be("default");
        keys["REDIS_PASSWORD"].Should().Be(await AdminPasswordAsync(tenantId, cluster.Id));
        keys["REDIS_KEY_PREFIX"].Should().Be("shop:", "the prefix is delivered before it is enforced, so the app can adopt it first");
        stdinSent.Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_moves_every_existing_app_to_its_own_user_with_all_keys()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId, "v7.0.15");
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        // A cluster from before per-app users: the shared password, a binding with no prefix.
        await db.Set<RedisCluster>().Where(c => c.Id == cluster.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.PerAppUsersSince, (DateTime?)null));
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        secretWrites.Single().Keys["REDIS_USERNAME"].Should().Be("default");
        applied.Clear();
        secretWrites.Clear();

        (int moved, IReadOnlyList<string> failures) = await sut.EnablePerAppUsersAsync(tenantId, cluster.Id);

        moved.Should().Be(1);
        failures.Should().BeEmpty();
        int usersFile = applied.FindIndex(m => m.Contains("cache-entkube-users") && m.Contains("kind: ConfigMap"));
        int template = applied.FindIndex(m => m.Contains("kind: RedisCluster"));
        usersFile.Should().BeLessThan(template, "the restarted pods must find the file they include");
        applied[template].Should().Contain("additionalRedisConfig: cache-entkube-users");

        IReadOnlyDictionary<string, string?> keys = secretWrites.Single().Keys;
        keys["REDIS_USERNAME"].Should().Be(RedisAcl.UsernameFor(binding.Id));
        keys["REDIS_KEY_PREFIX"].Should().Be("", "existing apps keep every key, so nothing that worked breaks");
        applied[usersFile].Should().Contain($"user {RedisAcl.UsernameFor(binding.Id)} on #").And.Contain("~* &*");
    }

    [Fact]
    public async Task A_binding_whose_switch_failed_stays_on_the_admin_password_and_is_reported()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId, "v7.0.15");
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        await db.Set<RedisCluster>().Where(c => c.Id == cluster.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.PerAppUsersSince, (DateTime?)null));
        await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        secretWrites.Clear();
        failingPod = "cache-leader-0";

        (int moved, IReadOnlyList<string> failures) = await sut.EnablePerAppUsersAsync(tenantId, cluster.Id);

        moved.Should().Be(0);
        failures.Should().ContainSingle().Which.Should().Contain("cache-leader-0");
        secretWrites.Should().BeEmpty("the app keeps the admin password, which still works, rather than one a node refuses");
    }

    [Fact]
    public async Task A_redis_6_cluster_cannot_be_switched()
    {
        (Guid tenantId, Guid k8sId, _) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId, "v6.2.14");

        Func<Task> act = () => sut.EnablePerAppUsersAsync(tenantId, cluster.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Redis 7*");
    }

    // ── Prefix changes and the background reconcile ──────────────────────────

    [Fact]
    public async Task Tightening_a_prefix_changes_the_user_and_the_apps_prefix_together()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        stdinSent.Clear();
        secretWrites.Clear();

        await sut.SetCacheBindingKeyPrefixAsync(tenantId, binding.Id, "shop");

        stdinSent.Should().Contain(s => s.Contains($"ACL SETUSER {RedisAcl.UsernameFor(binding.Id)} reset on #")
            && s.Contains("~shop:*"));
        secretWrites.Single().Keys["REDIS_KEY_PREFIX"].Should().Be("shop:");
    }

    /// <summary>
    /// Deleting a deployment removes its bindings by database cascade and touches no cluster. The
    /// app's user must not outlive that past the next reconcile.
    /// </summary>
    [Fact]
    public async Task A_binding_removed_by_cascade_loses_its_user_on_the_next_reconcile()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        (Guid keptDeployment, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "kept");
        CacheBinding gone = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        CacheBinding kept = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, keptDeployment, "redis");

        await db.Set<AppDeployment>().Where(d => d.Id == deploymentId).ExecuteDeleteAsync();

        IReadOnlyList<string> failures = await sut.ReconcileTenantUsersAsync(tenantId);

        failures.Should().BeEmpty();
        nodeUsers.Values.Should().AllSatisfy(u =>
        {
            u.Should().NotContain(RedisAcl.UsernameFor(gone.Id));
            u.Should().Contain(RedisAcl.UsernameFor(kept.Id), "only what is gone from the database is revoked");
            u.Should().Contain("default");
        });
        (await vault.GetRedisClusterSecretValueAsync(tenantId, cluster.Id, RedisAcl.PasswordSecretName(gone.Id)))
            .Should().BeNull();
    }

    [Fact]
    public async Task The_reconcile_of_one_tenant_never_revokes_another_tenants_users()
    {
        (Guid ours, Guid ourK8s, Guid ourEnv) = await SeedTenantAsync("ours");
        (Guid theirs, _, _) = await SeedTenantAsync("theirs");
        RedisCluster cluster = await CreateRedisAsync(ours, ourK8s);
        (Guid deploymentId, _) = await SeedDeploymentAsync(ours, ourK8s, ourEnv, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(ours, cluster.Id, deploymentId, "redis");
        stdinSent.Clear();

        (await sut.ReconcileTenantUsersAsync(theirs)).Should().BeEmpty();

        stdinSent.Should().BeEmpty("the other tenant has no Redis, so nothing of ours may be touched");
        nodeUsers.Values.Should().AllSatisfy(u => u.Should().Contain(RedisAcl.UsernameFor(binding.Id)));
    }

    // ── Found in review ──────────────────────────────────────────────────────

    /// <summary>
    /// redis-cli exits 0 when a command it read from stdin is refused; the refusal is only in its
    /// output. A sync that believed the exit code would hand the app a password that node refused.
    /// </summary>
    [Fact]
    public async Task A_node_that_refuses_the_users_with_exit_code_zero_fails_the_sync()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        refusals[("cache-follower-0", false)] = "ERR Error in ACL SETUSER modifier '+eval_ro': Unknown command\n";

        Func<Task> act = () => sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cache-follower-0*Unknown command*");
        secretWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task A_node_that_gives_no_answer_is_not_taken_for_one_with_no_users()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        refusals[("cache-leader-0", true)] = "";

        Func<Task> act = () => sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cache-leader-0*no answer*");
    }

    [Fact]
    public async Task A_second_app_cannot_take_a_prefix_another_app_uses_on_the_same_redis()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid first, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "shop");
        (Guid second, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "billing");
        await sut.CreateCacheBindingAsync(tenantId, cluster.Id, first, "redis", "api");

        Func<Task> create = () => sut.CreateCacheBindingAsync(tenantId, cluster.Id, second, "redis", "api");
        await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shop-deploy*already uses the key prefix 'api'*");

        CacheBinding other = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, second, "redis", "billing");
        Func<Task> change = () => sut.SetCacheBindingKeyPrefixAsync(tenantId, other.Id, "api");
        await change.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already uses the key prefix 'api'*");
    }

    [Fact]
    public async Task Two_deployments_in_one_namespace_cannot_write_the_same_secret()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid first, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "shop");
        (Guid second, Guid _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "billing");
        await db.Set<AppDeployment>().Where(d => d.Id == second)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Namespace, "shop-ns"));
        await sut.CreateCacheBindingAsync(tenantId, cluster.Id, first, "redis");

        Func<Task> act = () => sut.CreateCacheBindingAsync(tenantId, cluster.Id, second, "redis");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Secret 'redis' in shop-ns*");
    }

    [Fact]
    public async Task An_allow_list_in_another_environment_does_not_restrict_this_one()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        RedisCluster otherRedis = await sut.CreateClusterAsync(tenantId, k8sId, "other", "caching", 3, "v7", "1Gi", null, true);
        nodeUsers["other-leader-0"] = ["default"];
        nodeUsers["other-follower-0"] = ["default"];
        (Guid deploymentId, Guid appId) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");

        Guid elsewhere = Guid.NewGuid();
        db.Set<Data.Environment>().Add(new Data.Environment { Id = elsewhere, TenantId = tenantId, Name = "prod" });
        db.Set<AppAllowedCache>().Add(new AppAllowedCache
        {
            Id = Guid.NewGuid(), AppId = appId, EnvironmentId = elsewhere, RedisClusterId = cluster.Id,
        });
        await db.SaveChangesAsync();

        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, otherRedis.Id, deploymentId, "redis");

        binding.RedisClusterId.Should().Be(otherRedis.Id);
    }

    [Fact]
    public async Task Deleting_an_app_bound_before_the_switch_warns_that_it_still_knows_the_admin_password()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        await db.Set<RedisCluster>().Where(c => c.Id == cluster.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.PerAppUsersSince, (DateTime?)null));
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        await sut.EnablePerAppUsersAsync(tenantId, cluster.Id);

        string? warning = await sut.DeleteCacheBindingAsync(tenantId, binding.Id);

        warning.Should().Contain("once given").And.Contain("admin password");
        nodeUsers.Values.Should().AllSatisfy(u => u.Should().NotContain(RedisAcl.UsernameFor(binding.Id)));
    }

    /// <summary>
    /// The sync used to save back the whole entity graph it loaded at the start. A prefix changed
    /// while it ran came back as the old one, and a switch made meanwhile could be undone.
    /// </summary>
    [Fact]
    public async Task A_sync_never_writes_back_a_stale_copy_of_the_binding()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis", "shop");

        duringApply = async () =>
        {
            using ApplicationDbContext elsewhere = testDb.CreateContext();
            await elsewhere.Set<CacheBinding>().Where(b => b.Id == binding.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.KeyPrefix, "shop2").SetProperty(b => b.SyncPending, true));
        };

        await sut.SyncCacheBindingAsync(tenantId, binding.Id);

        CacheBinding after = await db.Set<CacheBinding>().AsNoTracking().SingleAsync(b => b.Id == binding.Id);
        after.KeyPrefix.Should().Be("shop2", "the change made while the sync ran is not overwritten");
        after.SyncPending.Should().BeTrue("the Secret still says 'shop:', so the newer prefix has not reached the app");
        (await db.Set<RedisCluster>().AsNoTracking().SingleAsync(c => c.Id == cluster.Id)).PerAppUsersSince
            .Should().NotBeNull();
    }

    [Fact]
    public async Task A_prefix_change_whose_sync_failed_is_finished_by_the_background_reconcile()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        failingPod = "cache-follower-0";

        Func<Task> act = () => sut.SetCacheBindingKeyPrefixAsync(tenantId, binding.Id, "shop");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*saved*retries*");
        (await db.Set<CacheBinding>().AsNoTracking().SingleAsync(b => b.Id == binding.Id)).SyncPending.Should().BeTrue();

        failingPod = null;
        secretWrites.Clear();
        (await sut.ReconcileTenantUsersAsync(tenantId)).Should().BeEmpty();

        secretWrites.Single().Keys["REDIS_KEY_PREFIX"].Should().Be("shop:");
        (await db.Set<CacheBinding>().AsNoTracking().SingleAsync(b => b.Id == binding.Id)).SyncPending.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_switch_finishes_on_the_next_reconcile_except_for_bindings_with_sync_off()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        (Guid pausedDeployment, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "paused");
        await db.Set<RedisCluster>().Where(c => c.Id == cluster.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.PerAppUsersSince, (DateTime?)null));
        CacheBinding binding = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        CacheBinding paused = await sut.CreateCacheBindingAsync(tenantId, cluster.Id, pausedDeployment, "redis");
        await db.Set<CacheBinding>().Where(b => b.Id == paused.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.SyncEnabled, false));
        failingPod = "cache-leader-0";
        (int moved, _) = await sut.EnablePerAppUsersAsync(tenantId, cluster.Id);
        moved.Should().Be(0);

        failingPod = null;
        secretWrites.Clear();
        (await sut.ReconcileTenantUsersAsync(tenantId)).Should().BeEmpty();

        secretWrites.Should().ContainSingle().Which.Keys["REDIS_USERNAME"].Should().Be(RedisAcl.UsernameFor(binding.Id));
        RedisCluster switched = await db.Set<RedisCluster>().AsNoTracking().SingleAsync(c => c.Id == cluster.Id);
        RedisAcl.StillOnAdminPassword(
            await db.Set<CacheBinding>().AsNoTracking().SingleAsync(b => b.Id == binding.Id), switched).Should().BeFalse();
    }

    /// <summary>
    /// A binding created while another reconcile of the same Redis is applying keeps its password:
    /// the prune asks the database again instead of trusting the set it read at the start.
    /// </summary>
    [Fact]
    public async Task A_binding_created_during_a_reconcile_keeps_its_password()
    {
        (Guid tenantId, Guid k8sId, Guid envId) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        (Guid deploymentId, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "ours");
        (Guid lateDeployment, _) = await SeedDeploymentAsync(tenantId, k8sId, envId, "late");
        await sut.CreateCacheBindingAsync(tenantId, cluster.Id, deploymentId, "redis");
        Guid lateId = Guid.NewGuid();

        duringApply = async () =>
        {
            using ApplicationDbContext elsewhere = testDb.CreateContext();
            elsewhere.Set<CacheBinding>().Add(new CacheBinding
            {
                Id = lateId, TenantId = tenantId, RedisClusterId = cluster.Id, AppDeploymentId = lateDeployment,
                KubernetesSecretName = "redis",
            });
            await elsewhere.SaveChangesAsync();
            await vault.SetRedisClusterSecretAsync(tenantId, cluster.Id, RedisAcl.PasswordSecretName(lateId), "late-pw");
        };

        await sut.ReconcileUsersAsync(tenantId, cluster.Id, excludeBindingId: null);

        (await vault.GetRedisClusterSecretValueAsync(tenantId, cluster.Id, RedisAcl.PasswordSecretName(lateId)))
            .Should().Be("late-pw");
    }

    [Fact]
    public async Task A_reconcile_releases_its_lease_and_takes_over_an_expired_one()
    {
        (Guid tenantId, Guid k8sId, _) = await SeedTenantAsync("ours");
        RedisCluster cluster = await CreateRedisAsync(tenantId, k8sId);
        await db.Set<RedisCluster>().Where(c => c.Id == cluster.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsersLeaseUntil, DateTime.UtcNow.AddMinutes(-1)));

        await sut.ReconcileUsersAsync(tenantId, cluster.Id, excludeBindingId: null);

        (await db.Set<RedisCluster>().AsNoTracking().SingleAsync(c => c.Id == cluster.Id)).UsersLeaseUntil
            .Should().BeNull("an expired lease is taken over, and a finished reconcile hands it back");
    }

    // ── Which pods are nodes to apply to ─────────────────────────────────────

    [Fact]
    public void Only_pods_whose_redis_container_is_running_are_applied_to()
    {
        const string json = """
            {"items":[
              {"metadata":{"name":"ok"},"status":{"phase":"Running","containerStatuses":[{"name":"rc-leader","ready":true,"state":{"running":{}}}]}},
              {"metadata":{"name":"not-ready-yet"},"status":{"phase":"Running","containerStatuses":[{"name":"rc-leader","ready":false,"state":{"running":{}}}]}},
              {"metadata":{"name":"crash-looping"},"status":{"phase":"Running","containerStatuses":[{"name":"rc-leader","state":{"waiting":{"reason":"CrashLoopBackOff"}}},{"name":"redis-exporter","state":{"running":{}}}]}},
              {"metadata":{"name":"terminating","deletionTimestamp":"2026-11-23T10:00:00Z"},"status":{"phase":"Running","containerStatuses":[{"name":"rc-leader","state":{"running":{}}}]}},
              {"metadata":{"name":"pending"},"status":{"phase":"Pending"}}
            ]}
            """;

        RedisService.RunningPodNames(json, "rc-leader").Should().Equal("ok", "not-ready-yet");
    }
}
