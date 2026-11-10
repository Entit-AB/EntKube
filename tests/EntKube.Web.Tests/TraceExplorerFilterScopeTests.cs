using Bunit;
using EntKube.Telemetry;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// What the trace explorer puts on screen when the answer it got is not the answer it asked for.
///
/// <para>All three cases here are the same class of fault: the page keeps showing something it is no
/// longer entitled to show. A failed search keeps the previous search's rows; a cluster switch keeps the
/// previous cluster's service filter (invisibly, because the dropdown cannot render it); and a
/// namespace-confined mount keeps every namespace's log lines, because the correlated-log read is the one
/// log read in the codebase that takes no namespace.</para>
///
/// <para>Rendered rather than asserted statically, because every one of them is about what the operator
/// sees — the trace list, the warning banner, and the log pane are the evidence.</para>
/// </summary>
public class TraceExplorerFilterScopeTests : BunitContext
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid ClusterA = Guid.NewGuid();
    private static readonly Guid ClusterB = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeTraceBackend traces = new();
    private readonly FakeLogs logs = new();

    public TraceExplorerFilterScopeTests()
        => Services.AddSingleton<ITelemetryBackends>(new FakeBackends(traces, logs));

    private static TraceSummary Summary(string id, string rootName, double durationMs = 120) =>
        new(id, T0, durationMs, SpanCount: 3, ErrorCount: 0, RootService: "checkout", RootName: rootName);

    private IRenderedComponent<TraceExplorer> Mount(
        Guid clusterId, IReadOnlyList<string>? namespaces = null, string? presetTraceId = null) =>
        Render<TraceExplorer>(p =>
        {
            p.Add(c => c.TenantId, Tenant);
            p.Add(c => c.ClusterId, clusterId);
            if (namespaces is not null) p.Add(c => c.Namespaces, namespaces);
            if (presetTraceId is not null) p.Add(c => c.PresetTraceId, presetTraceId);
        });

    private static void Search(IRenderedComponent<TraceExplorer> cut) => cut.Find("button.btn-primary").Click();

    // ── A failed search must not leave the previous search's rows on screen ───────────────────────

    /// <summary>
    /// The positive half, first — without it the negative assertion below would pass on a page that
    /// never rendered a trace at all.
    /// </summary>
    [Fact]
    public void A_successful_search_lists_its_traces()
    {
        traces.Traces[ClusterA] = [Summary("aaaa", "POST /checkout")];
        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA);

        Search(cut);

        cut.Markup.Should().Contain("POST /checkout");
    }

    /// <summary>
    /// The reported fault: the second search fails, and the first search's rows stay — now read as the
    /// result of the filters currently in the boxes. A degraded tier is a routine outcome here (see
    /// FederatedTraceService's budget policy), so this is not an exotic path.
    /// </summary>
    [Fact]
    public void A_failed_search_does_not_leave_the_previous_result_on_screen()
    {
        traces.Traces[ClusterA] = [Summary("aaaa", "POST /checkout")];
        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA);
        Search(cut);
        cut.Markup.Should().Contain("POST /checkout", "the first search succeeded");

        traces.ListError = "the hot telemetry tier did not answer within 5s";
        Search(cut);

        cut.Markup.Should().Contain("did not answer within 5s", "the failure has to be visible");
        cut.Markup.Should().NotContain("POST /checkout",
            "those rows answered a different query and nothing on screen says so");
        cut.Markup.Should().Contain("No traces match the selected criteria.");
    }

    // ── An empty service list must say whether the window was quiet or the read failed ────────────

    [Fact]
    public void A_failed_service_list_says_so_and_a_quiet_one_does_not()
    {
        traces.ServicesError = "The cluster's telemetry node did not answer within 40 seconds.";
        IRenderedComponent<TraceExplorer> failed = Mount(ClusterA);

        failed.Find("[data-test=services-error]").TextContent.Should().Contain("did not answer within 40 seconds",
            "an empty dropdown was all the page showed, which reads as 'nothing traced this hour'");

        traces.ServicesError = null;
        IRenderedComponent<TraceExplorer> quiet = Mount(ClusterB);

        quiet.FindAll("[data-test=services-error]").Should().BeEmpty("a quiet hour is not a failure");
    }

    // ── The dropdown is bounded: an unbounded one froze the browser tab with no error ─────────────

    [Fact]
    public void A_huge_service_list_renders_at_most_the_cap_and_says_how_many_there_are()
    {
        traces.Services[ClusterA] = [.. Enumerable.Range(0, 5000).Select(i => $"svc-{i:D5}")];

        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA);

        cut.FindAll("select")[0].QuerySelectorAll("option").Length
            .Should().Be(TraceExplorer.MaxServiceOptions + 1, "the cap plus the 'All services' option");
        cut.Find("[data-test=services-capped]").TextContent
            .Should().Contain($"first {TraceExplorer.MaxServiceOptions} of 5000");
    }

    [Fact]
    public void A_list_under_the_cap_renders_whole_and_says_nothing()
    {
        traces.Services[ClusterA] = ["billing", "checkout"];

        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA);

        cut.FindAll("select")[0].QuerySelectorAll("option").Length.Should().Be(3);
        cut.FindAll("[data-test=services-capped]").Should().BeEmpty();
    }

    // ── A cluster switch must not keep a service filter the new cluster's dropdown cannot show ────

    /// <summary>
    /// The tenant host mounts this component with no <c>@key</c>, so a cluster switch re-parameterises
    /// the same instance. Before the fix "checkout" stayed in the field while the dropdown — rebuilt
    /// from cluster B's services — had no option to render it with, so the filter was both invisible
    /// and still applied.
    /// </summary>
    [Fact]
    public void Switching_cluster_drops_a_service_filter_the_new_cluster_does_not_offer()
    {
        traces.Services[ClusterA] = ["checkout"];
        traces.Services[ClusterB] = ["billing"];
        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA);

        cut.Find("select").Change("checkout");
        Search(cut);
        traces.LastService.Should().Be("checkout", "the filter applies on the cluster that offers it");

        cut.Render(p => p.Add(c => c.ClusterId, ClusterB));
        Search(cut);

        traces.LastService.Should().BeNull(
            "cluster B has no 'checkout', and a filter its dropdown cannot show must not be applied");
    }

    /// <summary>A service both clusters run is kept — this is a membership test, not a blanket reset.</summary>
    [Fact]
    public void Switching_cluster_keeps_a_service_filter_the_new_cluster_also_offers()
    {
        traces.Services[ClusterA] = ["checkout"];
        traces.Services[ClusterB] = ["checkout", "billing"];
        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA);

        cut.Find("select").Change("checkout");
        cut.Render(p => p.Add(c => c.ClusterId, ClusterB));
        Search(cut);

        traces.LastService.Should().Be("checkout");
    }

    // ── Correlated logs must honour the namespace scope ───────────────────────────────────────────

    /// <summary>
    /// The confinement case. A trace id is shared by every service the request touched, so the
    /// correlated-log read is cluster-wide; in the customer portal the waterfall was scoped to the
    /// customer's namespaces while the pane below it printed other namespaces' lines.
    /// </summary>
    [Fact]
    public void A_namespace_confined_mount_shows_only_its_own_namespaces_log_lines()
    {
        traces.Spans["t-1"] = [Span()];
        logs.Streams =
        [
            Stream("prod-a", "the customer's own line"),
            Stream("prod-b", "another namespace's line"),
        ];

        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA, namespaces: ["prod-a"], presetTraceId: "t-1");

        cut.Markup.Should().Contain("the customer's own line",
            "without this the absence assertion below would be vacuous");
        cut.Markup.Should().NotContain("another namespace's line",
            "this mount is confined to prod-a and the span half of the same fetch already is");
    }

    /// <summary>
    /// The unconfined tenant view is cluster-wide by design (the parameter's own contract), so the
    /// filter must not quietly narrow it — otherwise the test above would pass on a page that drops
    /// every line whose namespace it has not been told about.
    /// </summary>
    [Fact]
    public void An_unscoped_mount_still_shows_every_namespaces_log_lines()
    {
        traces.Spans["t-1"] = [Span()];
        logs.Streams =
        [
            Stream("prod-a", "line from prod-a"),
            Stream("prod-b", "line from prod-b"),
        ];

        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA, presetTraceId: "t-1");

        cut.Markup.Should().Contain("line from prod-a");
        cut.Markup.Should().Contain("line from prod-b");
    }

    /// <summary>A line whose namespace is unknown fails closed: "whose line is this" has no safe default.</summary>
    [Fact]
    public void A_log_stream_with_no_namespace_label_is_not_shown_to_a_confined_mount()
    {
        traces.Spans["t-1"] = [Span()];
        logs.Streams = [Stream("prod-a", "labelled line"), Unlabelled("unlabelled line")];

        IRenderedComponent<TraceExplorer> cut = Mount(ClusterA, namespaces: ["prod-a"], presetTraceId: "t-1");

        cut.Markup.Should().Contain("labelled line");
        cut.Markup.Should().NotContain("unlabelled line");
    }

    private static SpanRecord Span() =>
        new(T0, "span-1", null, "POST /checkout", "checkout", Kind: 2, DurationMs: 40, StatusCode: 0,
            AttributesJson: null);

    private static LokiLogStream Stream(string ns, string line) => new()
    {
        Labels = new Dictionary<string, string> { ["namespace"] = ns, ["pod"] = "api-1" },
        Entries = [new LokiLogEntry { Timestamp = T0, Line = line }],
    };

    private static LokiLogStream Unlabelled(string line) => new()
    {
        Labels = new Dictionary<string, string> { ["pod"] = "api-1" },
        Entries = [new LokiLogEntry { Timestamp = T0, Line = line }],
    };

    private sealed class FakeBackends(ITraceQueryService traces, ILogBackend logs) : ITelemetryBackends
    {
        public ILogBackend LogsFor(Guid tenantId) => logs;
        public ITraceQueryService TracesFor(Guid tenantId) => traces;
    }

    /// <summary>Per-cluster canned answers, plus a record of the service filter the page actually sent.</summary>
    private sealed class FakeTraceBackend : ITraceQueryService
    {
        public Dictionary<Guid, List<string>> Services { get; } = [];
        public Dictionary<Guid, List<TraceSummary>> Traces { get; } = [];
        public Dictionary<string, List<SpanRecord>> Spans { get; } = [];
        public string? ListError { get; set; }
        public string? LastService { get; private set; }
        public bool Asked { get; private set; }

        public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
            Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
            string? podPattern = null, int windowMinutes = 60)
            => Task.FromResult(ServicesError is not null
                ? KubernetesOperationResult<List<string>>.Failure(ServicesError)
                : KubernetesOperationResult<List<string>>.Success(
                    Services.TryGetValue(clusterId, out List<string>? s) ? s : []));

        public string? ServicesError { get; set; }

        public Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
            Guid clusterId, string? service, DateTime from, DateTime to,
            double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
            IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        {
            LastService = service;
            Asked = true;
            return Task.FromResult(ListError is not null
                ? KubernetesOperationResult<List<TraceSummary>>.Failure(ListError)
                : KubernetesOperationResult<List<TraceSummary>>.Success(
                    Traces.TryGetValue(clusterId, out List<TraceSummary>? t) ? t : []));
        }

        public Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
            Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
            => Task.FromResult(KubernetesOperationResult<List<SpanRecord>>.Success(
                Spans.TryGetValue(traceId, out List<SpanRecord>? s) ? s : []));

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

    /// <summary>
    /// A log backend that answers a by-trace read cluster-wide, which is what the real one does: the
    /// interface has no namespace parameter to honour.
    /// </summary>
    private sealed class FakeLogs : ILogBackend
    {
        public List<LokiLogStream> Streams { get; set; } = [];

        public bool IsEnabled => true;
        public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
            Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Success(Streams));

        public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
            Guid clusterId, int windowMinutes = 60, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));

        public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
            Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));

        public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
            Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));

        public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
            Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200,
            CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Success([]));

        public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
            Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48,
            CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<LogHistogramBucket>>.Success([]));

        public Task<KubernetesOperationResult<long>> CountAsync(
            Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
            CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<long>.Success(0));
    }
}
