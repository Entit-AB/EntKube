namespace EntKube.Web.Services.ClusterChanges;

/// <summary>How a planned cluster change ended up at the acknowledgment boundary.</summary>
public enum ClusterChangeOutcome
{
    /// <summary>An operator was shown the diff and said yes.</summary>
    Acknowledged,

    /// <summary>An operator was shown the diff and said no; the calling method was aborted.</summary>
    Cancelled,

    /// <summary>
    /// Nobody was asked, because there was no interactive sink on the scope — a background
    /// service, a scheduled reconcile, a bootstrap runner, or anything else running without a
    /// Blazor circuit. This is the outcome worth counting: the change reached the cluster
    /// unsupervised.
    /// </summary>
    AppliedUnattended,

    /// <summary>
    /// Nobody was asked, because <c>ClusterChanges:RequireAcknowledgment</c> is off. An operator
    /// opted out of the dialog, which is not the same as opting out of the record.
    /// </summary>
    GateDisabled,
}

/// <summary>
/// Durable record of what the acknowledgment gate decided.
///
/// <para><b>Why this exists.</b> <see cref="ClusterChangeGate"/> opens with
/// <c>if (!Enabled || _sink is null) return;</c>. That one line is the whole of the gate's
/// behaviour outside an interactive session: a change applied by a background service is not
/// merely unacknowledged, it is <em>untraced</em> — the method returns before a diff is even
/// computed, and nothing anywhere says a cluster was altered without a human.</para>
///
/// <para><b>What this is not.</b> It does not make the gate durable in the sense agents need.
/// An agent writing to a cluster requires an acknowledgment an operator can give
/// <em>out of band</em> — a pending queue, with the caller waiting or refusing — and that is a
/// product decision about whether unattended work stalls, proceeds or fails, not something to
/// settle in passing. This is the half that needs no such decision: before choosing what should
/// happen to unattended changes, it is worth being able to see them. See docs/decomposition.md §5.</para>
///
/// <para><b>Deliberately not recorded: diff bodies and manifests.</b> A manifest on its way to a
/// cluster routinely contains a Secret, and a <c>kubectl diff</c> of one contains its old and new
/// values. Writing either into an audit row would turn a safety feature into a second copy of
/// every credential EntKube applies. The record carries what happened, where, and how big the
/// diff was — never the diff itself.</para>
/// </summary>
public interface IClusterChangeRecorder
{
    /// <summary>
    /// Records one gate outcome. Must never throw: a failure to record is a failure to observe,
    /// not a reason to abandon a change an operator has already approved.
    /// </summary>
    Task RecordAsync(
        PlannedClusterChange change,
        ClusterChangeOutcome outcome,
        ClusterChangeDiff? diff,
        CancellationToken ct = default);
}

/// <summary>
/// Records nothing. The default when no recorder is supplied, which keeps the gate constructible
/// in tests that are about acknowledgment rather than about auditing.
/// </summary>
public sealed class NullClusterChangeRecorder : IClusterChangeRecorder
{
    public Task RecordAsync(
        PlannedClusterChange change, ClusterChangeOutcome outcome, ClusterChangeDiff? diff,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
