using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// What the acknowledgment gate leaves behind.
///
/// <para><b>The gap these close.</b> <c>ClusterChangeGate</c> began with
/// <c>if (!Enabled || _sink is null) return;</c>. Outside an interactive session that line was the
/// gate's entire behaviour: a background service, a scheduled reconcile or a bootstrap runner
/// altered a cluster and nothing recorded that it had happened without a human — the method
/// returned before a diff was even computed. The acknowledgment model was documented as
/// "interactive only", which is true, and read as though the other case were covered, which it
/// was not.</para>
///
/// <para>These are deliberately about the <em>record</em>, not the decision — the decision is
/// already covered by <see cref="ClusterChangeGateTests"/>. A Patch-verb change is used
/// throughout for the same reason it is there: its preview is computed in-process, so no cluster
/// is needed.</para>
/// </summary>
public class ClusterChangeRecordTests
{
    private static PlannedClusterChange PatchChange(string? summary = "Scale Deployment/api to 3") => new()
    {
        Verb = ChangeVerb.Patch,
        Kubeconfig = "irrelevant",
        ClusterLabel = "test-cluster",
        Namespace = "ns",
        Resource = "deployment",
        Name = "api",
        Patch = "{\"spec\":{\"replicas\":3}}",
        Summary = summary,
    };

    private sealed class RecordingRecorder : IClusterChangeRecorder
    {
        public List<(ClusterChangeOutcome Outcome, PlannedClusterChange Change, ClusterChangeDiff? Diff)> Entries { get; } = [];

        public Task RecordAsync(
            PlannedClusterChange change, ClusterChangeOutcome outcome, ClusterChangeDiff? diff,
            CancellationToken ct = default)
        {
            Entries.Add((outcome, change, diff));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRecorder : IClusterChangeRecorder
    {
        public Task RecordAsync(
            PlannedClusterChange change, ClusterChangeOutcome outcome, ClusterChangeDiff? diff,
            CancellationToken ct = default)
            => throw new InvalidOperationException("the audit database is down");
    }

    private sealed class FakeSink(ClusterChangeDecision decision) : IClusterChangeAckSink
    {
        public Task<ClusterChangeDecision> RequestAsync(
            PlannedClusterChange change, ClusterChangeDiff diff, CancellationToken ct)
            => Task.FromResult(decision);
    }

    private static ClusterChangeGate NewGate(IClusterChangeRecorder recorder, bool enabled = true)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClusterChanges:RequireAcknowledgment"] = enabled ? "true" : "false",
            })
            .Build();

