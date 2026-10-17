using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for service-to-service mTLS inside the mesh.
///
/// STRICT is the one setting in this feature that can take a working namespace offline: it makes
/// every workload without a mesh identity unreachable, and the platform's own default posture is
/// PERMISSIVE precisely because sidecar-less pods are supported. These tests pin the readiness
/// gate, and the rule that the gateway's side of the hop moves with the pod's.
/// </summary>
public class MeshMtlsServiceTests
{
    // ──────── Readiness gate ────────

    [Fact]
    public void EvaluateReadiness_WithEveryPodSidecarInjected_IsReady()
    {
        MeshReadiness readiness = MeshMtlsService.EvaluateReadiness(
            isAmbient: false,
            [new MeshPodStatus("api-1", true), new MeshPodStatus("worker-1", true)]);

        readiness.IsReady.Should().BeTrue();
        readiness.TotalPods.Should().Be(2);
        readiness.PodsOutsideMesh.Should().BeEmpty();
    }

    [Fact]
    public void EvaluateReadiness_NamesThePodsThatWouldBreak()
    {
        MeshReadiness readiness = MeshMtlsService.EvaluateReadiness(
            isAmbient: false,
            [new MeshPodStatus("api-1", true), new MeshPodStatus("legacy-batch-7", false)]);

        readiness.IsReady.Should().BeFalse();
        readiness.PodsOutsideMesh.Should().ContainSingle().Which.Should().Be("legacy-batch-7");
    }

    [Fact]
    public void EvaluateReadiness_UnderAmbient_CountsSidecarlessPodsAsMeshed()
    {
        // Ambient gives every pod an identity via ztunnel — no sidecar container ever appears, so
        // treating its absence as "outside the mesh" would block STRICT on exactly the setup that
        // supports it best.
        MeshReadiness readiness = MeshMtlsService.EvaluateReadiness(
            isAmbient: true,
            [new MeshPodStatus("api-1", false), new MeshPodStatus("worker-1", false)]);

        readiness.IsReady.Should().BeTrue();
        readiness.IsAmbient.Should().BeTrue();
    }

    [Fact]
    public void EvaluateReadiness_WithNoPods_IsReady()
    {
        // An empty namespace has nothing to break.
        MeshMtlsService.EvaluateReadiness(false, []).IsReady.Should().BeTrue();
    }

    // ──────── PeerAuthentication rendering ────────

    [Theory]
    [InlineData(MeshMtlsMode.Permissive, "PERMISSIVE")]
    [InlineData(MeshMtlsMode.Strict, "STRICT")]
    public void BuildPeerAuthenticationYaml_RendersTheMode(MeshMtlsMode mode, string expected)
    {
        string yaml = MeshMtlsService.BuildPeerAuthenticationYaml("acme-prod", mode);

        yaml.Should().Contain("kind: PeerAuthentication");
        yaml.Should().Contain("namespace: acme-prod");
        yaml.Should().Contain($"mode: {expected}");
    }

    [Fact]
    public void BuildPeerAuthenticationYaml_KeepsOneResourceNameAcrossModes()
    {
        // Two namespace-wide PeerAuthentications leave the winner up to Istio, so switching posture
        // must overwrite the existing object rather than add a second one beside it.
        string permissive = MeshMtlsService.BuildPeerAuthenticationYaml("acme-prod", MeshMtlsMode.Permissive);
        string strict = MeshMtlsService.BuildPeerAuthenticationYaml("acme-prod", MeshMtlsMode.Strict);

        permissive.Should().Contain($"name: {MeshMtlsService.PeerAuthenticationName}");
        strict.Should().Contain($"name: {MeshMtlsService.PeerAuthenticationName}");
    }

    // ──────── The gateway's side of the hop ────────

    [Theory]
    [InlineData(MeshMtlsMode.Permissive, "DISABLE")]
    [InlineData(MeshMtlsMode.Strict, "ISTIO_MUTUAL")]
    public void BackendTlsMode_FollowsTheNamespacePosture(MeshMtlsMode mode, string expected)
    {
        MeshMtlsService.BackendTlsMode(mode).Should().Be(expected);
    }

    [Fact]
    public void BackendDestinationRule_UnderStrict_StopsSendingPlaintextToThePod()
    {
        // The DISABLE rule is what makes a sidecar-less backend reachable; against a STRICT
        // namespace it is what makes it unreachable.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "api", "acme-prod", "istio-system", [], alwaysEmit: true,
            serviceWideTlsMode: MeshMtlsService.BackendTlsMode(MeshMtlsMode.Strict));

