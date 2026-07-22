using Microsoft.Extensions.Options;

namespace EntKube.Web.Services;

/// <summary>
/// Periodically re-applies every <see cref="Data.CertificateDistribution"/> so mirrored Secrets
/// reach newly-created namespaces and pick up certificate renewals. Runs in a background scope
/// where the cluster-change gate has no interactive sink, so it applies without blocking for
/// acknowledgment (the same "automated flows bypass the gate" boundary as remediation).
///
/// trust-manager Bundles need no reconciler — trust-manager keeps those in sync itself. This
/// loop exists only for the cert+key (Secret-mirror) mechanism.
/// </summary>
public class CertificateDistributionReconcileService(
    IServiceScopeFactory scopeFactory,
    IOptions<CertificateDistributionReconcileOptions> options,
    ILogger<CertificateDistributionReconcileService> logger) : BackgroundService
{
    private readonly CertificateDistributionReconcileOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Certificate distribution reconciler disabled.");
            return;
        }

        // Let the app fully start before the first pass.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                CertificateDistributionService svc =
                    scope.ServiceProvider.GetRequiredService<CertificateDistributionService>();
                await svc.ReconcileAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Certificate distribution reconcile pass failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);
        }
    }
}

/// <summary>Configuration for <see cref="CertificateDistributionReconcileService"/> (bound from "CertificateDistribution").</summary>
public sealed class CertificateDistributionReconcileOptions
{
    /// <summary>When false, the reconciler does not run (manual apply still works).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes between reconcile passes.</summary>
    public int IntervalMinutes { get; set; } = 30;
}
