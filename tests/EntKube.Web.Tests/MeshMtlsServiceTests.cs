using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

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
}
