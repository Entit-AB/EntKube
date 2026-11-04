using System.Data.Common;
using EntKube.Web.Data;
using EntKube.Web.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>What this instance can tell about who is running the work exactly one should run.</summary>
public enum SingletonWorkPlacement
{
    /// <summary>
    /// The provider takes no lock and this instance sweeps — a single-instance install, where
    /// there is nothing to coordinate and nothing to warn about.
    /// </summary>
    NotApplicable,

    /// <summary>A provider nobody has taught a lock. The question cannot be answered here.</summary>
    ProviderUnknown,

    /// <summary>This instance sweeps and holds the claim. The expected answer on the worker.</summary>
    Holding,

    /// <summary>
    /// This instance sweeps, and another instance already holds the claim. ⚠️ Two instances are
    /// configured to run singleton work — the mistake
    /// <c>docs/running-two-instances.md</c> says is the likely one: forgetting to set the flag
    /// false on the instance you just added.
    /// </summary>
    Contended,

    /// <summary>
    /// ⚠️ NOTHING is sweeping. No cost accrual, no alert evaluation, no certificate-expiry
    /// checks, no backup verification — and every one of them fails by simply not happening.
    /// </summary>
    Unclaimed,

    /// <summary>Another instance sweeps and this one correctly does not. The expected answer on a web instance.</summary>
    Delegated,
}

