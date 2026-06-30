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
        VaultService vaultService = scope.ServiceProvider.GetRequiredService<VaultService>();

        List<Guid> secretIds = await vaultService.GetObservedAppSecretIdsAsync(ct);
        if (secretIds.Count == 0)
        {
            return;
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
                // Returns true only when the live value differed and the vault was updated.
                if (await vaultService.RefreshAppSecretFromClusterAsync(id, ct))
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

        if (updated > 0)
        {
            logger.LogInformation(
                "ObservedSecretRefreshService: refreshed {Updated} of {Total} observed secret(s)",
                updated, secretIds.Count);
        }
    }
}
