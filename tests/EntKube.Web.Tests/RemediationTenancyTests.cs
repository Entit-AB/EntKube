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

        // This used to point at a closed port, so the owner's case failed on the connection and
        // the two refusals could be told apart by their message. The remediation goes through the
        // seam now, which is mocked here, so the call the guard protects is asserted directly and
        // there is nothing left for an unreachable address to prove.
        testDb.SeedKubeconfigAsync(vault, ourTenant, ourCluster, TestKubeconfig.Valid)
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

    private RemediationService Build(
        EntKube.Web.Services.ClusterChanges.IClusterChangeGate? gate = null)
        => new(new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            gate ?? TestServices.NewGate(),
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
    /// <para>This used to prove it indirectly: the owner's kubeconfig pointed at a closed port, so
    /// the failure was a connection error rather than an authorization one and the messages told
    /// them apart. Since the remediation goes through <c>IClusterClient</c> — mocked here — the
    /// step the guard protects can be asserted for what it is: the seam was asked to delete
    /// <em>that</em> pod in <em>that</em> namespace. A weaker version of this test would assert
    /// only that the call succeeded, which a method that deleted the wrong pod would also
    /// pass.</para>
    /// </summary>
    [Fact]
    public async Task The_owner_passes_the_tenant_check()
    {
        KubernetesOperationResult result =
            await Build().ExecuteAsync(ourTenant, ourCluster, DeletePod(), "alice");

        result.IsSuccess.Should().BeTrue(result.Error);

        k8s.Verify(f => f.DeleteManifestAsync(
                "pod", "billing-api-7d9f", "billing",
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the owner's remediation must reach the seam, and reach it for the named resource");
    }

    /// <summary>
    /// The remediation declares itself a consequence of the operator's own click, and declares it
    /// <em>before</em> the change reaches the cluster.
    ///
    /// <para>⚠️ <b>This is the substance of the change that added it.</b> Remediation used to be
    /// exempt from the gate by not calling it at all, so <c>IClusterChangeRecorder</c> held no
    /// outcome for a pod deletion — invisible rather than ungated, which is the distinction
    /// docs/decomposition.md §5.4.1 exists to kill. The <c>AuditService</c> row it already wrote is
    /// a different trail: it records that a human asked for a remediation, not that a cluster was
    /// changed.</para>
    ///
    /// <para>The ordering is asserted rather than assumed, because a declaration raised
    /// <em>after</em> the write would produce the same record and prevent nothing — and the gate
    /// reads its scope at the moment <c>AcknowledgeAsync</c> is called, so "somewhere in the
    /// method" is not good enough.</para>
    ///
    /// <para><c>DeclareUnattended</c> must not be what it reaches for, which is why that is
    /// asserted too: §5.4's fourth bullet proposed an <c>Ungated("remediation")</c> marker and
    /// unattended is the primitive that grew into that slot, but it only takes effect in a scope
    /// with <b>no sink</b>. This flow's one caller is a Blazor circuit, and
    /// <c>ClusterChangeAckDialog</c> is mounted in <c>MainLayout</c>, so there is always a sink and
    /// the operator would have been asked to acknowledge every remediation twice.</para>
    /// </summary>
    [Fact]
    public async Task The_remediation_declares_a_consequence_before_it_changes_anything()
    {
        Mock<EntKube.Web.Services.ClusterChanges.IClusterChangeGate> gate = new();
        bool declared = false, declaredBeforeTheChange = false;

        gate.Setup(g => g.DeclareConsequence(It.IsAny<string>()))
            .Returns(() => { declared = true; return new Nothing(); });

        k8s.Setup(f => f.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => declaredBeforeTheChange = declared)
            .Returns(Task.CompletedTask);

        KubernetesOperationResult result =
            await Build(gate.Object).ExecuteAsync(ourTenant, ourCluster, DeletePod(), "alice");

        result.IsSuccess.Should().BeTrue(result.Error);

        gate.Verify(g => g.DeclareConsequence(It.Is<string>(r =>
                r.Contains("DeletePod", StringComparison.Ordinal)
                && r.Contains("alerts page", StringComparison.Ordinal))),
            Times.Once,
            "the recorded reason has to say which remediation and on whose authority, or the "
            + "AppliedAsConsequence row says only that something was approved by someone");

        declaredBeforeTheChange.Should().BeTrue(
            "a consequence declared after the write records the same row and prevents nothing");

        gate.Verify(g => g.DeclareUnattended(It.IsAny<string>()), Times.Never,
            "unattended only takes effect without a sink, and the alerts page always has one");
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
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