        return new ClusterChangeGate(config, NullLogger<ClusterChangeGate>.Instance, recorder);
    }

    // ════════════════════════════════════════════════════════════════
    //  Every outcome that reaches a cluster is recorded once
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_declared_change_with_no_operator_present_is_recorded_as_unattended()
    {
        RecordingRecorder recorder = new();
        ClusterChangeGate gate = NewGate(recorder);

        // No sink, but the scope has said what it is — a scheduled reconcile rather than an
        // agent nobody authorised.
        using IDisposable declaration = gate.DeclareUnattended("drift-remediation");

        await gate.AcknowledgeAsync(PatchChange());

        recorder.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(ClusterChangeOutcome.AppliedUnattended);
    }

    /// <summary>
    /// A refusal is recorded, not only thrown. A background job that has stopped working needs to
    /// be findable in the audit trail, not only in whatever log swallowed its exception — and the
    /// count of these is what says whether the declarations are complete.
    /// </summary>
    [Fact]
    public async Task An_undeclared_change_with_no_operator_present_is_recorded_as_refused()
    {
        RecordingRecorder recorder = new();

        Func<Task> act = () => NewGate(recorder).AcknowledgeAsync(PatchChange());

        await act.Should().ThrowAsync<InvalidOperationException>();

        recorder.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(ClusterChangeOutcome.RefusedUndeclared);
    }

    [Fact]
    public async Task Turning_the_gate_off_is_recorded_separately_from_having_nobody_to_ask()
    {
        RecordingRecorder recorder = new();

        await NewGate(recorder, enabled: false).AcknowledgeAsync(PatchChange());

        // Two different problems: an operator opted out of the dialog, versus unattended work
        // growing as more of EntKube moves off the Blazor circuit. Only the second is a trend.
        recorder.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(ClusterChangeOutcome.GateDisabled);
    }

    [Fact]
    public async Task An_acknowledged_change_is_recorded_with_the_diff_it_was_approved_against()
    {
        RecordingRecorder recorder = new();
        ClusterChangeGate gate = NewGate(recorder);
        using IDisposable sink = gate.RegisterSink(new FakeSink(ClusterChangeDecision.Acknowledged));

        await gate.AcknowledgeAsync(PatchChange());

        (ClusterChangeOutcome outcome, _, ClusterChangeDiff? diff) = recorder.Entries.Should().ContainSingle().Subject;
        outcome.Should().Be(ClusterChangeOutcome.Acknowledged);
        diff.Should().NotBeNull("what was approved is part of what happened");
    }

    [Fact]
    public async Task A_cancelled_change_is_recorded_and_still_aborts_the_caller()
    {
        RecordingRecorder recorder = new();
        ClusterChangeGate gate = NewGate(recorder);
        using IDisposable sink = gate.RegisterSink(new FakeSink(ClusterChangeDecision.Cancelled));

        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Entries.Should().ContainSingle()
            .Which.Outcome.Should().Be(ClusterChangeOutcome.Cancelled);
    }

    [Fact]
    public async Task A_recorder_that_fails_does_not_abandon_an_approved_change()
    {
        ClusterChangeGate gate = NewGate(new ThrowingRecorder());
        using IDisposable sink = gate.RegisterSink(new FakeSink(ClusterChangeDecision.Acknowledged));

        // The interface asks implementations not to throw; the gate does not rely on being
        // obeyed, because the consequence of trusting it is a change failing for the sake of a
        // row about the change.
        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_cancelled_change_still_throws_even_when_recording_fails()
    {
        ClusterChangeGate gate = NewGate(new ThrowingRecorder());
        using IDisposable sink = gate.RegisterSink(new FakeSink(ClusterChangeDecision.Cancelled));

        // Swallowing the recorder's failure must not swallow the operator's refusal with it.
        Func<Task> act = () => gate.AcknowledgeAsync(PatchChange());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ════════════════════════════════════════════════════════════════
    //  What the audit row may and may not contain
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task The_audit_row_names_the_change_without_copying_its_contents()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        TestDbContextFactory factory = new(connection);
        using ApplicationDbContext db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        AuditClusterChangeRecorder recorder = new(
            new AuditService(factory), NullLogger<AuditClusterChangeRecorder>.Instance);

        PlannedClusterChange change = PatchChange();
        ClusterChangeDiff diff = new()
        {
            HasChanges = true,
            DiffText = "- password: aG9yc2UtYmF0dGVyeQ==\n+ password: Y29ycmVjdC1zdGFwbGU=\n",
        };

        await recorder.RecordAsync(change, ClusterChangeOutcome.AppliedUnattended, diff);

        AuditEvent row = db.AuditEvents.Single();
        row.Action.Should().Be("ClusterChangeAppliedUnattended");
        row.ResourceName.Should().Be("api");
        row.Details.Should().Contain("test-cluster").And.Contain("namespace=ns");

        // A manifest on its way to a cluster routinely contains a Secret, and a diff of one
        // contains both its old and new values. Recording either would make the safety feature a
        // second copy of every credential EntKube applies.
        row.Details.Should().NotContain("aG9yc2UtYmF0dGVyeQ==");
        row.Details.Should().NotContain("Y29ycmVjdC1zdGFwbGU=");
        row.Details.Should().NotContain(change.Patch!);

        // The size survives, which is what distinguishes a one-field patch from a wholesale
        // replacement without carrying the contents.
        row.Details.Should().Contain("diffLines=2");
    }

    /// <summary>
    /// Pins a limitation rather than a feature, so it is not mistaken for working.
    ///
    /// <para><c>PlannedClusterChange.ClusterLabel</c> is a non-nullable string defaulting to the
    /// literal <c>"cluster"</c>, and the model carries no cluster id at all. A change from a path
    /// that never set the label therefore records as <c>cluster=cluster</c> — which reads exactly
    /// like a cluster someone named that. These rows are findable but not groupable by cluster,
    /// and the fix is a real <c>ClusterId</c> on the model, which belongs with the §4.0.1 work
    /// that needs the same thing.</para>
    /// </summary>
    [Fact]
    public async Task A_change_that_never_named_its_cluster_records_the_placeholder_as_is()
    {
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        TestDbContextFactory factory = new(connection);
        using ApplicationDbContext db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        AuditClusterChangeRecorder recorder = new(
            new AuditService(factory), NullLogger<AuditClusterChangeRecorder>.Instance);

        await recorder.RecordAsync(
            new PlannedClusterChange { Verb = ChangeVerb.Apply, Kubeconfig = "x" },
            ClusterChangeOutcome.AppliedUnattended, null);

        // Recorded, and recorded uselessly. Both halves matter: the row exists, and it cannot
        // tell you where the change went.
        string details = db.AuditEvents.Single().Details!;
        details.Should().Contain("cluster=cluster");
        details.Should().Contain("verb=Apply");
    }
}
