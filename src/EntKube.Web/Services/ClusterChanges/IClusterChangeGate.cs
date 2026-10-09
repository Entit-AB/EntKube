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
/// The gate is scoped (per Blazor circuit), and the global acknowledgment dialog registers an
/// <see cref="IClusterChangeAckSink"/> on that scope. A scope with no sink has nobody to ask, and
/// what happens then is now a decision the context has to have made: see
/// <see cref="DeclareUnattended"/>. A declared scope proceeds and is recorded against its reason;
/// an undeclared one is refused. It used to proceed either way, which is what made the gate
/// something an in-cluster agent would simply have walked past.
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

    /// <summary>
    /// Declares that this scope may change clusters with no operator to ask, and why.
    ///
    /// <para><b>Why a declaration and not a default.</b> A scope with no sink used to apply
    /// straight through. That meant "nobody is watching" and "this is allowed to run unwatched"
    /// were the same state, so a background reconcile and a remote agent were indistinguishable
    /// to the gate — and the agent would have inherited every bypass in the product. The
    /// declaration separates them: a context that is supposed to act on its own says so, by name,
    /// and anything else is refused.</para>
    ///
    /// <para>It sits beside <see cref="RegisterSink"/> on purpose. Both answer "who is
    /// answerable for what happens in this scope", both are made once where the scope is created,
    /// and both last until disposed. A background service declares around its unit of work; an
    /// authenticated API request declares for the request, naming the caller.</para>
    ///
    /// <para><paramref name="reason"/> is written to the audit trail, so it should name the work
    /// rather than the mechanism — "drift-remediation", not "background".</para>
    /// </summary>
    IDisposable DeclareUnattended(string reason);
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