        yaml.Should().Contain("mode: ISTIO_MUTUAL");
        yaml.Should().NotContain("mode: DISABLE");
    }

    [Fact]
    public void BackendDestinationRule_DefaultsToDisable_SoExistingClustersAreUntouched()
    {
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "api", "acme-prod", "istio-system", [], alwaysEmit: true);

        yaml.Should().Contain("mode: DISABLE");
    }

    [Fact]
    public void BackendDestinationRule_KeepsItsNameAcrossPostures()
    {
        // Same reasoning as the PeerAuthentication: a renamed rule leaves the old one in the
        // cluster still forcing plaintext into a namespace that now refuses it.
        string permissive = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "api", "acme-prod", "istio-system", [], alwaysEmit: true);
        string strict = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "api", "acme-prod", "istio-system", [], alwaysEmit: true, serviceWideTlsMode: "ISTIO_MUTUAL");

        permissive.Should().Contain("name: entkube-disable-mtls-api");
        strict.Should().Contain("name: entkube-disable-mtls-api");
    }

    [Fact]
    public void BackendDestinationRule_UnderStrict_StillOverridesTlsServingPorts()
    {
        // A backend that terminates TLS itself is orthogonal to the mesh posture — the port-level
        // override must survive the switch, or the TLS port regresses to the bug it was added for.
        string yaml = ExternalRouteService.GenerateBackendDestinationRuleYaml(
            "keycloakx", "acme-prod", "istio-system",
            [new KubeServicePort("https-8443", 8443, "TCP")],
            alwaysEmit: true, serviceWideTlsMode: "ISTIO_MUTUAL");

        yaml.Should().Contain("portLevelSettings");
        yaml.Should().Contain("number: 8443");
    }

    // ──────── Whose mesh ────────

    /// <summary>
    /// ⚠️ <c>SetModeAsync</c> took a tenant and used it only to <b>stamp</b> a new row. The
    /// cluster was never checked, and the policy it upserts was found by cluster id and namespace
    /// alone — so the else-branch overwrote whatever row matched, whoever owned it.
    ///
    /// <para>The mode is mTLS enforcement for a namespace, and there are only two: Permissive
    /// and Strict. This file's own summary says STRICT is the one setting that can take a working
    /// namespace offline; the reverse is just as bad and quieter — moving somebody else's
    /// namespace back to Permissive accepts plaintext again, and nothing appears to break.</para>
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_set_the_mtls_mode_of_a_namespace()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();

        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        TestDbContextFactory factory = new(connection);
        MeshMtlsService sut = new(factory, NullLogger<MeshMtlsService>.Instance);

        (Guid Tenant, Guid Cluster) Seed(string slug)
        {
            Guid tenant = Guid.NewGuid(), environment = Guid.NewGuid(), cluster = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenant, Name = slug, Slug = $"{slug}-{tenant:N}" });
            db.Set<Data.Environment>().Add(new Data.Environment
            {
                Id = environment, TenantId = tenant, Name = $"{slug}-prod",
            });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = cluster, TenantId = tenant, EnvironmentId = environment,
                Name = $"{slug}-cluster", ApiServerUrl = "https://k8s.example.com",
            });
            db.SaveChanges();
            return (tenant, cluster);
        }

        (Guid Tenant, Guid Cluster) us = Seed("us");
        (Guid Tenant, Guid Cluster) them = Seed("them");

        // Their namespace is STRICT, set by them.
        MeshMtlsPolicy theirs = await sut.SetModeAsync(
            them.Tenant, them.Cluster, "payments", MeshMtlsMode.Strict);
        theirs.Mode.Should().Be(MeshMtlsMode.Strict);

        // We cannot reach their cluster at all.
        Func<Task> downgrade = () => sut.SetModeAsync(
            us.Tenant, them.Cluster, "payments", MeshMtlsMode.Permissive);
        await downgrade.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext after = factory.CreateDbContext();
        after.Set<MeshMtlsPolicy>().Single(p => p.Id == theirs.Id)
            .Mode.Should().Be(MeshMtlsMode.Strict,
                "downgrading their mTLS would not look like a failure to them");

        // Nor can we read it, or mark it applied.
        (await sut.GetModeAsync(us.Tenant, them.Cluster, "payments"))
            .Should().Be(MeshMtlsMode.Permissive,
                "the miss default is Permissive, so an unscoped read would report their STRICT "
                + "namespace as unenforced");
        (await sut.GetModeAsync(them.Tenant, them.Cluster, "payments"))
            .Should().Be(MeshMtlsMode.Strict, "the owner's own read still answers");

        await sut.MarkAppliedAsync(us.Tenant, them.Cluster, "payments");
        using ApplicationDbContext unmarked = factory.CreateDbContext();
        unmarked.Set<MeshMtlsPolicy>().Single(p => p.Id == theirs.Id)
            .AppliedAt.Should().BeNull();

        // And our own policy on our own cluster is a separate row, not theirs rewritten.
        MeshMtlsPolicy ours = await sut.SetModeAsync(
            us.Tenant, us.Cluster, "payments", MeshMtlsMode.Permissive);
        ours.Id.Should().NotBe(theirs.Id);
        ours.TenantId.Should().Be(us.Tenant);
    }
}
