using EntKube.Contracts.Telemetry;
using EntKube.Web.Data.Modules;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ContractIncidentStatus = EntKube.Contracts.Telemetry.IncidentStatus;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Telemetry's implementation of <see cref="ITelemetryApi"/>, over its own module context.
///
/// <para>Tenancy reaches an incident through its cluster, which is where the tenant lives in the
/// schema, so every query goes <c>i.Cluster.TenantId</c> rather than taking a column at face
/// value — the same shape as <c>CatalogApi</c>, for the same reason.</para>
/// </summary>
public sealed class TelemetryApi(IDbContextFactory<TelemetryDbContext> dbFactory) : ITelemetryApi
{
    public async Task<IReadOnlyList<IncidentSummary>> GetIncidentsAsync(
        Guid tenantId, bool openOnly = false, int limit = 200, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<Data.AlertIncident> query = Scoped(db, tenantId);

        if (openOnly)
        {
            query = query.Where(i => i.Status != Data.IncidentStatus.Resolved);
        }

        // The rows come back carrying the ENTITY's enum, and the contract's copy is mapped in
        // memory below. That is not a style choice: AlertIncident.Status is persisted as text
        // (HasConversion<string>() in TelemetryModel), so a cast inside this projection would
        // become CAST("Status" AS integer) in SQL — which PostgreSQL rejects outright and SQLite
        // answers with 0. That exact mistake took the cost report down (#223) and passed its unit
        // tests the whole time.
        List<Row> rows = await query
            .OrderByDescending(i => i.StartsAt)
            .Take(limit)
            .Select(ToRow)
            .ToListAsync(ct);

        return [.. rows.Select(ToContract)];
    }

    private static IQueryable<Data.AlertIncident> Scoped(TelemetryDbContext db, Guid tenantId)
        => db.AlertIncidents.AsNoTracking().Where(i => i.Cluster.TenantId == tenantId);

    /// <summary>The row as the database hands it over, carrying Telemetry's own enum.</summary>
    private sealed record Row(
        Guid Id,
        Guid ClusterId,
        string ClusterName,
        string AlertName,
        string Summary,
        string Severity,
        Data.IncidentStatus Status,
        DateTime StartsAt,
        DateTime? ResolvedAt,
        string? AcknowledgedBy,
        string? RunbookUrl);

    /// <summary>Applied after any filter — see CatalogApi for why the order matters.</summary>
    private static readonly Expression<Func<Data.AlertIncident, Row>> ToRow =
        i => new Row(
            i.Id,
            i.ClusterId,
            i.Cluster.Name,
            i.AlertName,
            i.Summary,
            i.Severity,
            i.Status,
            i.StartsAt,
            i.ResolvedAt,
            i.AcknowledgedBy,
            i.RunbookUrl);

    /// <summary>
    /// Entity enum to contract enum by ordinal, which is sound only while the two agree member for
    /// member — so <c>ModuleApiTests</c> checks that they do rather than trusting it. The public
    /// API publishes these names.
    /// </summary>
    private static IncidentSummary ToContract(Row r) => new(
        r.Id,
        r.ClusterId,
        r.ClusterName,
        r.AlertName,
        r.Summary,
        r.Severity,
        (ContractIncidentStatus)(int)r.Status,
        r.StartsAt,
        r.ResolvedAt,
        r.AcknowledgedBy,
        r.RunbookUrl);
}
