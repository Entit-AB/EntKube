using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The ClusterChangeGate is the central acknowledgment boundary for every human-triggered
/// mutation of Kubernetes state. These tests pin the boundary's contract:
///
///  • no interactive sink on the scope (background/automated flows, or the feature switched off)
///    ⇒ the gate passes straight through, so unattended remediation/bootstrap never blocks;
///  • an interactive sink present ⇒ the operator's decision governs — acknowledge proceeds,
///    cancel throws OperationCanceledException to abort the calling service method;
///  • unregistering the sink (dialog disposed / circuit gone) restores pass-through.
///
/// A Patch-verb change is used throughout: its preview is computed in-process (no kubectl),
/// so the tests are deterministic without a cluster.
/// </summary>
public class ClusterChangeGateTests
{
    private static ClusterChangeGate NewGate(bool enabled = true)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClusterChanges:RequireAcknowledgment"] = enabled ? "true" : "false",
            })
            .Build();
        return new ClusterChangeGate(config, NullLogger<ClusterChangeGate>.Instance);
    }

    private static PlannedClusterChange PatchChange() => new()
    {
        Verb = ChangeVerb.Patch,
        Kubeconfig = "irrelevant",
        ClusterLabel = "test-cluster",
        Namespace = "ns",
        Resource = "deployment",
        Name = "api",
        Patch = "{\"spec\":{\"replicas\":3}}",
        Summary = "Scale Deployment/api to 3",
    };

    private sealed class FakeSink(ClusterChangeDecision decision) : IClusterChangeAckSink
    {
        public int Calls { get; private set; }
        public PlannedClusterChange? LastChange { get; private set; }
        public ClusterChangeDiff? LastDiff { get; private set; }

        public Task<ClusterChangeDecision> RequestAsync(
            PlannedClusterChange change, ClusterChangeDiff diff, CancellationToken ct)
        {
            Calls++;
            LastChange = change;
            LastDiff = diff;
            return Task.FromResult(decision);
        }
    }

    [Fact]
    public async Task No_sink_and_no_declaration_is_refused()
    {
        ClusterChangeGate gate = NewGate();

        // This used to complete silently, and that was the whole problem: "nobody is watching"
        // and "this is allowed to run unwatched" were the same state, so an in-cluster agent
        // would have inherited every bypass in the product (docs/decomposition.md §5.4).
        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("DeclareUnattended",
                "the refusal has to say how to declare, or the first person to hit it has only a "
                + "stack trace to go on");
    }

    [Fact]
    public async Task A_declared_scope_applies_without_asking()
    {
        ClusterChangeGate gate = NewGate();

        using IDisposable declaration = gate.DeclareUnattended("drift-remediation");

        // No throw: a background context that has said what it is still proceeds, which is what
        // keeps scheduled work running.
        await gate.AcknowledgeAsync(PatchChange());
    }

    [Fact]
    public async Task A_declaration_ends_when_it_is_disposed()
    {
        ClusterChangeGate gate = NewGate();

        using (gate.DeclareUnattended("drift-remediation"))
        {
            await gate.AcknowledgeAsync(PatchChange());
        }

        // A scope reused for something else must not inherit the declaration.
        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void A_declaration_must_say_what_the_work_is()
    {
        ClusterChangeGate gate = NewGate();

        // The reason is the entire value of the declaration: it is what the audit trail shows for
        // every change the scope goes on to make.
        gate.Invoking(g => g.DeclareUnattended("  ")).Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A sink outranks a declaration. A background scope that somehow has an operator attached
    /// should still ask them — the declaration says "there may be nobody", not "do not ask".
    /// </summary>
    // ──────── Consequences of an already-acknowledged change ────────

    /// <summary>
    /// An approved act with per-resource consequences — an upgrade that has to take three
    /// workloads down and put them back — asks once, not seven times.
    /// </summary>
    [Fact]
    public async Task A_consequence_declaration_does_not_ask_again()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Acknowledged);
        using IDisposable registration = gate.RegisterSink(sink);

        using (gate.DeclareConsequence("the Harbor 2.11 upgrade the operator acknowledged"))
        {
            await gate.AcknowledgeAsync(PatchChange());
            await gate.AcknowledgeAsync(PatchChange());
        }

        sink.Calls.Should().Be(0, "the operator approved the act these follow from");
    }

    /// <summary>
    /// <b>The safety property.</b> A consequence declaration suppresses the question, never the
    /// refusal: a scope with nobody to ask and no unattended declaration is still refused. Without
    /// this, declaring a consequence would be a way to manufacture an approver who was never
    /// there — which is exactly the bypass §4.0.1.2's decision closed.
    /// </summary>
    [Fact]
    public async Task A_consequence_declaration_does_not_rescue_a_scope_with_nobody_to_ask()
    {
        ClusterChangeGate gate = NewGate();

        using (gate.DeclareConsequence("something an operator supposedly approved"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => gate.AcknowledgeAsync(PatchChange()));
        }
    }

    [Fact]
    public async Task A_consequence_declaration_ends_when_it_is_disposed()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Acknowledged);
        using IDisposable registration = gate.RegisterSink(sink);

        using (gate.DeclareConsequence("an acknowledged upgrade"))
        {
            await gate.AcknowledgeAsync(PatchChange());
        }

        await gate.AcknowledgeAsync(PatchChange());

        sink.Calls.Should().Be(1, "outside the declaration the gate asks as it always did");
    }

    [Fact]
    public void A_consequence_must_name_the_act_it_follows_from()
    {
        ClusterChangeGate gate = NewGate();

        Assert.Throws<ArgumentException>(() => gate.DeclareConsequence("   "));
    }

    [Fact]
    public async Task A_declared_scope_with_a_sink_still_asks()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Cancelled);
        gate.RegisterSink(sink);

        using IDisposable declaration = gate.DeclareUnattended("drift-remediation");

        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Feature_disabled_passes_through_even_with_a_sink()
    {
        ClusterChangeGate gate = NewGate(enabled: false);
        FakeSink sink = new(ClusterChangeDecision.Cancelled);
        gate.RegisterSink(sink);

        await gate.AcknowledgeAsync(PatchChange());

        sink.Calls.Should().Be(0, "the master switch is off, so the gate must never consult the sink");
    }

    [Fact]
    public async Task Acknowledged_decision_proceeds()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Acknowledged);
        gate.RegisterSink(sink);

        await gate.AcknowledgeAsync(PatchChange());

        sink.Calls.Should().Be(1);
        sink.LastChange!.Summary.Should().Be("Scale Deployment/api to 3");
        sink.LastDiff!.DiffText.Should().Contain("replicas");
    }

    [Fact]
    public async Task Cancelled_decision_throws_to_abort_the_operation()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Cancelled);
        gate.RegisterSink(sink);

        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Disposing_the_registration_stops_the_gate_asking_that_sink()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Cancelled);
        IDisposable reg = gate.RegisterSink(sink);

        reg.Dispose();

        // The sink is gone, so there is nobody to ask — and with nothing declared that is now a
        // refusal rather than a pass-through. What this still pins is that the unregistered sink
        // is not consulted.
        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        await act.Should().ThrowAsync<InvalidOperationException>();
        sink.Calls.Should().Be(0);
    }
}
