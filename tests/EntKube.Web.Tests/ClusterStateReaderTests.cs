using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The judgement that turns replica counts into "is anybody woken up". Two mistakes matter here
/// and neither is loud: calling a mid-rollout cluster degraded trains people to ignore the signal,
/// and calling a cluster with no ready control plane healthy means nobody hears about the one
/// failure that takes everything with it.
/// </summary>
public class ClusterStateReaderTests
{
    private static PoolObservation Pool(
        string name = "general", int desired = 3, int current = 3, int ready = 3, int updated = 3) =>
        new(name, desired, current, ready, updated, "v1.31.4");

    [Fact]
    public void A_cluster_that_matches_its_spec_is_healthy()
    {
        ClusterObservation observation = ClusterStateReader.Judge(3, 3, "v1.31.4", false, [Pool()], []);

        observation.Health.Should().Be(ClusterHealth.Healthy);
        observation.Summary.Should().BeNull("there is nothing to say about a cluster that is fine");
    }

    [Fact]
    public void No_ready_control_plane_outranks_everything_else()
    {
        // Whatever else is true, nothing about this cluster works — and it must not be reported as
        // merely progressing because a rollout also happens to be in flight.
        ClusterObservation observation = ClusterStateReader.Judge(3, 0, "v1.31.4", true, [Pool()], []);

        observation.Health.Should().Be(ClusterHealth.Degraded);
        observation.Summary.Should().Contain("No control-plane node is ready");
    }

    [Fact]
    public void A_failed_machine_is_degraded_rather_than_progressing()
    {
        ClusterObservation observation = ClusterStateReader.Judge(3, 3, "v1.31.4", false, [Pool()], ["prod-eu-1-general-abc"]);

        observation.Health.Should().Be(ClusterHealth.Degraded);
        observation.Summary.Should().Contain("prod-eu-1-general-abc");
    }

    [Fact]
    public void Several_failed_machines_are_counted_rather_than_listed()
    {
        ClusterObservation observation = ClusterStateReader.Judge(
            3, 3, "v1.31.4", false, [Pool()], ["a", "b", "c"]);

        observation.Summary.Should().Be("3 machines have failed.");
    }

    [Fact]
    public void A_control_plane_short_of_its_count_is_progressing_not_broken()
    {
        // Two of three ready is a cluster replacing a node, which is the health check working
        // rather than a fault. Reporting it as degraded is how alerts get ignored.
        ClusterObservation observation = ClusterStateReader.Judge(3, 2, "v1.31.4", false, [Pool()], []);

        observation.Health.Should().Be(ClusterHealth.Progressing);
        observation.Summary.Should().Contain("2 of 3");
    }

    [Fact]
    public void A_rollout_is_progressing()
    {
        ClusterObservation observation = ClusterStateReader.Judge(3, 3, "v1.31.4", true, [Pool()], []);

        observation.Health.Should().Be(ClusterHealth.Progressing);
        observation.Summary.Should().Contain("rollout");
    }

    [Fact]
    public void A_pool_still_coming_up_is_progressing_and_says_which()
    {
        ClusterObservation observation = ClusterStateReader.Judge(
            3, 3, "v1.31.4", false, [Pool(), Pool("memory", desired: 4, current: 2, ready: 2, updated: 2)], []);

        observation.Health.Should().Be(ClusterHealth.Progressing);
        observation.Summary.Should().Contain("memory");
        observation.Summary.Should().Contain("2 of 4");
    }

    [Fact]
    public void A_settled_pool_is_one_where_every_count_agrees()
    {
        Pool().IsSettled.Should().BeTrue();
        // Ready but not updated is a pool mid-replacement: the old machines are serving while the
        // new ones come up, and treating that as settled would let an upgrade look finished early.
        Pool(updated: 1).IsSettled.Should().BeFalse();
    }

    [Fact]
    public void An_unreachable_cluster_says_so_rather_than_reporting_zero_of_everything()
    {
        // The distinction the whole reader turns on: a cluster we cannot reach is not a cluster
        // with nothing in it, and conflating them is how a reconciler acts on an outage in its own
        // network path.
        ClusterObservation observation = ClusterObservation.Unreachable("connection refused");

        observation.Health.Should().Be(ClusterHealth.Unreachable);
        observation.ControlPlaneDesired.Should().Be(0);
        observation.Summary.Should().Contain("connection refused");
    }

    [Fact]
    public void An_observation_survives_being_stored_and_read_back()
    {
        ClusterObservation original = ClusterStateReader.Judge(
            3, 2, "v1.31.4", true, [Pool(), Pool("memory", 4, 4, 4, 4)], ["broken-machine"]);

        ClusterObservation? restored = ClusterObservation.FromJson(original.ToJson());

        restored.Should().NotBeNull();
        restored!.Health.Should().Be(original.Health);
        restored.ControlPlaneVersion.Should().Be("v1.31.4");
        restored.Pools.Should().HaveCount(2);
        restored.FailedMachines.Should().ContainSingle().Which.Should().Be("broken-machine");
    }

    [Fact]
    public void Unreadable_stored_state_is_null_rather_than_an_exception()
    {
        // It is displayed, not depended on. A row written by an older version should degrade to
        // "nothing observed yet", not take a page down.
        ClusterObservation.FromJson("{not json").Should().BeNull();
        ClusterObservation.FromJson(null).Should().BeNull();
        ClusterObservation.FromJson("").Should().BeNull();
    }
}
