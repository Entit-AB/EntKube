using EntKube.Agent;
using EntKube.Agents.Protocol;
using EntKube.Web.Services.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// What EntKube is willing to ask an agent to do, and what the agent is willing to take.
///
/// <para><b>The rule being held.</b> docs/decomposition.md §5.4.1: an in-cluster agent may read
/// and may propose; every mutation needs a human approver. The change gate cannot help here —
/// it is an in-process scoped service and the agent is a separate binary in the customer's
/// network, so by the time a frame is on the socket there is nothing left to gate. Sending the
/// lease <em>is</em> the dispatch, which puts the whole decision on this side of the wire.</para>
///
/// <para>So the rule is held three ways, none of which is a field the agent is asked to believe:
/// the effect enum cannot express a mutation, the registry decides what a name means, and the
/// agent binary has no apply path to reach. Each has a test below.</para>
/// </summary>
public class AgentCapabilityTests
{
    private const string AgentSource = "../../../../../src/EntKube.Agent";

    /// <summary>
    /// How many capabilities EntKube can ask for. Item 12 of docs/decomposition.md makes this 1
    /// by adding a read-only component scan.
    /// </summary>
    private const int RegisteredCapabilities = 0;

    // ──────── A mutating capability is inexpressible ────────

    [Fact]
    public void There_is_no_way_to_register_a_capability_that_changes_anything()
    {
        // Adding a member here means EntKube can now ask an agent to change a cluster, which needs
        // the durable approval queue §5.4 names as the prerequisite — a persisted change with its
        // diff, and an approval carrying who approved it and when. None of that exists. This
        // assertion is here so that decision cannot arrive as a one-line diff nobody read.
        Enum.GetValues<AgentCapabilityEffect>().Should().Equal(AgentCapabilityEffect.Read);
    }

    [Fact]
    public void Every_registered_capability_is_named_and_read_only()
    {
        AgentCapabilityRegistry.All.Should().HaveCount(RegisteredCapabilities);

        foreach (AgentCapability capability in AgentCapabilityRegistry.All)
        {
            AgentTaskFrames.IsValidCapabilityName(capability.Name).Should().BeTrue(
                $"'{capability.Name}' travels in a lease and must name what to do, never how");

            capability.Effect.Should().Be(AgentCapabilityEffect.Read);

            capability.What.Should().NotBeNullOrWhiteSpace(
                "the enumeration is only auditable if each row says what it actually runs");
        }
    }

    [Fact]
    public async Task A_capability_EntKube_has_not_registered_is_refused_before_anything_else_happens()
    {
        // The registry is the authorization. An agent advertising a name cannot bring it into
        // existence — it is a binary on a customer's box that EntKube neither builds nor upgrades.
        //
        // The null database factory is the assertion: this refusal has to come before any
        // connection is picked or any row is read, so a version of this check that reached further
        // would fail here with a NullReferenceException instead of passing.
        AgentRegistry registry = new(null!, NullLogger<AgentRegistry>.Instance);

        Func<Task> dispatch = () =>
            registry.DispatchAsync(Guid.NewGuid(), "component.scan", TimeSpan.FromMinutes(1));

        (await dispatch.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not a capability EntKube knows how to ask for*");
    }

    // ──────── The agent's own answer ────────

    [Fact]
    public void The_agent_declares_this_build_and_claims_nothing_it_cannot_do()
    {
        CapabilityPayload declared = AgentCapabilities.Declare();

        declared.ProtocolVersion.Should().Be(AgentProtocol.ProtocolVersion);
        declared.Capabilities.Should().BeEmpty(
            "claiming a capability that is not wired makes the server dispatch work that then fails");
        declared.AgentVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_agent_refuses_every_lease_and_says_why()
    {
        LeaseAckPayload ack = AgentCapabilities.Decide(
            new LeasePayload(Guid.NewGuid(), "component.scan", 30_000));

        ack.Accepted.Should().BeFalse();
        ack.Reason.Should().Be("This agent implements no capabilities, so it cannot run 'component.scan'.");
    }

    [Theory]
    [InlineData("kubectl apply -f -", "*is not a capability name*")]
    [InlineData("scan", "*is not a capability name*")]
    public void The_agent_refuses_a_lease_whose_capability_is_not_a_name(string capability, string expected)
    {
        LeaseAckPayload ack = AgentCapabilities.Decide(
            new LeasePayload(Guid.NewGuid(), capability, 30_000));

        ack.Accepted.Should().BeFalse();
        ack.Reason.Should().Match(expected);
    }

    [Fact]
    public void The_agent_refuses_a_lease_with_no_deadline_to_count_down_from()
    {
        LeaseAckPayload ack = AgentCapabilities.Decide(
            new LeasePayload(Guid.NewGuid(), "component.scan", 0));

        ack.Accepted.Should().BeFalse();
        ack.Reason.Should().Match("*deadline*");
    }

    [Fact]
    public void A_refusal_keeps_the_lease_it_is_answering()
    {
        // A refusal that loses the id answers nobody: the server is waiting on that lease, and an
        // ack for a different one would leave the dispatch to time out instead.
        Guid leaseId = Guid.NewGuid();

        AgentCapabilities.Decide(new LeasePayload(leaseId, "component.scan", 30_000))
            .LeaseId.Should().Be(leaseId);
    }

    // ──────── The agent has no apply path to reach ────────

    [Fact]
    public void The_agent_binary_cannot_change_a_cluster_even_if_it_wanted_to()
    {
        // The strongest form of "never apply" is the absence of anything that could. This scans
        // the agent's own source for the two ways everything else in this product mutates a
        // cluster: launching kubectl or helm, and the typed Kubernetes client.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, AgentSource));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the agent's source; if it moved, the test has to follow rather than "
            + "quietly stop checking anything");

        string[] sources = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

        sources.Should().HaveCountGreaterThan(3);

        Dictionary<string, string> read = sources.ToDictionary(p => p, File.ReadAllText);

        // A positive control. Four absence assertions can all pass against a scan that read
        // nothing at all, so first prove this one is looking at the real agent.
        read.Values.Should().Contain(
            text => text.Contains("TcpClient", StringComparison.Ordinal),
            "the scan must be reading AgentClient.cs, or the assertions below mean nothing");

        foreach ((string path, string text) in read)
        {
            string name = Path.GetFileName(path);

            text.Should().NotContain("Process.Start", $"{name} would be able to run a command");
            text.Should().NotContain("k8s.Kubernetes", $"{name} would be able to call a cluster's API");
            text.Should().NotContain("kubectl", $"{name} would be able to run kubectl");
            text.Should().NotContain("helm ", $"{name} would be able to run helm");
        }
    }
}
