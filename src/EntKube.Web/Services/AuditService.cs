using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Writes and retrieves AuditEvents. Injected into services that perform
/// destructive operations so there is always a human-readable activity
/// timeline attached to every deployment.
/// </summary>
public class AuditService(
    IDbContextFactory<IdentityDbContext> dbFactory,
    EntKube.Contracts.Delivery.IDeliveryApi delivery)
{
    /// <summary>
    /// ⚠️ <b>Takes no tenant, on a stated precondition.</b> <paramref name="deploymentId"/> is
    /// the only argument that could misattribute an event, and its sole caller —
    /// <c>AuditClusterChangeRecorder</c> — passes null for it always, because the change gate
    /// "is handed a change and a sink, never an identity". There is no tenant for it to give,
    /// and inventing one is what that caller already refuses to do for <c>performedBy</c>.
    ///
    /// <para>So the precondition is this: <b>the first caller that passes a real
    /// <paramref name="deploymentId"/> must pass the tenant too</b>, and this method must then
    /// check it the way <see cref="GetDeploymentEventsAsync"/> does. Writing an event onto
    /// another tenant's deployment puts a line in their audit trail, which is worse than a
    /// missing one: an audit trail is read as evidence.</para>
    /// </summary>
    public async Task RecordAsync(
        Guid? deploymentId,
        string action,
        string resourceKind,
        string? resourceName = null,
        string? details = null,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        using IdentityDbContext db = dbFactory.CreateDbContext();

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Action = action,
            ResourceKind = resourceKind,
            ResourceName = resourceName,
            Details = details,
            PerformedBy = performedBy,
            OccurredAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the most recent audit events for a deployment, newest first.
    /// </summary>
    /// <param name="tenantId">
    /// Whose deployment. An audit event names what was done, to which resource, by whom — a
    /// restart, a scale, a helm install, with the operator's address in <c>PerformedBy</c>. So
    /// another tenant's trail is an operations diary and a staff list at once.
    ///
    /// <para><c>AuditEvent</c> carries no tenant of its own, and <c>AppDeployments</c> is
    /// Delivery's table — IdentityDbContext does not expose it, and reaching into it would be
    /// the coupling the module tests reject. So the question goes to the contract, which
    /// already answers it: <c>GetDeploymentAsync</c> returns null for a deployment that is not
    /// this tenant's.</para>
    /// </summary>
    public async Task<List<AuditEvent>> GetDeploymentEventsAsync(
        Guid tenantId, Guid deploymentId, int limit = 50, CancellationToken ct = default)
    {
        if (await delivery.GetDeploymentAsync(tenantId, deploymentId, ct) is null)
        {
            return [];
        }

        using IdentityDbContext db = dbFactory.CreateDbContext();

        return await db.AuditEvents
            .Where(a => a.DeploymentId == deploymentId)
            .OrderByDescending(a => a.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
