using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Jit;

/// <summary>Why a namespace cannot be the target of a grant right now.</summary>
public sealed record NamespaceExclusivityResult(bool IsExclusive, string? Reason)
{
    public static readonly NamespaceExclusivityResult Ok = new(true, null);
    public static NamespaceExclusivityResult Refused(string reason) => new(false, reason);
}

/// <summary>Where a grant will point, resolved from the app's deployments.</summary>
public sealed record JitTarget(Guid ClusterId, string ClusterName, string Namespace);

/// <summary>
/// The request/approve/revoke lifecycle of a just-in-time access grant.
///
/// Everything that touches a cluster lives behind <see cref="IJitProvisioner"/>, so this service
/// stays testable against a database alone and the workflow is meaningful even where no
/// provisioner is wired — a grant that is requested, approved and revoked with nothing minted is
/// still a complete audit record.
/// </summary>
public class JitAccessService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IJitProvisioner provisioner,
    AuditService auditService,
    ILogger<JitAccessService> logger)
{
    /// <summary>Default lifetime when an approver does not choose one.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Hard ceiling on a grant's lifetime. Anything longer stops being just-in-time and becomes a
    /// standing credential with extra steps, which is the thing this feature exists to avoid.
    /// </summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(8);

    /// <summary>
    /// The tenant permission an approver must hold.
    ///
    /// Its own feature rather than Deployments, which was the obvious reuse and is too wide:
    /// deploying an app changes what runs in a namespace, while approving this hands someone
    /// outside the organisation a shell into it. Nobody holds it until a tenant admin grants it,
    /// so the queue has no approvers until somebody decides who they are — which is the right way
    /// round for this particular permission.
    ///
    /// Deliberately not <see cref="CustomerAccessRole.Admin"/> either: that is a portal role held
    /// by the customer, so checking it would let a customer approve their own cluster access.
    /// </summary>
    public const TenantFeature ApproverFeature = TenantFeature.JitAccess;

    // ── Namespace exclusivity ─────────────────────────────────────────────────

    /// <summary>
    /// Checks that <paramref name="ns"/> on <paramref name="clusterId"/> belongs to this app and
    /// no other.
    ///
    /// The whole grant is namespace-scoped RBAC, so this is the assumption the security of the
    /// feature rests on: nothing in the schema stops two apps — potentially two customers — being
    /// pointed at one namespace, and if that has happened, a correctly-scoped grant quietly
    /// exposes the other app's workloads and Secrets.
    ///
    /// Public so the UI can warn before anyone fills in a request, and called again at approval
    /// because governance can re-point an app in between.
    /// </summary>
    public async Task<NamespaceExclusivityResult> CheckNamespaceExclusivityAsync(
        Guid appId, Guid clusterId, string ns, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return NamespaceExclusivityResult.Refused("The app has no namespace on this cluster.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Every app with a deployment in this namespace on this cluster, plus every app whose
        // governance lock points here — an app can be locked to a namespace before it has
        // deployed into it, and it would still be sharing.
        List<Guid> byDeployment = await db.AppDeployments
            .Where(d => d.ClusterId == clusterId && d.Namespace == ns && d.AppId != appId)
            .Select(d => d.AppId)
            .Distinct()
            .ToListAsync(ct);

        List<Guid> byGovernance = await db.AppEnvironments
            .Where(ae => ae.Namespace == ns && ae.AppId != appId)
            .Select(ae => ae.AppId)
            .Distinct()
            .ToListAsync(ct);

        List<Guid> others = byDeployment.Concat(byGovernance).Distinct().ToList();
        if (others.Count == 0) return NamespaceExclusivityResult.Ok;

        List<string> names = await db.Apps
            .Where(a => others.Contains(a.Id))
            .Select(a => a.Name)
            .OrderBy(n => n)
            .ToListAsync(ct);

        return NamespaceExclusivityResult.Refused(
            $"Namespace '{ns}' is shared with {string.Join(", ", names)}. A namespace-scoped grant "
            + "would expose those apps too, so access cannot be granted until they are separated.");
    }

    /// <summary>
    /// Resolves where a grant for this app+environment would point.
    ///
    /// An environment can host several clusters, so an app with deployments on more than one is
    /// ambiguous and the caller has to say which. Returning the candidates rather than picking
    /// one keeps that decision with the approver.
    /// </summary>
    public async Task<List<JitTarget>> ResolveTargetsAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        string? lockedNs = await db.AppEnvironments
            .Where(ae => ae.AppId == appId && ae.EnvironmentId == environmentId)
            .Select(ae => ae.Namespace)
            .FirstOrDefaultAsync(ct);

        var deployments = await db.AppDeployments
            .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
            .Select(d => new { d.ClusterId, ClusterName = d.Cluster.Name, d.Namespace })
            .ToListAsync(ct);

        return deployments
            .Select(d => new JitTarget(
                d.ClusterId,
                d.ClusterName,
                string.IsNullOrWhiteSpace(lockedNs) ? d.Namespace : lockedNs!))
            .DistinctBy(t => (t.ClusterId, t.Namespace))
            .OrderBy(t => t.ClusterName, StringComparer.Ordinal)
            .ToList();
    }

    // ── Request ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a request for access. Nothing is granted here and no cluster is touched — the row
    /// exists so an approver has something to act on.
    ///
    /// The two people involved are identified differently on purpose.
    /// <paramref name="subjectUserId"/> is an account id, because it is a foreign key and the
    /// thing every later authorisation decision is made against; <paramref name="requestedBy"/>
    /// is a display name, because it is only ever read by a human working the queue. Passing an
    /// email as the subject is the one mistake this signature invites, so it is refused below
    /// rather than left to surface as a foreign-key violation.
    /// </summary>
    public async Task<JitGrant> RequestAsync(
        Guid appId,
        Guid environmentId,
        string subjectUserId,
        string reason,
        string requestedBy,
        Guid? clusterId = null,
        string? ticketRef = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("A reason is required — an unexplained grant is not reviewable.");

        List<JitTarget> targets = await ResolveTargetsAsync(appId, environmentId, ct);

        if (targets.Count == 0)
            throw new InvalidOperationException(
                "This app has no deployment in this environment, so there is no namespace to grant access to.");

        JitTarget target;
        if (clusterId is null)
        {
            if (targets.Select(t => t.ClusterId).Distinct().Count() > 1)
                throw new InvalidOperationException(
                    "This app is deployed to more than one cluster in this environment. "
                    + "Specify which cluster the access is for.");
            target = targets[0];
        }
        else
        {
            target = targets.FirstOrDefault(t => t.ClusterId == clusterId)
                ?? throw new InvalidOperationException(
                    "That cluster does not host this app in this environment.");
        }

        NamespaceExclusivityResult exclusivity =
            await CheckNamespaceExclusivityAsync(appId, target.ClusterId, target.Namespace, ct);
        if (!exclusivity.IsExclusive)
            throw new InvalidOperationException(exclusivity.Reason);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        var app = await db.Apps
            .Where(a => a.Id == appId)
            .Select(a => new { a.CustomerId, a.Customer.TenantId })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("App not found.");

        var subject = await db.Users
            .Where(u => u.Id == subjectUserId)
            .Select(u => new { u.Email, u.UserName })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "The person this access is for could not be identified. A grant is keyed to an "
                + "account, not to an email address.");

        string subjectName = subject.Email ?? subject.UserName ?? subjectUserId;

        JitGrant grant = new()
        {
            Id = Guid.NewGuid(),
            TenantId = app.TenantId,
            CustomerId = app.CustomerId,
            AppId = appId,
            EnvironmentId = environmentId,
            KubernetesClusterId = target.ClusterId,
            Namespace = target.Namespace,
            UserId = subjectUserId,
            Reason = reason.Trim(),
            TicketRef = string.IsNullOrWhiteSpace(ticketRef) ? null : ticketRef.Trim(),
            RequestedBy = requestedBy,
            RequestedAt = DateTime.UtcNow,
        };

        db.JitGrants.Add(grant);
        await db.SaveChangesAsync(ct);

        await auditService.RecordAsync(null, "JitAccessRequested", "JitGrant", grant.Id.ToString(),
            $"{target.Namespace} on {target.ClusterName} for {subjectName}: {grant.Reason}",
            requestedBy, ct);

        logger.LogInformation(
            "JIT access requested for {User} on {Namespace} (grant {GrantId}) by {RequestedBy}",
            subjectName, target.Namespace, grant.Id, requestedBy);

        return grant;
    }

    // ── Approve ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Approves a pending request, mints the cluster credential and returns the plaintext token —
    /// the only time it exists outside the caller's hands.
    ///
    /// The exclusivity check runs again here rather than trusting the one from request time:
    /// governance can re-point an app between the two, and this is the moment the grant's
    /// namespace is frozen.
    ///
    /// The approver arrives as both an account id and a display name for the same reason the
    /// requester does: <paramref name="approverUserId"/> is what the permission check and the
    /// subject comparison are made against, <paramref name="approverDisplay"/> is what a person
    /// reads in the history and what <see cref="JitGrant.RequestedBy"/> is compared to.
    /// </summary>
    public async Task<JitApproval> ApproveAsync(
        Guid grantId,
        string approverUserId,
        string approverDisplay,
        JitAccessLevel level,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        JitGrant grant = await db.JitGrants
            .Include(g => g.KubernetesCluster)
            .Include(g => g.User)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct)
            ?? throw new InvalidOperationException("Grant not found.");

        DateTime now = DateTime.UtcNow;
        if (grant.StatusAt(now) != JitGrantStatus.Pending)
            throw new InvalidOperationException(
                $"This request is {grant.StatusAt(now).ToString().ToLowerInvariant()} and cannot be approved.");

        // Self-approval defeats the point of an approval step. Checked on both the requester and
        // the subject: approving your own request and approving a request someone filed on your
        // behalf are the same thing from the cluster's side. Each is compared against the
        // identifier it was stored as — an id against an id, a display name against a display
        // name — because comparing across the two silently never matches.
        if (string.Equals(grant.RequestedBy, approverDisplay, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A request cannot be approved by the person who made it.");

        if (string.Equals(grant.UserId, approverUserId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A request cannot be approved by the person it grants access to.");

        if (!await IsApproverAsync(db, grant.TenantId, approverUserId, ct))
            throw new InvalidOperationException(
                "Approving JIT access needs the 'JIT cluster access' permission at Manage in this tenant.");

        NamespaceExclusivityResult exclusivity = await CheckNamespaceExclusivityAsync(
            grant.AppId, grant.KubernetesClusterId, grant.Namespace, ct);
        if (!exclusivity.IsExclusive)
            throw new InvalidOperationException(exclusivity.Reason);

        TimeSpan window = ClampDuration(duration);

        grant.Level = level;
        grant.ApprovedAt = now;
        grant.ApprovedBy = approverDisplay;
        grant.ExpiresAt = now.Add(window);

        MintedCredential credential = await provisioner.MintAsync(grant, window, ct);

        grant.ServiceAccountName = credential.ServiceAccountName;
        grant.TokenHash = credential.TokenHash;
        grant.DisplayPrefix = credential.DisplayPrefix;
        grant.TokenExpiresAt = credential.TokenExpiresAt;

        await db.SaveChangesAsync(ct);

        await auditService.RecordAsync(null, "JitAccessApproved", "JitGrant", grant.Id.ToString(),
            $"{level} on {grant.Namespace} until {grant.ExpiresAt:u} for {SubjectName(grant)}",
            approverDisplay, ct);

        logger.LogInformation(
            "JIT grant {GrantId} approved by {Approver} at {Level} until {ExpiresAt}",
            grant.Id, approverDisplay, level, grant.ExpiresAt);

        return new JitApproval(grant, credential.Plaintext, credential.Kubeconfig);
    }

    /// <summary>
    /// Who a grant is for, written the way a person reads it. <see cref="JitGrant.UserId"/> is an
    /// account id: right as a key, meaningless in an audit row or a confirmation dialog. Falls
    /// back to the id when the account was not loaded, so a caller that forgot the include gets a
    /// less useful string rather than a null reference.
    /// </summary>
    public static string SubjectName(JitGrant grant) =>
        grant.User?.Email ?? grant.User?.UserName ?? grant.UserId;

    /// <summary>
    /// Clamps a requested lifetime into the allowed window. A caller asking for longer than
    /// <see cref="MaximumDuration"/> gets the maximum rather than an error — refusing would only
    /// push people to approve twice in a row for the same effect.
    /// </summary>
    public static TimeSpan ClampDuration(TimeSpan? requested)
    {
        if (requested is null) return DefaultDuration;
        if (requested.Value <= TimeSpan.Zero) return DefaultDuration;
        return requested.Value > MaximumDuration ? MaximumDuration : requested.Value;
    }

    /// <summary>
    /// Whether a user may approve grants in a tenant. Membership alone is not enough — a viewer
    /// is a member. <paramref name="userId"/> is an account id: tenant membership is keyed to
    /// one, so an email here is not a permission failure but a lookup that never matches.
    /// </summary>
    public async Task<bool> IsApproverAsync(Guid tenantId, string userId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await IsApproverAsync(db, tenantId, userId, ct);
    }

    private static async Task<bool> IsApproverAsync(
        ApplicationDbContext db, Guid tenantId, string userId, CancellationToken ct)
    {
        string? permissionsJson = await db.TenantMemberships
            .Where(m => m.TenantId == tenantId && m.UserId == userId)
            .Select(m => m.Role.PermissionsJson)
            .FirstOrDefaultAsync(ct);

        if (permissionsJson is null) return false;

        Dictionary<TenantFeature, AccessLevel> permissions =
            TenantRoleService.DecodePermissions(permissionsJson);

        return TenantRoleService.GetPermission(permissions, ApproverFeature) >= AccessLevel.Manage;
    }

    // ── Deny / revoke ─────────────────────────────────────────────────────────

    /// <summary>Refuses a pending request. The row is kept — a refusal is part of the trail.</summary>
    public async Task<JitGrant> DenyAsync(
        Guid grantId, string deniedBy, string? reason = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        JitGrant grant = await db.JitGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct)
            ?? throw new InvalidOperationException("Grant not found.");

        if (grant.StatusAt(DateTime.UtcNow) != JitGrantStatus.Pending)
            throw new InvalidOperationException("Only a pending request can be denied.");

        grant.DeniedAt = DateTime.UtcNow;
        grant.DeniedBy = deniedBy;
        grant.RevokeReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        await db.SaveChangesAsync(ct);

        await auditService.RecordAsync(null, "JitAccessDenied", "JitGrant", grant.Id.ToString(),
            reason, deniedBy, ct);

        return grant;
    }

    /// <summary>
    /// Ends a live grant immediately.
    ///
    /// The cluster objects go first. A bound ServiceAccount token cannot be invalidated before it
    /// expires, so deleting the RoleBinding is what actually stops the credential working —
    /// marking the row revoked only stops the proxy, and the proxy is not the only thing that
    /// could reach the API server with that token.
    /// </summary>
    public async Task<JitGrant> RevokeAsync(
        Guid grantId, string revokedBy, string? reason = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        JitGrant grant = await db.JitGrants
            .Include(g => g.KubernetesCluster)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct)
            ?? throw new InvalidOperationException("Grant not found.");

        if (grant.RevokedAt is not null) return grant;

        if (grant.ApprovedAt is not null && grant.TornDownAt is null)
        {
            await provisioner.TearDownAsync(grant, ct);
            grant.TornDownAt = DateTime.UtcNow;
        }

        grant.RevokedAt = DateTime.UtcNow;
        grant.RevokedBy = revokedBy;
        grant.RevokeReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        await db.SaveChangesAsync(ct);

        await auditService.RecordAsync(null, "JitAccessRevoked", "JitGrant", grant.Id.ToString(),
            reason, revokedBy, ct);

        logger.LogInformation("JIT grant {GrantId} revoked by {RevokedBy}", grant.Id, revokedBy);

        return grant;
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>Grants for a tenant, newest first. Includes finished ones — that is the history.</summary>
    public async Task<List<JitGrant>> ListForTenantAsync(
        Guid tenantId, int limit = 100, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.JitGrants
            .Include(g => g.User)
            .Include(g => g.App)
            .Include(g => g.KubernetesCluster)
            .Include(g => g.Environment)
            .Where(g => g.TenantId == tenantId)
            .OrderByDescending(g => g.RequestedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>Requests awaiting a decision in a tenant, oldest first — a queue, not a feed.</summary>
    public async Task<List<JitGrant>> ListPendingAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.JitGrants
            .Include(g => g.User)
            .Include(g => g.App)
            .Include(g => g.KubernetesCluster)
            .Include(g => g.Environment)
            .Where(g => g.TenantId == tenantId
                        && g.ApprovedAt == null && g.DeniedAt == null && g.RevokedAt == null)
            .OrderBy(g => g.RequestedAt)
            .ToListAsync(ct);
    }

    /// <summary>A user's own grants, newest first — what the portal shows them.</summary>
    public async Task<List<JitGrant>> ListForUserAsync(
        string userId, int limit = 50, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.JitGrants
            .Include(g => g.App)
            .Include(g => g.KubernetesCluster)
            .Include(g => g.Environment)
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.RequestedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<JitGrant?> GetAsync(Guid grantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.JitGrants
            .Include(g => g.App)
            .Include(g => g.KubernetesCluster)
            .Include(g => g.Environment)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct);
    }
}

/// <summary>What an approval hands back. The plaintext and kubeconfig exist only here.</summary>
public sealed record JitApproval(JitGrant Grant, string? Plaintext, string? Kubeconfig);
