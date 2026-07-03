using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Encapsulates the full "install a component" sequence — the state-machine
/// transitions plus all the component-specific hooks (Keycloak DB prep, Harbor
/// credential refresh, Headscale/wg-easy route + gateway handling) that must run
/// around a Helm install. Both the interactive UI (ClusterDetail) and the
/// blueprint bootstrap runner call this so the behavior stays identical.
///
/// This is a thin orchestration layer over <see cref="ComponentLifecycleService"/>;
/// it holds no state of its own. It throws <see cref="InvalidOperationException"/>
/// on validation failures (e.g. unmet dependencies) so callers can surface them;
/// a non-throwing Helm failure is returned as a <see cref="HelmExecutionResult"/>
/// with <c>Success = false</c>.
/// </summary>
public class ComponentInstallOrchestrator(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ComponentLifecycleService lifecycleService,
    KeycloakService keycloakService,
    HarborService harborService,
    HeadscaleService headscaleService)
{
    /// <summary>
    /// Runs the complete install flow for a single registered component and
    /// returns the aggregated Helm/kubectl output. Mirrors the sequence used by
    /// the Components tab so a bootstrap step behaves exactly like a manual install.
    /// </summary>
    public async Task<HelmExecutionResult> InstallAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        await lifecycleService.PrepareInstallAsync(componentId, ct);

        // Refresh Keycloak DB config into HelmValues from the stored config (if any).
        await keycloakService.RefreshDatabaseHelmValuesIfConfiguredAsync(tenantId, componentId);
        // Grant all schema privileges to the database owner so Liquibase can run on install.
        await keycloakService.GrantDatabaseOwnerPermissionsIfConfiguredAsync(tenantId, componentId);
        // Release any stuck Liquibase lock so migrations can proceed on startup.
        await keycloakService.ReleaseLiquibaseLockIfConfiguredAsync(tenantId, componentId);
        // Update realm frontendUrl to match the configured admin URL (fixes restored-database URL mismatch).
        await keycloakService.FixRealmUrlsIfConfiguredAsync(tenantId, componentId);
        // Refresh Harbor CNPG + S3 credentials in Helm values before install/upgrade.
        await harborService.RefreshHelmValuesIfConfiguredAsync(tenantId, componentId);

        HelmCommand command = await lifecycleService.GetInstallCommandAsync(componentId, ct);

        // Pre-sync component secrets so they exist in K8s before the pod starts.
        // Components like Keycloak reference secrets via extraEnvFrom at pod startup,
        // which happens during Helm's --wait phase, so the secret must exist first.
        HelmExecutionResult preSyncResult = await lifecycleService.SyncComponentSecretsAsync(componentId);

        HelmExecutionResult result = await lifecycleService.ExecuteHelmAsync(componentId, command, ct);

        // Prepend pre-sync output so the caller can see exactly how many keys were synced.
        if (!string.IsNullOrWhiteSpace(preSyncResult.Output))
        {
            result = new HelmExecutionResult
            {
                Success = result.Success,
                ExitCode = result.ExitCode,
                Output = $"--- Pre-install Secret Sync ---\n{preSyncResult.Output}\n\n{result.Output}"
            };
        }

        if (result.Success)
        {
            HelmExecutionResult subchartResult = await lifecycleService.InstallSubchartsAsync(componentId);

            if (!string.IsNullOrWhiteSpace(subchartResult.Output))
            {
                result = new HelmExecutionResult
                {
                    Success = result.Success && subchartResult.Success,
                    Output = result.Output + "\n" + subchartResult.Output
                };
            }
        }

        await lifecycleService.MarkInstallResultAsync(
            componentId, result.Success, result.Success ? null : result.Output, ct);

        // After successful install, sync any secrets marked for K8s sync.

        if (result.Success)
        {
            HelmExecutionResult syncResult = await lifecycleService.SyncComponentSecretsAsync(componentId);

            if (!string.IsNullOrWhiteSpace(syncResult.Output))
            {
                result = new HelmExecutionResult
                {
                    Success = result.Success,
                    Output = result.Output + "\n\n--- Secret Sync ---\n" + syncResult.Output
                };
            }

            // For components that self-expose (e.g. headscale): ensure the external route
            // record exists before applying so it's included in the Gateway manifest.
            await headscaleService.EnsureExternalRouteAfterInstallAsync(componentId);

            // Apply HTTPRoute + Certificate manifests for any configured external routes.
            HelmExecutionResult routeResult = await lifecycleService.ApplyExternalRoutesAsync(componentId);
            if (!string.IsNullOrWhiteSpace(routeResult.Output))
            {
                result = new HelmExecutionResult
                {
                    Success = result.Success && routeResult.Success,
                    Output = result.Output + "\n\n--- Route Apply ---\n" + routeResult.Output
                };
            }

            // wg-easy: automatically re-apply its target Istio gateway so the
            // gateway's LoadBalancer exposes the WireGuard UDP port — no manual
            // Save & Apply on the gateway required.
            if (await IsComponentNamedAsync(componentId, "wg-easy", ct))
            {
                HelmExecutionResult? gwResult = await lifecycleService.EnsureGatewayWireGuardPortAsync(componentId);
                if (gwResult is not null && !string.IsNullOrWhiteSpace(gwResult.Output))
                {
                    result = new HelmExecutionResult
                    {
                        Success = result.Success && gwResult.Success,
                        Output = result.Output + "\n\n--- Gateway UDP Port ---\n" + gwResult.Output
                    };
                }
            }
        }

        return result;
    }

    private async Task<bool> IsComponentNamedAsync(Guid componentId, string name, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents.AnyAsync(c => c.Id == componentId && c.Name == name, ct);
    }
}
