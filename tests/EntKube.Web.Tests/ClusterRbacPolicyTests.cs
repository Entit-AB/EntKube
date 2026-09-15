using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Covers the cluster-scoped half of the RBAC deny-list: the ClusterPolicy that stops a Helm chart
/// binding a customer app's ServiceAccount to a cluster-wide role, and the cluster-wide data it is
/// built from.
///
/// The namespaced policies are per-tenant and per-environment. This one is not — there is a single
/// object per cluster — so most of what can go wrong here is a scoping mistake that quietly drops
/// somebody else's namespaces, which is what the resolver tests are for.
/// </summary>
public class ClusterRbacPolicyTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;

    public ClusterRbacPolicyTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        db.Database.EnsureCreated();
    }

    // ── Manifest ──────────────────────────────────────────────────────────────

    [Fact]
    public void It_is_a_ClusterPolicy_not_a_namespaced_Policy()
    {
        string yaml = KyvernoPolicyService.BuildClusterRbacPolicy(["acme-billing"], "Enforce")!;

        yaml.Should().Contain("kind: ClusterPolicy");
        yaml.Should().Contain($"name: {KyvernoPolicyService.ClusterRbacPolicyName}");

        // A ClusterPolicy has no namespace of its own; emitting one would be rejected outright.
        yaml.Should().NotContain("  namespace:");
    }

    [Fact]
    public void It_is_not_emitted_by_the_namespaced_manifest_builder()
    {
        // Both policy types live in the same table and come back from the same query. If the
        // namespaced builder rendered this one it would apply a ClusterPolicy body per namespace.
        KyvernoPolicy policy = Policy(KyvernoPolicyType.RestrictClusterRbac);

        KyvernoPolicyService.BuildManifest([policy], "acme-billing").Should().BeEmpty();
    }

    [Fact]
    public void Each_app_namespace_is_denied_by_service_account_and_by_group()
    {
        // A binding can name one ServiceAccount, or the namespace's ServiceAccounts collectively.
        // Covering only the first leaves the second as an unguarded way to grant the same thing.
        string yaml = KyvernoPolicyService.BuildClusterRbacPolicy(["acme-billing", "acme-web"], "Audit")!;

        foreach (string ns in new[] { "acme-billing", "acme-web" })
        {
            yaml.Should().Contain($"- \"{ns}\"");
            yaml.Should().Contain($"- \"system:serviceaccounts:{ns}\"");
        }
    }

    [Theory]
    [InlineData("system:serviceaccounts")]
    [InlineData("system:authenticated")]
    [InlineData("system:anonymous")]
    public void Catch_all_groups_are_denied_regardless_of_namespace(string group)
    {
        KyvernoPolicyService.BuildClusterRbacPolicy(["acme-billing"], "Audit")!
            .Should().Contain($"- \"{group}\"");
    }

    [Fact]
    public void The_api_servers_own_bindings_are_excluded()
    {
        // system:basic-user and system:discovery bind to system:authenticated and are recreated by
        // the API server on startup. Without this exclusion, Enforce mode fights the control plane.
        string yaml = KyvernoPolicyService.BuildClusterRbacPolicy(["acme-billing"], "Enforce")!;

        yaml.Should().Contain("exclude:");
        yaml.Should().Contain("- \"system:*\"");
    }

    [Fact]
    public void Subject_projections_are_defaulted()
    {
        // A ClusterRoleBinding with only User subjects projects to null, and a rule that fails to
        // evaluate denies the resource — so a missing default blocks ordinary bindings.
        string yaml = KyvernoPolicyService.BuildClusterRbacPolicy(["acme-billing"], "Audit")!;

        yaml.Should().Contain("?kind=='ServiceAccount'].namespace || `[]`");
        yaml.Should().Contain("?kind=='Group'].name || `[]`");
    }

    [Fact]
    public void It_matches_bindings_not_cluster_roles()
    {
        // A ClusterRole bound to nothing grants nothing, and denying the kind would break every
        // operator chart in the catalog.
        string yaml = KyvernoPolicyService.BuildClusterRbacPolicy(["acme-billing"], "Enforce")!;

        yaml.Should().Contain("- ClusterRoleBinding");
        yaml.Should().NotContain("- ClusterRole\n");
    }

    [Fact]
    public void No_app_namespaces_means_no_policy()
    {
        // An AnyIn against an empty list matches nothing. Applying it would look like protection
        // while denying nothing at all.
        KyvernoPolicyService.BuildClusterRbacPolicy([], "Enforce").Should().BeNull();
        KyvernoPolicyService.BuildClusterRbacPolicy(["  "], "Enforce").Should().BeNull();
    }

    [Fact]
    public void Namespaces_are_deduplicated_and_ordered()
    {
        // The manifest is applied repeatedly; an unstable ordering would make every apply look
        // like a change.
        string first  = KyvernoPolicyService.BuildClusterRbacPolicy(["b-ns", "a-ns", "b-ns"], "Audit")!;
        string second = KyvernoPolicyService.BuildClusterRbacPolicy(["a-ns", "b-ns"], "Audit")!;

        first.Should().Be(second);
        first.IndexOf("\"a-ns\"", StringComparison.Ordinal)
            .Should().BeLessThan(first.IndexOf("\"b-ns\"", StringComparison.Ordinal));
    }

    // ── Cluster-wide resolution ───────────────────────────────────────────────

    [Fact]
    public async Task Namespaces_come_from_every_tenant_on_the_cluster()
    {
        // The scoping mistake this guards against: building the shared object from the namespaces
        // of whichever tenant happened to trigger the apply, dropping the others.
        Guid clusterId = Guid.NewGuid();
        Seed(clusterId, "tenant-a", "acme-billing");
        Seed(clusterId, "tenant-b", "globex-api");
        await db.SaveChangesAsync();

        List<string> namespaces = await KyvernoPolicyService.ResolveClusterAppNamespacesAsync(
            db, clusterId, default);

        namespaces.Should().BeEquivalentTo(["acme-billing", "globex-api"]);
    }

    [Fact]
    public async Task Namespaces_from_other_clusters_are_not_included()
    {
        Guid clusterId = Guid.NewGuid();
        Seed(clusterId, "tenant-a", "acme-billing");
        Seed(Guid.NewGuid(), "tenant-b", "elsewhere");
        await db.SaveChangesAsync();

        List<string> namespaces = await KyvernoPolicyService.ResolveClusterAppNamespacesAsync(
            db, clusterId, default);

        namespaces.Should().BeEquivalentTo(["acme-billing"]);
    }

    [Fact]
    public async Task The_governance_locked_namespace_wins_over_the_deployment_namespace()
    {
        // Governance can re-point an app. The deny-list has to follow where the app actually runs.
        Guid clusterId = Guid.NewGuid();
        (Guid appId, Guid envId) = Seed(clusterId, "tenant-a", "stale-ns");
        db.AppEnvironments.Add(new AppEnvironment
        {
            AppId = appId, EnvironmentId = envId, Namespace = "locked-ns"
        });
        await db.SaveChangesAsync();

        List<string> namespaces = await KyvernoPolicyService.ResolveClusterAppNamespacesAsync(
            db, clusterId, default);

        namespaces.Should().BeEquivalentTo(["locked-ns"]);
    }

    [Fact]
    public async Task Mode_is_null_when_nobody_enabled_it()
    {
        Guid clusterId = Guid.NewGuid();
        Seed(clusterId, "tenant-a", "acme-billing");
        await db.SaveChangesAsync();

        (await KyvernoPolicyService.ResolveClusterRbacModeAsync(db, clusterId, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task The_strongest_mode_on_the_cluster_wins()
    {
        // One object, many tenants. Last-writer-wins would let one tenant's apply drop another's
        // Enforce to Audit, and the two would flip it back and forth on every deploy.
        Guid clusterId = Guid.NewGuid();
        (_, Guid envA, Guid tenantA) = SeedFull(clusterId, "tenant-a", "acme-billing");
        (_, Guid envB, Guid tenantB) = SeedFull(clusterId, "tenant-b", "globex-api");

        db.KyvernoPolicies.Add(Policy(KyvernoPolicyType.RestrictClusterRbac, tenantA, envA,
            KyvernoValidationFailureAction.Audit));
        db.KyvernoPolicies.Add(Policy(KyvernoPolicyType.RestrictClusterRbac, tenantB, envB,
            KyvernoValidationFailureAction.Enforce));
        await db.SaveChangesAsync();

        (await KyvernoPolicyService.ResolveClusterRbacModeAsync(db, clusterId, default))
            .Should().Be(KyvernoValidationFailureAction.Enforce);
    }

    [Fact]
    public async Task A_policy_from_a_tenant_with_no_footprint_here_does_not_enable_it()
    {
        // tenantId and environmentId are matched as separate lists, so a tenant on this cluster
        // plus an environment on this cluster must not combine into a scope that is on neither.
        Guid clusterId = Guid.NewGuid();
        (_, Guid envA, Guid tenantA) = SeedFull(clusterId, "tenant-a", "acme-billing");
        (_, Guid envB, Guid tenantB) = SeedFull(Guid.NewGuid(), "tenant-b", "elsewhere");

        // tenantB is not on this cluster; envA is. The cross-product would pair them.
        db.KyvernoPolicies.Add(Policy(KyvernoPolicyType.RestrictClusterRbac, tenantB, envA,
            KyvernoValidationFailureAction.Enforce));
        await db.SaveChangesAsync();

        (await KyvernoPolicyService.ResolveClusterRbacModeAsync(db, clusterId, default))
            .Should().BeNull();

        _ = tenantA; _ = envB;
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private (Guid AppId, Guid EnvId) Seed(Guid clusterId, string tenantSlug, string ns)
    {
        (Guid appId, Guid envId, _) = SeedFull(clusterId, tenantSlug, ns);
        return (appId, envId);
    }

    private (Guid AppId, Guid EnvId, Guid TenantId) SeedFull(Guid clusterId, string tenantSlug, string ns)
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = tenantSlug, Slug = tenantSlug };
        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = $"{tenantSlug}-customer" };
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = $"{tenantSlug}-app" };
        EntKube.Web.Data.Environment env = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Name = $"{tenantSlug}-prod"
        };

        db.Tenants.Add(tenant);
        db.Customers.Add(customer);
        db.Apps.Add(app);
        db.Environments.Add(env);

        // The deployment's cluster has to exist for the foreign key. Several tenants share one
        // cluster row, which is the situation these tests are about, so only create it once.
        if (!db.KubernetesClusters.Local.Any(c => c.Id == clusterId))
        {
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId,
                TenantId = tenant.Id,
                EnvironmentId = env.Id,
                Name = $"cluster-{clusterId:N}"[..20],
                ApiServerUrl = "https://cluster.example:6443",
            });
        }

        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            EnvironmentId = env.Id,
            ClusterId = clusterId,
            Name = $"{tenantSlug}-deployment",
            Namespace = ns,
        });

        return (app.Id, env.Id, tenant.Id);
    }

    private static KyvernoPolicy Policy(
        KyvernoPolicyType type,
        Guid? tenantId = null,
        Guid? environmentId = null,
        KyvernoValidationFailureAction mode = KyvernoValidationFailureAction.Audit) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId ?? Guid.NewGuid(),
        EnvironmentId = environmentId ?? Guid.NewGuid(),
        PolicyType = type,
        ValidationFailureAction = mode,
    };

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
