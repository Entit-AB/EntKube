using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading node names and addresses out of <c>kubectl get nodes -o json</c>.
///
/// <para><b>Why this is tested and the rest of the conversion is not.</b> Moving a call onto the
/// seam is supposed to move the transport and nothing else. This one could not: the seam has no
/// way to pass a jsonpath expression, and deliberately so, so the query became a JSON read and a
/// parse. That is a behaviour change, which is exactly the thing a conversion is not allowed to
/// smuggle in — so the new parse is a public static and these assert it directly.</para>
///
/// <para>The old version split kubectl's output on <c>|</c> and newlines. Two of the cases below
/// are what that could not handle.</para>
/// </summary>
public class NodeInventoryParseTests
{
    private static string Nodes(string body) => $"{{\"items\":[{body}]}}";

    private static string Node(string name, params (string Type, string Address)[] addresses)
    {
        string list = string.Join(",", addresses.Select(a =>
            $"{{\"type\":\"{a.Type}\",\"address\":\"{a.Address}\"}}"));

        return $"{{\"metadata\":{{\"name\":\"{name}\"}},\"status\":{{\"addresses\":[{list}]}}}}";
    }

    [Fact]
    public void A_node_yields_its_name_and_internal_address()
    {
        string json = Nodes(Node("worker-1", ("Hostname", "worker-1"), ("InternalIP", "10.0.0.7")));

        ClusterProvisioningService.ParseNodeInventory(json)
            .Should().BeEquivalentTo([("worker-1", "10.0.0.7")]);
    }

    /// <summary>
    /// ExternalIP must not be mistaken for the internal one. Recording a public address as a
    /// node's IP would send later SSH and inventory work to the wrong side of the network, and on
    /// a cloud VM both addresses are present.
    /// </summary>
    [Fact]
    public void Only_the_internal_address_is_taken()
    {
        string json = Nodes(Node("worker-1",
            ("ExternalIP", "203.0.113.9"), ("InternalIP", "10.0.0.7"), ("Hostname", "worker-1")));

        ClusterProvisioningService.ParseNodeInventory(json)
            .Single().InternalIp.Should().Be("10.0.0.7");
    }

    /// <summary>
    /// A node that exists but has no address yet is still inventory. Dropping it would hide the
    /// node an operator is looking at the list to find — one that joined and has not finished
    /// coming up.
    /// </summary>
    [Fact]
    public void A_node_with_no_internal_address_is_still_recorded()
    {
        string json = Nodes(Node("worker-pending", ("Hostname", "worker-pending")));

        ClusterProvisioningService.ParseNodeInventory(json)
            .Should().BeEquivalentTo([("worker-pending", (string?)null)]);
    }

    [Fact]
    public void A_node_with_no_status_at_all_is_still_recorded()
    {
        string json = "{\"items\":[{\"metadata\":{\"name\":\"worker-bare\"}}]}";

        ClusterProvisioningService.ParseNodeInventory(json)
            .Should().BeEquivalentTo([("worker-bare", (string?)null)]);
    }

    /// <summary>
    /// What the old string split could not do. A pipe in a node name is unusual but legal in the
    /// output format the previous version parsed, and it silently produced an inventory row named
    /// after the fragment before the pipe, with the rest read as an IP address.
    /// </summary>
    [Fact]
    public void A_name_containing_the_old_separator_survives()
    {
        string json = Nodes(Node("worker|odd", ("InternalIP", "10.0.0.9")));

        ClusterProvisioningService.ParseNodeInventory(json)
            .Should().BeEquivalentTo([("worker|odd", "10.0.0.9")]);
    }

    [Fact]
    public void Several_nodes_come_back_in_order()
    {
        string json = Nodes(
            Node("cp-1", ("InternalIP", "10.0.0.2")) + "," +
            Node("worker-1", ("InternalIP", "10.0.0.7")) + "," +
            Node("worker-2", ("InternalIP", "10.0.0.8")));

        ClusterProvisioningService.ParseNodeInventory(json).Select(n => n.NodeName)
            .Should().Equal("cp-1", "worker-1", "worker-2");
    }

    /// <summary>
    /// Inventory is best-effort — it runs after the cluster is already registered and provisioned,
    /// so a failure here must not fail a provision that otherwise succeeded. Each of these returns
    /// nothing rather than throwing, and the caller logs a skip.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":[{}]}")]
    [InlineData("{\"items\":[{\"metadata\":{}}]}")]
    [InlineData("{\"items\":[{\"metadata\":{\"name\":\"\"}}]}")]
    public void An_answer_it_cannot_read_yields_nothing_rather_than_throwing(string json)
    {
        ClusterProvisioningService.ParseNodeInventory(json).Should().BeEmpty();
    }
}
