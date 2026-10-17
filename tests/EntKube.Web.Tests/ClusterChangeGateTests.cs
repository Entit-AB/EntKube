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

    // ──────── One acknowledgment per operator action ────────

    /// <summary>
    /// The rule: an operator acknowledges an <em>action</em>, and everything that action goes on
    /// to do follows from that one answer.
    ///
    /// <para><b>Why it was needed.</b> Every mutating primitive gates itself — fifteen raise
    /// points in <c>KubernetesClientFactory</c> alone — so any service method that performs more
    /// than one primitive asked more than once. A Helm install asked for the release, then for
    /// the Kyverno apply, then once per hostname for the route re-apply: four dialogs for one
    /// button, all of them about work the operator had already approved. That is how an operator
    /// learns to click through dialogs without reading them, which costs more safety than it
    /// buys.</para>
    /// </summary>
    [Fact]
    public async Task An_operation_asks_once_and_treats_the_rest_as_consequences()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Acknowledged);
        using IDisposable registration = gate.RegisterSink(sink);

        using (gate.BeginOperation("helm upgrade --install flow in flow"))
        {
            await gate.AcknowledgeAsync(PatchChange());   // the release: asks
            await gate.AcknowledgeAsync(PatchChange());   // kyverno: follows from it
            await gate.AcknowledgeAsync(PatchChange());   // route refresh: likewise
        }

        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    /// <summary>
    /// <b>The boundary, and the reason this is an operation rather than a flag.</b> The gate is
    /// scoped per circuit and a circuit lasts as long as the page, so "stop asking once something
    /// has been acknowledged" would carry across to the operator's next button press and quietly
    /// retire the gate. The window closes with the action.
    /// </summary>
    [Fact]
    public async Task The_next_action_asks_again()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Acknowledged);
        using IDisposable registration = gate.RegisterSink(sink);

        using (gate.BeginOperation("apply 3 manifest(s) to billing-ns"))
        {
            await gate.AcknowledgeAsync(PatchChange());
            await gate.AcknowledgeAsync(PatchChange());
        }

        using (gate.BeginOperation("remove billing-api from billing-ns"))
        {
            await gate.AcknowledgeAsync(PatchChange());
        }

        sink.Calls.Should().Be(2, "a second deliberate action is a second decision");
    }

    /// <summary>
    /// Nesting defers to the outermost action, so the first change of the action gets the dialog
    /// however deep the call tree goes. <c>DeleteAppFromClusterAsync</c> relies on this: it wraps
    /// the whole app, and each per-deployment delete inside opens its own operation.
    /// </summary>
    [Fact]
    public async Task A_nested_operation_does_not_start_a_second_window()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Acknowledged);
        using IDisposable registration = gate.RegisterSink(sink);

        using (gate.BeginOperation("remove every deployment of app X"))
        {
            await gate.AcknowledgeAsync(PatchChange());

            using (gate.BeginOperation("remove deployment 1"))
            {
                await gate.AcknowledgeAsync(PatchChange());
            }

            // ⚠️ The inner dispose must not reopen the window. If it cleared the flag, the next
            // change would ask again — one dialog per deployment, which is the bug this whole
            // mechanism exists to remove.
            await gate.AcknowledgeAsync(PatchChange());

            using (gate.BeginOperation("remove deployment 2"))
            {
                await gate.AcknowledgeAsync(PatchChange());
            }
        }

        sink.Calls.Should().Be(1, "the outermost action owns the window");
    }

    /// <summary>
    /// <b>The safety property, same as for a consequence declaration.</b> An operation suppresses
    /// the question, never the refusal: a scope with nobody to ask and no unattended declaration
    /// is still refused inside one. Otherwise opening an operation would be a way to manufacture
    /// an approver who was never there — and since the entry points now open operations as a
    /// matter of course, that would have been a bypass on every one of them.
    /// </summary>
    [Fact]
    public async Task An_operation_does_not_rescue_a_scope_with_nobody_to_ask()
    {
        ClusterChangeGate gate = NewGate();

        using (gate.BeginOperation("an action with no operator behind it"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => gate.AcknowledgeAsync(PatchChange()));
        }
    }

    /// <summary>
    /// A cancelled first change must not open the window either — nothing was approved, so there
    /// is nothing for the rest of the action to follow from. The cancellation aborts the caller,
    /// but a caller that swallowed it would otherwise find the gate silent afterwards.
    /// </summary>
    [Fact]
    public async Task A_cancelled_first_change_leaves_the_operation_unacknowledged()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Cancelled);
        using IDisposable registration = gate.RegisterSink(sink);

        using (gate.BeginOperation("an action the operator declined"))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => gate.AcknowledgeAsync(PatchChange()));

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => gate.AcknowledgeAsync(PatchChange()));
        }

        sink.Calls.Should().Be(2, "a refusal is not an approval to ride on");
    }

    [Fact]
    public void An_operation_must_name_the_action()
    {
        ClusterChangeGate gate = NewGate();
        Assert.Throws<ArgumentException>(() => gate.BeginOperation("  "));
    }
}
