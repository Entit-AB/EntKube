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

    private static FederatedLogBackend Federated(ILogBackend sealedTier, ILogBackend hotTier) =>
        new(sealedTier, hotTier, NullLogger<FederatedLogBackend>.Instance);

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
    private sealed class FakeLogBackend : ILogBackend
    {
        public List<LokiLogStream> Streams { get; init; } = [];
        public List<LogHistogramBucket> Buckets { get; init; } = [];
        public List<string> Labels { get; init; } = [];
        public long Count { get; init; }
        public string? Error { get; init; }

        public bool IsEnabled => true;

        private Task<KubernetesOperationResult<T>> Result<T>(T value) =>
            Task.FromResult(Error is null
                ? KubernetesOperationResult<T>.Success(value)
                : KubernetesOperationResult<T>.Failure(Error));

        public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
            => Task.FromResult(Error is null && Streams.Count > 0);

        public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
            Guid clusterId, int windowMinutes = 60, CancellationToken ct = default) => Result(Labels);

        public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
            Guid clusterId, string ns, int windowMinutes = 60, CancellationToken ct = default) => Result(Labels);

        public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
            Guid clusterId, string ns, int windowMinutes = 60, CancellationToken ct = default) => Result(Labels);

        public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
            Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200,
            CancellationToken ct = default) => Result(Streams);

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