/// <summary>
/// Notices when the singleton-work flag is wrong.
///
/// <para><b>The gap this closes.</b> <c>HostedWork</c> made <em>N web + 1 worker</em>
/// expressible, and <c>docs/running-two-instances.md</c> says of it: "⚠️ The flag is placement,
/// not a lease. <b>Nothing checks that exactly one container has it true.</b>" Both ways of
/// getting it wrong are silent. Two sweepers duplicate every cycle; zero sweepers means no
/// background work happens at all, anywhere, and nothing says so — the failure is the absence of
/// events, which no amount of log reading finds.</para>
///
/// <para>⭐ <b>It uses the lock that already exists.</b> <c>MigrationLock</c> has taken a real
/// advisory lock since the two-instance work, so the primitive §9.4 of the decomposition says is
/// missing is present — it was simply hardcoded to one key. <see cref="AdvisoryLock"/> takes the
/// key as a parameter, and this holds a second one. No table, no migration, and the hold is
/// released when the connection closes, so an instance that is killed does not leave the
/// installation looking like somebody is still sweeping.</para>
///
/// <para>⚠️ <b>This reports; it does not enforce.</b> An instance that loses the race keeps
/// sweeping. Standing down on a lost race would mean a transient database hiccup could stop all
/// background work, which is the direction <c>HostedWork.RunsSingletonWork</c> deliberately
/// refuses to fail in — and the four genuinely damaging sweeps have their own per-row claims, so
/// duplication is wasteful rather than destructive. Turning this into enforcement is a separate
/// decision with a separate risk.</para>
/// </summary>
public sealed class SingletonWorkWitness(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration config,
    ILogger<SingletonWorkWitness> logger,
    TimeSpan? interval = null) : BackgroundService
{
    /// <summary>
    /// How often an instance that is NOT holding the claim looks again.
    ///
    /// <para>Re-probed rather than checked once at startup, because the interesting transition
    /// happens later: the worker dies, and the web instances are the only things left to notice
    /// that nothing is sweeping any more.</para>
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private readonly TimeSpan interval = interval ?? DefaultInterval;

    /// <summary>
    /// The whole decision, as a table, so it can be tested without a database.
    ///
    /// <para>⚠️ The SQLite row is the one worth reading twice. That provider takes no lock
    /// because two processes sharing one file is not a deployment this supports — so on SQLite
    /// there IS no other instance, and <c>RunSingletonWork=false</c> is unambiguously wrong
    /// rather than merely unverifiable. It is the one case that needs no lock to diagnose.</para>
    /// </summary>
    /// <param name="granted">Null when no attempt could be made.</param>
    public static SingletonWorkPlacement Classify(
        bool runsSingletonWork, string? providerName, bool? granted)
    {
        if (AdvisoryLock.NeedsNoLock(providerName))
        {
            return runsSingletonWork
                ? SingletonWorkPlacement.NotApplicable
                : SingletonWorkPlacement.Unclaimed;
        }

        if (granted is null)
        {
            return SingletonWorkPlacement.ProviderUnknown;
        }

        if (runsSingletonWork)
        {
            return granted.Value
                ? SingletonWorkPlacement.Holding
                : SingletonWorkPlacement.Contended;
        }

        return granted.Value
            ? SingletonWorkPlacement.Unclaimed
            : SingletonWorkPlacement.Delegated;
    }

    /// <summary>Whether a placement is something an operator has to act on.</summary>
    public static bool IsMisconfiguration(SingletonWorkPlacement placement)
        => placement is SingletonWorkPlacement.Contended or SingletonWorkPlacement.Unclaimed;

    // ── Wiring below. Deliberately thin and deliberately untested: it opens a connection, runs
    //    one statement and keeps the connection if it won. The decisions are all above.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool runsSingletonWork = HostedWork.RunsSingletonWork(config);
        SingletonWorkPlacement? reported = null;
        ApplicationDbContext? held = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (held is null)
                {
                    (SingletonWorkPlacement placement, ApplicationDbContext? keep) =
                        await ProbeAsync(runsSingletonWork, stoppingToken);

                    held = keep;

                    // Logged on CHANGE, not every pass: a misconfiguration an operator has not
                    // fixed must not bury the log it would be found in.
                    if (placement != reported)
                    {
                        reported = placement;
                        Report(placement);
                    }
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            // Disposing closes the connection, which releases the lock.
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }
    }

    private async Task<(SingletonWorkPlacement, ApplicationDbContext?)> ProbeAsync(
        bool runsSingletonWork, CancellationToken ct)
    {
        ApplicationDbContext? db = null;

        try
        {
            db = await dbFactory.CreateDbContextAsync(ct);
            string? provider = db.Database.ProviderName;
            string? acquire = AdvisoryLock.TryAcquireSql(
                provider, AdvisoryLock.SingletonWorkKey, AdvisoryLock.SingletonWorkResource);

            bool? granted = null;

            if (acquire is not null)
            {
                DbConnection connection = db.Database.GetDbConnection();
                await connection.OpenAsync(ct);

                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText = acquire;
                granted = AdvisoryLock.WasGranted(await cmd.ExecuteScalarAsync(ct));
            }

            SingletonWorkPlacement placement = Classify(runsSingletonWork, provider, granted);

            // The context is kept open ONLY while holding, because the hold is the open
            // connection. Everything else releases it immediately.
            if (placement == SingletonWorkPlacement.Holding)
            {
                return (placement, db);
            }

            await db.DisposeAsync();
            return (placement, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (db is not null)
            {
                await db.DisposeAsync();
            }

            // A probe that could not run says nothing about the configuration, and must not be
            // reported as though it had found a misconfiguration.
            logger.LogDebug(ex, "Could not determine which instance runs singleton work.");
            return (SingletonWorkPlacement.ProviderUnknown, null);
        }
    }

    private void Report(SingletonWorkPlacement placement)
    {
        switch (placement)
        {
            case SingletonWorkPlacement.Contended:
                logger.LogError(
                    "Another instance already claims the background work that must run on exactly "
                    + "one. Both are configured to run it, so every sweep runs twice. Set {Key}=false "
                    + "on all but one instance.",
                    HostedWork.RunSingletonWorkKey);
                break;

            case SingletonWorkPlacement.Unclaimed:
                logger.LogError(
                    "NO instance is running the background work that must run on exactly one. Cost "
                    + "accrual, alert evaluation, certificate expiry and backup checks are not "
                    + "happening anywhere. Set {Key}=true on exactly one instance.",
                    HostedWork.RunSingletonWorkKey);
                break;

            case SingletonWorkPlacement.Holding:
                logger.LogInformation(
                    "This instance runs the background work that must run on exactly one.");
                break;

            case SingletonWorkPlacement.Delegated:
                logger.LogInformation(
                    "Another instance runs the background work that must run on exactly one.");
                break;

            case SingletonWorkPlacement.ProviderUnknown:
                logger.LogWarning(
                    "Cannot tell which instance runs singleton work on this database provider, so "
                    + "a misconfiguration here would go unreported.");
                break;
        }
    }
}
