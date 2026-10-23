using EntKube.Telemetry;
using EntKube.TelemetryNode;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The querier's merge of one trace's summary rows across the sealed and hot tiers — the trace-side twin
/// of <see cref="FederatedLogBackendTests"/>, and for the same reason: the two halves are independently
/// correct, and it is combining them that can produce a number that looks plausible and is wrong.
///
/// <para>A trace that straddles a seal roll returns one row per half, each with its own start and its own
/// extent. Its duration is the extent of the UNION — which is what <c>SegmentTraceService.Summarize</c>
/// and <c>SegmentTraceService.MergePartials</c> compute everywhere else in this chain. The merge used to
/// take <c>Math.Max</c> of the two halves' durations, which is the union's duration only if both halves
/// start at the same instant — the one thing a tier split guarantees cannot happen.</para>
/// </summary>
public class FederatedTraceMergeTests
{
    private static readonly Guid Cluster = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    private static FederatedTraceService Federated(ITraceQueryService sealedTier, ITraceQueryService hotTier) =>
        new(sealedTier, hotTier, allTiers: hotTier, NullLogger<FederatedTraceService>.Instance);

    private static TraceSummary Row(
        DateTime start, double durationMs, int spans = 1, int errors = 0,
        string rootService = "checkout", string rootName = "POST /checkout") =>
        new("t-1", start, durationMs, spans, errors, rootService, rootName);

    /// <summary>
    /// ⭐ The case the whole merge exists for: early spans sealed, later spans still hot. The trace really
    /// lasted from T to T+4.06s; the badge used to read the hot half's own 60 ms.
    /// </summary>
    [Fact]
    public async Task A_trace_straddling_the_tiers_lasts_from_its_earliest_start_to_its_latest_end()
    {
        FederatedTraceService sut = Federated(
            new FakeTraces([Row(T0, 120)]),
            new FakeTraces([Row(T0.AddSeconds(4), 60)]));

        KubernetesOperationResult<List<TraceSummary>> result =
            await sut.ListTracesAsync(Cluster, null, T0.AddHours(-1), T0.AddHours(1));

        result.IsSuccess.Should().BeTrue();
        TraceSummary merged = result.Data.Should().ContainSingle().Subject;
        merged.Start.Should().Be(T0, "the trace starts at the earliest span either half saw");
        merged.DurationMs.Should().Be(4060,
            "the hot half ran from T+4.0s for 60ms, so the trace's wall-clock extent is 4060ms — not the "
            + "120ms the longer half happened to measure");
        merged.SpanCount.Should().Be(2);
    }

    /// <summary>
    /// The order the halves arrive in must not change the answer. (The sealed half is listed first in the
    /// merge, but which half holds the LATER spans is a property of the data, not of the loop.)
    /// </summary>
    [Fact]
    public async Task The_extent_does_not_depend_on_which_tier_holds_the_later_spans()
    {
        FederatedTraceService sut = Federated(
            new FakeTraces([Row(T0.AddSeconds(4), 60)]),
            new FakeTraces([Row(T0, 120)]));

        KubernetesOperationResult<List<TraceSummary>> result =
            await sut.ListTracesAsync(Cluster, null, T0.AddHours(-1), T0.AddHours(1));

        result.Data.Should().ContainSingle().Which.DurationMs.Should().Be(4060);
    }

    /// <summary>
    /// A half wholly contained in the other's window does not stretch the trace. Without this, "sum the
    /// two durations" would also pass the test above.
    /// </summary>
    [Fact]
    public async Task A_half_contained_inside_the_other_does_not_extend_the_trace()
    {
        FederatedTraceService sut = Federated(
            new FakeTraces([Row(T0, 5000)]),
            new FakeTraces([Row(T0.AddSeconds(1), 60)]));

        KubernetesOperationResult<List<TraceSummary>> result =
            await sut.ListTracesAsync(Cluster, null, T0.AddHours(-1), T0.AddHours(1));

        result.Data.Should().ContainSingle().Which.DurationMs.Should().Be(5000);
    }

    /// <summary>A trace only one tier holds passes through untouched — the merge must not invent an extent.</summary>
    [Fact]
    public async Task A_trace_held_by_one_tier_only_keeps_its_own_duration()
    {
        FederatedTraceService sut = Federated(
            new FakeTraces([Row(T0, 120)]),
            new FakeTraces([Row(T0.AddMinutes(1), 90) with { TraceId = "t-2" }]));

        KubernetesOperationResult<List<TraceSummary>> result =
            await sut.ListTracesAsync(Cluster, null, T0.AddHours(-1), T0.AddHours(1));

        result.Data.Should().HaveCount(2);
        result.Data!.Single(t => t.TraceId == "t-1").DurationMs.Should().Be(120);
        result.Data!.Single(t => t.TraceId == "t-2").DurationMs.Should().Be(90);
    }

    /// <summary>
    /// The half that saw the root span still names the trace, and the counts still add — the properties
    /// the duration fix sits next to, kept here so a later edit to this merge cannot drop them silently.
    /// </summary>
    [Fact]
    public async Task Counts_add_and_the_root_comes_from_the_half_that_saw_it()
    {
        FederatedTraceService sut = Federated(
            new FakeTraces([Row(T0, 120, spans: 3, errors: 1, rootService: "", rootName: "")]),
            new FakeTraces([Row(T0.AddSeconds(4), 60, spans: 2, errors: 2)]));

        KubernetesOperationResult<List<TraceSummary>> result =
            await sut.ListTracesAsync(Cluster, null, T0.AddHours(-1), T0.AddHours(1));

        TraceSummary merged = result.Data.Should().ContainSingle().Subject;
        merged.SpanCount.Should().Be(5);
        merged.ErrorCount.Should().Be(3);
        merged.RootService.Should().Be("checkout");
        merged.RootName.Should().Be("POST /checkout");
    }

    private sealed class FakeTraces(List<TraceSummary> rows) : ITraceQueryService
    {
        public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
            Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
            string? podPattern = null, int windowMinutes = 60)
            => Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));

        public Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
            Guid clusterId, string? service, DateTime from, DateTime to,
            double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
            IReadOnlyList<string>? namespaces = null, string? podPattern = null)
            => Task.FromResult(KubernetesOperationResult<List<TraceSummary>>.Success(rows));

        public Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
            Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
            => Task.FromResult(KubernetesOperationResult<List<SpanRecord>>.Success([]));

        public Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
            Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48,
            CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null)
            => Task.FromResult(KubernetesOperationResult<List<RedBucket>>.Success([]));

        public Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
            Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
            IReadOnlyList<string>? namespaces = null, string? podPattern = null)
            => Task.FromResult(KubernetesOperationResult<List<ServiceEdge>>.Success([]));

        public Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
            Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<ServiceStats>.Success(new ServiceStats(0, 0, 0)));
    }
}
