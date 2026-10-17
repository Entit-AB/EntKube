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

    /// <summary>
    /// Declares that changes made in this scope are <b>consequences of a change an operator has
    /// already acknowledged</b>, and what that work is. Within the returned disposable's lifetime
    /// the gate does not ask again; every change is still recorded, as
    /// <see cref="ClusterChangeOutcome.AppliedAsConsequence"/>.
    ///
    /// <para><b>Why this exists.</b> Some approved acts have per-resource consequences. An upgrade
    /// of a chart whose volumes are ReadWriteOnce has to take each blocked workload to zero and put
    /// it back; an apply that removes a resource from a manifest has to delete it. The dialog for
    /// the act already named them. Asking per consequence would mean three dialogs for one upgrade
    /// an operator has just approved, which is how an operator learns to click through dialogs
    /// without reading them — so the alternative to this declaration is not more safety, it is a
    /// service that keeps its own <c>kubectl</c> and is invisible to the gate entirely. That is
    /// what <c>ReleaseVolumeGuard</c> did, and §5.4 names the remedy: the ungated path should be
    /// auditable rather than accidental.</para>
    ///
    /// <para><b>It suppresses the question, never the refusal.</b> A scope with no sink and no
    /// <see cref="DeclareUnattended"/> is still refused — declaring a consequence cannot conjure
    /// an approver who was never there. So this is only ever a shortcut past a dialog that exists,
    /// which is the one case where skipping it is honest.</para>
    ///
    /// <para><paramref name="reason"/> goes to the audit trail and should name the approved act it
    /// follows from, not the mechanism — "scaled down for the Harbor 2.11 upgrade", not
    /// "volume guard".</para>
    /// </summary>
    IDisposable DeclareConsequence(string reason);

    /// <summary>
    /// Opens an <b>operator action</b>: one acknowledgment covers everything the action goes on
    /// to do. The first gated change inside asks as usual; every later one is recorded as
    /// <see cref="ClusterChangeOutcome.AppliedAsConsequence"/> against <paramref name="what"/>.
    /// Disposing closes the window, so the operator's next action asks again.
    ///
    /// <para><b>Why the boundary has to be explicit.</b> "Do not ask twice" needs an end as well
    /// as a beginning. The gate is scoped per circuit and a circuit lasts as long as the page, so
    /// a rule like "stop asking once something has been acknowledged" would carry across to the
    /// next button press and quietly retire the gate. This is the scope of one action, which is
    /// the unit the operator actually decided about.</para>
    ///
    /// <para><b>The outermost one wins.</b> Nesting is a no-op: a service inside an action that
    /// opens its own operation does not restart the window, so the first change of the action is
    /// the one that gets the dialog however deep the call tree goes.</para>
    ///
    /// <para><b>It suppresses the question, never the refusal</b> — the same invariant
    /// <see cref="DeclareConsequence"/> holds. A scope with no sink and no
    /// <see cref="DeclareUnattended"/> is still refused inside an operation, because an operation
    /// cannot manufacture an approver who was never there.</para>
    ///
    /// <para><paramref name="what"/> reaches the audit trail as the reason each consequence was
    /// applied, so it should name the action an operator chose — "helm upgrade --install flow",
    /// not "operation".</para>
    /// </summary>
    IDisposable BeginOperation(string what);
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
