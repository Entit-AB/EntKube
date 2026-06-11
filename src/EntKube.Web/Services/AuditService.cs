using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Writes and retrieves AuditEvents. Injected into services that perform
/// destructive operations so there is always a human-readable activity
/// timeline attached to every deployment.
/// </summary>
public class AuditService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task RecordAsync(
        Guid? deploymentId,
        string action,
        string resourceKind,
        string? resourceName = null,
        string? details = null,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

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
    public async Task<List<AuditEvent>> GetDeploymentEventsAsync(
        Guid deploymentId, int limit = 50, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.AuditEvents
            .Where(a => a.DeploymentId == deploymentId)
            .OrderByDescending(a => a.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
