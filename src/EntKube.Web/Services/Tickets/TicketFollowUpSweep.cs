using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// Runs <see cref="TicketFollowUp"/> once a minute, for each tenant in turn — the tenant is an
/// input to every query, never read off the row being acted on.
/// </summary>
public sealed class TicketFollowUpSweep(
    IDbContextFactory<SupportDbContext> dbFactory,
    IServiceScopeFactory scopes,
    ILogger<TicketFollowUpSweep> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ticket follow-up sweep failed.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        List<Guid> tenants;

        await using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenants = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using IServiceScope scope = scopes.CreateScope();
                using IDisposable unattended = scope.DeclareUnattended("ticket-follow-up");

                FollowUpPass pass = await scope.ServiceProvider
                    .GetRequiredService<TicketFollowUp>()
                    .RunAsync(tenantId, DateTime.UtcNow, ct);

                if (pass is not { RemindersSent: 0, Closed: 0, DigestsSent: 0 })
                {
                    logger.LogInformation(
                        "Ticket follow-up for tenant {Tenant}: {Reminders} reminder(s), {Closed} closed, {Digests} digest(s).",
                        tenantId, pass.RemindersSent, pass.Closed, pass.DigestsSent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Ticket follow-up failed for tenant {Tenant}.", tenantId);
            }
        }
    }
}
