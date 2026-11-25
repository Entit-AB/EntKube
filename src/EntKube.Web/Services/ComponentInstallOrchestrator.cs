using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// How one run of the component apply flow should behave. The default is a first
/// install; an upgrade differs only in accepting an already-Installed release.
/// </summary>
public sealed record ComponentApplyOptions
{
    /// <summary>True for an in-place upgrade of a release that is already installed.</summary>
    public bool IsUpgrade { get; init; }

    /// <summary>Skip Helm's --wait so the call returns as soon as the manifests are applied.</summary>
    public bool NoWait { get; init; }
}

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
    IDbContextFactory<CatalogDbContext> dbFactory,
    ComponentLifecycleService lifecycleService,
    KeycloakService keycloakService,
    HarborService harborService,
    OpenLdapService openLdapService,
    Dr.VeleroService veleroService,
    StalwartService stalwartService,
    HeadscaleService headscaleService,
    ClusterProvidedComponents providedComponents,
    KyvernoUpgradeGuard kyvernoGuard,
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate)
{
    /// <summary>
    /// Runs the complete install flow for a single registered component and
    /// returns the aggregated Helm/kubectl output. Mirrors the sequence used by
    /// the Components tab so a bootstrap step behaves exactly like a manual install.
    /// </summary>
    public async Task<HelmExecutionResult> InstallAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default) =>
        await ApplyAsync(tenantId, componentId, new ComponentApplyOptions(), ct);

    /// <summary>
    /// The same flow as <see cref="InstallAsync"/>, parameterised for the cases that are
    /// not a first install: an in-place upgrade of a release that is already Installed,
    /// and a run that should not block on Helm's --wait.
    /// </summary>
    public async Task<HelmExecutionResult> ApplyAsync(
        Guid tenantId, Guid componentId, ComponentApplyOptions options, CancellationToken ct = default)
    {
        // Before the component is marked as installing, and before helm is given anything to wait on: a
        // Harbor pointed at a database written by a newer Harbor cannot start, and every way of finding
        // that out later costs half an hour and points at the wrong thing.
        // Installing or upgrading a component is one decision. What follows it is not: the helm
        // release, the component's own secrets sync, its external routes, and for wg-easy a
        // re-apply of the Istio gateway — every one a separately gated primitive for an act the
        // operator approved once.
        using IDisposable operation = gate.BeginOperation($"apply component {componentId}");

        if (await harborService.DescribeDatabaseSchemaConflictAsync(tenantId, componentId, ct) is string conflict)
        {
            return new HelmExecutionResult { Success = false, Output = conflict };
        }

        // And for the same reason, a Harbor pointed at a Redis the cluster does not have: the core never
        // becomes ready, so every way of finding that out costs the full Helm deadline and then points at
        // a namespace with no Redis pod in it.
        if (await harborService.DescribeRedisConflictAsync(tenantId, componentId, ct) is string redisConflict)
        {
            return new HelmExecutionResult { Success = false, Output = redisConflict };
        }

        // And a Kyverno that would stop enforcing the policies already on the cluster. Helm refuses that
        // too, but in terms of a flag that turns the policies off.
        if (await kyvernoGuard.DescribeLegacyPolicyConflictAsync(tenantId, componentId, ct) is string policyConflict)
        {
            return new HelmExecutionResult { Success = false, Output = policyConflict };
        }

        // Managed clusters usually already run a metrics-server, in a namespace of their own choosing and
        // not as a Helm release. Only one server can back the API, so a second install fails on the
        // existing APIService; track what is there instead. Before anything is marked Installing.
        if (await providedComponents.TrackIfProvidedAsync(tenantId, componentId, options.IsUpgrade, ct) is { } provided)
        {
            return provided;
        }

        if (options.IsUpgrade)
        {
            await lifecycleService.PrepareUpgradeAsync(tenantId, componentId, ct);
        }
        else
        {
            await lifecycleService.PrepareInstallAsync(tenantId, componentId, ct);
        }

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
        // Regenerate OpenLDAP Helm values + seed LDIF from the authored directory.
        await openLdapService.RefreshHelmValuesIfConfiguredAsync(tenantId, componentId);
        // For ClusterIssuer TLS, provision the cert-manager Certificate → tls Secret BEFORE the
        // install so the StatefulSet's init container can mount it (chart does not self-sign).
        await openLdapService.ApplyTlsCertificateIfNeededAsync(tenantId, componentId);
        // Refresh Velero's S3 backup-target values + credentials before install/upgrade.
        await veleroService.RefreshHelmValuesIfConfiguredAsync(tenantId, componentId, ct);
        // Regenerate the mail components' manifests from what the operator authored, so an install
        // deploys the current configuration rather than whatever was rendered when it was added.
        await stalwartService.RefreshManifestIfConfiguredAsync(tenantId, componentId);
        await stalwartService.RefreshRspamdManifestAsync(tenantId, componentId, ct);
        await stalwartService.RefreshRoundcubeManifestAsync(tenantId, componentId, ct);
        await stalwartService.RefreshSnappyMailManifestAsync(tenantId, componentId, ct);
        // Same reason as OpenLDAP: the StatefulSet mounts the TLS Secret, so a pod started before
        // cert-manager has issued it sits in ContainerCreating until somebody looks.
        await stalwartService.ApplyTlsCertificateIfNeededAsync(tenantId, componentId, ct);
        // Turning high availability on or off changes whether the StatefulSet has a local data
        // volume, and volumeClaimTemplates is immutable — so the install would be refused on a field
        // nobody edited. No-op unless that boundary is actually being crossed.
        await stalwartService.PrepareStorageShapeIfNeededAsync(tenantId, componentId, ct);

        HelmCommand command = await lifecycleService.GetInstallCommandAsync(tenantId, componentId, ct);
        command.NoWait = options.NoWait;

        // Pre-sync component secrets so they exist in K8s before the pod starts.
        // Components like Keycloak reference secrets via extraEnvFrom at pod startup,
        // which happens during Helm's --wait phase, so the secret must exist first.
        HelmExecutionResult preSyncResult = await lifecycleService.SyncComponentSecretsAsync(tenantId, componentId);

        HelmExecutionResult result = await lifecycleService.ExecuteHelmAsync(tenantId, componentId, command, ct);

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
            HelmExecutionResult subchartResult = await lifecycleService.InstallSubchartsAsync(tenantId, componentId);

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
            tenantId, componentId, result.Success, result.Success ? null : result.Output, ct);

        // After successful install, sync any secrets marked for K8s sync.

        if (result.Success)
        {
            HelmExecutionResult syncResult = await lifecycleService.SyncComponentSecretsAsync(tenantId, componentId);

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
            await headscaleService.EnsureExternalRouteAfterInstallAsync(tenantId, componentId);

            // Apply HTTPRoute + Certificate manifests for any configured external routes.
            HelmExecutionResult routeResult = await lifecycleService.ApplyExternalRoutesAsync(tenantId, componentId);
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
                HelmExecutionResult? gwResult = await lifecycleService.EnsureGatewayWireGuardPortAsync(tenantId, componentId);
                if (gwResult is not null && !string.IsNullOrWhiteSpace(gwResult.Output))
                {
                    result = new HelmExecutionResult
                    {
                        Success = result.Success && gwResult.Success,
                        Output = result.Output + "\n\n--- Gateway UDP Port ---\n" + gwResult.Output
                    };
                }
            }

            // There used to be a hook here that re-applied the cluster's collector when the telemetry
            // indexer was installed, so writes and reads moved into the cluster together. The indexer is
            // retired (docs/telemetry-central-again.md) and the collector's destination is the management
            // plane again, so there is nothing to move: a collector points there from registration
            // onwards, and an older one addressed at an indexer is moved back on its next apply by
            // TelemetryIngestDefaults.RepointToManagementPlane.
        }

        return result;
    }

    private async Task<bool> IsComponentNamedAsync(Guid componentId, string name, CancellationToken ct)
    {
        using CatalogDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents.AnyAsync(c => c.Id == componentId && c.Name == name, ct);
    }
}
