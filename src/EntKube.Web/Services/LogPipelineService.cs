using EntKube.Telemetry.LogPipelines;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>What became of a query. Exactly one of the three is set.</summary>
public sealed record LogPipelineOutcome(
    LogPipelineResult? Result, LogPipelineError? SyntaxError, string? Failure);

/// <summary>
/// Runs a log query against one cluster through <see cref="LogQueryService"/>, so a cluster on Loki
/// answers it as well as one on the native store.
///
/// <para>⚠️ <b>The tenant check on the cluster is here, once, for every caller.</b> The native log
/// layer resolves the tenant from the cluster, so it does not refuse a foreign id: it reads that
/// cluster's own tenant's logs. A cluster id often arrives from a browser's select element, so
/// leaving this to each page would make one forgotten guard a cross-tenant read.</para>
/// </summary>
public sealed class LogPipelineService(LogQueryService logs, IDbContextFactory<TelemetryDbContext> dbFactory)
{
    public async Task<LogPipelineOutcome> RunAsync(
        Guid tenantId, Guid clusterId, string text, DateTime from, DateTime to, CancellationToken ct = default)
    {
        (LogPipelineQuery? query, LogPipelineError? error) = LogPipelineParser.Parse(text);
        if (query is null) return new LogPipelineOutcome(null, error, null);

        await using (TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (!await db.KubernetesClusters.AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct))
            {
                return new LogPipelineOutcome(null, null, "That cluster does not exist.");
            }
        }

        int windowMinutes = Math.Max(1, (int)Math.Ceiling((to - from).TotalMinutes));

        LogPipelineSource source = new(
            (filter, limit, token) => logs.QueryAsync(tenantId, clusterId, filter, from, to, limit, ct: token),
            token => logs.GetNamespacesAsync(tenantId, clusterId, windowMinutes: windowMinutes, ct: token));

        KubernetesOperationResult<LogPipelineResult> run =
            await LogPipelineEngine.RunAsync(query, source, ct: ct);

        return run.IsSuccess
            ? new LogPipelineOutcome(run.Data, null, null)
            : new LogPipelineOutcome(null, null, run.Error ?? "The logs could not be read.");
    }
}

/// <summary>CRUD for a tenant's saved log queries.</summary>
public sealed class SavedLogQueryService(IDbContextFactory<TelemetryDbContext> dbFactory)
{
    public async Task<List<SavedLogQuery>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SavedLogQueries.AsNoTracking()
            .Where(q => q.TenantId == tenantId)
            .OrderBy(q => q.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Saves under a name, replacing a query of that name in this tenant, so saving twice edits
    /// rather than duplicates. Returns the problem when the text would not run, else null: a saved
    /// query that cannot run is a trap for whoever opens it next.
    /// </summary>
    public async Task<string?> SaveAsync(
        Guid tenantId, string name, string query, string? actor, CancellationToken ct = default)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return "Give the query a name.";
        if (trimmed.Length > 200) return "A name can be at most 200 characters.";

        (_, LogPipelineError? error) = LogPipelineParser.Parse(query);
        if (error is not null) return error.Message;

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime now = DateTime.UtcNow;

        SavedLogQuery? existing = await db.SavedLogQueries
            .FirstOrDefaultAsync(q => q.TenantId == tenantId && q.Name == trimmed, ct);

        if (existing is null)
        {
            db.SavedLogQueries.Add(new SavedLogQuery
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = trimmed,
                Query = query,
                CreatedBy = actor,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Query = query;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.SavedLogQueries.Where(q => q.Id == id && q.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }
}
