using EntKube.Web.Services.Upgrades;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the rule that decides whether a Helm release can be rolled in place.
///
/// The failure being guarded against: a Deployment with the default RollingUpdate
/// strategy and a ReadWriteOnce claim, where Kubernetes starts the replacement pod
/// before deleting the old one and the new pod can never attach the volume. Every
/// case here is about not over-reporting (which would impose needless downtime) or
/// under-reporting (which leaves the upgrade to hang).
/// </summary>
public class ReleaseVolumeGuardTests
{
    private const string Release = "grafana";
    private const string Namespace = "monitoring";

    private static string Deployments(params string[] items) =>
        "{\"items\":[" + string.Join(",", items) + "]}";

    /// <summary>A Deployment owned by the release, carrying the fields the rule reads.</summary>
    private static string Deployment(
        string name = "grafana",
        int? replicas = 1,
        string? strategyType = null,
        string? maxSurge = null,
        string? claim = "grafana-data",
        string? releaseName = Release)
    {
        string replicaField = replicas is null ? "" : "\"replicas\":" + replicas + ",";

        string strategy = strategyType is null && maxSurge is null
            ? ""
            : "\"strategy\":{\"type\":\"" + (strategyType ?? "RollingUpdate") + "\""
              + (maxSurge is null ? "" : ",\"rollingUpdate\":{\"maxSurge\":" + maxSurge + "}")
              + "},";

        string volumes = claim is null
            ? "\"volumes\":[]"
            : "\"volumes\":[{\"name\":\"storage\",\"persistentVolumeClaim\":{\"claimName\":\"" + claim + "\"}}]";

        string ownership = releaseName is null
            ? ""
            : "\"annotations\":{\"meta.helm.sh/release-name\":\"" + releaseName
              + "\",\"meta.helm.sh/release-namespace\":\"" + Namespace + "\"},";

        return "{\"metadata\":{\"name\":\"" + name + "\"," + ownership + "\"labels\":{}},"
             + "\"spec\":{" + replicaField + strategy
             + "\"template\":{\"spec\":{" + volumes + "}}}}";
    }

    private static Dictionary<string, string[]> Claims(params (string Name, string[] Modes)[] claims) =>
        claims.ToDictionary(c => c.Name, c => c.Modes, StringComparer.Ordinal);

    private static readonly Dictionary<string, string[]> RwoClaim =
        Claims(("grafana-data", ["ReadWriteOnce"]));

    [Fact]
    public void Blocks_a_rolling_deployment_holding_a_read_write_once_claim()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment()), RwoClaim, Release, Namespace);

        result.RequiresScaleDown.Should().BeTrue();
        result.Blocked.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Name = "grafana",
                Namespace,
                Replicas = 1,
                Claims = new[] { "grafana-data" },
            }, o => o.ExcludingMissingMembers());
        result.ReplicasAffected.Should().Be(1);
    }

    [Fact]
    public void Treats_a_missing_replicas_field_as_one_replica()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment(replicas: null)), RwoClaim, Release, Namespace);

        result.Blocked.Should().ContainSingle().Which.Replicas.Should().Be(1);
    }

    [Fact]
    public void Allows_a_read_write_many_claim_to_roll_in_place()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment()),
            Claims(("grafana-data", ["ReadWriteOnce", "ReadWriteMany"])),
            Release, Namespace);

        result.RequiresScaleDown.Should().BeFalse();
    }

    [Fact]
    public void Allows_a_deployment_that_already_recreates()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment(strategyType: "Recreate")), RwoClaim, Release, Namespace);

        result.RequiresScaleDown.Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("\"0%\"")]
    public void Allows_a_rolling_update_that_never_surges(string maxSurge)
    {
        // maxSurge 0 deletes the old pod before creating the new one, so the volume is free.
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment(maxSurge: maxSurge)), RwoClaim, Release, Namespace);

        result.RequiresScaleDown.Should().BeFalse();
    }

    [Fact]
    public void Blocks_a_rolling_update_that_does_surge()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment(maxSurge: "\"25%\"")), RwoClaim, Release, Namespace);

        result.RequiresScaleDown.Should().BeTrue();
    }

    [Fact]
    public void Ignores_a_deployment_already_scaled_to_zero()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment(replicas: 0)), RwoClaim, Release, Namespace);

        result.RequiresScaleDown.Should().BeFalse();
    }

    [Fact]
    public void Ignores_a_deployment_with_no_persistent_volume()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment(claim: null)), RwoClaim, Release, Namespace);

        result.RequiresScaleDown.Should().BeFalse();
    }

    [Fact]
    public void Ignores_a_deployment_belonging_to_a_different_release()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment(releaseName: "loki")), RwoClaim, Release, Namespace);

        result.Blocked.Should().BeEmpty();
    }

    [Fact]
    public void Falls_back_to_the_instance_label_when_helm_annotations_are_absent()
    {
        string deployment =
            "{\"metadata\":{\"name\":\"grafana\",\"labels\":{\"app.kubernetes.io/instance\":\"" + Release + "\"}},"
            + "\"spec\":{\"replicas\":1,\"template\":{\"spec\":{\"volumes\":["
            + "{\"name\":\"storage\",\"persistentVolumeClaim\":{\"claimName\":\"grafana-data\"}}]}}}}";

        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(deployment), RwoClaim, Release, Namespace);

        result.Blocked.Should().ContainSingle().Which.Name.Should().Be("grafana");
    }

    [Fact]
    public void Warns_rather_than_assuming_safety_when_a_claim_cannot_be_seen()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(Deployment()), Claims(), Release, Namespace);

        result.RequiresScaleDown.Should().BeFalse();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("grafana-data").And.Contain("unknown");
    }

    [Fact]
    public void Reports_every_blocked_workload_in_the_release()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate(
            Deployments(
                Deployment(name: "grafana", claim: "grafana-data", replicas: 1),
                Deployment(name: "grafana-image-renderer", claim: "renderer-cache", replicas: 2)),
            Claims(("grafana-data", ["ReadWriteOnce"]), ("renderer-cache", ["ReadWriteOnce"])),
            Release, Namespace);

        result.Blocked.Select(b => b.Name).Should()
            .Equal("grafana", "grafana-image-renderer");
        result.ReplicasAffected.Should().Be(3);
    }

    [Fact]
    public void Prefers_the_bound_volumes_access_modes_over_the_request()
    {
        // A claim that asked for RWX but bound to an RWO volume is still exclusive.
        string pvcJson = """
            {"items":[{"metadata":{"name":"grafana-data"},
                       "spec":{"accessModes":["ReadWriteMany"]},
                       "status":{"accessModes":["ReadWriteOnce"]}}]}
            """;

        Dictionary<string, string[]> modes = ReleaseVolumeGuard.ParseClaimAccessModes(pvcJson);

        modes["grafana-data"].Should().Equal("ReadWriteOnce");
    }

    [Fact]
    public void Reads_the_requested_access_modes_when_the_claim_is_not_yet_bound()
    {
        string pvcJson = """
            {"items":[{"metadata":{"name":"grafana-data"},"spec":{"accessModes":["ReadWriteOnce"]}}]}
            """;

        ReleaseVolumeGuard.ParseClaimAccessModes(pvcJson)["grafana-data"]
            .Should().Equal("ReadWriteOnce");
    }

    [Fact]
    public void Survives_unparseable_kubectl_output()
    {
        VolumePreflight result = ReleaseVolumeGuard.Evaluate("not json", RwoClaim, Release, Namespace);

        result.Blocked.Should().BeEmpty();
    }
}
