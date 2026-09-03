using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Upgrades;

/// <summary>What the operator asked for on one row of the upgrade report.</summary>
public sealed record ComponentUpgradeRequest
{
    public required Guid TenantId { get; init; }
    public required Guid ComponentId { get; init; }

    /// <summary>Chart version to move to. Null keeps the version already recorded (a re-apply).</summary>
    public string? TargetChartVersion { get; init; }

    /// <summary>
    /// Permission to take blocked workloads to zero replicas for the duration of the
    /// upgrade. Without it, an upgrade that needs downtime is refused rather than hung.
    /// </summary>
    public bool AllowScaleDown { get; init; }

    /// <summary>Skip Helm's --wait — for repair paths that must not block a page render.</summary>
    public bool NoWait { get; init; }
}

/// <summary>Result of one upgrade attempt, with the whole transcript for the output pane.</summary>
public sealed record ComponentUpgradeOutcome
{
    public required bool Success { get; init; }
    public required string Output { get; init; }

    /// <summary>Set when the upgrade never started (validation, refused downtime).</summary>
    public string? Error { get; init; }

    /// <summary>The version the component is now pinned to.</summary>
    public string? AppliedVersion { get; init; }

    public static ComponentUpgradeOutcome Refused(string error) =>
        new() { Success = false, Output = "", Error = error };
}

/// <summary>
/// Runs an upgrade for one installed component from start to finish, so the fleet
/// upgrade report can act on a finding instead of only linking to the cluster.
///
/// The sequence is: pin the component to the target chart version, ask
/// <see cref="ReleaseVolumeGuard"/> what cannot roll in place, take those workloads down,
/// run the ordinary component apply through <see cref="ComponentInstallOrchestrator"/> —
/// the same path a manual install uses, so every component-specific hook still runs — and
/// then bring the workloads back whatever the outcome.
///
/// The restore is in a finally block on purpose: a failed or cancelled upgrade that left a
/// component at zero replicas would turn "the upgrade did not apply" into an outage.
/// </summary>
public class ComponentUpgradeRunner(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ComponentLifecycleService lifecycleService,
    ComponentInstallOrchestrator orchestrator,
    ReleaseVolumeGuard volumeGuard,
    ILogger<ComponentUpgradeRunner> logger)
{
    public async Task<ComponentUpgradeOutcome> UpgradeAsync(
        ComponentUpgradeRequest request, CancellationToken ct = default)
    {
        ClusterComponent? component;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            component = await db.ClusterComponents
                .Include(c => c.Cluster)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == request.ComponentId
                    && c.Cluster.TenantId == request.TenantId, ct);
        }

        if (component is null)
            return ComponentUpgradeOutcome.Refused("Component not found in this tenant.");

        if (component.Status is ComponentStatus.Installing or ComponentStatus.Uninstalling)
            return ComponentUpgradeOutcome.Refused(
                $"'{component.Name}' has an operation in progress. Wait for it to finish.");

        if (string.IsNullOrWhiteSpace(component.Cluster.Kubeconfig))
            return ComponentUpgradeOutcome.Refused(
                $"Cluster '{component.Cluster.Name}' has no kubeconfig configured.");

        string kubeconfig = component.Cluster.Kubeconfig;
        bool wasInstalled = component.Status == ComponentStatus.Installed;

        // Helm builds the command from the component's *stored* chart version, so without
        // repinning first an upgrade re-runs `--version <current>` and changes nothing.
        if (!string.IsNullOrWhiteSpace(request.TargetChartVersion)
            && !string.Equals(request.TargetChartVersion, component.HelmChartVersion, StringComparison.Ordinal))
        {
            await lifecycleService.UpdateConfigurationAsync(
                request.ComponentId, helmValues: null, chartVersion: request.TargetChartVersion, ct: ct);
        }

        StringBuilder log = new();
        string version = request.TargetChartVersion ?? component.HelmChartVersion ?? "latest";

        log.AppendLine($"Upgrading {component.Name} on {component.Cluster.Name} "
            + $"({component.HelmChartVersion ?? "unversioned"} → {version}).");
        log.AppendLine();

        VolumePreflight preflight = await volumeGuard.InspectAsync(request.ComponentId, ct);

        foreach (string warning in preflight.Warnings)
            log.AppendLine($"NOTE: {warning}");

        if (preflight.Error is not null)
            log.AppendLine($"NOTE: volume check could not run — {preflight.Error}");

        if (preflight.RequiresScaleDown && !request.AllowScaleDown)
        {
            return new ComponentUpgradeOutcome
            {
                Success = false,
                Output = log.ToString(),
                Error = $"{DescribeBlocked(preflight)} cannot be rolled in place on this cluster and would leave "
                    + "the upgrade stuck on a Multi-Attach error. Re-run the upgrade with downtime accepted.",
                AppliedVersion = component.HelmChartVersion,
            };
        }

        IReadOnlyList<BlockedWorkload> blocked = preflight.Blocked;
        bool scaledDown = false;

        bool success = false;
        string? error = null;
        string appliedVersion = component.HelmChartVersion ?? version;

        try
        {
            if (blocked.Count > 0)
            {
                log.AppendLine("--- Scale down (volumes cannot be shared between old and new pods) ---");
                HelmExecutionResult down = await volumeGuard.ScaleDownAsync(kubeconfig, blocked, ct);
                scaledDown = true;
                log.AppendLine(down.Output);
                log.AppendLine();

                if (!down.Success)
                {
                    // Upgrading over a workload that would not drain reproduces exactly the
                    // hang we are here to avoid, so stop while the restore can still undo it.
                    error = "Could not take the blocked workloads down cleanly; the upgrade was not started.";
                }
            }

            if (error is null)
            {
                log.AppendLine("--- Helm upgrade ---");

                HelmExecutionResult result = await orchestrator.ApplyAsync(
                    request.TenantId, request.ComponentId,
                    new ComponentApplyOptions { IsUpgrade = wasInstalled, NoWait = request.NoWait }, ct);

                log.AppendLine(result.Output);

                success = result.Success;
                appliedVersion = version;
                error = result.Success ? null : "The Helm upgrade failed — see the output below.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Upgrade of component {ComponentId} failed", request.ComponentId);
            log.AppendLine($"ERROR: {ex.Message}");
            error = ex.Message;
        }
        finally
        {
            // Always before returning: a component left at zero replicas turns "the upgrade
            // did not apply" into an outage. Also mandatory on success — Helm's three-way
            // merge omits an unchanged replica count from its patch, so it would leave our
            // zero in place.
            if (scaledDown)
            {
                log.AppendLine();
                log.AppendLine("--- Scale up ---");
                try
                {
                    HelmExecutionResult up = await volumeGuard.RestoreAsync(kubeconfig, blocked, ct);
                    log.AppendLine(up.Output);

                    if (!up.Success)
                    {
                        success = false;
                        error ??= "The upgrade applied but a workload did not come back up — see the output below.";
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to scale {Component} back up after an upgrade", component.Name);
                    log.AppendLine($"ERROR: could not scale the workloads back up — {ex.Message}. "
                        + "They are still at zero replicas and need attention.");
                    success = false;
                    error ??= "The workloads could not be scaled back up — see the output below.";
                }
            }
        }

        return new ComponentUpgradeOutcome
        {
            Success = success,
            Output = log.ToString(),
            Error = error,
            AppliedVersion = appliedVersion,
        };
    }

    private static string DescribeBlocked(VolumePreflight preflight) =>
        preflight.Blocked.Count == 1
            ? $"Deployment/{preflight.Blocked[0].Name}"
            : $"{preflight.Blocked.Count} workloads";
}
