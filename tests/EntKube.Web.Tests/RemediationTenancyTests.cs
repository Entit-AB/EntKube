using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Who may remediate a cluster.
///
/// <para><c>RemediationService.ExecuteAsync</c> deletes pods and restarts Deployments,
/// StatefulSets and DaemonSets, and it loaded its cluster with
/// <c>FirstOrDefaultAsync(c =&gt; c.Id == clusterId)</c> — no tenant predicate anywhere in the
/// file — so a cluster id was the whole authorization for the most destructive verb in the
/// product. Its one caller is the alerts page, which has always had a tenant.</para>
/// </summary>
public class RemediationTenancyTests : IDisposable
{
    private static readonly byte[] RootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb = new(RootKey);
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly Guid ourTenant = Guid.NewGuid();
    private readonly Guid theirTenant = Guid.NewGuid();
    private Guid ourCluster, theirCluster;

    public RemediationTenancyTests()
    {
        VaultService vault = testDb.CreateVaultService();
        using ApplicationDbContext db = testDb.CreateContext();

        db.Tenants.Add(new Tenant { Id = ourTenant, Name = "ours", Slug = $"ours-{ourTenant:N}" });
        db.Tenants.Add(new Tenant { Id = theirTenant, Name = "theirs", Slug = $"theirs-{theirTenant:N}" });
        ourCluster = Seed(db, ourTenant, "ours");
        theirCluster = Seed(db, theirTenant, "theirs");
        db.SaveChanges();

        // Points at a port nothing listens on, so the SDK call the owner's case reaches fails
        // immediately with a connection error instead of waiting out a DNS/TLS timeout. The test
        // below needs that call ATTEMPTED to prove the guard let it through.
        testDb.SeedKubeconfigAsync(vault, ourTenant, ourCluster,
            TestKubeconfig.Valid.Replace("https://k8s.example.com", "https://127.0.0.1:1"))
            .GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, theirTenant, theirCluster, TestKubeconfig.Valid).GetAwaiter().GetResult();
    }

    private static Guid Seed(ApplicationDbContext db, Guid tenantId, string label)
    {
        Guid environmentId = Guid.NewGuid(), clusterId = Guid.NewGuid();

        db.Environments.Add(new Data.Environment { Id = environmentId, TenantId = tenantId, Name = label });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = $"{label}-cluster", ApiServerUrl = $"https://{label}.example",
        });

        return clusterId;
    }

    public void Dispose()
    {
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private RemediationService Build()
        => new(new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            new AuditService(testDb.Factory, new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory)),
            NullLogger<RemediationService>.Instance);

    private static AlertRemediation DeletePod() => new(
        "Delete the crash-looping pod", RemediationActionKind.DeletePod,
        "Pod", "billing-api-7d9f", "billing");

    [Fact]
    public async Task Another_tenants_cluster_is_not_remediated()
    {
        RemediationService sut = Build();

        KubernetesOperationResult theirs = await sut.ExecuteAsync(ourTenant, theirCluster, DeletePod());
        KubernetesOperationResult ours = await sut.ExecuteAsync(theirTenant, ourCluster, DeletePod());

        theirs.IsSuccess.Should().BeFalse();
        theirs.Error.Should().Contain("not this tenant's");
        ours.IsSuccess.Should().BeFalse();
        ours.Error.Should().Contain("not this tenant's");
    }

    /// <summary>
    /// The owner gets <em>past</em> the guard — otherwise the test above would pass against a
    /// method that refuses everyone.
    ///
    /// <para>Proving that needs the next step to be attempted, and the next step is a live API
    /// call, so this cluster's kubeconfig points at a closed port: the call fails on the
    /// connection rather than on the authorization, and the two are told apart by the message.
    /// ⚠️ A cancelled token does <em>not</em> work here — it aborts inside the seam's own cluster
    /// lookup, before the guard is reached, so the method throws instead of answering.</para>
    /// </summary>
    [Fact]
    public async Task The_owner_passes_the_tenant_check()
    {
        KubernetesOperationResult result =
            await Build().ExecuteAsync(ourTenant, ourCluster, DeletePod(), "alice");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotContain("not this tenant's",
            "the cluster is this tenant's, so the failure must come from the unreachable API "
            + "server rather than from the authorization check");
    }

    /// <summary>
    /// A cluster of this tenant's with no stored kubeconfig is the same null from the seam, and the
    /// message says both so neither reason is asserted wrongly — the mistake this seam has had to
    /// correct in four other services.
    /// </summary>
    [Fact]
    public async Task A_cluster_with_no_kubeconfig_names_both_reasons()
    {
        Guid bare;
        using (ApplicationDbContext db = testDb.CreateContext())
        {
            bare = Seed(db, ourTenant, "bare");
            db.SaveChanges();
        }

        KubernetesOperationResult result = await Build().ExecuteAsync(ourTenant, bare, DeletePod());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not this tenant's").And.Contain("no kubeconfig");
    }
}
