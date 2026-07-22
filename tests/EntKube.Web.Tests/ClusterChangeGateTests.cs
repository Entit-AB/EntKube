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
    public async Task No_sink_registered_passes_through_without_asking()
    {
        ClusterChangeGate gate = NewGate();

        // Should complete silently — no sink means a non-interactive (background) scope.
        await gate.AcknowledgeAsync(PatchChange());
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
    public async Task Disposing_the_registration_restores_pass_through()
    {
        ClusterChangeGate gate = NewGate();
        FakeSink sink = new(ClusterChangeDecision.Cancelled);
        IDisposable reg = gate.RegisterSink(sink);

        reg.Dispose();

        // Sink is gone → gate must pass through (no throw, no call).
        await gate.AcknowledgeAsync(PatchChange());
        sink.Calls.Should().Be(0);
    }
}
