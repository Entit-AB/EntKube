using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What <see cref="AppGovernanceService"/> sends to a cluster, and whose cluster it is willing to
/// send it to.
///
/// <para><b>Why there was none.</b> <c>GovernanceNamespaceTests</c> has four tests and none of them
/// constructs the service — they check name validation and a result shape. The three paths that
/// actually reach a cluster were untestable: one built its own <c>Kubernetes</c> client from the
/// kubeconfig, and two ran <c>kubectl apply</c> through a <c>private static</c> process call. Moving
/// them onto <see cref="IClusterClient"/> is what makes these writable, the same way it did for the
/// routing paths.</para>
///
/// <para>These assert on <em>what reached the cluster</em>. A governance apply that silently goes
/// nowhere leaves a quota unenforced and a NetworkPolicy unwritten, with the page reporting
/// success — and the three methods took an app, an environment and a cluster id with no tenant
/// anywhere, so the id was the whole authorization.</para>
/// </summary>
public class GovernanceClusterCallTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly AppGovernanceService sut;

    /// <summary>Every manifest applied, as (manifest, namespace).</summary>
    private readonly List<(string Manifest, string Namespace)> applied = [];

    /// <summary>Namespaces the service asked the cluster to ensure.</summary>
    private readonly List<string> ensured = [];

    private Guid tenantId, clusterId, appId, environmentId;

    public GovernanceClusterCallTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(f => f.ApplyManifestInNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>(
                (manifest, ns, _, _, _) => applied.Add((manifest, ns)))
            .ReturnsAsync("applied");

        k8s.Setup(f => f.EnsureNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ns, _, _) => ensured.Add(ns))
            .Returns(Task.CompletedTask);

        sut = new AppGovernanceService(
            testDb.Factory,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<AppGovernanceService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync()
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        appId = Guid.NewGuid();
        environmentId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = appId, Name = "billing-deploy", Type = DeploymentType.Manual,
            EnvironmentId = environmentId, ClusterId = clusterId, Namespace = "billing-ns",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    private KubernetesCluster Cluster() => db.KubernetesClusters.First(c => c.Id == clusterId);

    // ──────── Applying governance ────────

    [Fact]
    public async Task Governance_is_applied_into_the_deployments_namespace()
    {
        await SeedAsync();

        (bool ok, string output) = await sut.ApplyToClusterAsync(tenantId, appId, environmentId, Cluster());

        ok.Should().BeTrue(output);
        applied.Should().ContainSingle()
            .Which.Namespace.Should().Be("billing-ns",
                "the namespace comes from the deployment targeting this cluster in this environment");
    }

    /// <summary>
    /// The wiring behind <c>PrometheusScrapeAllowanceTests</c>: the apply asks Catalog where this
    /// cluster's Prometheus runs and hands that to the manifest. Those tests pass the namespace in
    /// by hand, so without this one an apply that never asked would pass all of them.
    /// </summary>
    [Fact]
    public async Task A_deny_all_applied_where_Prometheus_runs_lets_Prometheus_scrape()
    {
        await SeedAsync();
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = "kube-prometheus-stack",
            ComponentType = "HelmChart", HelmChartName = "kube-prometheus-stack",
            ReleaseName = "kube-prometheus-stack", Namespace = "observability",
            Status = ComponentStatus.Installed,
        });
        db.AppNetworkPolicies.Add(new AppNetworkPolicy
        {
            Id = Guid.NewGuid(), AppId = appId, EnvironmentId = environmentId,
            Name = "deny-all", PolicyType = AppNetworkPolicyType.DenyAll,
        });
        await db.SaveChangesAsync();

        (bool ok, string output) = await sut.ApplyToClusterAsync(tenantId, appId, environmentId, Cluster());

        ok.Should().BeTrue(output);
        string manifest = applied.Should().ContainSingle().Subject.Manifest;
        PrometheusJudge.LetsIn(manifest, "billing-ns", prometheusLivesIn: "observability").Should().BeTrue();
    }

    [Fact]
    public async Task Another_tenant_cannot_apply_governance_to_this_cluster()
    {
        await SeedAsync();

        (bool ok, string output) =
            await sut.ApplyToClusterAsync(Guid.NewGuid(), appId, environmentId, Cluster());

        ok.Should().BeFalse();
        output.Should().Contain("not this tenant's");
        applied.Should().BeEmpty("nothing should reach anyone's cluster");
    }

    // ──────── Creating a namespace ────────

    [Fact]
    public async Task Creating_a_namespace_asks_the_cluster_to_ensure_it()
    {
        await SeedAsync();

        (bool ok, string output) = await sut.CreateNamespaceOnClusterAsync(tenantId, clusterId, "team-a");

        ok.Should().BeTrue(output);
        ensured.Should().Equal(["team-a"]);
    }

    [Fact]
    public async Task Another_tenant_cannot_create_a_namespace_on_this_cluster()
    {
        await SeedAsync();

        (bool ok, _) = await sut.CreateNamespaceOnClusterAsync(Guid.NewGuid(), clusterId, "team-a");

        ok.Should().BeFalse();
        ensured.Should().BeEmpty();
    }

    /// <summary>
    /// An invalid name is refused before the tenant is even considered — the cluster is never
    /// reached either way, and a name SQLite would accept but Kubernetes would not is the caller's
    /// mistake rather than an authorization question.
    /// </summary>
    [Fact]
    public async Task An_invalid_namespace_name_never_reaches_the_cluster()
    {
        await SeedAsync();

        (bool ok, _) = await sut.CreateNamespaceOnClusterAsync(tenantId, clusterId, "Not Valid");

        ok.Should().BeFalse();
        ensured.Should().BeEmpty();
    }

    // ──────── Checking a namespace ────────

    [Fact]
    public async Task A_namespace_check_reports_a_cluster_it_may_not_reach_rather_than_failing()
    {
        await SeedAsync();

        NamespaceCheckResult result =
            await sut.CheckNamespaceAsync(Guid.NewGuid(), appId, environmentId, "billing-ns");

        result.Clusters.Should().ContainSingle()
            .Which.Error.Should().Contain("not this tenant's",
                "the check runs across a tenant's clusters, so one it cannot reach is a status on "
                + "that cluster and not a failure of the whole answer");
    }
    /// <summary>
    /// Governance for an app in an environment — its namespace lock, resource quota, network
    /// policies, RBAC and the allow-lists of databases, caches and storage. Fifteen read and
    /// write entry points took an app id and an environment id with no tenant, while
    /// <c>CheckNamespaceAsync</c>, <c>CreateNamespaceOnClusterAsync</c>, <c>ApplyToClusterAsync</c>
    /// and the three <c>GetAvailable*</c> methods beside them had always taken one.
    ///
    /// <para>The guard is a <b>pairing</b> check, so both halves are exercised: an app of ours
    /// with an environment of theirs is as wrong as the reverse, and governance written against
    /// that pair would be applied to a namespace in the wrong tenant.</para>
    /// </summary>
    [Fact]
    public async Task Governance_refuses_an_app_or_an_environment_that_is_not_this_tenants()
    {
        await SeedAsync();

        Guid strangerTenant = Guid.NewGuid();
        Guid strangerCustomer = Guid.NewGuid();
        Guid strangerApp = Guid.NewGuid();
        Guid strangerEnvironment = Guid.NewGuid();

        db.Tenants.Add(new Tenant
        {
            Id = strangerTenant, Name = "Stranger", Slug = $"stranger-{strangerTenant:N}",
        });
        db.Customers.Add(new Customer
        {
            Id = strangerCustomer, TenantId = strangerTenant, Name = "Elsewhere",
        });
        db.Apps.Add(new App { Id = strangerApp, CustomerId = strangerCustomer, Name = "theirs" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = strangerEnvironment, TenantId = strangerTenant, Name = "their-prod",
        });
        // ⚠️ The namespace lock lives on the AppEnvironment LINK row, so the owner case below
        // needs one to exist — SeedAsync does not create it. Without this the owner assertion
        // fails on a missing row and says nothing about scoping.
        db.Set<AppEnvironment>().Add(new AppEnvironment
        {
            AppId = appId, EnvironmentId = environmentId,
        });
        await db.SaveChangesAsync();

        // ⚠️ Every crossing needs a row that WOULD come back if the guard were missing.
        // The first draft asserted .BeEmpty() for combinations with nothing seeded, so it passed
        // whichever half of the guard was deleted — empty because the data was absent, not
        // because the call was refused. An empty result only proves something when a row exists.
        db.Set<AppNetworkPolicy>().AddRange(
            new AppNetworkPolicy
            {
                Id = Guid.NewGuid(), AppId = appId, EnvironmentId = environmentId,
                Name = "ours", AllowFromNamespace = "ours",
            },
            new AppNetworkPolicy
            {
                Id = Guid.NewGuid(), AppId = strangerApp, EnvironmentId = environmentId,
                Name = "their-app-our-env", AllowFromNamespace = "their-app-our-env",
            },
            new AppNetworkPolicy
            {
                Id = Guid.NewGuid(), AppId = appId, EnvironmentId = strangerEnvironment,
                Name = "our-app-their-env", AllowFromNamespace = "our-app-their-env",
            });
        await db.SaveChangesAsync();

        // The owner sees its own row — so the assertions below are about refusal, not absence.
        (await sut.GetNetworkPoliciesAsync(tenantId, appId, environmentId))
            .Should().ContainSingle();

        // Our app, asked as them.
        (await sut.GetNetworkPoliciesAsync(strangerTenant, appId, environmentId))
            .Should().BeEmpty("the row exists, so this is a refusal");
        (await sut.GetQuotaAsync(strangerTenant, appId, environmentId)).Should().BeNull();
        (await sut.GetAppEnvironmentsAsync(strangerTenant, appId)).Should().BeEmpty();

        // Their app with our environment: only the APP half of the guard can refuse this, and a
        // seeded row means a missing app check would hand it over.
        (await sut.GetNetworkPoliciesAsync(tenantId, strangerApp, environmentId))
            .Should().BeEmpty("their app, our environment");

        // Our app with their environment: only the ENVIRONMENT half can refuse this.
        (await sut.GetNetworkPoliciesAsync(tenantId, appId, strangerEnvironment))
            .Should().BeEmpty("our app, their environment");

        // A write must not land either.
        await sut.SaveNamespaceAsync(strangerTenant, appId, environmentId, "stolen");
        (await sut.GetNamespaceAsync(tenantId, appId, environmentId))
            .Should().NotBe("stolen", "SaveNamespaceAsync returns void, so the row has to say it");

        // And the owner still works.
        await sut.SaveNamespaceAsync(tenantId, appId, environmentId, "ours");
        (await sut.GetNamespaceAsync(tenantId, appId, environmentId)).Should().Be("ours");
    }

    // ──────── Unit B: by-id rows, grants, and the copy ────────

    /// <summary>
    /// A stranger's tenant with everything of its own: a cluster, a customer, an app, an
    /// environment, and one database, cache and storage link. Returned as a tuple so each test
    /// names only the ids it crosses.
    /// </summary>
    private async Task<(Guid Tenant, Guid App, Guid Environment,
                        Guid CnpgDatabase, Guid MongoDatabase, Guid RegisteredDatabase,
                        Guid Redis, Guid Storage)> SeedStrangerAsync()
    {
        Guid t = Guid.NewGuid();
        Guid customer = Guid.NewGuid();
        Guid app = Guid.NewGuid();
        Guid env = Guid.NewGuid();
        Guid cluster = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = t, Name = "Stranger", Slug = $"stranger-{t:N}" });
        db.Customers.Add(new Customer { Id = customer, TenantId = t, Name = "Elsewhere" });
        db.Apps.Add(new App { Id = app, CustomerId = customer, Name = "theirs" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = env, TenantId = t, Name = "their-prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = cluster, TenantId = t, EnvironmentId = env,
            Name = "their-cluster", ApiServerUrl = "https://their-k8s.example.com",
        });

        Guid cnpgCluster = Guid.NewGuid(), mongoCluster = Guid.NewGuid();
        Guid instance = Guid.NewGuid();
        Guid cnpgDb = Guid.NewGuid(), mongoDb = Guid.NewGuid(), regDb = Guid.NewGuid();
        Guid redis = Guid.NewGuid(), storage = Guid.NewGuid();

        db.CnpgClusters.Add(new CnpgCluster
        {
            Id = cnpgCluster, TenantId = t, KubernetesClusterId = cluster,
            Name = "their-pg", Namespace = "their-pg", PostgresVersion = "17", StorageSize = "10Gi",
        });
        db.CnpgDatabases.Add(new CnpgDatabase
        {
            Id = cnpgDb, CnpgClusterId = cnpgCluster, Name = "their-db", Owner = "them",
        });
        db.MongoClusters.Add(new MongoCluster
        {
            Id = mongoCluster, TenantId = t, KubernetesClusterId = cluster,
            Name = "their-mongo", Namespace = "their-mongo", MongoVersion = "7", StorageSize = "10Gi",
        });
        db.MongoDatabases.Add(new MongoDatabase
        {
            Id = mongoDb, MongoClusterId = mongoCluster, Name = "their-mdb", Owner = "them",
        });
        db.RegisteredPostgresInstances.Add(new RegisteredPostgresInstance
        {
            Id = instance, TenantId = t, KubernetesClusterId = cluster,
            Name = "their-registered", Namespace = "their-pg",
            ServiceName = "pg", AdminPodName = "pg-0",
        });
        db.RegisteredPostgresDatabases.Add(new RegisteredPostgresDatabase
        {
            Id = regDb, RegisteredPostgresInstanceId = instance, Name = "their-rdb", Owner = "them",
        });
        db.RedisClusters.Add(new RedisCluster
        {
            Id = redis, TenantId = t, KubernetesClusterId = cluster,
            Name = "their-redis", Namespace = "their-redis", RedisVersion = "7", StorageSize = "5Gi",
        });
        db.StorageLinks.Add(new StorageLink
        {
            Id = storage, TenantId = t, EnvironmentId = env, Name = "their-bucket",
        });
        await db.SaveChangesAsync();

        return (t, app, env, cnpgDb, mongoDb, regDb, redis, storage);
    }

    private async Task LinkAppToEnvironmentAsync()
    {
        db.Set<AppEnvironment>().Add(new AppEnvironment
        {
            AppId = appId, EnvironmentId = environmentId,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The five deletes that take only a row id. Each used <c>FindAsync</c>, which takes a key and
    /// cannot carry a predicate — so the id was the whole authorization, and a governance row's id
    /// travels in page markup. The row has to still be there afterwards: a <c>false</c> return with
    /// the row gone would be the same answer for the wrong reason.
    /// </summary>
    [Fact]
    public async Task A_delete_by_id_cannot_reach_another_tenants_governance_row()
    {
        await SeedAsync();
        var them = await SeedStrangerAsync();

        Guid policyId = Guid.NewGuid();
        Guid rbacPolicyId = Guid.NewGuid();
        Guid ruleId = Guid.NewGuid();
        Guid allowedDbId = Guid.NewGuid();
        Guid allowedCacheId = Guid.NewGuid();
        Guid allowedStorageId = Guid.NewGuid();

        db.Set<AppNetworkPolicy>().Add(new AppNetworkPolicy
        {
            Id = policyId, AppId = them.App, EnvironmentId = them.Environment,
            Name = "theirs", AllowFromNamespace = "theirs",
        });
        db.AppRbacPolicies.Add(new AppRbacPolicy
        {
            Id = rbacPolicyId, AppId = them.App, EnvironmentId = them.Environment,
            ServiceAccountName = "their-sa",
            Rules = [new AppRbacRule
            {
                Id = ruleId, AppRbacPolicyId = rbacPolicyId,
                ApiGroups = "", Resources = "pods", Verbs = "get",
            }],
        });
        db.AppAllowedDatabases.Add(new AppAllowedDatabase
        {
            Id = allowedDbId, AppId = them.App, EnvironmentId = them.Environment,
            CnpgDatabaseId = them.CnpgDatabase,
        });
        db.AppAllowedCaches.Add(new AppAllowedCache
        {
            Id = allowedCacheId, AppId = them.App, EnvironmentId = them.Environment,
            RedisClusterId = them.Redis,
        });
        db.AppAllowedStorages.Add(new AppAllowedStorage
        {
            Id = allowedStorageId, AppId = them.App, EnvironmentId = them.Environment,
            StorageLinkId = them.Storage,
        });
        await db.SaveChangesAsync();

        // Asked as us, about their rows.
        (await sut.DeleteNetworkPolicyAsync(tenantId, policyId)).Should().BeFalse();
        (await sut.DeleteRbacRuleAsync(tenantId, ruleId)).Should().BeFalse(
            "a rule has no app of its own — the scope runs through its policy");
        (await sut.DeleteAllowedDatabaseAsync(tenantId, allowedDbId)).Should().BeFalse();
        (await sut.DeleteAllowedCacheAsync(tenantId, allowedCacheId)).Should().BeFalse();
        (await sut.DeleteAllowedStorageAsync(tenantId, allowedStorageId)).Should().BeFalse();

        using ApplicationDbContext after = testDb.CreateContext();
        after.Set<AppNetworkPolicy>().Any(x => x.Id == policyId).Should().BeTrue();
        after.Set<AppRbacRule>().Any(x => x.Id == ruleId).Should().BeTrue();
        after.AppAllowedDatabases.Any(x => x.Id == allowedDbId).Should().BeTrue();
        after.AppAllowedCaches.Any(x => x.Id == allowedCacheId).Should().BeTrue();
        after.AppAllowedStorages.Any(x => x.Id == allowedStorageId).Should().BeTrue();

        // And their own tenant can still delete them, so "false" above was a refusal.
        (await sut.DeleteNetworkPolicyAsync(them.Tenant, policyId)).Should().BeTrue();
        (await sut.DeleteRbacRuleAsync(them.Tenant, ruleId)).Should().BeTrue();
        (await sut.DeleteAllowedDatabaseAsync(them.Tenant, allowedDbId)).Should().BeTrue();
        (await sut.DeleteAllowedCacheAsync(them.Tenant, allowedCacheId)).Should().BeTrue();
        (await sut.DeleteAllowedStorageAsync(them.Tenant, allowedStorageId)).Should().BeTrue();
    }

    /// <summary>
    /// A rule is added to a policy by the policy's id alone, and the policy is what carries the app.
    /// </summary>
    [Fact]
    public async Task An_rbac_rule_cannot_be_added_to_another_tenants_policy()
    {
        await SeedAsync();
        var them = await SeedStrangerAsync();

        Guid rbacPolicyId = Guid.NewGuid();
        db.AppRbacPolicies.Add(new AppRbacPolicy
        {
            Id = rbacPolicyId, AppId = them.App, EnvironmentId = them.Environment,
            ServiceAccountName = "their-sa",
        });
        await db.SaveChangesAsync();

        Func<Task> add = () => sut.AddRbacRuleAsync(tenantId, rbacPolicyId, "", "secrets", "get");

        await add.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext after = testDb.CreateContext();
        after.Set<AppRbacRule>().Any(r => r.AppRbacPolicyId == rbacPolicyId).Should().BeFalse(
            "a rule granting `get secrets` must not be written into a stranger's service account");
    }

    /// <summary>
    /// A grant names <em>two</em> things: the app+environment it is written against, and the
    /// resource it opens. The pair guard alone would let a tenant point its own app at a
    /// stranger's database — and the allow-list is what the deployment's credentials are minted
    /// from, so that grant is read access to their data.
    ///
    /// <para>All three database kinds are crossed separately because none of them carries a
    /// <c>TenantId</c>: a CNPG and a Mongo database are owned by their cluster, a registered one
    /// by its instance, so each is a different traversal and a different clause.</para>
    /// </summary>
    [Fact]
    public async Task A_grant_cannot_name_another_tenants_database_cache_or_storage()
    {
        await SeedAsync();
        await LinkAppToEnvironmentAsync();
        var them = await SeedStrangerAsync();

        // Our app, our environment, their resource.
        Func<Task> cnpg = () => sut.AddAllowedDatabaseAsync(
            tenantId, appId, environmentId, them.CnpgDatabase, null, null);
        Func<Task> mongo = () => sut.AddAllowedDatabaseAsync(
            tenantId, appId, environmentId, null, them.MongoDatabase, null);
        Func<Task> registered = () => sut.AddAllowedDatabaseAsync(
            tenantId, appId, environmentId, null, null, them.RegisteredDatabase);
        Func<Task> cache = () => sut.AddAllowedCacheAsync(
            tenantId, appId, environmentId, them.Redis);
        Func<Task> storage = () => sut.AddAllowedStorageAsync(
            tenantId, appId, environmentId, them.Storage);

        await cnpg.Should().ThrowAsync<InvalidOperationException>();
        await mongo.Should().ThrowAsync<InvalidOperationException>();
        await registered.Should().ThrowAsync<InvalidOperationException>();
        await cache.Should().ThrowAsync<InvalidOperationException>();
        await storage.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext after = testDb.CreateContext();
        after.AppAllowedDatabases.Any(a => a.AppId == appId).Should().BeFalse();
        after.AppAllowedCaches.Any(a => a.AppId == appId).Should().BeFalse();
        after.AppAllowedStorages.Any(a => a.AppId == appId).Should().BeFalse();
    }

    /// <summary>The other half of a grant: whose app and whose environment it is written against.</summary>
    [Fact]
    public async Task A_grant_cannot_be_written_against_another_tenants_app_or_environment()
    {
        await SeedAsync();
        await LinkAppToEnvironmentAsync();
        var them = await SeedStrangerAsync();

        Guid ourRedis = Guid.NewGuid();
        db.RedisClusters.Add(new RedisCluster
        {
            Id = ourRedis, TenantId = tenantId, KubernetesClusterId = clusterId,
            Name = "our-redis", Namespace = "our-redis", RedisVersion = "7", StorageSize = "5Gi",
        });
        await db.SaveChangesAsync();

        // Our resource, their app.
        Func<Task> theirApp = () => sut.AddAllowedCacheAsync(
            tenantId, them.App, environmentId, ourRedis);
        // Our resource, our app, their environment.
        Func<Task> theirEnvironment = () => sut.AddAllowedCacheAsync(
            tenantId, appId, them.Environment, ourRedis);

        await theirApp.Should().ThrowAsync<InvalidOperationException>();
        await theirEnvironment.Should().ThrowAsync<InvalidOperationException>();

        // The owner's own grant goes through, so the refusals above are about the crossing.
        AppAllowedCache ours = await sut.AddAllowedCacheAsync(
            tenantId, appId, environmentId, ourRedis);
        ours.RedisClusterId.Should().Be(ourRedis);
    }

    /// <summary>
    /// The copy names three things — an app and <em>two</em> environments — so it is guarded twice.
    /// Each side is a different breach: a source that is not ours reads a stranger's quota and
    /// network policies into our environment, and a target that is not ours overwrites theirs with
    /// ours. The target case is the destructive one: the copy removes the target's existing
    /// policies before writing the source's.
    /// </summary>
    [Fact]
    public async Task Copying_governance_refuses_a_crossing_on_either_environment()
    {
        await SeedAsync();
        var them = await SeedStrangerAsync();

        Guid ourOtherEnvironment = Guid.NewGuid();
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = ourOtherEnvironment, TenantId = tenantId, Name = "staging",
        });
        db.Set<AppNetworkPolicy>().AddRange(
            new AppNetworkPolicy
            {
                Id = Guid.NewGuid(), AppId = appId, EnvironmentId = ourOtherEnvironment,
                Name = "ours-staging", AllowFromNamespace = "ours-staging",
            },
            new AppNetworkPolicy
            {
                Id = Guid.NewGuid(), AppId = them.App, EnvironmentId = them.Environment,
                Name = "theirs", AllowFromNamespace = "theirs",
            });
        await db.SaveChangesAsync();

        // 1. Their app.
        Func<Task> theirApp = () => sut.CopyFromEnvironmentAsync(
            tenantId, them.App, them.Environment, them.Environment);
        // 2. Their environment as the SOURCE — reading their governance.
        Func<Task> theirSource = () => sut.CopyFromEnvironmentAsync(
            tenantId, appId, them.Environment, environmentId);
        // 3. Their environment as the TARGET — overwriting their governance.
        Func<Task> theirTarget = () => sut.CopyFromEnvironmentAsync(
            tenantId, appId, ourOtherEnvironment, them.Environment);

        await theirApp.Should().ThrowAsync<InvalidOperationException>();
        await theirSource.Should().ThrowAsync<InvalidOperationException>();
        await theirTarget.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext after = testDb.CreateContext();
        after.Set<AppNetworkPolicy>()
            .Count(p => p.EnvironmentId == them.Environment).Should().Be(1,
                "the target case deletes before it writes, so their policy had to survive");
        after.Set<AppNetworkPolicy>()
            .Any(p => p.EnvironmentId == environmentId).Should().BeFalse(
                "and nothing of theirs was copied into ours");

        // Our own copy, between two of our environments, still works.
        await sut.CopyFromEnvironmentAsync(tenantId, appId, ourOtherEnvironment, environmentId);
        using ApplicationDbContext copied = testDb.CreateContext();
        copied.Set<AppNetworkPolicy>()
            .Count(p => p.AppId == appId && p.EnvironmentId == environmentId).Should().Be(1);
    }
}
