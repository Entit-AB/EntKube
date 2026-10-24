using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Background service that keeps "observed" app secrets in step with the cluster.
///
/// Imported secrets land in the vault with Kubernetes sync DISABLED but a target
/// cluster + Secret name recorded — EntKube tracks them without taking ownership,
/// because the live Secret is likely managed by ArgoCD/Flux. This service re-reads
/// those live values on an interval so the vault copy reflects whatever the owning
/// controller last set. When the operator enables sync (takes ownership), the secret
/// drops out of the observed set and is no longer refreshed.
///
/// Runs every 5 minutes. <see cref="VaultService.RefreshAppSecretFromClusterAsync"/>
/// is a no-op when the live value is unchanged, so this does not churn version history.
///
/// <para>⚠️ <b>Why that sentence was not enough for a second instance.</b> "No-op when
/// unchanged" guards against this timer polling faster than the owning controller writes.
/// It says nothing about two instances polling the same secret at the same time: both read
/// the same vault copy, both see the same live change, and — before the claim added in
/// <see cref="VaultService.RefreshAppSecretFromClusterAsync"/> — both appended a version row,
/// so one change was recorded as two. The value converged either way (the ciphertexts differ
/// only by nonce); the audit trail did not. The refresh now claims the vault copy it diffed
/// against, so the archive-and-write happens once per observed change no matter how many
/// instances see it. This service is unchanged in behaviour and needs no switch or leader
/// election of its own: a duplicate pass costs cluster API calls, not correctness.</para>
/// </summary>
public class ObservedSecretRefreshService(
    IServiceScopeFactory scopeFactory,
    ILogger<ObservedSecretRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InterSecretDelay = TimeSpan.FromMilliseconds(150);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so the app finishes starting before the first run.
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshObservedSecretsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ObservedSecretRefreshService run failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RefreshObservedSecretsAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("observed-secret-refresh");
        VaultService vaultService = scope.ServiceProvider.GetRequiredService<VaultService>();
        var dbFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<EntKube.Web.Data.Modules.SecretsDbContext>>();

        // One tenant at a time, outermost (docs/decomposition.md §4.0.1.2). This used to ask for
        // every observed secret in the installation and then refresh each one from the cluster its
        // own row named — so the tenant it acted as came from the row it was processing. That is
        // the shape the sweep decision rejects: here the tenant is an input, and a secret whose
        // KubernetesClusterId points outside its tenant now resolves to no cluster at all rather
        // than being read with that cluster's credential.
        List<Guid> tenants;
        await using (EntKube.Web.Data.Modules.SecretsDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenants = await db.Tenants.Select(x => x.Id).ToListAsync(ct);
        }

        int updated = 0, total = 0;

        foreach (Guid tenantId in tenants)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Per tenant, so one tenant's unreachable cluster cannot end the cycle for the rest —
            // the isolation half of §4.0.1.2, and what the worker extraction needs.
            try
            {
                (int tenantUpdated, int tenantTotal) =
                    await RefreshForTenantAsync(vaultService, tenantId, ct);

                updated += tenantUpdated;
                total += tenantTotal;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Observed-secret refresh failed for tenant {TenantId}", tenantId);
            }
        }

        if (updated > 0)
        {
            logger.LogInformation(
                "ObservedSecretRefreshService: refreshed {Updated} of {Total} observed secret(s)",
                updated, total);
        }
    }

    private async Task<(int Updated, int Total)> RefreshForTenantAsync(
        VaultService vaultService, Guid tenantId, CancellationToken ct)
    {
        List<Guid> secretIds = await vaultService.GetObservedAppSecretIdsAsync(tenantId, ct);
        if (secretIds.Count == 0)
        {
            return (0, 0);
        }

        int updated = 0;

        foreach (Guid id in secretIds)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // Counts only secrets whose live value differed and were rewritten.
                if (await vaultService.RefreshAppSecretFromClusterAsync(tenantId, id, ct)
                    == VaultService.ClusterRefreshResult.Updated)
                {
                    updated++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh observed secret {SecretId}", id);
            }

            await Task.Delay(InterSecretDelay, ct);
        }

        return (updated, secretIds.Count);
    }
}
