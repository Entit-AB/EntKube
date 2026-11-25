namespace EntKube.Web.Services;

/// <summary>
/// Lets only the most recent of overlapping loads write a component's state.
///
/// <para>⚠️ Written for AppWorkloadMetricsPanel, whose range selector re-runs the load on every
/// change and was never disabled. Two loads in flight raced: whichever finished <i>last</i> won,
/// so the selector could say "Last 30 min" over charts of the last 24 hours; the first to finish
/// cleared the loading state while the other was still fetching; and a failure of the abandoned
/// load stayed on screen as the new one's warning. Each <see cref="RunAsync{T}"/> cancels the one
/// before it and carries a generation; a load that is no longer the latest writes nothing.</para>
///
/// <para>Each load owns its own <see cref="CancellationTokenSource"/> and disposes it when it
/// finishes — never the next load, which would dispose a source the previous one may still be
/// registering callbacks on. Not thread-safe: a Blazor circuit runs one continuation at a time,
/// and that is the only caller.</para>
/// </summary>
public sealed class LatestLoad : IDisposable
{
    private CancellationTokenSource? current;
    private int generation;
    private bool disposed;

    /// <summary>
    /// Runs one load: <paramref name="fetch"/> under a token cancelled by the next load or by
    /// <see cref="Dispose"/>; then <paramref name="apply"/> with its result and, last,
    /// <paramref name="settled"/> (clear the spinner) — both only if this is still the latest load.
    /// A cancellation of this load's own token ends it quietly; any other exception propagates,
    /// after <paramref name="settled"/>, as it did before there was a guard.
    /// </summary>
    public async Task RunAsync<T>(Func<CancellationToken, Task<T>> fetch, Action<T> apply, Action settled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        current?.Cancel();
        using CancellationTokenSource mine = new();
        current = mine;
        int ticket = ++generation;

        try
        {
            T result = await fetch(mine.Token);
            if (IsLatest(ticket)) apply(result);
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested)
        {
            // Superseded or disposed: the load that replaced it, if any, writes the state.
        }
        finally
        {
            if (IsLatest(ticket)) settled();
            if (ReferenceEquals(current, mine)) current = null;
        }
    }

    private bool IsLatest(int ticket) => !disposed && ticket == generation;

    /// <summary>Cancels the load in flight; nothing it fetches is applied afterwards.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        current?.Cancel();
    }
}
