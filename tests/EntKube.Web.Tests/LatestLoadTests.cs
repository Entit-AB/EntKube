using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The guard AppWorkloadMetricsPanel's loads run through. Its range selector is never disabled,
/// so changing it twice quickly ran two loads at once, and whichever finished last wrote the
/// charts: the selector could read "Last 30 min" over 24 hours of data, the first to finish hid
/// the spinner while the other was still loading, and the abandoned load's failure stayed on
/// screen. The fetches here are lambdas finished by hand, in the order that used to go wrong.
///
/// <para>The panel's own wiring — that LoadAsync goes through <see cref="LatestLoad.RunAsync{T}"/>
/// and that the component disposes it — needs a live <see cref="PrometheusService"/> (concrete,
/// not virtual) and is not tested.</para>
/// </summary>
public sealed class LatestLoadTests
{
    [Fact]
    public async Task A_superseded_load_that_finishes_last_does_not_overwrite_the_latest()
    {
        using LatestLoad loads = new();
        TaskCompletionSource<string> slow24h = new(), quick30m = new();
        List<string> applied = [];
        int settled = 0;

        Task first = loads.RunAsync(_ => slow24h.Task, applied.Add, () => settled++);
        Task second = loads.RunAsync(_ => quick30m.Task, applied.Add, () => settled++);

        quick30m.SetResult("30 min");
        await second;
        slow24h.SetResult("24 h");
        await first;

        applied.Should().Equal(["30 min"], "the selector says 30 minutes, so the charts must too");
        settled.Should().Be(1, "only the latest load ends the loading state");
    }

    [Fact]
    public async Task The_earlier_load_finishing_first_neither_applies_nor_hides_the_spinner()
    {
        using LatestLoad loads = new();
        TaskCompletionSource<string> earlier = new(), later = new();
        List<string> applied = [];
        bool loading = true;

        Task first = loads.RunAsync(_ => earlier.Task, applied.Add, () => loading = false);
        Task second = loads.RunAsync(_ => later.Task, applied.Add, () => loading = false);

        earlier.SetResult("earlier");
        await first;
        applied.Should().BeEmpty();
        loading.Should().BeTrue("the load the selector asked for is still running");

        later.SetResult("later");
        await second;
        applied.Should().Equal("later");
        loading.Should().BeFalse();
    }

    [Fact]
    public async Task Starting_a_load_cancels_the_one_before_it()
    {
        using LatestLoad loads = new();
        CancellationToken firstToken = default;
        TaskCompletionSource<int> never = new();

        Task first = loads.RunAsync(ct => { firstToken = ct; return never.Task.WaitAsync(ct); }, _ => { }, () => { });
        Task second = loads.RunAsync(_ => Task.FromResult(2), _ => { }, () => { });

        firstToken.IsCancellationRequested.Should().BeTrue();
        await first; // ends quietly: its own cancellation is not an error
        await second;
    }

    [Fact]
    public async Task Disposing_cancels_the_load_in_flight_and_applies_nothing_it_returns()
    {
        LatestLoad loads = new();
        CancellationToken token = default;
        TaskCompletionSource<string> pending = new();
        List<string> applied = [];
        int settled = 0;

        Task load = loads.RunAsync(ct => { token = ct; return pending.Task; }, applied.Add, () => settled++);
        loads.Dispose();
        pending.SetResult("after the component went away");
        await load;

        token.IsCancellationRequested.Should().BeTrue();
        applied.Should().BeEmpty();
        settled.Should().Be(0);
    }

    [Fact]
    public async Task A_failure_of_the_latest_load_still_ends_the_loading_state_and_surfaces()
    {
        using LatestLoad loads = new();
        bool loading = true;

        Func<Task> run = () => loads.RunAsync<int>(
            _ => throw new InvalidOperationException("boom"), _ => { }, () => loading = false);

        await run.Should().ThrowAsync<InvalidOperationException>();
        loading.Should().BeFalse("the old finally cleared the spinner on a throw, and so must this");
    }
}
