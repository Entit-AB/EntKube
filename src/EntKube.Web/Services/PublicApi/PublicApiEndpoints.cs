using EntKube.Web.Data;
using EntKube.Web.Services.Cost;
using EntKube.Web.Services.SupplyChain;
using EntKube.Web.Services.Upgrades;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.PublicApi;

/// <summary>
/// EntKube's public REST API — the surface that lets CI, ticketing systems, a CLI, a
/// Terraform provider or an MCP server drive the platform without a browser.
///
/// Every route is authenticated by a scoped <see cref="ApiToken"/> and is implicitly
/// scoped to that token's tenant: no route accepts a tenant id from the caller, so a
/// token cannot be pointed at another tenant's data by changing a parameter. That is
/// the single most important property of this file.
///
/// Responses are deliberately shaped as plain DTOs rather than the internal entities,
/// so the API stays stable when the data model moves underneath it.
/// </summary>
public static class PublicApiEndpoints
{
    private const string BasePath = "/api/v1";

    public static void MapPublicApi(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup(BasePath);

        // ── Identity ──

        api.MapGet("/whoami", (HttpContext ctx) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;
            return Results.Ok(new
            {
                tenantId = principal.TenantId,
                token = principal.TokenName,
                scopes = principal.Scopes.OrderBy(s => s),
            });
        }).RequireApiScope();

        // ── Fleet ──

