using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// CRUD for cluster blueprints (tenant-scoped bootstrap recipes) and the entry
/// points for launching / resuming / cancelling a bootstrap run. Actual step
/// execution is performed asynchronously by <see cref="BootstrapRunnerService"/>;
/// this service only manages the data/state side.
///
/// Step parameters — both component form-field values and service create args —
/// are stored uniformly as a JSON string→string map so bootstrap-time overrides
/// and form rendering can be handled generically.
/// </summary>
public class ClusterBlueprintService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // ──────── Blueprint CRUD ────────

    public async Task<List<ClusterBlueprint>> GetBlueprintsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterBlueprints
            .Where(b => b.TenantId == tenantId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
    }

    public async Task<ClusterBlueprint?> GetBlueprintAsync(Guid blueprintId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterBlueprints
            .Include(b => b.Steps.OrderBy(s => s.Order))
            .FirstOrDefaultAsync(b => b.Id == blueprintId, ct);
    }

    public async Task<ClusterBlueprint> CreateBlueprintAsync(
        Guid tenantId, string name, string? description, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (await db.ClusterBlueprints.AnyAsync(b => b.TenantId == tenantId && b.Name == name, ct))
        {
            throw new InvalidOperationException($"A blueprint named '{name}' already exists.");
        }

        ClusterBlueprint blueprint = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
        };

        db.ClusterBlueprints.Add(blueprint);
        await db.SaveChangesAsync(ct);
        return blueprint;
    }

    public async Task UpdateBlueprintAsync(
        Guid blueprintId, string name, string? description, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterBlueprint blueprint = await db.ClusterBlueprints.FirstAsync(b => b.Id == blueprintId, ct);
        blueprint.Name = name.Trim();
        blueprint.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteBlueprintAsync(Guid blueprintId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterBlueprint? blueprint = await db.ClusterBlueprints.FirstOrDefaultAsync(b => b.Id == blueprintId, ct);
        if (blueprint is not null)
        {
            db.ClusterBlueprints.Remove(blueprint);
            await db.SaveChangesAsync(ct);
        }
    }

    // ──────── Step CRUD ────────

    public async Task<BlueprintStep> AddStepAsync(
        Guid blueprintId, BlueprintStepType stepType, string key, string name,
        string? @namespace, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        int nextOrder = await db.BlueprintSteps
            .Where(s => s.BlueprintId == blueprintId)
            .Select(s => (int?)s.Order)
            .MaxAsync(ct) is int max ? max + 1 : 0;

        BlueprintStep step = new()
        {
            Id = Guid.NewGuid(),
            BlueprintId = blueprintId,
            Order = nextOrder,
            StepType = stepType,
            Key = key,
            Name = name.Trim(),
            Namespace = string.IsNullOrWhiteSpace(@namespace) ? null : @namespace.Trim(),
            ParametersJson = SerializeParams(parameters)
        };

        db.BlueprintSteps.Add(step);
        await db.SaveChangesAsync(ct);
        return step;
    }

    public async Task UpdateStepAsync(
        Guid stepId, string name, string? @namespace,
        IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BlueprintStep step = await db.BlueprintSteps.FirstAsync(s => s.Id == stepId, ct);
        step.Name = name.Trim();
        step.Namespace = string.IsNullOrWhiteSpace(@namespace) ? null : @namespace.Trim();
        step.ParametersJson = SerializeParams(parameters);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteStepAsync(Guid stepId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BlueprintStep? step = await db.BlueprintSteps.FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (step is null) return;

        Guid blueprintId = step.BlueprintId;
        db.BlueprintSteps.Remove(step);
        await db.SaveChangesAsync(ct);

        // Re-pack order values so they stay contiguous (0,1,2,...).
        List<BlueprintStep> remaining = await db.BlueprintSteps
            .Where(s => s.BlueprintId == blueprintId)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);
        for (int i = 0; i < remaining.Count; i++)
        {
            remaining[i].Order = i;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Swaps a step with its neighbour in the given direction (-1 up, +1 down).</summary>
    public async Task MoveStepAsync(Guid stepId, int direction, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BlueprintStep step = await db.BlueprintSteps.FirstAsync(s => s.Id == stepId, ct);

        List<BlueprintStep> siblings = await db.BlueprintSteps
            .Where(s => s.BlueprintId == step.BlueprintId)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

        int idx = siblings.FindIndex(s => s.Id == stepId);
        int target = idx + direction;
        if (target < 0 || target >= siblings.Count) return;

        (siblings[idx].Order, siblings[target].Order) = (siblings[target].Order, siblings[idx].Order);
        await db.SaveChangesAsync(ct);
    }

    // ──────── Bootstrap runs ────────

    /// <summary>
    /// Snapshots the blueprint's steps into a new queued <see cref="BootstrapRun"/> for
    /// the given cluster, applying any per-step overrides (keyed by BlueprintStep.Id).
    /// The background runner picks it up and executes the steps in order.
    /// </summary>
    public async Task<BootstrapRun> StartBootstrapAsync(
        Guid clusterId, Guid blueprintId,
        IReadOnlyDictionary<Guid, Dictionary<string, string>>? overrides,
        string? triggeredBy, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterBlueprint blueprint = await db.ClusterBlueprints
            .Include(b => b.Steps)
            .FirstAsync(b => b.Id == blueprintId, ct);

        if (blueprint.Steps.Count == 0)
        {
            throw new InvalidOperationException("This blueprint has no steps to bootstrap.");
        }

        // Guard against launching two active runs against the same cluster at once.
        bool activeExists = await db.BootstrapRuns.AnyAsync(
            r => r.ClusterId == clusterId
                && (r.Status == BootstrapRunStatus.Queued || r.Status == BootstrapRunStatus.Running), ct);
        if (activeExists)
        {
            throw new InvalidOperationException("A bootstrap is already in progress for this cluster.");
        }

        BootstrapRun run = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            BlueprintId = blueprintId,
            BlueprintName = blueprint.Name,
            Status = BootstrapRunStatus.Queued,
            TriggeredBy = triggeredBy
        };
        db.BootstrapRuns.Add(run);

        foreach (BlueprintStep step in blueprint.Steps.OrderBy(s => s.Order))
        {
            Dictionary<string, string> resolved = DeserializeParams(step.ParametersJson);
            if (overrides is not null && overrides.TryGetValue(step.Id, out Dictionary<string, string>? ov))
            {
                foreach ((string k, string v) in ov)
                {
                    resolved[k] = v;
                }
            }

            db.BootstrapStepRuns.Add(new BootstrapStepRun
            {
                Id = Guid.NewGuid(),
                BootstrapRunId = run.Id,
                Order = step.Order,
                StepType = step.StepType,
                Key = step.Key,
                Name = step.Name,
                Namespace = step.Namespace,
                ResolvedParametersJson = SerializeParams(resolved),
                Status = BootstrapStepStatus.Pending
            });
        }

        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Re-queues a failed run so the runner retries from the first non-succeeded step.</summary>
    public async Task ResumeAsync(Guid runId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BootstrapRun run = await db.BootstrapRuns
            .Include(r => r.StepRuns)
            .FirstAsync(r => r.Id == runId, ct);

        if (run.Status != BootstrapRunStatus.Failed && run.Status != BootstrapRunStatus.Cancelled)
        {
            throw new InvalidOperationException("Only a failed or cancelled run can be resumed.");
        }

        // Reset any failed step back to pending; succeeded steps are left untouched.
        foreach (BootstrapStepRun step in run.StepRuns.Where(s =>
                     s.Status is BootstrapStepStatus.Failed or BootstrapStepStatus.Skipped))
        {
            step.Status = BootstrapStepStatus.Pending;
            step.Error = null;
            step.StartedAt = null;
            step.FinishedAt = null;
        }

        run.Status = BootstrapRunStatus.Queued;
        run.FinishedAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task CancelAsync(Guid runId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BootstrapRun? run = await db.BootstrapRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        if (run.Status is BootstrapRunStatus.Queued or BootstrapRunStatus.Running)
        {
            run.Status = BootstrapRunStatus.Cancelled;
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<List<BootstrapRun>> GetRunsForClusterAsync(Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.BootstrapRuns
            .Where(r => r.ClusterId == clusterId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<BootstrapRun?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.BootstrapRuns
            .Include(r => r.StepRuns.OrderBy(s => s.Order))
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    // ──────── Param (de)serialization ────────

    public static string? SerializeParams(IReadOnlyDictionary<string, string>? parameters)
        => parameters is null || parameters.Count == 0 ? null : JsonSerializer.Serialize(parameters, JsonOpts);

    public static Dictionary<string, string> DeserializeParams(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
}
