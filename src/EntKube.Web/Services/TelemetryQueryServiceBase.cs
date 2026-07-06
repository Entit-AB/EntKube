using Npgsql;

namespace EntKube.Web.Services;

/// <summary>
/// Shared plumbing for the native telemetry query services (logs, traces): tenant resolution gated on
/// the store being enabled, a uniform failure result, and a table-presence probe. Keeps PgLogService
/// and PgTraceService from each re-implementing the same connect/resolve/guard boilerplate.
/// </summary>
public abstract class TelemetryQueryServiceBase(TelemetryStore store, ClusterTenantResolver tenants, ILogger logger)
{
    /// <summary>The telemetry store — subclasses query through this rather than capturing it themselves.</summary>
    protected TelemetryStore Store { get; } = store;

    /// <summary>Logger — subclasses log through this rather than capturing their own.</summary>
    protected ILogger Logger { get; } = logger;

    /// <summary>Tenant for the cluster, or null when the store is disabled or the cluster is unknown.</summary>
    protected async Task<Guid?> ResolveOrNull(Guid clusterId, CancellationToken ct)
        => Store.IsEnabled ? await tenants.ResolveAsync(clusterId, ct) : null;

    protected static KubernetesOperationResult<T> Fail<T>(
        string message = "Native telemetry store is not configured or cluster not found.")
        => KubernetesOperationResult<T>.Failure(message);

    /// <summary>True when <paramref name="table"/> holds any row for this cluster (feature-gating / routing).</summary>
    protected async Task<bool> HasAnyAsync(string table, Guid clusterId, CancellationToken ct)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return false;

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            // table is an internal literal (logs/spans), never user input.
            cmd.CommandText = $"SELECT 1 FROM {table} WHERE tenant_id = @t AND cluster_id = @c LIMIT 1";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Telemetry presence probe failed ({Table}, cluster {Cluster})", table, clusterId);
            return false;
        }
    }
}
