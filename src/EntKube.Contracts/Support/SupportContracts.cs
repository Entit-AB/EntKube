namespace EntKube.Contracts.Support;

/// <summary>
/// One availability objective, resolved most-specific-wins.
///
/// <para>A target applies to an app (<see cref="AppId"/> set), to a whole customer
/// (<see cref="CustomerId"/> set, app null), or to the tenant as a default (both null).
/// Nothing here says which of the three it is — the nesting is the answer, and the caller
/// picks the narrowest row that matches.</para>
/// </summary>
/// <param name="CustomerId">The customer it applies to, or null for the tenant default.</param>
/// <param name="AppId">The app it applies to, or null for every app at that level.</param>
/// <param name="TargetPercent">The objective, as a percentage of healthy observations.</param>
/// <param name="MeasurementWindowDays">How long a window the objective is judged over.</param>
/// <remarks>
/// Four fields, because four are read. The entity also carries its own id, a creation stamp
/// and three navigations: the id has no reader outside Support's own editor, which holds the
/// entity, and the stamp has none at all. Same rule that trimmed
/// <c>MaintenanceWindowSummary</c> from six fields to four.
/// </remarks>
public sealed record SlaTargetSummary(
    Guid? CustomerId,
    Guid? AppId,
    double TargetPercent,
    int MeasurementWindowDays);

/// <summary>
/// What the rest of the product may ask of Support.
///
/// <para>Support owns the commercial layer — contracts, tickets, time, the service-level
/// objectives those agreements promise. Reliability code elsewhere needs the objective in
/// order to say whether it was met, and that is the whole of this surface today: Telemetry
/// computes error budgets and monthly availability against targets it does not own.</para>
///
/// <para>⚠️ The write side is deliberately absent, and the reason is a finding rather than an
/// omission. <c>IncidentService</c> — a <em>Telemetry</em> service — is currently the product's
/// SLA-target CRUD: it hosts <c>GetSlaTargetAsync</c> and <c>UpsertSlaTargetAsync</c> over
/// Support's table and hands the EF entity back to three Razor pages, one of which also adds
/// and removes rows straight off the <c>DbSet</c>. Moving that CRUD into Support is a change
/// with its own callers to convert and its own reason to state, so it is not smuggled in here
/// behind a read.</para>
///
/// <para>⚠️ Resolution is also absent on purpose, and it will not stay that way. There are two
/// implementations of most-specific-wins in the product today, and they are not the same
/// predicate. <c>IncidentService</c> resolves in SQL with
/// <c>appId == null || s.AppId == appId || s.AppId == null</c>, so a caller passing no app
/// short-circuits the first clause and admits rows belonging to <em>other</em> apps — which the
/// following <c>OrderByDescending(s =&gt; s.AppId.HasValue)</c> then prefers.
/// <c>ErrorBudgetService</c> resolves in memory with <c>t.AppId == null || t.AppId == appId</c>
/// and cannot. <b>The difference is latent, not live:</b> both callers of the SQL version pass a
/// concrete customer and app, so no caller reaches the branch where they diverge and behaviour
/// agrees today. That is precisely why this is the wrong moment to publish one of them as
/// <em>the</em> resolution — a contract method would have to choose, and choosing is a change in
/// behaviour disguised as a move. The read below hands over the rows, each caller keeps the
/// predicate it already has, and unifying the two is the follow-up that brings the write side
/// with it.</para>
/// </summary>
public interface ISupportApi
{
    /// <summary>
    /// Every availability objective a tenant has set, unresolved.
    ///
    /// <para>Returns rows rather than an answer because a caller computing error budgets has to
    /// resolve most-specific-wins per deployment, and one call per deployment would be the same
    /// question asked N times.</para>
    ///
    /// <para>⚠️ <b>The original reason given here no longer holds, and is recorded because a stale
    /// rationale is worse than none.</b> It said the caller "has to know the widest measurement
    /// window across the tenant before it can decide how much health history to load" — true while
    /// <c>ErrorBudgetService</c> loaded every sample in one wide fetch and sliced in memory. It now
    /// asks <c>IDeliveryApi.GetHealthRatiosAsync</c> once per <em>distinct</em> window, so there is
    /// no widest window to discover; it groups by the window each target names. The method is
    /// unchanged, but only the first sentence above is still its reason.</para>
    /// </summary>
    Task<IReadOnlyList<SlaTargetSummary>> GetSlaTargetsAsync(
        Guid tenantId, CancellationToken ct = default);
}
