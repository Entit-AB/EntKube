using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The guards that stand between an operator and the expensive mistakes: a second rollout started
/// on top of one already running, and a version jump kubeadm will refuse halfway through. Both are
/// mistakes whose cost is paid by machines being replaced, so they are decided here — before
/// anything is applied — rather than discovered by the cluster.
/// </summary>
public class ClusterUpgradeRulesTests
{
    private static ClusterObservation Healthy(string version = "v1.31.4") =>
        ClusterStateReader.Judge(3, 3, version, false, [new PoolObservation("general", 3, 3, 3, 3, version)], []);

    private static ClusterObservation Rolling() =>
        ClusterStateReader.Judge(3, 3, "v1.31.4", true, [new PoolObservation("general", 3, 3, 3, 3, "v1.31.4")], []);

    private static ClusterObservation Degraded() =>
        ClusterStateReader.Judge(3, 3, "v1.31.4", false, [new PoolObservation("general", 3, 3, 3, 3, "v1.31.4")], ["dead-node"]);

    // ── When a cluster may be changed ──

    [Fact]
    public void A_healthy_cluster_accepts_anything()
    {
        foreach (ClusterOperation operation in Enum.GetValues<ClusterOperation>())
        {
            ClusterUpgradeRules.CanStart(Healthy(), operation).Allowed
                .Should().BeTrue($"{operation} should be allowed on a healthy cluster");
        }
    }

    [Fact]
    public void A_rollout_in_flight_blocks_a_second_one()
    {
        // CAPI accepts the second happily, and the result replaces more machines at once than
        // either rollout's surge budget intended.
        OperationVerdict verdict = ClusterUpgradeRules.CanStart(Rolling(), ClusterOperation.UpgradeControlPlane);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("already in flight");
    }

    [Fact]
    public void Scaling_and_adding_are_still_allowed_mid_rollout()
    {
        // They add machines rather than replacing them. Refusing would mean a cluster under load
        // could not be grown while it happened to be updating, which is when growth is wanted.
        ClusterUpgradeRules.CanStart(Rolling(), ClusterOperation.ScalePool).Allowed.Should().BeTrue();
        ClusterUpgradeRules.CanStart(Rolling(), ClusterOperation.AddPool).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_degraded_cluster_refuses_anything_that_replaces_machines()
    {
        OperationVerdict verdict = ClusterUpgradeRules.CanStart(Degraded(), ClusterOperation.ReshapePool);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("degraded");
    }

    [Fact]
    public void A_degraded_cluster_can_still_be_grown()
    {
        // Adding capacity is often the response to a fault, not something to withhold until it is
        // fixed.
        ClusterUpgradeRules.CanStart(Degraded(), ClusterOperation.ScalePool).Allowed.Should().BeTrue();
    }

    [Fact]
    public void An_unreachable_cluster_refuses_everything()
    {
        // Including the additive operations: there is no way to tell what the change would do, and
        // "it seemed to work" against a cluster we cannot see is not a report worth making.
        ClusterObservation unreachable = ClusterObservation.Unreachable("connection refused");

        ClusterUpgradeRules.CanStart(unreachable, ClusterOperation.ScalePool).Allowed.Should().BeFalse();
        ClusterUpgradeRules.CanStart(unreachable, ClusterOperation.AddPool).Allowed.Should().BeFalse();
    }

    // ── Which version moves are allowed ──

    [Fact]
    public void One_minor_forward_is_the_supported_move()
    {
        ClusterUpgradeRules.CanUpgrade("v1.31.4", "v1.32.0").Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_patch_bump_is_allowed()
    {
        ClusterUpgradeRules.CanUpgrade("v1.31.4", "v1.31.7").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Two_minors_at_once_is_refused_with_the_next_step_named()
    {
        // kubeadm refuses this too, but only after the first control-plane machine has already
        // been replaced — leaving a cluster sitting between two versions it was never meant to.
        OperationVerdict verdict = ClusterUpgradeRules.CanUpgrade("v1.30.5", "v1.32.0");

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("v1.31");
    }

    [Fact]
    public void Downgrades_are_refused_and_say_what_to_do_instead()
    {
        OperationVerdict verdict = ClusterUpgradeRules.CanUpgrade("v1.31.4", "v1.30.5");

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("backup");
    }

    [Fact]
    public void Upgrading_to_the_version_already_running_is_refused_rather_than_rolling_for_nothing()
    {
        ClusterUpgradeRules.CanUpgrade("v1.31.4", "1.31.4").Allowed.Should().BeFalse();
    }

    [Fact]
    public void An_unparseable_version_is_refused_rather_than_guessed_at()
    {
        ClusterUpgradeRules.CanUpgrade("v1.31.4", "latest").Allowed.Should().BeFalse();
    }

    // ── Workers against the control plane ──

    [Fact]
    public void A_pool_may_follow_the_control_plane()
    {
        ClusterUpgradeRules.CanUpgradePool("v1.32.0", "v1.31.4", "v1.32.0").Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_pool_may_not_lead_the_control_plane()
    {
        // kubelet ahead of the API server is unsupported, and shows up as nodes that will not
        // register — a long way from the upgrade that caused it.
        OperationVerdict verdict = ClusterUpgradeRules.CanUpgradePool("v1.31.4", "v1.31.4", "v1.32.0");

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("ahead of the control plane");
    }

    [Fact]
    public void A_pool_lagging_by_one_minor_may_stay_where_it_is_and_still_be_patched()
    {
        ClusterUpgradeRules.CanUpgradePool("v1.32.0", "v1.31.4", "v1.31.9").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Pools_are_upgraded_furthest_behind_first()
    {
        List<PoolObservation> pools =
        [
            new("general", 3, 3, 3, 3, "v1.31.4"),
            new("memory", 2, 2, 2, 2, "v1.30.5"),
            new("gpu", 1, 1, 1, 1, "v1.32.0")
        ];

        ClusterUpgradeRules.UpgradeOrder(pools).Select(p => p.Name)
            .Should().ContainInOrder("memory", "general", "gpu");
    }
}
