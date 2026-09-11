using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Teardown's reporting, which is the part an operator reads after the machines are already gone.
/// The sweep deliberately does not delete what it finds: resources are matched by name, names get
/// reused, and deleting the wrong one is the single unrecoverable mistake in this whole feature.
/// </summary>
public class ClusterTeardownTests
{
    [Fact]
    public void A_clean_teardown_reports_nothing_left()
    {
        LeftoverResources.None.Any.Should().BeFalse();
        LeftoverResources.None.Describe().Should().Be("nothing");
    }

    [Fact]
    public void Leftovers_are_named_so_they_can_be_checked_before_being_removed()
    {
        // The operator has to be able to find them. A count alone means hunting through a project.
        LeftoverResources leftovers = new(
            Servers: ["prod-eu-1-general-abc123"],
            FloatingIps: [],
            LoadBalancers: ["prod-eu-1-kubeapi"],
            Volumes: []);

        leftovers.Any.Should().BeTrue();
        leftovers.Describe().Should().Contain("prod-eu-1-general-abc123");
        leftovers.Describe().Should().Contain("prod-eu-1-kubeapi");
    }

    [Fact]
    public void Each_kind_of_resource_is_counted_separately()
    {
        // Because they are cleaned up in different places and cost differently: a stray floating IP
        // is a few euros a month, a stray volume can be a few hundred.
        LeftoverResources leftovers = new(
            Servers: ["a", "b"],
            FloatingIps: ["c"],
            LoadBalancers: [],
            Volumes: ["d", "e", "f"]);

        string description = leftovers.Describe();

        description.Should().Contain("2 server(s)");
        description.Should().Contain("1 floating IP(s)");
        description.Should().Contain("3 volume(s)");
        description.Should().NotContain("load balancer");
    }
}
