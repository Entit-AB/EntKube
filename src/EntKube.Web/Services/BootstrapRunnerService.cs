using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Background worker that executes queued <see cref="BootstrapRun"/>s — installing a
/// blueprint's components and services onto a registered cluster, in order. Mirrors
/// the established BackgroundService pattern (see DeploymentSyncService): it polls the
/// database, resolves scoped services per run, and drives the same install/registration
/// code paths the interactive UI uses (CatalogComponentRegistrar +
/// ComponentInstallOrchestrator for components, the *Service.CreateClusterAsync methods
/// for services).
///
/// Failure policy: a required step that fails halts the run (status Failed); the operator
/// can fix the cause and resume, which re-runs from the failed step. Optional steps that
/// fail are recorded but do not stop the run. Cancellation is cooperative — checked between
/// steps, so a step already running to completion (e.g. a helm --wait) is not aborted.
/// </summary>
public class BootstrapRunnerService(
    IServiceScopeFactory scopeFactory,
    ILogger<BootstrapRunnerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first poll.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueuedRunsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BootstrapRunnerService: poll loop error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessQueuedRunsAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        List<Guid> queuedRunIds;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            queuedRunIds = await db.BootstrapRuns
                .Where(r => r.Status == BootstrapRunStatus.Queued)
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.Id)
                .ToListAsync(ct);
        }

        foreach (Guid runId in queuedRunIds)
        {
            if (ct.IsCancellationRequested) break;
            await ExecuteRunAsync(runId, ct);
        }
    }

    private async Task ExecuteRunAsync(Guid runId, CancellationToken ct)
    {
        // Each run gets its own scope so scoped services (registrar, orchestrator,
        // CNPG/Redis/RabbitMQ services) resolve cleanly outside any request context.
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var registrar = scope.ServiceProvider.GetRequiredService<CatalogComponentRegistrar>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ComponentInstallOrchestrator>();
        var cnpgService = scope.ServiceProvider.GetRequiredService<CnpgService>();
        var redisService = scope.ServiceProvider.GetRequiredService<RedisService>();
        var rabbitService = scope.ServiceProvider.GetRequiredService<RabbitMQService>();
        var blueprintService = scope.ServiceProvider.GetRequiredService<ClusterBlueprintService>();

        Guid tenantId;
        List<Guid> stepIds;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BootstrapRun? run = await db.BootstrapRuns
                .Include(r => r.Cluster)
                .Include(r => r.StepRuns)
                .FirstOrDefaultAsync(r => r.Id == runId, ct);

            // Skip if it was cancelled or picked up by another pass in the meantime.
            if (run is null || run.Status != BootstrapRunStatus.Queued) return;

            tenantId = run.Cluster.TenantId;
            run.Status = BootstrapRunStatus.Running;
            run.StartedAt ??= DateTime.UtcNow;
            stepIds = run.StepRuns.OrderBy(s => s.Order).Select(s => s.Id).ToList();
            await db.SaveChangesAsync(ct);

            logger.LogInformation("BootstrapRunnerService: starting run {RunId} ({Blueprint}) with {Count} steps",
                runId, run.BlueprintName, stepIds.Count);
        }

        Guid clusterId;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            clusterId = await db.BootstrapRuns.Where(r => r.Id == runId).Select(r => r.ClusterId).FirstAsync(ct);
        }

        bool failed = false;

        foreach (Guid stepId in stepIds)
        {
            if (ct.IsCancellationRequested) return;

            // Honour cancellation requested via the UI between steps.
            if (await IsRunCancelledAsync(dbFactory, runId, ct)) return;

            BootstrapStepStatus stepStatus;
            bool optional;
            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                BootstrapStepRun s = await db.BootstrapStepRuns.FirstAsync(x => x.Id == stepId, ct);
                stepStatus = s.Status;
                // "Optional" is carried on the source blueprint step; look it up if present.
                optional = false;
            }

            if (stepStatus == BootstrapStepStatus.Succeeded) continue;

            (bool ok, string? error) = await ExecuteStepAsync(
                dbFactory, registrar, orchestrator, cnpgService, redisService, rabbitService,
                runId, stepId, clusterId, tenantId, ct);

            if (!ok && !optional)
            {
                failed = true;
                break;
            }
        }

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BootstrapRun? run = await db.BootstrapRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null || run.Status == BootstrapRunStatus.Cancelled) return;

            run.Status = failed ? BootstrapRunStatus.Failed : BootstrapRunStatus.Succeeded;
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // If this run belongs to a staged rollout, update its target and advance.
        await blueprintService.OnRunFinishedAsync(runId, ct);

        logger.LogInformation("BootstrapRunnerService: run {RunId} finished ({Status})",
            runId, failed ? "Failed" : "Succeeded");
    }

    private async Task<(bool ok, string? error)> ExecuteStepAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CatalogComponentRegistrar registrar,
        ComponentInstallOrchestrator orchestrator,
        CnpgService cnpgService,
        RedisService redisService,
        RabbitMQService rabbitService,
        Guid runId, Guid stepId, Guid clusterId, Guid tenantId, CancellationToken ct)
    {
        // Load the step + mark it running.
        BlueprintStepType stepType;
        string key, name;
        string? ns;
        Dictionary<string, string> parameters;
        Guid? existingComponentId;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BootstrapStepRun step = await db.BootstrapStepRuns.FirstAsync(s => s.Id == stepId, ct);
            stepType = step.StepType;
            key = step.Key;
            name = step.Name;
            ns = step.Namespace;
            parameters = ClusterBlueprintService.DeserializeParams(step.ResolvedParametersJson);
            existingComponentId = step.CreatedComponentId;

            step.Status = BootstrapStepStatus.Running;
            step.StartedAt ??= DateTime.UtcNow;
            step.Error = null;

            BootstrapRun run = await db.BootstrapRuns.FirstAsync(r => r.Id == runId, ct);
            run.CurrentStepOrder = step.Order;
            await db.SaveChangesAsync(ct);
        }

        string? output = null;
        string? error = null;
        Guid? createdComponentId = existingComponentId;
        bool ok;

        try
        {
            if (stepType == BlueprintStepType.Component)
            {
                (ok, output, createdComponentId) = await ExecuteComponentStepAsync(
                    registrar, orchestrator, key, name, ns, parameters,
                    clusterId, tenantId, ct);
                if (!ok && output is not null) error = "Install failed — see output.";
            }
            else
            {
                output = await ExecuteServiceStepAsync(
                    dbFactory, cnpgService, redisService, rabbitService, key, name, ns, parameters,
                    clusterId, tenantId, ct);
                ok = true;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
            logger.LogWarning(ex, "BootstrapRunnerService: step {StepId} ({Key}) failed", stepId, key);
        }

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            BootstrapStepRun step = await db.BootstrapStepRuns.FirstAsync(s => s.Id == stepId, ct);
            step.Status = ok ? BootstrapStepStatus.Succeeded : BootstrapStepStatus.Failed;
            step.Output = output;
            step.Error = error;
            step.CreatedComponentId = createdComponentId;
            step.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return (ok, error);
    }

    private static async Task<(bool ok, string? output, Guid? componentId)> ExecuteComponentStepAsync(
        CatalogComponentRegistrar registrar,
        ComponentInstallOrchestrator orchestrator,
        string key, string name, string? ns, Dictionary<string, string> parameters,
        Guid clusterId, Guid tenantId, CancellationToken ct)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)
            ?? throw new InvalidOperationException($"Unknown catalog component '{key}'.");

        // Upsert: creates the component on a fresh bootstrap, or refreshes its values/
        // version on a blueprint-update run. Also handles resume-after-failure, since
        // ApplyAsync matches an already-registered component by name.
        ClusterComponent applied = await registrar.ApplyAsync(
            clusterId, tenantId, entry, parameters,
            namespaceOverride: ns,
            releaseNameOverride: string.IsNullOrWhiteSpace(name) ? null : name,
            ct: ct);

        // helm upgrade --install makes the install idempotent, so this both installs a
        // new component and upgrades an existing one to the refreshed values/version.
        HelmExecutionResult result = await orchestrator.InstallAsync(tenantId, applied.Id, ct);
        return (result.Success, result.Output, applied.Id);
    }

    private static async Task<string> ExecuteServiceStepAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CnpgService cnpgService,
        RedisService redisService,
        RabbitMQService rabbitService,
        string key, string name, string? ns, Dictionary<string, string> p,
        Guid clusterId, Guid tenantId, CancellationToken ct)
    {
        string @namespace = string.IsNullOrWhiteSpace(ns) ? "default" : ns;
        string kind = key.ToLowerInvariant();

        // Stateful service instances are not modified once they exist — a blueprint-update
        // run leaves an already-created CNPG/Redis/RabbitMQ cluster untouched.
        if (await ServiceInstanceExistsAsync(dbFactory, kind, clusterId, name, @namespace, ct))
        {
            return $"{kind} '{name}' already exists in namespace '{@namespace}' — left unchanged.";
        }

        switch (kind)
        {
            case "cnpg":
            {
                await cnpgService.CreateClusterAsync(
                    tenantId, clusterId, name, @namespace,
                    instances: ParseInt(p, "instances", 1),
                    storageSize: ParseStrOr(p, "storageSize", "10Gi"),
                    storageLinkId: ParseGuid(p, "storageLinkId"),
                    backupSchedule: ParseStr(p, "backupSchedule", null),
                    retentionDays: ParseInt(p, "retentionDays", 30),
                    maxBackups: ParseInt(p, "maxBackups", 20),
                    postgresVersion: ParseStrOr(p, "postgresVersion", "18"),
                    ct: ct);
                return $"CNPG cluster '{name}' created in namespace '{@namespace}'.";
            }
            case "redis":
            {
                await redisService.CreateClusterAsync(
                    tenantId, clusterId, name, @namespace,
                    clusterSize: ParseInt(p, "clusterSize", 3),
                    redisVersion: ParseStrOr(p, "redisVersion", "7.4.1"),
                    storageSize: ParseStrOr(p, "storageSize", "5Gi"),
                    storageClass: ParseStr(p, "storageClass", null),
                    persistenceEnabled: ParseBool(p, "persistenceEnabled", true),
                    ct: ct);
                return $"Redis cluster '{name}' created in namespace '{@namespace}'.";
            }
            case "rabbitmq":
            {
                await rabbitService.CreateClusterAsync(
                    tenantId, clusterId, name, @namespace,
                    version: ParseStrOr(p, "version", "4.0.5"),
                    replicas: ParseInt(p, "replicas", 3),
                    storageSize: ParseStrOr(p, "storageSize", "10Gi"),
                    storageClass: ParseStr(p, "storageClass", null),
                    storageLinkId: ParseGuid(p, "storageLinkId"),
                    ct: ct);
                return $"RabbitMQ cluster '{name}' created in namespace '{@namespace}'.";
            }
            default:
                throw new InvalidOperationException($"Unknown service kind '{key}'.");
        }
    }

    private static async Task<bool> ServiceInstanceExistsAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        string kind, Guid clusterId, string name, string ns, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return kind switch
        {
            "cnpg" => await db.CnpgClusters.AnyAsync(
                c => c.KubernetesClusterId == clusterId && c.Name == name && c.Namespace == ns, ct),
            "redis" => await db.RedisClusters.AnyAsync(
                c => c.KubernetesClusterId == clusterId && c.Name == name && c.Namespace == ns, ct),
            "rabbitmq" => await db.RabbitMQClusters.AnyAsync(
                c => c.KubernetesClusterId == clusterId && c.Name == name && c.Namespace == ns, ct),
            _ => false
        };
    }

    private static async Task<bool> IsRunCancelledAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, Guid runId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        BootstrapRunStatus status = await db.BootstrapRuns
            .Where(r => r.Id == runId).Select(r => r.Status).FirstAsync(ct);
        return status == BootstrapRunStatus.Cancelled;
    }

    // ── Parameter parsing helpers (all params are stored as strings) ──

    private static int ParseInt(IReadOnlyDictionary<string, string> p, string key, int fallback)
        => p.TryGetValue(key, out string? v) && int.TryParse(v, out int i) ? i : fallback;

    private static bool ParseBool(IReadOnlyDictionary<string, string> p, string key, bool fallback)
        => p.TryGetValue(key, out string? v) && bool.TryParse(v, out bool b) ? b : fallback;

    private static string? ParseStr(IReadOnlyDictionary<string, string> p, string key, string? fallback)
        => p.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static string ParseStrOr(IReadOnlyDictionary<string, string> p, string key, string fallback)
        => p.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static Guid? ParseGuid(IReadOnlyDictionary<string, string> p, string key)
        => p.TryGetValue(key, out string? v) && Guid.TryParse(v, out Guid g) && g != Guid.Empty ? g : null;
}
