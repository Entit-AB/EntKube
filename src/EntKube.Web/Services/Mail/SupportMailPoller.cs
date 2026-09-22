using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// Polls every enabled support mailbox on its own interval.
///
/// <para><b>Why polling and not push.</b> IMAP IDLE would notice a message sooner, but it
/// holds a connection open per mailbox and reconnects on every network hiccup. Support
/// mail is answered in minutes at best — P1 has an hour — so a poll every couple of
/// minutes loses nothing that matters and fails in ways that are easy to reason about.</para>
///
/// <para><b>A mailbox that keeps failing is left alone.</b> Presenting a rejected password
/// over and over is how a service account gets locked, which takes support mail down for
/// everyone rather than for one poll. After
/// <see cref="SupportMailboxService.FailuresBeforeBackingOff"/> consecutive failures the
/// mailbox is skipped until someone saves its settings again — which clears the count, so
/// fixing the password is enough to start it.</para>
/// </summary>
public class SupportMailPoller(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceScopeFactory scopes,
    ILogger<SupportMailPoller> logger) : BackgroundService
{
    /// <summary>
    /// How often the loop wakes to see whose turn it is. Each mailbox still polls on its
    /// own interval; this only bounds how far past it a poll can be.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The floor on a mailbox's own interval. A server will start refusing connections
    /// long before a support mailbox needs reading every second.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing here is urgent at start-up, and the migration runner and vault want the
        // first moments to themselves.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
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
                // A failure here is the loop itself, not a mailbox — those are recorded on
                // the row. Losing the loop would stop every tenant's mail silently.
                logger.LogError(ex, "Support mailbox sweep failed.");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        List<Guid> due = await DueTenantsAsync(ct);

        foreach (Guid tenantId in due)
        {
            ct.ThrowIfCancellationRequested();

            // A scope per mailbox: the service and its DbContext factory are scoped, and
            // one tenant's failure must not carry state into the next.
            using IServiceScope scope = scopes.CreateScope();

            SupportMailboxService mailboxes =
                scope.ServiceProvider.GetRequiredService<SupportMailboxService>();

            MailPollResult result = await mailboxes.PollAsync(tenantId, ct);

            if (result is { Ok: true, Taken: > 0 })
            {
                logger.LogInformation(
                    "Took in {Taken} support message(s) for tenant {Tenant}.",
                    result.Taken, tenantId);
            }
        }
    }

    /// <summary>
    /// Whose mailbox is due. Read in one query so a sweep does not open a connection per
    /// tenant to discover there is nothing to do.
    /// </summary>
    private async Task<List<Guid>> DueTenantsAsync(CancellationToken ct)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<SupportMailbox> enabled = await db.SupportMailboxes.AsNoTracking()
            .Where(m => m.IsEnabled
                && m.ConsecutiveFailures < SupportMailboxService.FailuresBeforeBackingOff)
            .ToListAsync(ct);

        DateTime now = DateTime.UtcNow;

        return
        [
            .. enabled
                .Where(m => m.LastPolledAt is null || now - m.LastPolledAt >= IntervalOf(m))
                .Select(m => m.TenantId)
        ];
    }

    private static TimeSpan IntervalOf(SupportMailbox mailbox)
    {
        TimeSpan configured = TimeSpan.FromSeconds(mailbox.PollIntervalSeconds);

        return configured < MinimumInterval ? MinimumInterval : configured;
    }
}
