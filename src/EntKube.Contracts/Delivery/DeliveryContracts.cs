namespace EntKube.Contracts.Delivery;

/// <summary>How a deployment is delivered.</summary>
/// <summary>
/// How a deployment is defined.
///
/// <para><b>These mirror Delivery's own members exactly, in order, on purpose.</b> The first
/// version of this enum was a coarser vocabulary — <c>Yaml, Helm, Git, Manual</c> — and the
/// implementation mapped onto it with <c>(DeploymentType)(int)d.Type</c>. Nothing lined up: the
/// entity's <c>Manual</c> is 0 and arrived as <c>Yaml</c>, its <c>HelmChart</c> arrived as
/// <c>Git</c>, and <c>GitHelm</c> and <c>GitAppOfApps</c> had no member to land on at all, so they
/// produced an enum value outside the declared range. It went unnoticed because this contract had
/// no consumer that read the field.</para>
///
/// <para>A coarser public vocabulary would have been defensible, but it has to be an explicit
/// mapping rather than a cast, and collapsing <c>GitHelm</c> into either "Helm" or "Git" discards
/// something true. Mirroring is the only version that cannot invent a value, and
/// <c>ModuleApiTests</c> keeps the two declarations in step.</para>
/// </summary>
public enum DeploymentType
{
    /// <summary>Built from a form; the platform generates the manifests.</summary>
    Manual,

    /// <summary>Raw YAML manifests supplied by the operator.</summary>
    Yaml,

    /// <summary>A Helm chart from a repository.</summary>
    HelmChart,

    /// <summary>Raw YAML manifests sourced from a git repository.</summary>
    GitYaml,

    /// <summary>A Helm chart sourced from a git repository.</summary>
    GitHelm,

    /// <summary>An app-of-apps tree sourced from a git repository.</summary>
    GitAppOfApps,
}

/// <summary>Whether what is running matches what was asked for.</summary>
public enum SyncStatus
{
    Unknown,
    Synced,
    OutOfSync,
    Syncing,
    Failed,
}

/// <summary>
/// Whether what is running is well. Mirrors Delivery's members in order — <c>Suspended</c> was
/// missing here, so a suspended deployment had no member to map onto.
/// </summary>
public enum HealthStatus
{
    Unknown,
    Healthy,
    Progressing,
    Degraded,
    Missing,
    Suspended,
}

/// <summary>
/// Lifecycle of one watched release.
///
/// <para>Mirrors <c>Delivery.DeploymentRolloutStatus</c> member for member and in order. ⚠️ Not to
/// be confused with <c>RolloutStatus</c>, which belongs to a blueprint rollout and has entirely
/// different members — the two names sit one letter apart and mapping one onto the other would
/// publish a wrong verdict for every release.</para>
///
/// <para>The public API serialises this by <em>name</em>, so a member renamed on either side
/// changes the wire format. <c>ApiRolloutStatusTests</c> is what keeps the two in step.</para>
/// </summary>
public enum RolloutVerdictStatus
{
    /// <summary>Applied; inside the analysis window.</summary>
    Watching,
    /// <summary>Analysis passed. The release stands.</summary>
    Promoted,
    /// <summary>Analysis failed and the workload was rolled back.</summary>
    RolledBack,
    /// <summary>Analysis failed and the policy said to alert rather than roll back.</summary>
    Alerted,
    /// <summary>Nothing could be measured and the policy said to hold.</summary>
    Inconclusive,
    /// <summary>The rollback itself failed — needs a human now.</summary>
    RollbackFailed,
    /// <summary>Superseded by a newer apply before the window closed.</summary>
    Superseded,
}

