namespace EntKube.Web.Data;

/// <summary>
/// How much a just-in-time grant is allowed to do inside the app's namespace.
///
/// A deny-list rather than a trimmed allow-list: a namespaced Role still reaches past its
/// namespace through a handful of verbs, so each level is defined by what it deliberately
/// withholds. See <see cref="Services.Jit.JitRbacBuilder"/> for the rules each one renders.
/// </summary>
public enum JitAccessLevel
{
    /// <summary>
    /// Read the workload and its logs. What the feature exists for, and the only level that
    /// grants nothing an operator would have to think twice about.
    /// </summary>
    Observe = 0,

    /// <summary>
    /// Observe, plus ConfigMaps and <c>pods/exec</c>. Exec yields the target pod's own
    /// ServiceAccount token, so this level is worth exactly what that ServiceAccount is
    /// constrained to — never grant it where the app's RBAC has not been reviewed.
    /// </summary>
    Troubleshoot = 1,

    /// <summary>
    /// Troubleshoot, plus deleting pods and scaling deployments. Enough to restart a stuck
    /// workload; still nothing that reads a Secret or leaves the namespace.
    /// </summary>
    Operate = 2,
}

/// <summary>Where a grant is in its lifecycle. Derived from timestamps, never stored.</summary>
public enum JitGrantStatus
{
    Pending,
    Denied,
    Active,
    Expired,
    Revoked,
}

/// <summary>
/// A time-boxed grant of <c>kubectl</c> access to one app, in one environment, on one cluster,
/// for one person.
///
/// Modelled on <see cref="ApiToken"/>: only the hash of the credential is stored, the row is
/// retained after revocation rather than deleted, and expiry is an explicit column instead of
/// something inferred. The difference is that an API token is a standing credential and this is
/// not — a grant is requested, approved by someone else, and dies on its own.
///
/// Rows are never deleted. A grant that was used is the only record that it happened.
/// </summary>
public class JitGrant
{
    public Guid Id { get; set; }

    // ── Scope ─────────────────────────────────────────────────────────────────

    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid AppId { get; set; }
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// The cluster this grant reaches. Named explicitly rather than derived: an environment can
    /// host several clusters, so "the app's cluster" is not a well-defined thing.
    /// </summary>
    public Guid KubernetesClusterId { get; set; }

    /// <summary>
    /// The namespace this grant is confined to, captured when the grant was approved.
    ///
    /// A snapshot, not a lookup. If governance later re-points the app at a different namespace,
    /// a live grant must keep meaning what it meant when someone approved it — resolving this
    /// at request time would silently re-target a credential that is already in someone's hands.
    /// </summary>
    public required string Namespace { get; set; }

    // ── Subject ───────────────────────────────────────────────────────────────

    /// <summary>The person the grant is for. Not necessarily the person who requested it.</summary>
    public required string UserId { get; set; }

    /// <summary>
    /// What was actually granted. Meaningless until <see cref="ApprovedAt"/> is set — read it
    /// through <see cref="StatusAt"/> rather than on its own, or a pending request looks like an
    /// Observe grant.
    /// </summary>
    public JitAccessLevel Level { get; set; } = JitAccessLevel.Observe;

    // ── Justification ─────────────────────────────────────────────────────────

    /// <summary>Why this access is needed. Required — an unexplained grant is not reviewable.</summary>
    public required string Reason { get; set; }

    /// <summary>Optional pointer to a ticket or incident.</summary>
    public string? TicketRef { get; set; }

    /// <summary>
    /// What the requester said they need, and for how long.
    ///
    /// Part of the ask, not of the grant: <see cref="Level"/> and <see cref="ExpiresAt"/> are what
    /// an approver actually decided, and stay unset until they do. Keeping the two apart is what
    /// lets the queue show "asked for Operate, granted Observe" — a record of a request being
    /// trimmed, which is exactly the kind of decision this table exists to preserve.
    ///
    /// Neither binds the approver. They seed the pickers and no more.
    /// </summary>
    public JitAccessLevel RequestedLevel { get; set; } = JitAccessLevel.Observe;

    /// <summary>
    /// How long the requester asked for, in minutes. Clamped into the allowed window on the way
    /// in, so a stored value is always one an approver could grant as it stands.
    /// </summary>
    public int RequestedMinutes { get; set; } = 60;

    // ── Workflow ──────────────────────────────────────────────────────────────

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public required string RequestedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    /// <summary>Set when an approver refuses. A denied grant is kept, like a revoked one.</summary>
    public DateTime? DeniedAt { get; set; }
    public string? DeniedBy { get; set; }

    /// <summary>
    /// When the grant stops being valid. Null until approval — a pending request has no clock
    /// running, because nothing has been granted yet.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevokeReason { get; set; }

    // ── Credential ────────────────────────────────────────────────────────────

    /// <summary>
    /// The per-grant ServiceAccount in the app's namespace. Per-grant rather than shared because
    /// a bound token cannot be invalidated early — "revoke now" has to mean deleting the
    /// RoleBinding, and that only works when the binding belongs to exactly one grant.
    /// </summary>
    public string? ServiceAccountName { get; set; }

    /// <summary>SHA-256 of the EntKube-issued token, hex-encoded. The plaintext is shown once.</summary>
    public string? TokenHash { get; set; }

    /// <summary>First characters of the token, in the clear, so two grants can be told apart.</summary>
    public string? DisplayPrefix { get; set; }

    /// <summary>
    /// When the bound ServiceAccount token itself expires, as reported by the API server — which
    /// may cap a requested duration. Authoritative over <see cref="ExpiresAt"/> for the cluster
    /// credential; <see cref="ExpiresAt"/> governs the proxy.
    /// </summary>
    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>Stamped on use, throttled. Null means the grant was approved but never used.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// The bound ServiceAccount token, AES-256-GCM encrypted under <see cref="ClusterTokenKey"/>.
    ///
    /// This is a live cluster credential and never leaves EntKube — the customer holds the
    /// <see cref="TokenHash"/> side instead, and the proxy swaps one for the other. Its key is
    /// sealed per-grant rather than shared, so there is no single key whose loss exposes every
    /// grant, and clearing this row destroys the credential rather than merely orphaning it.
    /// </summary>
    public byte[]? EncryptedClusterToken { get; set; }

    public byte[]? ClusterTokenNonce { get; set; }

    /// <summary>The grant's own data key, sealed with the platform root key.</summary>
    public byte[]? ClusterTokenKey { get; set; }

    public byte[]? ClusterTokenKeyNonce { get; set; }

    /// <summary>
    /// Set once the cluster objects have been removed, so the reaper can tell a grant it has
    /// already cleaned up from one that expired a second ago and still has RBAC in the cluster.
    /// </summary>
    public DateTime? TornDownAt { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────

    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    // ── Derived ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Where this grant stands, as of <paramref name="now"/>. Computed rather than stored: a
    /// status column would need something to write "Expired" into it, and a grant whose expiry
    /// depends on a background job having run is not actually time-boxed.
    /// </summary>
    public JitGrantStatus StatusAt(DateTime now) =>
        RevokedAt is not null ? JitGrantStatus.Revoked
        : DeniedAt is not null ? JitGrantStatus.Denied
        : ApprovedAt is null ? JitGrantStatus.Pending
        : ExpiresAt is null || ExpiresAt <= now ? JitGrantStatus.Expired
        : JitGrantStatus.Active;

    /// <summary>True when the grant may still be used to reach the cluster.</summary>
    public bool IsLiveAt(DateTime now) => StatusAt(now) == JitGrantStatus.Active;
}
