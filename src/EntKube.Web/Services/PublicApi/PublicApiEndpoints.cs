using EntKube.Web.Data;
using EntKube.Web.Services.Cost;
using EntKube.Web.Services.Dr;
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
            HttpContext ctx, EntKube.Contracts.Fleet.IFleetApi fleet,
            EntKube.Contracts.Catalog.ICatalogApi catalog, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            // The component count is Catalog's, not Fleet's, so it is asked of Catalog rather than
            // added to ClusterSummary — one call for the tenant's components, grouped, instead of
            // one per cluster.
            IReadOnlyList<EntKube.Contracts.Fleet.ClusterSummary> clusters =
                await fleet.GetClustersAsync(tenantId, ct);

            Dictionary<Guid, int> componentCounts =
                (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                .GroupBy(c => c.ClusterId)
                .ToDictionary(g => g.Key, g => g.Count());

            return Results.Ok(clusters
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    apiServerUrl = c.ApiServerUrl,
                    environment = c.EnvironmentName,
                    provisioningStatus = PublishedProvisioningStatus(c.ProvisioningStatus),
                    componentCount = componentCounts.GetValueOrDefault(c.Id),
                })
                .ToList());
        }).RequireApiScope(ApiScopes.FleetRead);

        api.MapGet("/clusters/{clusterId:guid}/components", async (
            Guid clusterId, HttpContext ctx,
            EntKube.Contracts.Fleet.IFleetApi fleet,
            EntKube.Contracts.Catalog.ICatalogApi catalog, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            // The existence check stays, and is not redundant: GetComponentsForClusterAsync is
            // tenant-first and would return an empty list for a cluster that is not this
            // tenant's, but an empty list is a 200 and the contract of this route is a 404.
            // "Does not exist" and "has no components" are different answers — and asking Fleet
            // keeps the tenant predicate on the CLUSTER, so a guessed id from another tenant is
            // a 404 rather than a leak.
            if (await fleet.GetClusterAsync(tenantId, clusterId, ct) is null)
            {
                return Results.NotFound();
            }

            // Asked of Catalog. The projection is unchanged field for field, including
            // Status.ToString() — the contract's enum is a copy with the same member names in the
            // same order, which ApiComponentStatusTests now asserts, because this route's JSON
            // would otherwise change if either enum were edited alone.
            var components = (await catalog.GetComponentsForClusterAsync(tenantId, clusterId, ct))
                .OrderBy(c => c.Name, StringComparer.Ordinal)
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
                .ToList();

            return Results.Ok(components);
        }).RequireApiScope(ApiScopes.FleetRead);

        // ── Apps and deployments ──

        api.MapGet("/apps", async (
            HttpContext ctx, EntKube.Contracts.Delivery.IDeliveryApi delivery, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            // Two contract calls rather than one query with a navigation count. Deliberate: the
            // count is over Delivery's own deployments, so one fetch per tenant and a group is both
            // correct and bounded — the alternative that avoids it is a field on AppSummary that
            // only this route would read.
            IReadOnlyList<EntKube.Contracts.Delivery.AppSummary> apps =
                await delivery.GetAppsAsync(tenantId, ct);

            Dictionary<Guid, int> deploymentCounts =
                (await delivery.GetDeploymentsForTenantAsync(tenantId, ct))
                .GroupBy(d => d.AppId)
                .ToDictionary(g => g.Key, g => g.Count());

            return Results.Ok(apps
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    customer = a.CustomerName,
                    deploymentCount = deploymentCounts.GetValueOrDefault(a.Id),
                })
                .ToList());
        }).RequireApiScope(ApiScopes.AppsRead);

        api.MapGet("/deployments", async (
            HttpContext ctx, EntKube.Contracts.Delivery.IDeliveryApi delivery,
            Guid? appId, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            IEnumerable<EntKube.Contracts.Delivery.DeploymentSummary> deployments =
                await delivery.GetDeploymentsForTenantAsync(tenantId, ct);

            if (appId is Guid id)
            {
                deployments = deployments.Where(d => d.AppId == id);
            }

            // DeploymentSummary carries ClusterName now — four callers wanted it, which is what
            // earned it a place beside ClusterId — so the separate Fleet lookup this route used to
            // make is gone. The enum strings are unchanged: #223 made the contract enums mirror
            // Delivery's member for member, and ModuleApiTests holds them there.
            return Results.Ok(deployments
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    app = d.AppName,
                    type = d.Type.ToString(),
                    environment = d.EnvironmentName,
                    cluster = d.ClusterName,
                    ns = d.Namespace,
                    syncStatus = d.SyncStatus.ToString(),
                    healthStatus = d.HealthStatus.ToString(),
                    lastSyncedAt = d.LastSyncedAt,
                    isManaged = d.IsManaged,
                })
                .ToList());
        }).RequireApiScope(ApiScopes.AppsRead);

        api.MapPost("/deployments/{deploymentId:guid}/sync", async (
            Guid deploymentId, HttpContext ctx,
            EntKube.Contracts.Delivery.IDeliveryApi delivery,
            KubernetesOperationsService operations,
            EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate,
            CancellationToken ct) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;

            // An API request has no Blazor circuit, so there is nobody for the gate to ask — but
            // there IS someone answerable: the token the request authenticated with, which is
            // already what this attributes the change to. The declaration names it, so the audit
            // trail says which token applied the change rather than only that nobody was asked.
            using IDisposable unattended = gate.DeclareUnattended(
                $"api-token:{principal.TokenName}");
            // Asked of Delivery: GetDeploymentAsync is tenant-first and reaches tenancy through the
            // app's customer, which is the same long way round this wrote by hand — and the place a
            // hand-written version goes wrong.
            if (await delivery.GetDeploymentAsync(principal.TenantId, deploymentId, ct) is null)
            {
                return Results.NotFound();
            }

            // Attributed to the token, not to a user: an audit trail that says "CI pipeline"
            // is more useful than one that says whoever happened to create the token.
            KubernetesOperationResult<string> result = await operations.ApplyYamlDeploymentAsync(
                principal.TenantId, deploymentId,
                performedBy: $"api-token:{principal.TokenName}", ct: ct);

            return result.IsSuccess
                ? Results.Ok(new { ok = true, output = result.Data })
                : Results.Problem(result.Error ?? "Sync failed.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }).RequireApiScope(ApiScopes.AppsWrite);

        // A restart targets one Kubernetes Deployment, not the whole EntKube deployment,
        // so the workload name is required rather than guessed. Guessing here would mean
        // restarting the wrong workload in a namespace that hosts several.
        api.MapPost("/deployments/{deploymentId:guid}/restart", async (
            Guid deploymentId, string workload, HttpContext ctx,
            EntKube.Contracts.Delivery.IDeliveryApi delivery,
            KubernetesOperationsService operations,
            EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate,
            CancellationToken ct) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;

            using IDisposable unattended = gate.DeclareUnattended(
                $"api-token:{principal.TokenName}");

            if (string.IsNullOrWhiteSpace(workload))
            {
                return Results.Problem(
                    "A 'workload' query parameter naming the Kubernetes Deployment is required.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Asked of Delivery: GetDeploymentAsync is tenant-first and reaches tenancy through the
            // app's customer, which is the same long way round this wrote by hand — and the place a
            // hand-written version goes wrong.
            if (await delivery.GetDeploymentAsync(principal.TenantId, deploymentId, ct) is null)
            {
                return Results.NotFound();
            }

            // The ownership check above is on the APP's tenant; the seam then checks the
            // CLUSTER's. Two different rows, so a deployment pointing at another tenant's cluster
            // is refused there rather than restarted here.
            KubernetesOperationResult result = await operations.RestartDeploymentAsync(
                principal.TenantId, deploymentId, workload.Trim(),
                performedBy: $"api-token:{principal.TokenName}", ct: ct);

            return result.IsSuccess
                ? Results.Ok(new { ok = true })
                : Results.Problem(result.Error ?? "Restart failed.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }).RequireApiScope(ApiScopes.AppsWrite);

        // ── Operations ──

        // Asked of Advisor, through Advisor's contract. Both of these routes used to inject the
        // module's own services — OperationsAdvisorService here and AdvisorStateService below —
        // which is the boundary this file crosses correctly for Fleet, Catalog, Delivery and
        // Telemetry and crossed for nobody in Advisor. IAdvisorApi had eight methods and no
        // callers at all; these two are its first.
        //
        // The JSON is unchanged. category/severity/horizon/state are still ToString() on enums
        // that now come from the contract, and AdvisorContractEnumTests pins their NAMES against
        // the entity enums for exactly that reason.
        api.MapGet("/advisor/findings", async (
            HttpContext ctx, EntKube.Contracts.Advisor.IAdvisorApi advisor, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            IReadOnlyList<EntKube.Contracts.Advisor.FindingSummary> findings =
                await advisor.GetFindingsAsync(tenantId, ct);

            return Results.Ok(findings.Select(f => new
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
            HttpContext ctx, EntKube.Contracts.Telemetry.ITelemetryApi telemetry,
            bool? open, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            // Asked of Telemetry. Incidents carry no TenantId of their own — they belong to a
            // cluster, and the cluster belongs to the tenant — so the filter goes through the
            // cluster, and the contract does that once rather than this route getting it right
            // again. The JSON is unchanged, status included, whose names ModuleApiTests pins
            // across the two enum declarations.
            IReadOnlyList<EntKube.Contracts.Telemetry.IncidentSummary> incidents =
                await telemetry.GetIncidentsAsync(tenantId, openOnly: open == true, limit: 200, ct);

            return Results.Ok(incidents
                .Select(i => new
                {
                    id = i.Id,
                    alertName = i.AlertName,
                    summary = i.Summary,
                    severity = i.Severity,
                    status = i.Status.ToString(),
                    cluster = i.ClusterName,
                    startsAt = i.StartsAt,
                    resolvedAt = i.ResolvedAt,
                    acknowledgedBy = i.AcknowledgedBy,
                    runbookUrl = i.RunbookUrl,
                })
                .ToList());
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/upgrades", async (
            HttpContext ctx, EntKube.Contracts.Catalog.ICatalogApi catalog, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            // Catalog's own question, asked through Catalog's contract. The report is a read —
            // the service behind it says so itself, and acting on a finding is a different class
            // that goes through the component apply path.
            EntKube.Contracts.Catalog.UpgradeReportSummary report =
                await catalog.GetUpgradeReportAsync(tenantId, DateTime.UtcNow, ct);

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
                sharedPoolMonthlyCost = report.SharedPoolMonthlyCost,
                // Node capacity nothing holds, priced. Inside the shared pool and the
                // total; reported apart because it is what makes the total approach
                // the invoice rather than the sum of what was requested.
                idleMonthlyCost = report.IdleMonthlyCost,
                multiAppMonthlyCost = report.MultiAppMonthlyCost,
                warnings = report.Warnings,
                capacity = report.Capacity.Select(c => new
                {
                    clusterId = c.ClusterId,
                    cluster = c.ClusterName,
                    hasCapacity = c.HasCapacity,
                    nodes = c.Nodes,
                    cpuCapacity = c.CpuCapacity,
                    cpuAllocated = c.CpuAllocated,
                    memoryCapacityGiB = c.MemoryCapacityGiB,
                    memoryAllocatedGiB = c.MemoryAllocatedGiB,
                    // What the nodes cost at the compute rates whatever runs on them —
                    // the figure to hold up against the provider's invoice.
                    nodeMonthlyCost = c.NodeMonthlyCost,
                    idleMonthlyCost = c.IdleMonthlyCost,
                    idleCharged = c.IdleCharged,
                    monthlyCost = c.TotalMonthlyCost,
                }),
                byCustomer = report.ByCustomer.Select(c => new
                {
                    customerId = c.CustomerId,
                    customer = c.CustomerName,
                    monthlyCost = c.MonthlyCost,
                }),
                byApp = report.ByApp.Select(a => new
                {
                    appId = a.AppId,
                    app = a.AppName,
                    customerId = a.CustomerId,
                    customer = a.CustomerName,
                    environments = a.Environments,
                    namespaces = a.Namespaces,
                    cpuCores = a.CpuCores,
                    memoryGiB = a.MemoryGiB,
                    storageGiB = a.StorageGiB,
                    directMonthlyCost = a.DirectMonthlyCost,
                    sharedMonthlyCost = a.SharedMonthlyCost,
                    monthlyCost = a.TotalMonthlyCost,
                    shareOfBillable = a.ShareOfBillable,
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
                    appId = n.AppId,
                    environment = n.EnvironmentName,
                    cpuCores = n.CpuCores,
                    memoryGiB = n.MemoryGiB,
                    storageGiB = n.StorageGiB,
                    loadBalancers = n.LoadBalancers,
                    publicIps = n.PublicIps,
                    directMonthlyCost = n.DirectMonthlyCost,
                    sharedMonthlyCost = n.SharedMonthlyCost,
                    shareOfCluster = n.ShareOfCluster,
                    monthlyCost = n.TotalMonthlyCost,
                    unattributed = n.IsUnattributed,
                    // Its cost was pooled onto the billable namespaces; it is reported so
                    // the pool can be audited, but it is not part of any total.
                    redistributed = n.IsRedistributed,
                    // Not a namespace: the cluster's unallocated node capacity, carried
                    // as a line so it can be pooled and audited like one.
                    idle = n.IsIdle,
                }),
            });
        }).RequireApiScope(ApiScopes.OpsRead);

        // Cost history — what was actually incurred, as opposed to /cost, which is a
        // projection of what today would cost over a month. This is the endpoint a
        // billing system reads; the run rate is the one a dashboard reads.
        api.MapGet("/cost/history", async (
            HttpContext ctx, CostLedgerService ledger,
            string? from, string? to, string? groupBy, Guid? customerId, Guid? appId, Guid? clusterId,
            CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            DateTime now = DateTime.UtcNow;
            DateTime toDay = ParseDay(to) ?? now;
            DateTime fromDay = ParseDay(from) ?? toDay.AddDays(-29);

            if ((toDay - fromDay).TotalDays > 400)
            {
                return Results.Problem(
                    "The window is limited to 400 days.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!Enum.TryParse(groupBy, ignoreCase: true, out CostGroupBy grouping))
            {
                grouping = CostGroupBy.Customer;
            }

            // An empty ledger and a quiet window are different answers, and a caller
            // polling this must not read "never measured" as "cost nothing". Same
            // contract as /drift and /supply-chain.
            if (!await ledger.HasAnyAsync(tenantId, ct))
            {
                return Results.Problem(
                    "The cost ledger has not recorded anything yet for this tenant. It accrues "
                    + "hourly once a cluster has a price sheet.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            CostHistoryReport report = await ledger.GetHistoryAsync(
                tenantId, fromDay, toDay, grouping, now, customerId, appId, clusterId, ct);

            return Results.Ok(new
            {
                from = report.From,
                to = report.To,
                currency = report.Currency,
                groupBy = report.GroupBy.ToString().ToLowerInvariant(),
                totalCost = report.TotalCost,
                previousTotalCost = report.PreviousTotalCost,
                change = report.Change,
                changeFraction = report.ChangeFraction,
                unattributedCost = report.UnattributedCost,
                multiAppCost = report.MultiAppCost,
                platformCost = report.PlatformCost,
                sharedChargedOutCost = report.SharedChargedOutCost,
                dailyAverage = report.DailyAverage,
                // Coverage travels with every figure: an under-measured window
                // under-states cost, and a caller that cannot see that will read the
                // shortfall as a saving.
                coverage = new
                {
                    coveredHours = report.CoveredHours,
                    expectedHours = report.ExpectedHours,
                    gapHours = report.GapHours,
                    completeness = report.Completeness,
                    complete = report.IsComplete,
                },
                warnings = report.Warnings,
                daily = report.Daily.Select(d => new
                {
                    day = d.Day,
                    cost = d.Cost,
                    coveredHours = d.CoveredHours,
                    expectedHours = d.ExpectedHours,
                    complete = d.IsComplete,
                }),
                groups = report.Groups.Select(g => new
                {
                    id = g.Id,
                    label = g.Label,
                    cost = g.Cost,
                    previousCost = g.PreviousCost,
                    change = g.Change,
                    changeFraction = g.ChangeFraction,
                    isNew = g.IsNew,
                    isGone = g.IsGone,
                    cpuCoreHours = g.CpuCoreHours,
                    memoryGiBHours = g.MemoryGiBHours,
                    storageGiBHours = g.StorageGiBHours,
                    unattributed = g.IsUnattributed,
                }),
            });
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/cost/months", async (
            HttpContext ctx, CostLedgerService ledger, int? months, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            if (!await ledger.HasAnyAsync(tenantId, ct))
            {
                return Results.Problem(
                    "The cost ledger has not recorded anything yet for this tenant.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            IReadOnlyList<CostMonth> all =
                await ledger.GetMonthlyAsync(
                    tenantId, Math.Clamp(months ?? 12, 1, 36), DateTime.UtcNow, customerId: null, ct);

            return Results.Ok(all.Select(m => new
            {
                year = m.Year,
                month = m.Month,
                label = m.Label,
                cost = m.Cost,
                currency = m.Currency,
                completeness = m.Completeness,
                complete = m.IsComplete,
            }));
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/rollouts", async (
            HttpContext ctx, EntKube.Contracts.Delivery.IDeliveryApi delivery, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;

            // The last direct module query in this file. It walked rollout -> deployment -> app ->
            // customer to reach its tenant, which is Delivery's own predicate and now Delivery's
            // own method. The published field names are unchanged.
            IReadOnlyList<EntKube.Contracts.Delivery.RolloutSummary> rollouts =
                await delivery.GetRolloutsAsync(tenantId, limit: 100, ct);

            return Results.Ok(rollouts.Select(r => new
            {
                id = r.Id,
                app = r.AppName,
                deployment = r.DeploymentName,
                environment = r.EnvironmentName,
                cluster = r.ClusterName,
                status = r.Status.ToString(),
                startedAt = r.StartedAt,
                finishedAt = r.FinishedAt,
                verdict = r.Verdict,
                triggeredBy = r.TriggeredBy,
            }));
        }).RequireApiScope(ApiScopes.OpsRead);

        api.MapGet("/disaster-recovery", (HttpContext ctx, DrScanCache cache) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            IReadOnlyList<ClusterDrStatus>? statuses = cache.Get(tenantId);

            if (statuses is null)
            {
                return Results.Problem(
                    "No disaster-recovery check has completed yet for this tenant.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            DateTime now = DateTime.UtcNow;

            return Results.Ok(statuses.Select(s => new
            {
                clusterId = s.ClusterId,
                cluster = s.ClusterName,
                veleroInstalled = s.IsVeleroInstalled,
                error = s.Error,
                // "restorable" reflects a clean backup only — a partially-failed backup has
                // skipped resources and is not something to report as recoverable.
                restorable = s.LastUsableBackup is not null,
                lastUsableBackupAt = s.LastUsableBackup?.CompletedAt,
                lastRestoreAt = s.LastSuccessfulRestore?.CompletedAt,
                scheduleCount = s.Schedules.Count,
                gaps = DrReadiness.Evaluate(s, now).Select(g => new
                {
                    key = g.Key,
                    severity = g.Severity.ToString(),
                    title = g.Title,
                    detail = g.Detail,
                    remediation = g.Remediation,
                }),
            }));
        }).RequireApiScope(ApiScopes.OpsRead);

        // ── Configuration (infrastructure-as-code) ──
        //
        // Full CRUD, unlike the rest of this API, because these are the resources a
        // Terraform provider has to be able to create, read back for drift detection,
        // update in place and destroy. A read-only surface cannot be managed declaratively.

        api.MapGet("/cost-rates", async (
            HttpContext ctx, CostRateService rates, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            List<ClusterCostRate> all = await rates.ListAsync(tenantId, ct);

            return Results.Ok(all.Select(ToCostRateDto));
        }).RequireApiScope(ApiScopes.ConfigRead);

        api.MapGet("/cost-rates/{clusterId:guid}", async (
            Guid clusterId, HttpContext ctx, CostRateService rates, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            ClusterCostRate? rate = (await rates.ListAsync(tenantId, ct))
                .FirstOrDefault(r => r.ClusterId == clusterId);

            // 404 rather than an empty object: a provider reading back a resource that no
            // longer exists must be able to tell, so it can plan a re-create instead of
            // reporting a zero-priced cluster as configured.
            return rate is null ? Results.NotFound() : Results.Ok(ToCostRateDto(rate));
        }).RequireApiScope(ApiScopes.ConfigRead);

        api.MapPut("/cost-rates/{clusterId:guid}", async (
            Guid clusterId, CostRateInput input, HttpContext ctx,
            CostRateService rates, CancellationToken ct) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;

            // ⚠️ PatchAsync, not SaveAsync. SaveAsync writes every field of the entity it is
            // handed, so building one here from an optional body meant each `?? default` was an
            // overwrite rather than a fallback: an omitted loadBalancerMonthlyCost zeroed a
            // configured price, which is the opposite of what CostRateInput's own comment
            // promised. And ChargeIdleCapacity, absent from that initializer entirely, took the
            // entity's `= true` on every single write.
            ClusterCostRate? saved = await rates.PatchAsync(
                principal.TenantId, clusterId,
                new CostRatePatch(
                    input.CpuCoreHourCost,
                    input.MemoryGiBHourCost,
                    input.StorageGiBMonthCost,
                    input.ClusterMonthlyOverhead,
                    input.Currency,
                    input.ChargeOnRequests,
                    input.ChargeIdleCapacity,
                    input.LoadBalancerMonthlyCost,
                    input.PublicIpMonthlyCost),
                updatedBy: $"api-token:{principal.TokenName}", ct);

            // SaveAsync returns null when the cluster is not this tenant's, so a crafted
            // cluster id cannot price another tenant's cluster.
            return saved is null ? Results.NotFound() : Results.Ok(ToCostRateDto(saved));
        }).RequireApiScope(ApiScopes.ConfigWrite);

        api.MapDelete("/cost-rates/{clusterId:guid}", async (
            Guid clusterId, HttpContext ctx, CostRateService rates, CancellationToken ct) =>
        {
            Guid tenantId = ctx.GetApiPrincipal()!.TenantId;
            bool deleted = await rates.DeleteAsync(tenantId, clusterId, ct);

            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireApiScope(ApiScopes.ConfigWrite);

        // IAdvisorApi.AcknowledgeAsync has always existed; this route reached past it to the
        // service that backs it, which is how a contract ends up with no consumers while the
        // product does the thing it describes.
        api.MapPost("/advisor/findings/{findingId}/acknowledge", async (
            string findingId, HttpContext ctx,
            EntKube.Contracts.Advisor.IAdvisorApi advisor, CancellationToken ct) =>
        {
            ApiTokenPrincipal principal = ctx.GetApiPrincipal()!;
            await advisor.AcknowledgeAsync(
                principal.TenantId, findingId, $"api-token:{principal.TokenName}", ct);
            return Results.Ok(new { ok = true });
        }).RequireApiScope(ApiScopes.OpsWrite);
    }

    /// <summary>
    /// Parses a yyyy-MM-dd query parameter as a UTC day. Returns null for anything else,
    /// so a mistyped date falls back to the default window rather than being read as
    /// some other date the caller did not ask for.
    /// </summary>
    /// <summary>
    /// The provisioning status string this API has always published.
    ///
    /// <para>⚠️ <b>Not <c>ToString()</c> on the contract enum, on purpose.</b> Fleet's entity calls
    /// the initial state <c>None</c> and the contract calls it <c>Registered</c> — the same state
    /// under a better name. Projecting the contract value directly would therefore have changed a
    /// <b>published</b> JSON string as a side effect of an internal refactor, and the CLI and MCP
    /// server both read this field. Renaming it is a deliberate API change for whoever owns that
    /// decision; it is not something a custody conversion gets to do quietly.</para>
    /// </summary>
    private static string PublishedProvisioningStatus(
        EntKube.Contracts.Fleet.ClusterProvisioningStatus status)
        => status == EntKube.Contracts.Fleet.ClusterProvisioningStatus.Registered
            ? "None"
            : status.ToString();

    private static DateTime? ParseDay(string? value) =>
        DateTime.TryParse(
            value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc)
            : null;

    /// <summary>Shape a price sheet is returned in. A DTO so the entity can move without breaking clients.</summary>
    private static object ToCostRateDto(ClusterCostRate rate) => new
    {
        clusterId = rate.ClusterId,
        cpuCoreHourCost = rate.CpuCoreHourCost,
        memoryGiBHourCost = rate.MemoryGiBHourCost,
        storageGiBMonthCost = rate.StorageGiBMonthCost,
        loadBalancerMonthlyCost = rate.LoadBalancerMonthlyCost,
        publicIpMonthlyCost = rate.PublicIpMonthlyCost,
        clusterMonthlyOverhead = rate.ClusterMonthlyOverhead,
        currency = rate.Currency,
        chargeOnRequests = rate.ChargeOnRequests,
        chargeIdleCapacity = rate.ChargeIdleCapacity,
        updatedAt = rate.UpdatedAt,
        updatedBy = rate.UpdatedBy,
    };

    /// <summary>
    /// Request body for a price sheet. Currency and ChargeOnRequests are nullable so an
    /// omitted field takes the documented default rather than silently becoming empty or
    /// false — a client that does not send ChargeOnRequests means "leave it as designed",
    /// not "bill on usage".
    /// </summary>
    public sealed record CostRateInput(
        decimal CpuCoreHourCost,
        decimal MemoryGiBHourCost,
        decimal StorageGiBMonthCost,
        decimal ClusterMonthlyOverhead,
        string? Currency,
        bool? ChargeOnRequests,
        // Per-month network charges. Nullable so an existing client that does not send
        // them leaves the stored values alone rather than silently zeroing a rate.
        decimal? LoadBalancerMonthlyCost = null,
        decimal? PublicIpMonthlyCost = null,
        // ⚠️ Added late, and its absence was a defect in three directions at once: the field
        // was not in this type, so no client could set it; not in ToCostRateDto, so no client
        // could read it back to detect drift; and not in the object the handler built, so every
        // PUT reset it to the entity's initializer — true — silently re-enabling idle-capacity
        // charging for a tenant who had turned it off in the settings page. Appended last so no
        // positional caller of this record is re-mapped.
        bool? ChargeIdleCapacity = null);
}