/// <summary>
/// One watched release, as the public API and anything outside Delivery sees it.
/// </summary>
/// <param name="Id">The rollout's id.</param>
/// <param name="AppName">The app the release belongs to.</param>
/// <param name="DeploymentName">The deployment that was applied.</param>
/// <param name="EnvironmentName">Which environment it went to.</param>
/// <param name="ClusterName">Which cluster it landed on.</param>
/// <param name="Status">Where the analysis window got to.</param>
/// <param name="StartedAt">When the release was applied.</param>
/// <param name="FinishedAt">When the window closed, or null while it is still open.</param>
/// <param name="Verdict">The analyser's own sentence, when it reached one.</param>
/// <param name="TriggeredBy">Who or what applied it.</param>
public sealed record RolloutSummary(
    Guid Id,
    string AppName,
    string DeploymentName,
    string EnvironmentName,
    string ClusterName,
    RolloutVerdictStatus Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string? Verdict,
    string? TriggeredBy);

/// <summary>
/// A customer application, as everyone outside Delivery sees it.
/// </summary>
/// <param name="Id">The app's id.</param>
/// <param name="TenantId">Owning tenant, by way of its customer.</param>
/// <param name="CustomerId">The customer it belongs to.</param>
/// <param name="CustomerName">That customer's name, to save a second lookup.</param>
/// <param name="Name">The app's name.</param>
/// <param name="Namespace">Its default namespace, when it has one.</param>
public sealed record AppSummary(
    Guid Id,
    Guid TenantId,
    Guid CustomerId,
    string CustomerName,
    string Name,
    string? Namespace);

/// <summary>
/// One deployment of an app into one environment on one cluster.
///
/// <para>Carries the app and cluster identities inline for the same reason
/// <see cref="Catalog.InstalledComponent"/> does: of the cross-module reads, 20 were
/// <c>.Include(d =&gt; d.Cluster)</c> and 8 <c>.Include(d =&gt; d.App)</c>. A deployment on its
/// own answers almost nothing.</para>
/// </summary>
/// <param name="Id">The deployment's id.</param>
/// <param name="TenantId">Owning tenant.</param>
/// <param name="AppId">The app being deployed.</param>
/// <param name="AppName">Its name, to save a second lookup.</param>
/// <param name="EnvironmentId">Which environment.</param>
/// <param name="EnvironmentName">
/// That environment's name, carried inline for the same reason <see cref="AppName"/> is: a caller
/// grouping deployments by environment — a cost report, a report by stage — would otherwise need
/// Delivery's <c>Environments</c> table, which is the dependency this contract exists to remove.
/// Added when the first consumer was written, because a contract missing the one field its callers
/// read cannot be adopted by them: that is how <c>InstalledComponent.Configuration</c> was missed,
/// and how an interface stays unused.
/// </param>
/// <param name="ClusterId">Which cluster.</param>
/// <param name="ClusterName">
/// That cluster's name, beside its id for the same reason <paramref name="EnvironmentName"/> is:
/// to save a second lookup. Four callers wanted it — three service tabs and the public API's
/// /deployments route, which was resolving it through a separate Fleet call. One caller would not
/// have earned a field here; a DTO that grows for one route's convenience is how a contract turns
/// back into a second name for the database.
/// </param>
/// <param name="Name">The deployment's name.</param>
/// <param name="Type">How it is delivered.</param>
/// <param name="Namespace">Where it lands.</param>
/// <param name="SyncStatus">Whether it matches what was asked for.</param>
/// <param name="HealthStatus">Whether it is well.</param>
/// <param name="StatusMessage">Any detail behind those two.</param>
/// <param name="LastSyncedAt">When it last synced.</param>
/// <param name="IsManaged">Whether EntKube owns its lifecycle.</param>
public sealed record DeploymentSummary(
    Guid Id,
    Guid TenantId,
    Guid AppId,
    string AppName,
    Guid EnvironmentId,
    string EnvironmentName,
    Guid ClusterId,
    string ClusterName,
    string Name,
    DeploymentType Type,
    string? Namespace,
    SyncStatus SyncStatus,
    HealthStatus HealthStatus,
    string? StatusMessage,
    DateTime? LastSyncedAt,
    bool IsManaged);

