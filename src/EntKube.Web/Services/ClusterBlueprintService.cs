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
    public Task<BootstrapRun> StartBootstrapAsync(
        Guid clusterId, Guid blueprintId,
        IReadOnlyDictionary<Guid, Dictionary<string, string>>? overrides,
        string? triggeredBy, CancellationToken ct = default)
        => CreateRunAsync(clusterId, blueprintId, overrides, BootstrapRunMode.Bootstrap, null, triggeredBy, ct);

    /// <summary>
    /// Creates a queued run that snapshots the blueprint's current steps for a cluster.
    /// Shared by the initial bootstrap and by staged rollout updates (mode = Update).
    /// </summary>
    private async Task<BootstrapRun> CreateRunAsync(
        Guid clusterId, Guid blueprintId,
        IReadOnlyDictionary<Guid, Dictionary<string, string>>? overrides,
        BootstrapRunMode mode, Guid? rolloutTargetId,
        string? triggeredBy, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterBlueprint blueprint = await db.ClusterBlueprints
            .Include(b => b.Steps)
            .FirstAsync(b => b.Id == blueprintId, ct);

        if (blueprint.Steps.Count == 0)
        {
            throw new InvalidOperationException("This blueprint has no steps to apply.");
        }

        // Guard against launching two active runs against the same cluster at once.
        bool activeExists = await db.BootstrapRuns.AnyAsync(
            r => r.ClusterId == clusterId
                && (r.Status == BootstrapRunStatus.Queued || r.Status == BootstrapRunStatus.Running), ct);
        if (activeExists)
        {
            throw new InvalidOperationException("A bootstrap or update is already in progress for this cluster.");
        }

        BootstrapRun run = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            BlueprintId = blueprintId,
            BlueprintName = blueprint.Name,
            Status = BootstrapRunStatus.Queued,
            Mode = mode,
            RolloutTargetId = rolloutTargetId,
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

    // ──────── Staged rollouts (push an updated blueprint to its clusters) ────────

    /// <summary>Clusters that have at least one bootstrap run for this blueprint.</summary>
    public async Task<List<(Guid ClusterId, string ClusterName)>> GetBootstrappedClustersAsync(
        Guid blueprintId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<Guid> clusterIds = await db.BootstrapRuns
            .Where(r => r.BlueprintId == blueprintId)
            .Select(r => r.ClusterId)
            .Distinct()
            .ToListAsync(ct);

        var clusters = await db.KubernetesClusters
            .Where(c => clusterIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        return clusters.Select(c => (c.Id, c.Name)).ToList();
    }

    /// <summary>
    /// Starts a staged rollout that pushes the blueprint's current state to the given
    /// clusters, in order, one at a time. The first target is started immediately.
    /// </summary>
    public async Task<BlueprintRollout> CreateRolloutAsync(
        Guid blueprintId, IReadOnlyList<Guid> orderedClusterIds, bool autoAdvance,
        string? triggeredBy, CancellationToken ct = default)
    {
        if (orderedClusterIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one cluster to roll out to.");
        }

        Guid rolloutId;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            ClusterBlueprint blueprint = await db.ClusterBlueprints.FirstAsync(b => b.Id == blueprintId, ct);

            bool activeRollout = await db.BlueprintRollouts.AnyAsync(
                r => r.BlueprintId == blueprintId && r.Status == RolloutStatus.InProgress, ct);
            if (activeRollout)
            {
                throw new InvalidOperationException("A rollout is already in progress for this blueprint.");
            }

            Dictionary<Guid, string> clusterNames = await db.KubernetesClusters
                .Where(c => orderedClusterIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

            BlueprintRollout rollout = new()
            {
                Id = Guid.NewGuid(),
                BlueprintId = blueprintId,
                BlueprintName = blueprint.Name,
                Status = RolloutStatus.InProgress,
                AutoAdvance = autoAdvance,
                TriggeredBy = triggeredBy
            };
            db.BlueprintRollouts.Add(rollout);

            int order = 0;
            foreach (Guid clusterId in orderedClusterIds)
            {
                db.BlueprintRolloutTargets.Add(new BlueprintRolloutTarget
                {
                    Id = Guid.NewGuid(),
                    RolloutId = rollout.Id,
                    ClusterId = clusterId,
                    ClusterName = clusterNames.TryGetValue(clusterId, out string? n) ? n : "(unknown)",
                    Order = order++,
                    Status = RolloutTargetStatus.Pending
                });
            }

            await db.SaveChangesAsync(ct);
            rolloutId = rollout.Id;
        }

        await StartNextTargetAsync(rolloutId, ct);

        return (await GetRolloutAsync(rolloutId, ct))!;
    }

    /// <summary>
    /// Starts the next pending target of an in-progress rollout by creating an
    /// update run for its cluster. No-op if a target is already running, none remain,
    /// or the rollout is no longer in progress. This is the manual "promote" action
    /// and is also used for auto-advance.
    /// </summary>
    public async Task StartNextTargetAsync(Guid rolloutId, CancellationToken ct = default)
    {
        Guid targetId, clusterId, blueprintId;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BlueprintRollout rollout = await db.BlueprintRollouts
                .Include(r => r.Targets)
                .FirstAsync(r => r.Id == rolloutId, ct);

            if (rollout.Status != RolloutStatus.InProgress) return;
            if (rollout.Targets.Any(t => t.Status == RolloutTargetStatus.Running)) return;

            BlueprintRolloutTarget? next = rollout.Targets
                .Where(t => t.Status == RolloutTargetStatus.Pending)
                .OrderBy(t => t.Order)
                .FirstOrDefault();
            if (next is null) return;

            targetId = next.Id;
            clusterId = next.ClusterId;
            blueprintId = rollout.BlueprintId;
        }

        BootstrapRun run;
        try
        {
            run = await CreateRunAsync(clusterId, blueprintId, null,
                BootstrapRunMode.Update, targetId, "rollout", ct);
        }
        catch (InvalidOperationException ex)
        {
            // Couldn't launch (e.g. the cluster already has an active run) — fail the
            // target and halt the rollout so the operator can resolve it.
            using ApplicationDbContext db = dbFactory.CreateDbContext();
            BlueprintRolloutTarget target = await db.BlueprintRolloutTargets.FirstAsync(t => t.Id == targetId, ct);
            target.Status = RolloutTargetStatus.Failed;
            target.FinishedAt = DateTime.UtcNow;
            BlueprintRollout rollout = await db.BlueprintRollouts.FirstAsync(r => r.Id == rolloutId, ct);
            rollout.Status = RolloutStatus.Failed;
            rollout.FinishedAt = DateTime.UtcNow;
            _ = ex;
            await db.SaveChangesAsync(ct);
            return;
        }

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BlueprintRolloutTarget target = await db.BlueprintRolloutTargets.FirstAsync(t => t.Id == targetId, ct);
            target.Status = RolloutTargetStatus.Running;
            target.StartedAt = DateTime.UtcNow;
            target.BootstrapRunId = run.Id;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Reconciles a rollout after one of its update runs finishes. Called by the runner.
    /// Updates the target's status and either completes/fails the rollout or (when
    /// auto-advance is on) starts the next target.
    /// </summary>
    public async Task OnRunFinishedAsync(Guid runId, CancellationToken ct = default)
    {
        Guid rolloutIdToAdvance = Guid.Empty;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BootstrapRun? run = await db.BootstrapRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run?.RolloutTargetId is not Guid targetId) return;

            BlueprintRolloutTarget? target = await db.BlueprintRolloutTargets
                .FirstOrDefaultAsync(t => t.Id == targetId, ct);
            if (target is null) return;

            bool succeeded = run.Status == BootstrapRunStatus.Succeeded;
            target.Status = succeeded ? RolloutTargetStatus.Succeeded : RolloutTargetStatus.Failed;
            target.FinishedAt = DateTime.UtcNow;

            BlueprintRollout rollout = await db.BlueprintRollouts
                .Include(r => r.Targets)
                .FirstAsync(r => r.Id == target.RolloutId, ct);

            if (!succeeded)
            {
                rollout.Status = RolloutStatus.Failed;
                rollout.FinishedAt = DateTime.UtcNow;
            }
            else if (rollout.Targets.All(t => t.Status != RolloutTargetStatus.Pending))
            {
                rollout.Status = RolloutStatus.Completed;
                rollout.FinishedAt = DateTime.UtcNow;
            }
            else if (rollout.AutoAdvance)
            {
                rolloutIdToAdvance = rollout.Id;
            }

            await db.SaveChangesAsync(ct);
        }

        if (rolloutIdToAdvance != Guid.Empty)
        {
            await StartNextTargetAsync(rolloutIdToAdvance, ct);
        }
    }

    public async Task CancelRolloutAsync(Guid rolloutId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BlueprintRollout? rollout = await db.BlueprintRollouts
            .Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == rolloutId, ct);
        if (rollout is null || rollout.Status != RolloutStatus.InProgress) return;

        rollout.Status = RolloutStatus.Cancelled;
        rollout.FinishedAt = DateTime.UtcNow;
        foreach (BlueprintRolloutTarget target in rollout.Targets.Where(t => t.Status == RolloutTargetStatus.Pending))
        {
            target.Status = RolloutTargetStatus.Skipped;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<BlueprintRollout>> GetRolloutsForBlueprintAsync(Guid blueprintId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.BlueprintRollouts
            .Include(r => r.Targets.OrderBy(t => t.Order))
            .Where(r => r.BlueprintId == blueprintId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<BlueprintRollout?> GetRolloutAsync(Guid rolloutId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.BlueprintRollouts
            .Include(r => r.Targets.OrderBy(t => t.Order))
            .FirstOrDefaultAsync(r => r.Id == rolloutId, ct);
    }

    // ──────── Param (de)serialization ────────

    public static string? SerializeParams(IReadOnlyDictionary<string, string>? parameters)
        => parameters is null || parameters.Count == 0 ? null : JsonSerializer.Serialize(parameters, JsonOpts);

    public static Dictionary<string, string> DeserializeParams(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
}