        api.MapGet("/clusters", async (
            HttpContext ctx, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            var clusters = await db.KubernetesClusters
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId)
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    apiServerUrl = c.ApiServerUrl,
                    environment = c.Environment.Name,
                    provisioningStatus = c.ProvisioningStatus.ToString(),
                    componentCount = c.Components.Count,
                })
                .ToListAsync(ct);

            return Results.Ok(clusters);
        }).RequireApiScope(ApiScopes.FleetRead);

        api.MapGet("/clusters/{clusterId:guid}/components", async (
            Guid clusterId, HttpContext ctx,
            IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            // The tenant predicate is on the CLUSTER, not just the component, so a guessed
            // cluster id from another tenant returns 404 rather than leaking its components.
            bool exists = await db.KubernetesClusters
                .AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
            if (!exists)
            {
                return Results.NotFound();
            }

            var components = await db.ClusterComponents
                .AsNoTracking()
                .Where(c => c.ClusterId == clusterId)
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    type = c.ComponentType,
                    status = c.Status.ToString(),
                    ns = c.Namespace,
                    chart = c.HelmChartName,
                    version = c.HelmChartVersion,
                    installedAt = c.InstalledAt,
                })
                .ToListAsync(ct);

            return Results.Ok(components);
        }).RequireApiScope(ApiScopes.FleetRead);

        // ── Apps and deployments ──

        api.MapGet("/apps", async (
            HttpContext ctx, IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            var apps = await db.Apps
                .AsNoTracking()
                .Where(a => a.Customer.TenantId == tenantId)
                .OrderBy(a => a.Name)
                .Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    customer = a.Customer.Name,
                    deploymentCount = a.Deployments.Count,
                })
                .ToListAsync(ct);

            return Results.Ok(apps);
        }).RequireApiScope(ApiScopes.AppsRead);

        api.MapGet("/deployments", async (
            HttpContext ctx, IDbContextFactory<ApplicationDbContext> dbFactory,
            Guid? appId, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            IQueryable<AppDeployment> query = db.AppDeployments
                .AsNoTracking()
                .Where(d => d.App.Customer.TenantId == tenantId);

            if (appId is Guid id)
            {
                query = query.Where(d => d.AppId == id);
            }

            var deployments = await query
                .OrderBy(d => d.Name)
                .Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    app = d.App.Name,
                    type = d.Type.ToString(),
                    environment = d.Environment.Name,
                    cluster = d.Cluster.Name,
                    ns = d.Namespace,
                    syncStatus = d.SyncStatus.ToString(),
                    healthStatus = d.HealthStatus.ToString(),
                    lastSyncedAt = d.LastSyncedAt,
                    isManaged = d.IsManaged,
                })
                .ToListAsync(ct);

            return Results.Ok(deployments);
        }).RequireApiScope(ApiScopes.AppsRead);

        api.MapPost("/deployments/{deploymentId:guid}/sync", async (
            Guid deploymentId, HttpContext ctx,
            IDbContextFactory<ApplicationDbContext> dbFactory,
            KubernetesOperationsService operations, CancellationToken ct) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            bool owned = await db.AppDeployments
                .AnyAsync(d => d.Id == deploymentId && d.App.Customer.TenantId == principal.TenantId, ct);
            if (!owned)
            {
                return Results.NotFound();
            }

            // Attributed to the token, not to a user: an audit trail that says "CI pipeline"
            // is more useful than one that says whoever happened to create the token.
            KubernetesOperationResult<string> result = await operations.ApplyYamlDeploymentAsync(
                deploymentId, performedBy: $"api-token:{principal.TokenName}", ct);

            return result.IsSuccess
                ? Results.Ok(new { ok = true, output = result.Data })
                : Results.Problem(result.Error ?? "Sync failed.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }).RequireApiScope(ApiScopes.AppsWrite);

        // A restart targets one Kubernetes Deployment, not the whole EntKube deployment,
        // so the workload name is required rather than guessed. Guessing here would mean
        // restarting the wrong workload in a namespace that hosts several.
        api.MapPost("/deployments/{deploymentId:guid}/restart", async (
            Guid deploymentId, string workload, HttpContext ctx,
            IDbContextFactory<ApplicationDbContext> dbFactory,
            KubernetesOperationsService operations, CancellationToken ct) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;

            if (string.IsNullOrWhiteSpace(workload))
            {
                return Results.Problem(
                    "A 'workload' query parameter naming the Kubernetes Deployment is required.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            bool owned = await db.AppDeployments
                .AnyAsync(d => d.Id == deploymentId && d.App.Customer.TenantId == principal.TenantId, ct);
            if (!owned)
            {
                return Results.NotFound();
            }

            KubernetesOperationResult result = await operations.RestartDeploymentAsync(
                deploymentId, workload.Trim(), performedBy: $"api-token:{principal.TokenName}", ct);

            return result.IsSuccess
                ? Results.Ok(new { ok = true })
                : Results.Problem(result.Error ?? "Restart failed.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }).RequireApiScope(ApiScopes.AppsWrite);

        // ── Operations ──

        api.MapGet("/advisor/findings", async (
            HttpContext ctx, OperationsAdvisorService advisor, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            AdvisorReport report = await advisor.GetReportAsync(tenantId, ct);

            return Results.Ok(report.Findings.Select(f => new
            {
                id = f.Id,
                category = f.Category.ToString(),
                severity = f.Severity.ToString(),
                horizon = f.Horizon.ToString(),
                title = f.Title,
                detail = f.Detail,
                scope = f.ScopeLabel,
                dueAt = f.DueAt,
                remediation = f.Remediation,
                source = f.Source,
                state = f.State.ToString(),
                clusterId = f.ClusterId,
                customer = f.CustomerName,
            }));
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/incidents", async (
            HttpContext ctx, IDbContextFactory<ApplicationDbContext> dbFactory,
            bool? open, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            // Incidents carry no TenantId of their own — they belong to a cluster, and the
            // cluster belongs to the tenant. Filtering through the cluster is what keeps
            // this query tenant-safe.
            IQueryable<AlertIncident> query = db.AlertIncidents
                .AsNoTracking()
                .Where(i => i.Cluster.TenantId == tenantId);

            if (open == true)
            {
                query = query.Where(i => i.Status != IncidentStatus.Resolved);
            }

            var incidents = await query
                .OrderByDescending(i => i.StartsAt)
                .Take(200)
                .Select(i => new
                {
                    id = i.Id,
                    alertName = i.AlertName,
                    summary = i.Summary,
                    severity = i.Severity,
                    status = i.Status.ToString(),
                    cluster = i.Cluster.Name,
                    startsAt = i.StartsAt,
                    resolvedAt = i.ResolvedAt,
                    acknowledgedBy = i.AcknowledgedBy,
                    runbookUrl = i.RunbookUrl,
                })
                .ToListAsync(ct);

            return Results.Ok(incidents);
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/upgrades", async (
            HttpContext ctx, ComponentUpgradeService upgrades, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            UpgradeReport report = await upgrades.GetTenantReportAsync(tenantId, DateTime.UtcNow, ct);

            return Results.Ok(new
            {
                upgradeCount = report.UpgradeCount,
                majorCount = report.MajorCount,
                deprecatedCount = report.DeprecatedCount,
                components = report.Components.Select(c => new
                {
                    cluster = c.ClusterName,
                    component = c.DisplayName,
                    chart = c.ChartName,
                    installed = c.InstalledVersion,
                    latest = c.LatestVersion,
                    status = c.Status.ToString(),
                    lag = c.Lag.ToString(),
                    versionsBehind = c.VersionsBehind,
                }),
            });
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/drift", (HttpContext ctx, DriftScanCache cache) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            DriftReport? report = cache.Get(tenantId);

            // 503 rather than an empty 200: "no sweep has run" and "nothing has drifted"
            // are different answers, and a caller polling this must not confuse them.
            if (report is null)
            {
                return Results.Problem(
                    "No drift sweep has completed yet for this tenant.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                generatedAt = report.GeneratedAt,
                driftedCount = report.DriftedCount,
                unknownCount = report.UnknownCount,
                inSyncCount = report.InSyncCount,
                results = report.Results.Select(r => new
                {
                    deploymentId = r.DeploymentId,
                    app = r.AppName,
                    deployment = r.DeploymentName,
                    cluster = r.ClusterName,
                    ns = r.Namespace,
                    state = r.State.ToString(),
                    changedLines = r.ChangedLines,
                    note = r.Note,
                }),
            });
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/supply-chain", (HttpContext ctx, SupplyChainScanCache cache) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            SupplyChainReport? report = cache.Get(tenantId);

            if (report is null)
            {
                return Results.Problem(
                    "No supply-chain sweep has completed yet for this tenant.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                generatedAt = report.GeneratedAt,
                vulnerableCount = report.VulnerableCount,
                unscannedCount = report.UnscannedCount,
                unmanagedCount = report.UnmanagedCount,
                cleanCount = report.CleanCount,
                totalCritical = report.TotalCritical,
                totalHigh = report.TotalHigh,
                warnings = report.Warnings,
                images = report.Images.Select(i => new
                {
                    image = i.Image,
                    cluster = i.ClusterName,
                    state = i.State.ToString(),
                    critical = i.CriticalCount,
                    high = i.HighCount,
                    fixable = i.FixableCount,
                    digestPinned = i.IsDigestPinned,
                    workloads = i.Workloads,
                }),
            });
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/cost", (HttpContext ctx, CostScanCache cache) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            CostReport? report = cache.Get(tenantId);

            if (report is null)
            {
                return Results.Problem(
                    "No cost calculation has completed yet for this tenant.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                generatedAt = report.GeneratedAt,
                currency = report.Currency,
                totalMonthlyCost = report.TotalMonthlyCost,
                totalHourlyCost = report.TotalHourlyCost,
                unattributedMonthlyCost = report.UnattributedMonthlyCost,
                warnings = report.Warnings,
                byCustomer = report.ByCustomer.Select(c => new
                {
                    customerId = c.CustomerId,
                    customer = c.CustomerName,
                    monthlyCost = c.MonthlyCost,
                }),
                byEnvironment = report.ByEnvironment.Select(e => new
                {
                    environment = e.Environment,
                    monthlyCost = e.MonthlyCost,
                }),
                namespaces = report.Namespaces.Select(n => new
                {
                    ns = n.Namespace,
                    cluster = n.ClusterName,
                    customer = n.CustomerName,
                    app = n.AppName,
                    environment = n.EnvironmentName,
                    cpuCores = n.CpuCores,
                    memoryGiB = n.MemoryGiB,
                    storageGiB = n.StorageGiB,
                    monthlyCost = n.TotalMonthlyCost,
                    unattributed = n.IsUnattributed,
                }),
            });
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapPost("/advisor/findings/{findingId}/acknowledge", async (
            string findingId, HttpContext ctx, AdvisorStateService state, CancellationToken ct) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;
            await state.AcknowledgeAsync(
                principal.TenantId, findingId, $"api-token:{principal.TokenName}", ct);
            return Results.Ok(new { ok = true });
        }).RequireApiScope(ApiScopes.OpsWrite);
    }
}