/// <summary>
/// One health sample of one deployment.
/// </summary>
/// <param name="DeploymentId">Which deployment it describes.</param>
/// <param name="Health">What the workload looked like at that moment.</param>
/// <param name="SnapshotAt">When the sample was taken (UTC).</param>
/// <remarks>
/// Three fields, because three are read. The entity also carries a sync status, ready/total
/// replica counts, its own id and a navigation: the replica counts are read only by Delivery's own
/// rollout analyser, and the sync status by nobody outside the writer. §14.1 samples every five
/// minutes — about 8,600 rows per deployment per month — so what this record leaves out is paid
/// for on every call.
/// </remarks>
public sealed record DeploymentHealthSample(
    Guid DeploymentId,
    HealthStatus Health,
    DateTime SnapshotAt);

/// <summary>
/// How many of one deployment's samples were healthy, over whatever interval was asked for.
/// </summary>
/// <param name="DeploymentId">Which deployment it describes.</param>
/// <param name="HealthySamples">Samples whose health was <c>Healthy</c>.</param>
/// <param name="TotalSamples">Samples in the interval, always &gt; 0 — a deployment with none is absent.</param>
/// <remarks>
/// <b>Counts, not a percentage, and that is the whole point of the shape.</b> Every caller that
/// aggregates across deployments has to sum the counts and divide once; averaging per-deployment
/// percentages weights a deployment with three samples the same as one with eight thousand. Two
/// call sites were already doing it correctly by counting raw samples, and returning a percentage
/// here would have made the correct thing impossible to express.
///
/// <para>A deployment with no samples in the interval is <b>omitted</b> rather than returned as
/// 0/0: "nothing was observed" and "nothing was healthy" are different answers, and every caller
/// distinguishes them — they report no availability figure at all rather than 0%.</para>
/// </remarks>
public sealed record DeploymentHealthRatio(
    Guid DeploymentId,
    int HealthySamples,
    int TotalSamples);

/// <summary>
/// What Delivery offers the rest of EntKube.
///
/// <para>Read-only: 22 services across other modules read <c>AppDeployment</c> and none of
/// them writes it. Deploying, syncing and rolling back are Delivery's own work.</para>
/// </summary>
public interface IDeliveryApi
{
    /// <summary>One app, or null when it is not this tenant's.</summary>
    Task<AppSummary?> GetAppAsync(Guid tenantId, Guid appId, CancellationToken ct = default);

    /// <summary>Every app the tenant has.</summary>
    Task<IReadOnlyList<AppSummary>> GetAppsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>One deployment, or null when it is not this tenant's.</summary>
    Task<DeploymentSummary?> GetDeploymentAsync(
        Guid tenantId, Guid deploymentId, CancellationToken ct = default);

    /// <summary>Every deployment in the tenant.</summary>
    Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForTenantAsync(
        Guid tenantId, CancellationToken ct = default);

