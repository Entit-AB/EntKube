using EntKube.Telemetry;
using EntKube.TelemetryNode;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The querier's merge of its own sealed-segment results with the indexer's hot tier.
///
/// This is where correctness actually lives in the split read path. The two halves are independently
/// correct; it is combining them that can double-count a bucket, split one pod's lines into two streams, or
/// quietly return more rows than the caller asked for. None of those would raise an error — they would just
/// make the log viewer subtly wrong.
/// </summary>
public class FederatedLogBackendTests
{
    private static readonly Guid Cluster = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static FederatedLogBackend Federated(
        ILogBackend sealedTier, ILogBackend hotTier,
        TimeSpan? halfBudget = null, TimeSpan? sealedBudget = null) =>
        new(sealedTier, hotTier, NullLogger<FederatedLogBackend>.Instance, halfBudget, sealedBudget);

    private static LokiLogStream Stream(string pod, params (DateTime Ts, string Line)[] entries) => new()
    {
        Labels = new Dictionary<string, string> { ["namespace"] = "prod", ["pod"] = pod },
        Entries = [.. entries.Select(e => new LokiLogEntry { Timestamp = e.Ts, Line = e.Line })],
    };

    private static LogQueryFilter Filter => new() { Namespaces = ["prod"] };

    [Fact]
    public async Task Lines_from_one_pod_land_in_a_single_stream_regardless_of_tier()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "sealed line"))] },
            new FakeLogBackend { Streams = [Stream("api-1", (T0.AddMinutes(1), "hot line"))] });

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));

        result.IsSuccess.Should().BeTrue();
        // The viewer groups by label set; two streams with identical labels would render the same pod twice.
        result.Data.Should().ContainSingle();
        result.Data![0].Entries.Select(e => e.Line).Should().Equal("hot line", "sealed line");
    }

    [Fact]
    public async Task Different_pods_stay_in_different_streams()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "a"))] },
            new FakeLogBackend { Streams = [Stream("api-2", (T0, "b"))] });

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));

        result.Data.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_limit_is_re_applied_to_the_merged_result()
    {
        // Each half independently honoured limit=3, so their union is 6 — more than the caller asked for.
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1",
                (T0, "s1"), (T0.AddMinutes(1), "s2"), (T0.AddMinutes(2), "s3"))] },
            new FakeLogBackend { Streams = [Stream("api-1",
                (T0.AddMinutes(10), "h1"), (T0.AddMinutes(11), "h2"), (T0.AddMinutes(12), "h3"))] });

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1), limit: 3);

        result.Data!.Sum(s => s.Entries.Count).Should().Be(3);
        result.Data.SelectMany(s => s.Entries).Select(e => e.Line).Should().Equal("h3", "h2", "h1");
    }

    [Fact]
    public async Task Entries_sharing_a_timestamp_do_not_break_the_limit()
    {
        // Log lines routinely share a millisecond. Trimming by timestamp VALUE would keep every tie and
        // overshoot the limit, or drop a whole burst; the trim has to rank the entries themselves.
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "s1"), (T0, "s2"), (T0, "s3"))] },
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "h1"), (T0, "h2"))] });

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1), limit: 2);

        result.Data!.Sum(s => s.Entries.Count).Should().Be(2);
    }

    [Fact]
    public async Task Histogram_buckets_are_summed_not_concatenated()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Buckets = [new LogHistogramBucket(T0, 5, 2), new LogHistogramBucket(T0.AddHours(1), 1, 0)] },
            new FakeLogBackend { Buckets = [new LogHistogramBucket(T0, 3, 1)] });

        KubernetesOperationResult<List<LogHistogramBucket>> result =
            await sut.GetHistogramAsync(Cluster, Filter, T0, T0.AddHours(2));

        result.Data.Should().HaveCount(2, "the two tiers bucket the same window, so their buckets line up");
        result.Data![0].Should().BeEquivalentTo(new LogHistogramBucket(T0, 8, 3));
        result.Data[1].Should().BeEquivalentTo(new LogHistogramBucket(T0.AddHours(1), 1, 0));
    }

    [Fact]
    public async Task Label_lists_are_unioned_and_deduplicated()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Labels = ["prod", "staging"] },
            new FakeLogBackend { Labels = ["prod", "kube-system"] });

        KubernetesOperationResult<List<string>> result = await sut.GetNamespacesAsync(Cluster);

        result.Data.Should().Equal("kube-system", "prod", "staging");
    }

    [Fact]
    public async Task A_failing_hot_tier_still_returns_the_sealed_history()
    {
        // An indexer restart must cost you the last few unsealed minutes, not the whole search — otherwise
        // a rolling restart reads as a total log outage.
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "sealed line"))] },
            new FakeLogBackend { Error = "connection refused" });

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));

        result.IsSuccess.Should().BeTrue();
        result.Data!.SelectMany(s => s.Entries).Select(e => e.Line).Should().Equal("sealed line");
    }

    [Fact]
    public async Task A_failing_sealed_tier_still_returns_what_is_happening_now()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Error = "object storage unreachable" },
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "hot line"))] });

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));

        result.IsSuccess.Should().BeTrue();
        result.Data!.SelectMany(s => s.Entries).Select(e => e.Line).Should().Equal("hot line");
    }

    [Fact]
    public async Task Only_both_tiers_failing_is_a_failed_query_and_it_names_both_causes()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Error = "object storage unreachable" },
            new FakeLogBackend { Error = "connection refused" });

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));

        result.IsSuccess.Should().BeFalse();
        // Both causes, because "logs are empty" is otherwise indistinguishable from "there are no logs".
        result.Error.Should().Contain("object storage unreachable").And.Contain("connection refused");
    }

    [Fact]
    public async Task Counts_are_added()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Count = 7 }, new FakeLogBackend { Count = 5 });

        KubernetesOperationResult<long> result =
            await sut.CountAsync(Cluster, "prod", null, LogLevel.None, T0, T0.AddHours(1));

        result.Data.Should().Be(12);
    }

    /// <summary>A stand-in tier that returns whatever it was configured with, or fails.</summary>
    [Fact]
    public async Task A_half_that_stops_answering_costs_the_budget_not_its_own_timeout()
    {
        // The hot tier's HTTP client allows 30s. Awaiting both halves spent all of it on EVERY query
        // before handing back a sealed answer that was already in hand — the difference between a
        // degraded search and a page that looks hung.
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "sealed line"))] },
            new FakeLogBackend { Delay = TimeSpan.FromSeconds(30) },
            halfBudget: TimeSpan.FromMilliseconds(150));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        result.IsSuccess.Should().BeTrue("the sealed half answered, and that is most of the data");
        result.Data!.SelectMany(s => s.Entries).Select(e => e.Line).Should().Equal("sealed line");
    }

    [Fact]
    public async Task The_routing_probe_is_budgeted_too()
    {
        // HasDataAsync runs before the viewer renders anything, so an unanswerable half here delays the
        // page before it has started loading.
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "sealed line"))] },
            new FakeLogBackend { Delay = TimeSpan.FromSeconds(30) },
            halfBudget: TimeSpan.FromMilliseconds(150));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool hasData = await sut.HasDataAsync(Cluster);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        hasData.Should().BeTrue();
    }

    /// <summary>
    /// The inverse of the cases above, and the one that reached a user: the SEALED half is the slow one.
    ///
    /// <para>Every other budget test models a dead hot tier, because that is what the budget was written
    /// for — one in-cluster hop that should answer in under a second. The sealed tier is not that. It reads
    /// immutable archives from object storage, and on a cold querier the first query of a window downloads
    /// and opens them, which legitimately takes longer than a hop. Abandoning it on the same short budget
    /// hands back the hot half alone; when the data being searched is already sealed, that half is empty,
    /// and an empty success is indistinguishable in the viewer from "this namespace has no logs".</para>
    ///
    /// <para>The reported symptom was exactly that shape: search a namespace, get nothing, search the same
    /// namespace again and the logs appear — because the abandoned first query finished in the background
    /// and left the segment readers open for the second.</para>
    /// </summary>
    [Fact]
    public async Task A_slow_sealed_half_does_not_come_back_as_an_empty_answer()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "sealed line"))], Delay = TimeSpan.FromMilliseconds(400) },
            new FakeLogBackend(),   // hot tier: healthy, fast, and holds nothing — everything is sealed
            halfBudget: TimeSpan.FromMilliseconds(100),
            sealedBudget: TimeSpan.FromMilliseconds(100));

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));

        result.IsSuccess.Should().BeFalse(
            "returning the empty hot half as a complete answer tells the viewer there are no logs, when in "
            + "fact the tier holding them was abandoned — the one outcome worse than being slow");
        result.Error.Should().Contain("sealed");
    }

    /// <summary>
    /// ⚠️ <b>THE SHIPPED VALUES, which no test could see.</b> Every budget test in this file supplies
    /// both budgets itself, so all of them passed with the two defaults set <em>equal</em> — which is
    /// the pre-fix state and the exact incident the member docs describe. And the defaults are not
    /// fallbacks: <c>Program.cs</c> constructs this with (sealedTier, hotTier, logger) and nothing
    /// else, so the <c>??</c> values <b>are</b> the production configuration.
    ///
    /// <para>The invariant worth pinning is the relation, not the number 30. A behavioural test
    /// cannot tell 30s from 5s without a sealed half that stalls for more than five seconds, which
    /// is five seconds of suite time to catch one constant — so the relation is asserted directly
    /// and the behaviour tests below keep doing what they are good at, which is proving the budgets
    /// reach the right halves.</para>
    /// </summary>
    [Fact]
    public void The_shipped_budgets_give_the_sealed_tier_longer_than_the_hot_one()
    {
        FederatedLogBackend.DefaultSealedBudget.Should().BeGreaterThan(
            FederatedLogBackend.DefaultHotBudget,
            "the sealed half reads immutable archives out of object storage and the hot half is one "
            + "in-cluster hop; holding the sealed half to the hot half's budget abandoned it and "
            + "returned the empty hot half as a SUCCESSFUL answer, which reads as 'no logs here'");

        FederatedLogBackend.DefaultSealedBudget.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(20),
            "a cold first query downloads and opens segments; a sealed budget shaved back towards "
            + "the hot one re-opens the same hole by degrees rather than at once");
    }

    /// <summary>
    /// The sealed tier gets a budget of its own, long enough to cover a cold read from object storage —
    /// otherwise the fix above just turns a silent empty into a reliable error.
    /// </summary>
    [Fact]
    public async Task The_sealed_tier_is_given_longer_than_the_hot_one()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "sealed line"))], Delay = TimeSpan.FromMilliseconds(300) },
            new FakeLogBackend(),
            halfBudget: TimeSpan.FromMilliseconds(100),
            sealedBudget: TimeSpan.FromSeconds(5));

        KubernetesOperationResult<List<LokiLogStream>> result =
            await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1));

        result.IsSuccess.Should().BeTrue("the sealed tier was well inside its own budget");
        result.Data!.SelectMany(s => s.Entries).Select(e => e.Line).Should().Equal("sealed line");
    }

    /// <summary>
    /// Both budgets run against the same clock. They used to be awaited one after the other, so the hot
    /// tier's window only opened once the sealed tier had finished or given up — two slow halves cost the
    /// sum of the budgets rather than the larger of them.
    ///
    /// <para><b>Why this one measures a clock, and why the margin is wide.</b> The property is
    /// about when the two <em>budget timers</em> start, and nothing observable happens when a timer
    /// starts — so there is no state to assert on. An attempt to make it deterministic by having
    /// each half announce its arrival measured nothing at all: <c>MergeAsync</c> is handed tasks
    /// that are already running, so "both halves were entered" is true whether the budgets overlap
    /// or queue. A sequential-budget mutant passed it.</para>
    ///
    /// <para>So the clock stays. ⚠️ But a FIXED ceiling had already been widened once and was
    /// still a guess: 300ms budgets against a 550ms ceiling — 250ms of headroom — were crossed
    /// twice by scheduling jitter, and the replacement 1s/1.5s pair is 500ms of headroom, only
    /// double a margin already known to be crossable on this machine. The jitter here is
    /// ADDITIVE, not proportional — one late timer plus a few threadpool hops — so quoting the
    /// ratio (1.5x) flatters it, and the contention is in-process: xUnit runs classes in
    /// parallel and <c>SegmentLogEngineTests</c> indexes 50,000 rows into Lucene in this same
    /// assembly, starving the pool that every continuation here needs.</para>
    ///
    /// <para>So the ceiling is now RELATIVE, following the pattern
    /// <c>SegmentLogEngineTests</c> already uses: the same pair of waits is timed on this host,
    /// in this run, immediately before the exercise, and the ceiling is derived from that. Jitter
    /// then inflates the yardstick and the measurement together instead of eating a fixed
    /// allowance. A bound that measures the host rather than the code teaches a team to re-run
    /// rather than to read.</para>
    ///
    /// <para><b>The budgets are also different from each other now — sealed 1s, hot 2s — for the
    /// margin, not for extra detection.</b> Overlapping costs the LARGER (~2s) and queueing costs
    /// the SUM (~3s), so the gap the ceiling has to sit inside is ~1000ms rather than the ~500ms
    /// an equal 1s/1s pair left. Same 1.5 ratio, twice the absolute room, which is what the
    /// relative ceiling needs in order to absorb additive jitter.</para>
    ///
    /// <para>⚠️ <b>What this test does NOT catch, measured rather than assumed.</b> Applying ONE
    /// budget value to both halves passes it: using <c>_hotBudget</c> for the sealed half costs
    /// ~2s and using <c>_sealedBudget</c> for the hot half costs ~1s, and both are under the
    /// ceiling. I had written that distinct budgets closed that hole; mutating the production code
    /// showed they do not. Those two mutants are caught by
    /// <see cref="The_sealed_tier_is_given_longer_than_the_hot_one"/> and
    /// <see cref="A_half_that_stops_answering_costs_the_budget_not_its_own_timeout"/>
    /// respectively, which is where a per-half budget belongs. This test owns exactly one claim —
    /// the two waits overlap — and dropping either budget outright (~30s) also fails it.</para>
    /// </summary>
    [Fact]
    public async Task The_two_budgets_run_concurrently_not_one_after_the_other()
    {
        using CancellationTokenSource cts = new();

        TimeSpan sealedBudget = TimeSpan.FromSeconds(1);
        TimeSpan hotBudget = TimeSpan.FromSeconds(2);

        // The yardstick: what these two waits cost on THIS host, in THIS run, when they genuinely
        // overlap. Measured before the exercise so the two readings face the same load.
        var yardstickWatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Task.Delay(sealedBudget), Task.Delay(hotBudget));
        yardstickWatch.Stop();

        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Streams = [Stream("api-1", (T0, "sealed"))], Delay = TimeSpan.FromSeconds(30) },
            new FakeLogBackend { Streams = [Stream("api-2", (T0, "hot"))], Delay = TimeSpan.FromSeconds(30) },
            halfBudget: hotBudget,
            sealedBudget: sealedBudget);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.QueryAsync(Cluster, Filter, T0.AddHours(-1), T0.AddHours(1), ct: cts.Token);
        sw.Stop();

        // Overlapping costs the larger budget, which is what the yardstick just measured.
        // Queueing costs the sum — 1.5x the yardstick — so the ceiling goes between them. A
        // quarter of the yardstick is the margin, and it grows with the yardstick under load.
        long ceilingMs = yardstickWatch.ElapsedMilliseconds * 5 / 4;

        sw.ElapsedMilliseconds.Should().BeLessThan(
            ceilingMs,
            "both halves are already in flight, so the budgets must overlap and cost the larger "
            + "of them, not queue and cost the sum (the same two waits, overlapped, took {0}ms on "
            + "this host just now; queueing them would cost about {1}ms)",
            yardstickWatch.ElapsedMilliseconds,
            yardstickWatch.ElapsedMilliseconds * 3 / 2);

        // Neither half ever answers; stop their delays rather than leaving them running.
        await cts.CancelAsync();
    }

    [Fact]
    public async Task A_healthy_pair_is_not_delayed_by_the_budget()
    {
        FederatedLogBackend sut = Federated(
            new FakeLogBackend { Labels = ["api-1"] },
            new FakeLogBackend { Labels = ["api-2"] },
            halfBudget: TimeSpan.FromSeconds(5));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        KubernetesOperationResult<List<string>> pods = await sut.GetPodsAsync(Cluster, "prod");
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        pods.Data.Should().BeEquivalentTo(["api-1", "api-2"]);
    }

    private sealed class FakeLogBackend : ILogBackend
    {
        public List<LokiLogStream> Streams { get; init; } = [];
        public List<LogHistogramBucket> Buckets { get; init; } = [];
        public List<string> Labels { get; init; } = [];
        public long Count { get; init; }
        public string? Error { get; init; }

        /// <summary>How long every call takes to answer — a hot tier that has stopped answering at all.</summary>
        public TimeSpan Delay { get; init; }

        public bool IsEnabled => true;

        private async Task<KubernetesOperationResult<T>> Result<T>(T value, CancellationToken ct = default)
        {
            // Honours the token so a cancelled query does not leave a long delay running behind it.
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return Error is null
                ? KubernetesOperationResult<T>.Success(value)
                : KubernetesOperationResult<T>.Failure(Error);
        }

        public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return Error is null && Streams.Count > 0;
        }

        public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
            Guid clusterId, int windowMinutes = 60, CancellationToken ct = default) => Result(Labels);

        public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
            Guid clusterId, string ns, int windowMinutes = 60, CancellationToken ct = default) => Result(Labels);

        public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
            Guid clusterId, string ns, int windowMinutes = 60, CancellationToken ct = default) => Result(Labels);

        public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
            Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200,
            CancellationToken ct = default) => Result(Streams, ct);

        public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
            Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default) => Result(Streams);

        public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
            Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48,
            CancellationToken ct = default) => Result(Buckets);

        public Task<KubernetesOperationResult<long>> CountAsync(
            Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
            CancellationToken ct = default) => Result(Count);
    }
}
