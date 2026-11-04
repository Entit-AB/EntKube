
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EntKube.Web.Modules;

/// <summary>
/// Where a background service runs when there is more than one EntKube instance.
///
/// <para><b>The problem this exists for.</b> All twelve module compositions are added
/// unconditionally, so every one of the ~32 hosted services starts on every instance — and
/// there was not even a switch to run an instance web-only. Two instances therefore run every
/// sweep twice. For most of them that is wasteful; for four of them it is damaging, and quietly:
/// two <c>RolloutWatcherService</c>s both call <c>kubectl rollout undo</c> with no
/// <c>--to-revision</c>, so the second undo rolls <em>forward</em> to the bad release while both
/// record success. See docs/decomposition.md §9.</para>
///
/// <para>⭐ <b>So placement is declared at the registration site, with no default.</b> That is the
/// same shape as <c>ChangeIntent</c> on the exec verbs, and for the same reason: a defaulted
/// parameter lets every existing site compile unchanged and declare nothing, which is how the gap
/// arose. <c>HostedWorkPlacementTests</c> refuses a bare <c>AddHostedService</c> outside this
/// file, so the next service added has to say which kind it is.</para>
///
/// <para><b>The two kinds are not interchangeable.</b> A sweep must run on exactly one instance;
/// <c>PresenceHeartbeatService</c> must run on <em>every</em> instance, because it refreshes the
/// circuits held by its own process and a single leader would report everyone else offline. A
/// blanket "don't run background work here" switch would have broken it — which is why the
/// distinction is in the API rather than in a config flag.</para>
///
/// <para>⚠️ This is a placement switch, NOT a lease. With
/// <c>Workers:RunSingletonWork</c> left true on two instances, every sweep still runs twice; this
/// is what makes <em>N web + 1 worker</em> expressible, and it is the cheapest form of the
/// <c>entkube-worker</c> extraction in §4.2.</para>
///
/// <para><b>Updated 2026-11-04.</b> This used to end "Claims on the four dangerous services are
/// separate work and still needed" — they are done. All four of §9.3 now take a per-row claim
/// (<c>RolloutJudgementClaimTests</c>, <c>BootstrapRunClaimTests</c>,
/// <c>ObservedSecretRefreshClaimTests</c>, <c>TelemetryIncidentClaimTests</c>), so duplication
/// from a mis-set flag is wasteful rather than destructive. <b>And nothing used to notice the
/// flag was wrong at all</b> — in either direction, including the worse one where it is false
/// everywhere and no background work happens anywhere. <see cref="T:EntKube.Web.Services.SingletonWorkWitness"/>
/// reports that now.</para>
/// </summary>
public static class HostedWork
{
    /// <summary>
    /// Whether this instance runs the work that must happen on exactly one of them.
    ///
    /// <para>⚠️ Defaults to <b>true</b>, deliberately. Every existing single-instance install
    /// must keep sweeping without being reconfigured; it is the <em>second</em> instance that
    /// opts out. A default of false would silently stop every sweep in every deployment on
    /// upgrade, which is the worst possible direction for this flag to fail.</para>
    /// </summary>
    public const string RunSingletonWorkKey = "Workers:RunSingletonWork";

    public static bool RunsSingletonWork(IConfiguration config) =>
        config.GetValue<bool?>(RunSingletonWorkKey) ?? true;

    /// <summary>
    /// Work that must happen on exactly ONE instance: sweeps, pollers, schedulers, reconcilers
    /// that write. Skipped entirely where <see cref="RunSingletonWorkKey"/> is false.
    /// </summary>
    public static IServiceCollection AddSingletonWork<T>(this IServiceCollection services)
        where T : class, IHostedService
    {
        // ⚠️ Registered BY TYPE, not through the factory overload, and that is not a style choice.
        // A factory descriptor has a null ImplementationType, which made the concrete service
        // invisible to ModuleCompositionTests — the ratchet that keeps ModuleMap and the
        // composition root from drifting apart. It failed loudly on the first attempt at this and
        // was right to: the mapping from module to service is the thing it exists to check, and a
        // factory erases it. `SingletonWorkHost<T>` keeps T in the descriptor where the ratchet
        // can unwrap it.
        //
        // TryAddSingleton so a service that is ALSO registered elsewhere keeps its own
        // registration and only one instance exists — `AddHostedService<T>()` would have
        // constructed a second copy, leaving the instance a caller resolves different from the one
        // running.
        services.TryAddSingleton<T>();
        services.AddHostedService<SingletonWorkHost<T>>();
        return services;
    }

    /// <summary>
    /// As above, for a service the caller constructs itself.
    ///
    /// <para>⚠️ Needed, not a convenience: the five <c>SegmentSealService</c> registrations are
    /// five instances of ONE type, one per signal, so a type-generic registration cannot express
    /// them — and <c>GitSyncService</c> is resolved from a registration it already has elsewhere,
    /// whose lifetime this must not change. The wrapper goes around the caller's instance, so each
    /// registration gates independently.</para>
    /// </summary>
    public static IServiceCollection AddSingletonWork<T>(
        this IServiceCollection services, Func<IServiceProvider, T> factory)
        where T : class, IHostedService
    {
        services.AddHostedService(sp => new SingletonWorkHost<T>(
            factory(sp),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<SingletonWorkHost<T>>>()));

        return services;
    }

    /// <summary>
    /// Work that must happen on EVERY instance, because it is about this process: presence
    /// heartbeats, local cache warmers, anything whose subject is the instance itself.
    /// </summary>
    public static IServiceCollection AddPerInstanceWork<T>(this IServiceCollection services)
        where T : class, IHostedService
        => services.AddHostedService<T>();

    /// <summary>
    /// As above, for a service the caller constructs — the five <c>SegmentSealService</c>
    /// registrations are five instances of one type, one per signal.
    /// </summary>
    public static IServiceCollection AddPerInstanceWork<T>(
        this IServiceCollection services, Func<IServiceProvider, T> factory)
        where T : class, IHostedService
    {
        services.AddHostedService(factory);
        return services;
    }
}

/// <summary>
/// Starts <typeparamref name="T"/> only where this instance runs singleton work.
///
/// <para>The gate is at <b>start</b> rather than at registration because the module composition
/// methods take only an <see cref="IServiceCollection"/> — threading
/// <see cref="IConfiguration"/> through all twelve would be a wide signature change for a
/// decision that is identical at every site. The cost is that the service is constructed and
/// then not started, which for a <c>BackgroundService</c> means its <c>ExecuteAsync</c> never
/// runs.</para>
///
/// <para>⚠️ <c>StopAsync</c> is forwarded only if <c>StartAsync</c> was, because a
/// <c>BackgroundService</c> that was never started has no task to stop and the base class would
/// be asked to wait on a null one.</para>
/// </summary>
public sealed class SingletonWorkHost<T>(
    T inner,
    IConfiguration config,
    ILogger<SingletonWorkHost<T>> logger) : IHostedService
    where T : IHostedService
{
    private bool _started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!HostedWork.RunsSingletonWork(config))
        {
            // Logged at Information once per service: an operator who set the flag should be able
            // to confirm from the log that it took, and an operator who did NOT set it should be
            // able to see that this instance is the one sweeping.
            logger.LogInformation(
                "{Service} is not started on this instance ({Key}=false).",
                typeof(T).Name, HostedWork.RunSingletonWorkKey);

            return Task.CompletedTask;
        }

        _started = true;
        return inner.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => _started ? inner.StopAsync(cancellationToken) : Task.CompletedTask;
}