    /// <summary>What one app has running in one environment — the most common question asked of this table.</summary>
    Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForAppEnvironmentAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default);

    /// <summary>
    /// The tenant's most recent watched releases, newest first.
    ///
    /// <para>A rollout reaches its tenant through deployment → app → customer, which is why the
    /// public API was querying the table directly: the predicate is four navigations long and
    /// there was nothing to ask. <paramref name="limit"/> is clamped by the implementation.</para>
    /// </summary>
    Task<IReadOnlyList<RolloutSummary>> GetRolloutsAsync(
        Guid tenantId, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Health samples for a set of deployments over an interval, oldest first.
    ///
    /// <para><b>Why rows and not a ratio.</b> Three callers outside Delivery read this table and
    /// all three slice the samples again after fetching them: the error-budget service carves each
    /// deployment's own SLA window and a one-day burn window out of one wide fetch, the monthly
    /// report drops individual samples that fall inside a maintenance window and reports how many
    /// it dropped, and the customer status dashboard carves 7 days out of 30. A pre-computed
    /// percentage cannot answer any of them.</para>
    ///
    /// <para>⚠️ <b>The paragraph above was too strong, and <see cref="GetHealthRatiosAsync"/> is
    /// the correction.</b> It said a pre-computed percentage "cannot answer any of them", which is
    /// true only of a SINGLE call: carving a deployment's own SLA window, a one-day burn window, or
    /// 7 days out of 30 is a time slice, and a time slice is expressible as another call with
    /// another <paramref name="from"/>. Counting five sites rather than three found that four of
    /// them want nothing but the ratio — so the count-returning method has earned the place this
    /// note reserved for it, and those four now take it.</para>
    ///
    /// <para><b>One caller genuinely needs the rows, and it is the only one left:</b> the monthly
    /// report drops individual samples that fall inside a maintenance window and reports how many
    /// it dropped. That exclusion is per sample on an arbitrary predicate, not a time slice, so no
    /// count can stand in for it. This method exists for that caller.</para>
    ///
    /// <para><paramref name="to"/> is exclusive and optional: the monthly report needs a closed
    /// calendar month.</para>
    /// </summary>
    Task<IReadOnlyList<DeploymentHealthSample>> GetHealthSamplesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> deploymentIds, DateTime from,
        DateTime? to = null, CancellationToken ct = default);

    /// <summary>
    /// How many of each deployment's samples were healthy over an interval — the aggregate done in
    /// the database, for the callers that only ever wanted the ratio.
    ///
    /// <para><b>Why this exists.</b> Five call sites outside Delivery compute the identical
    /// <c>healthy / total × 100</c>, and four of them need nothing else. At five-minute sampling
    /// (<c>UptimeTrackingService</c>, 90-day retention) that is ~8,600 rows per deployment per
    /// month shipped out of the database to produce one number — and two of those four were paying
    /// it <em>through this very contract</em>, which is the worst version: the waste was already
    /// locked in behind the boundary that was supposed to prevent it.</para>
    ///
    /// <para>A caller wanting several intervals calls this once per interval. That is deliberate
    /// and it is still the cheap direction: the error-budget service needs each deployment's own
    /// SLA window plus a one-day burn window, which is a handful of calls returning one row per
    /// deployment, against one call returning up to <c>window × 288</c> rows per deployment.</para>
    ///
    /// <para>Deployments with no samples in the interval are omitted — see
    /// <see cref="DeploymentHealthRatio"/> for why that is not 0/0.</para>
    ///
    /// <para><paramref name="to"/> is exclusive and optional, matching
    /// <see cref="GetHealthSamplesAsync"/>.</para>
    /// </summary>
    Task<IReadOnlyList<DeploymentHealthRatio>> GetHealthRatiosAsync(
        Guid tenantId, IReadOnlyCollection<Guid> deploymentIds, DateTime from,
        DateTime? to = null, CancellationToken ct = default);

    /// <summary>Everything deployed to one cluster.</summary>
    /// <summary>
    /// The distinct namespaces a tenant's apps occupy on one cluster, governance lock first.
    ///
    /// <para>An app's namespace has two sources: the <c>AppEnvironment</c> row, where governance
    /// pins it, and the deployment's own. The pin wins when it is set — an app can be locked to a
    /// namespace before anything has deployed into it, and the lock is the statement of intent.
    /// This is <b>Delivery's rule about three of Delivery's tables</b>, and it was written out in
    /// a Catalog service, which is the wrong place for it to live however correct it was.</para>
    ///
    /// <para>Returned as namespace names rather than rows because that is the whole of what both
    /// callers want: a trust bundle and a certificate distribution each need the list to put in a
    /// namespace selector. A row-shaped answer would hand back app and environment ids that
    /// neither reads.</para>
    ///
    /// <para>Ordered, which the hand-written version was not. The list goes into a generated
    /// manifest, so an unstable order shows up as drift in a resource nobody changed.</para>
    /// </summary>
    Task<IReadOnlyList<string>> GetTenantNamespacesOnClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default);

    Task<IReadOnlyList<DeploymentSummary>> GetDeploymentsForClusterAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default);
}
