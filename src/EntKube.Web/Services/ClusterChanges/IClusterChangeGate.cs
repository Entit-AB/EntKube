namespace EntKube.Web.Services.ClusterChanges;

/// <summary>
/// Central acknowledgment gate for every human-triggered change to Kubernetes cluster state.
///
/// Every mutating primitive (apply/delete/patch/scale/restart/helm) calls
/// <see cref="AcknowledgeAsync"/> on one line BEFORE it writes. The gate computes a
/// server-side dry-run diff and — when an interactive acknowledgment sink is registered on
/// the current scope — blocks until the operator acknowledges or cancels. On cancel it throws
/// <see cref="OperationCanceledException"/>, which cleanly aborts the calling service method.
///
/// "Interactive only" falls out of the scope model: the gate is scoped (per Blazor circuit),
/// and the global acknowledgment dialog registers an <see cref="IClusterChangeAckSink"/> on that
/// scope. Background/automated scopes register no sink, so the gate passes through ungated —
/// automated remediation, bootstrap and git-sync are unaffected.
/// </summary>
public interface IClusterChangeGate
{
    /// <summary>
    /// Requires operator acknowledgment for a planned cluster mutation.
    /// Returns normally once acknowledged (or when gating is bypassed / there is nothing to change).
    /// Throws <see cref="OperationCanceledException"/> if the operator cancels.
    /// </summary>
    Task AcknowledgeAsync(PlannedClusterChange change, CancellationToken ct = default);

    /// <summary>
    /// Registers the interactive acknowledgment sink for this scope (called by the global dialog).
    /// Returns a disposable that unregisters on dispose.
    /// </summary>
    IDisposable RegisterSink(IClusterChangeAckSink sink);
}

/// <summary>
/// The UI side of the gate. Implemented by the global acknowledgment dialog; presents the diff
/// and resolves once the operator chooses. Only present in interactive (circuit) scopes.
/// </summary>
public interface IClusterChangeAckSink
{
    Task<ClusterChangeDecision> RequestAsync(
        PlannedClusterChange change, ClusterChangeDiff diff, CancellationToken ct);
}
