using EntKube.Web.Data;
using EntKube.Web.Modules.Api;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Which namespaces a tenant's apps occupy on a cluster — the rule a trust bundle and a
/// certificate distribution both put into a namespace selector.
///
/// <para><b>This rule had no test.</b> It lived in <c>TrustBundleService</c> as an
/// <c>internal static</c> helper over the all-seeing context, called from there and from
/// <c>CertificateDistributionService</c>, and the only tests naming <c>TenantNamespaces</c> hand
/// the resolved list to <c>BuildBundleManifest</c> directly — so the resolution itself was never
/// exercised. It is <c>IDeliveryApi.GetTenantNamespacesOnClusterAsync</c> now, because the
/// predicate reads three of Delivery's tables and none of Catalog's, and moving a rule is the
/// moment to cover it rather than to assume it.</para>
///
/// <para>What distributes into a namespace is a CA trust store, so the failure modes are not
/// cosmetic: a namespace wrongly included gets another tenant's trust anchors, and one wrongly
/// left out silently never receives the CA it needs.</para>
/// </summary>
public class TenantNamespaceResolutionTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly DeliveryApi delivery;

    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private readonly Guid cluster = Guid.NewGuid();
    private readonly Guid otherCluster = Guid.NewGuid();
    private readonly Guid environment = Guid.NewGuid();

    public TenantNamespaceResolutionTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        delivery = new DeliveryApi(new TestDbContextFactory(connection));

        db.Tenants.AddRange(
            new Tenant { Id = ourTenant, Name = "Ours", Slug = "ours" },
            new Tenant { Id = theirTenant, Name = "Theirs", Slug = "theirs" });
        db.Environments.Add(new Data.Environment
        {
            Id = environment, TenantId = ourTenant, Name = "prod",
        });

        // Both clusters belong to one tenant on purpose: the resolution is scoped by the APP's
        // tenant, reached through its customer, not by the cluster's. Seeding the shared cluster
        // under one tenant and asking as the other is what proves that.
        db.KubernetesClusters.AddRange(
            new KubernetesCluster
            {
                Id = cluster, TenantId = ourTenant, EnvironmentId = environment,
                Name = "shared-1", ApiServerUrl = "https://k8s.example",
            },
            new KubernetesCluster
            {
                Id = otherCluster, TenantId = ourTenant, EnvironmentId = environment,
                Name = "shared-2", ApiServerUrl = "https://k8s2.example",
            });
        db.SaveChanges();
    }

    /// <summary>Seeds an app with one deployment, and optionally a governance-locked namespace.</summary>
    private Guid SeedApp(Guid tenantId, string name, string deploymentNs, string? lockedNs = null,
        Guid? onCluster = null)
    {
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{name} AB" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = name });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = appId, ClusterId = onCluster ?? cluster,
            EnvironmentId = environment, Name = $"{name}-prod", Namespace = deploymentNs,
        });

        if (lockedNs is not null)
        {
            db.AppEnvironments.Add(new AppEnvironment
            {
                AppId = appId, EnvironmentId = environment, Namespace = lockedNs,
            });
        }

        db.SaveChanges();
        return appId;
    }

    [Fact]
    public async Task The_governance_lock_wins_over_the_deployment_s_own_namespace()
    {
        SeedApp(ourTenant, "Records", deploymentNs: "records-old", lockedNs: "records");

        (await delivery.GetTenantNamespacesOnClusterAsync(ourTenant, cluster))
            .Should().Equal(["records"],
                "the AppEnvironment namespace is where governance pins the app, and a pin that "
                + "loses to the deployment is not a pin");
    }

    [Fact]
    public async Task The_deployment_s_namespace_is_used_when_nothing_is_pinned()
    {
        SeedApp(ourTenant, "Records", deploymentNs: "records");

        (await delivery.GetTenantNamespacesOnClusterAsync(ourTenant, cluster))
            .Should().Equal("records");
    }

    /// <summary>
    /// A blank pin is not a pin. The column is nullable and an empty string is reachable through
    /// it, and treating "" as an answer would put an unnamed namespace into a selector.
    /// </summary>
    [Fact]
    public async Task A_blank_lock_falls_back_rather_than_resolving_to_nothing()
    {
        SeedApp(ourTenant, "Records", deploymentNs: "records", lockedNs: "   ");

        (await delivery.GetTenantNamespacesOnClusterAsync(ourTenant, cluster))
            .Should().Equal("records");
    }

    [Fact]
    public async Task Another_tenant_s_apps_are_not_included()
    {
        SeedApp(ourTenant, "Ours", deploymentNs: "ours");
        SeedApp(theirTenant, "Theirs", deploymentNs: "theirs");

        (await delivery.GetTenantNamespacesOnClusterAsync(ourTenant, cluster))
            .Should().Equal(["ours"],
                "a trust store distributed into another tenant's namespace hands them our CA");

        (await delivery.GetTenantNamespacesOnClusterAsync(theirTenant, cluster))
            .Should().Equal("theirs");
    }

    [Fact]
    public async Task Only_the_named_cluster_counts()
    {
        SeedApp(ourTenant, "Here", deploymentNs: "here");
        SeedApp(ourTenant, "Elsewhere", deploymentNs: "elsewhere", onCluster: otherCluster);

        (await delivery.GetTenantNamespacesOnClusterAsync(ourTenant, cluster))
            .Should().Equal("here");
    }

    /// <summary>
    /// Distinct and ordered. Two apps sharing a namespace must not name it twice, and the order
    /// has to be stable because the list goes into a generated manifest — an unstable one reads
    /// as drift in a resource nobody touched.
    /// </summary>
    [Fact]
    public async Task The_list_is_distinct_and_ordered()
    {
        SeedApp(ourTenant, "Zebra", deploymentNs: "shared");
        SeedApp(ourTenant, "Alpha", deploymentNs: "shared");
        SeedApp(ourTenant, "Beta", deploymentNs: "beta");

        (await delivery.GetTenantNamespacesOnClusterAsync(ourTenant, cluster))
            .Should().Equal("beta", "shared");
    }

    [Fact]
    public async Task A_cluster_with_nothing_on_it_resolves_to_an_empty_list()
    {
        SeedApp(ourTenant, "Records", deploymentNs: "records");

        (await delivery.GetTenantNamespacesOnClusterAsync(ourTenant, otherCluster))
            .Should().BeEmpty();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
