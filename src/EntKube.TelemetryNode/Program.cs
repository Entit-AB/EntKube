using System.Text.Json;
using EntKube.Telemetry;
using EntKube.TelemetryNode;
using EntKube.Web.Services;

// The in-cluster telemetry node. One binary, two roles — see NodeOptions and
// docs/telemetry-in-cluster.md. Everything below is deliberately small: the engine itself lives in
// EntKube.Telemetry and is shared with the management plane, so this host only supplies the two seams the
// engine needs (a catalog and a blob store), the HTTP surface, and the background seal loop.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Name of the HttpClient pointed at the indexer's Service. Only the querier registers it.
const string IndexerClient = "indexer";

NodeOptions node = builder.Configuration.GetSection("Node").Get<NodeOptions>() ?? new NodeOptions();
node.Validate();
builder.Services.AddSingleton(node);

// Engine tunables. Same keys as the management plane, so the two are configured identically and a value
// learned in one place transfers to the other.
SegmentEngineOptions engine = new()
{
    DataPath = builder.Configuration.GetValue<string>("Telemetry:DataPath") ?? "/data/telemetry",
    RollMaxDocs = builder.Configuration.GetValue<long?>("Telemetry:SegmentMaxDocs") ?? 1_000_000,
    RollMaxAge = TimeSpan.FromMinutes(builder.Configuration.GetValue<int?>("Telemetry:SegmentMaxAgeMinutes") ?? 60),
    RetentionDays = builder.Configuration.GetValue<int?>("Telemetry:RetentionDays") ?? 90,
    ArchiveZstdLevel = builder.Configuration.GetValue<int?>("Telemetry:ArchiveZstdLevel") ?? 19,
    RawSpanRetentionDays = builder.Configuration.GetValue<int?>("Telemetry:RawSpanRetentionDays") ?? 30,
    TraceSampleRatePercent = builder.Configuration.GetValue<int?>("Telemetry:TraceSampleRatePercent") ?? 100,
    TraceKeepMinDurationMs = builder.Configuration.GetValue<double?>("Telemetry:TraceKeepMinDurationMs") ?? 500,
    TieredLogRetention = builder.Configuration.GetValue<bool?>("Telemetry:TieredLogRetention") ?? true,
    VerboseLogRetentionDays = builder.Configuration.GetValue<int?>("Telemetry:VerboseLogRetentionDays") ?? 14,
    WarmRetentionDays = builder.Configuration.GetValue<int?>("Telemetry:WarmRetentionDays") ?? 3,
    WarmMaxBytes = builder.Configuration.GetValue<long?>("Telemetry:WarmMaxBytes") ?? 8L * 1024 * 1024 * 1024,
};
builder.Services.AddSingleton(engine);

// Seam 1 — the catalog.
//
// The indexer owns it: a SQLite file on its own volume, not a remote database, because coupling ingest to
// a database across the WAN is the property this whole design exists to remove. The querier cannot open
// that file from another pod, so it borrows the segment list from the indexer over HTTP and reads the
// archives from object storage itself.
if (node.Role == NodeRole.Indexer)
{
    builder.Services.AddSingleton<ISegmentCatalog>(_ =>
        new SqliteSegmentCatalog(node.CatalogPath ?? Path.Combine(engine.DataPath, "catalog.db")));
}
else
{
    builder.Services.AddHttpClient(IndexerClient, http =>
    {
        http.BaseAddress = new Uri(node.IndexerUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddSingleton<ISegmentCatalog>(sp => new RemoteSegmentCatalog(
        sp.GetRequiredService<IHttpClientFactory>(), IndexerClient, node,
        sp.GetRequiredService<ILogger<RemoteSegmentCatalog>>()));
}

// Seam 2 — object storage for sealed archives, configured from Telemetry:ObjectStorage (in a cluster,
// mounted from a Secret). Falls back to a local directory when no bucket is set, so the node still runs
// standalone for development.
builder.Services.AddSingleton<ISegmentBlobStore>(sp =>
{
    var s3 = new S3SegmentBlobStore(sp.GetRequiredService<IConfiguration>());
    if (s3.IsConfigured) return s3;

    string dir = Path.Combine(engine.DataPath, "blobs");
    Directory.CreateDirectory(dir);
    sp.GetRequiredService<ILogger<Program>>().LogWarning(
        "Telemetry object storage is not configured; sealing to {Dir} on the local volume instead. "
        + "Sealed segments will be lost if the volume is lost.", dir);
    return new LocalSegmentBlobStore(dir);
});

// This pod serves exactly one cluster for one tenant, so the resolver is a constant rather than a lookup.
builder.Services.AddSingleton<IClusterTenantResolver>(
    new FixedClusterTenantResolver(node.ClusterId, node.TenantId));

// One manager per (tenant, signal). In-cluster there is only ever one tenant, but the registry is what
// the engine's background seal loop iterates, so the shape is kept.
builder.Services.AddSingleton(sp => new SegmentManagerRegistry<LogSegmentManager>(tenantId =>
    new LogSegmentManager(tenantId, sp.GetRequiredService<ISegmentCatalog>(),
        sp.GetRequiredService<ISegmentBlobStore>(), engine, sp.GetRequiredService<ILogger<LogSegmentManager>>())));
builder.Services.AddSingleton(sp => new SegmentManagerRegistry<VerboseLogSegmentManager>(tenantId =>
    new VerboseLogSegmentManager(tenantId, sp.GetRequiredService<ISegmentCatalog>(),
        sp.GetRequiredService<ISegmentBlobStore>(), engine, sp.GetRequiredService<ILogger<LogSegmentManager>>())));
builder.Services.AddSingleton(sp => new LogTierRegistries(
    sp.GetRequiredService<SegmentManagerRegistry<LogSegmentManager>>(),
    sp.GetRequiredService<SegmentManagerRegistry<VerboseLogSegmentManager>>(),
    engine));
builder.Services.AddSingleton(sp => new SegmentManagerRegistry<SpanSegmentManager>(tenantId =>
    new SpanSegmentManager(tenantId, sp.GetRequiredService<ISegmentCatalog>(),
        sp.GetRequiredService<ISegmentBlobStore>(), engine, sp.GetRequiredService<ILogger<SpanSegmentManager>>())));
builder.Services.AddSingleton(sp => new SegmentManagerRegistry<RumSegmentManager>(tenantId =>
    new RumSegmentManager(tenantId, sp.GetRequiredService<ISegmentCatalog>(),
        sp.GetRequiredService<ISegmentBlobStore>(), engine, sp.GetRequiredService<ILogger<RumSegmentManager>>())));
builder.Services.AddSingleton(sp => new SegmentManagerRegistry<TraceSummarySegmentManager>(tenantId =>
    new TraceSummarySegmentManager(tenantId, sp.GetRequiredService<ISegmentCatalog>(),
        sp.GetRequiredService<ISegmentBlobStore>(), engine, sp.GetRequiredService<ILogger<TraceSummarySegmentManager>>())));

builder.Services.AddSingleton<ITelemetryIngest, SegmentTelemetryStore>();

if (node.Role == NodeRole.Indexer)
{
    // One process owns the whole index, so a single unscoped backend answers everything.
    builder.Services.AddSingleton<ILogBackend>(sp => new SegmentLogService(
        sp.GetRequiredService<LogTierRegistries>(),
        sp.GetRequiredService<IClusterTenantResolver>(),
        sp.GetRequiredService<ILogger<SegmentLogService>>()));

    // A second, hot-only backend behind /internal/logs, for a querier to merge with its own sealed
    // results. Separate instance rather than a per-request flag so the scope can never be set by a caller.
    builder.Services.AddKeyedSingleton<ILogBackend>("hot", (sp, _) => new SegmentLogService(
        sp.GetRequiredService<LogTierRegistries>(),
        sp.GetRequiredService<IClusterTenantResolver>(),
        sp.GetRequiredService<ILogger<SegmentLogService>>(),
        SegmentScope.Hot));

    builder.Services.AddSingleton<ITraceQueryService>(sp => NewTraceService(sp, SegmentScope.All));
    builder.Services.AddKeyedSingleton<ITraceQueryService>("hot", (sp, _) => NewTraceService(sp, SegmentScope.Hot));
}
else
{
    // The querier reads sealed segments itself and asks the indexer for the hot tier it cannot see.
    builder.Services.AddSingleton<ILogBackend>(sp => new FederatedLogBackend(
        sealedTier: new SegmentLogService(
            sp.GetRequiredService<LogTierRegistries>(),
            sp.GetRequiredService<IClusterTenantResolver>(),
            sp.GetRequiredService<ILogger<SegmentLogService>>(),
            SegmentScope.Sealed),
        hotTier: new HttpLogBackend(RemoteApi(sp, "internal/logs")),
        logger: sp.GetRequiredService<ILogger<FederatedLogBackend>>()));

    // Traces take a third backend: the indexer's ALL-tier route, for the aggregates that cannot be
    // reconstructed from two halves (percentiles). See FederatedTraceService.
    builder.Services.AddSingleton<ITraceQueryService>(sp => new FederatedTraceService(
        sealedTier: NewTraceService(sp, SegmentScope.Sealed),
        hotTier: new HttpTraceBackend(RemoteApi(sp, "internal/traces")),
        allTiers: new HttpTraceBackend(RemoteApi(sp, "api/traces")),
        sp.GetRequiredService<ILogger<FederatedTraceService>>()));
}

// Only the indexer seals. A querier sharing this loop would try to roll an active index it never writes
// to, and would race the indexer's retention over the same catalog.
if (node.Role == NodeRole.Indexer)
{
    builder.Services.AddHostedService(sp => new SegmentSealService(
        sp.GetRequiredService<SegmentManagerRegistry<LogSegmentManager>>(), engine,
        sp.GetRequiredService<ILogger<SegmentSealService>>()));
    builder.Services.AddHostedService(sp => new SegmentSealService(
        sp.GetRequiredService<SegmentManagerRegistry<VerboseLogSegmentManager>>(), engine,
        sp.GetRequiredService<ILogger<SegmentSealService>>()));
    builder.Services.AddHostedService(sp => new SegmentSealService(
        sp.GetRequiredService<SegmentManagerRegistry<SpanSegmentManager>>(), engine,
        sp.GetRequiredService<ILogger<SegmentSealService>>()));
    builder.Services.AddHostedService(sp => new SegmentSealService(
        sp.GetRequiredService<SegmentManagerRegistry<RumSegmentManager>>(), engine,
        sp.GetRequiredService<ILogger<SegmentSealService>>()));
    builder.Services.AddHostedService(sp => new SegmentSealService(
        sp.GetRequiredService<SegmentManagerRegistry<TraceSummarySegmentManager>>(), engine,
        sp.GetRequiredService<ILogger<SegmentSealService>>()));
}

else
{
    // The querier never seals, but it does download cold segments onto its volume, so its local footprint
    // still has to be bounded or the pod eventually fills its disk with a read-through cache.
    builder.Services.AddHostedService(sp => new SegmentCacheTrimService(
        [
            sp.GetRequiredService<SegmentManagerRegistry<LogSegmentManager>>(),
            sp.GetRequiredService<SegmentManagerRegistry<VerboseLogSegmentManager>>(),
            sp.GetRequiredService<SegmentManagerRegistry<SpanSegmentManager>>(),
            sp.GetRequiredService<SegmentManagerRegistry<RumSegmentManager>>(),
            sp.GetRequiredService<SegmentManagerRegistry<TraceSummarySegmentManager>>(),
        ],
        sp.GetRequiredService<ILogger<SegmentCacheTrimService>>()));
}

WebApplication app = builder.Build();
ILogger startupLog = app.Services.GetRequiredService<ILogger<Program>>();
startupLog.LogInformation(
    "EntKube telemetry node starting: role={Role}, cluster={Cluster}, tenant={Tenant}, data={DataPath}, "
    + "retention={Retention}d, warm={Warm}d/{WarmBytes} bytes.",
    node.Role, node.ClusterId, node.TenantId, engine.DataPath,
    engine.RetentionDays, engine.WarmRetentionDays, engine.WarmMaxBytes);

// ── Health ───────────────────────────────────────────────────────────────────────────────────────────
// Liveness is "the process is up". Readiness additionally requires the data volume to be writable,
// because an indexer that cannot write its index accepts OTLP and silently loses every batch.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", role = node.Role.ToString().ToLowerInvariant() }));
app.MapGet("/readyz", () =>
{
    try
    {
        Directory.CreateDirectory(engine.DataPath);
        string probe = Path.Combine(engine.DataPath, ".readyz");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "not-ready", error = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// ── Ingest (indexer only) ────────────────────────────────────────────────────────────────────────────
if (node.Role == NodeRole.Indexer)
{
    // OTLP/JSON from the cluster's collector. Status codes are chosen for how the collector reacts:
    // it retries 5xx (and buffers meanwhile) and drops 4xx, so a malformed batch must never be a 500 or
    // the collector will resend it forever.
    app.MapPost("/ingest/otlp/v1/logs", async (
        HttpContext ctx, ITelemetryIngest telemetry, ILoggerFactory loggerFactory, CancellationToken ct) =>
    {
        ILogger log = loggerFactory.CreateLogger("NodeIngest");
        NodeIngest.Result r = await NodeIngest.ReadAsync(ctx, node, log, ct);
        if (r.Error is not null) return r.Error;

        using JsonDocument doc = r.Doc!;
        List<LogIngestRecord> records;
        try { records = OtlpLogsParser.Parse(doc); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to parse OTLP logs payload.");
            return Results.BadRequest();
        }

        try
        {
            await telemetry.WriteLogsAsync(node.TenantId, node.ClusterId, records, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to write OTLP logs batch.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        return Results.Json(new { });
    }).DisableAntiforgery();

    app.MapPost("/ingest/otlp/v1/traces", async (
        HttpContext ctx, ITelemetryIngest telemetry, ILoggerFactory loggerFactory, CancellationToken ct) =>
    {
        ILogger log = loggerFactory.CreateLogger("NodeIngest");
        NodeIngest.Result r = await NodeIngest.ReadAsync(ctx, node, log, ct);
        if (r.Error is not null) return r.Error;

        using JsonDocument doc = r.Doc!;
        List<SpanIngestRecord> spans;
        try { spans = OtlpTracesParser.Parse(doc); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to parse OTLP traces payload.");
            return Results.BadRequest();
        }

        try
        {
            await telemetry.WriteSpansAsync(node.TenantId, node.ClusterId, spans, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to write OTLP traces batch.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        return Results.Json(new { });
    }).DisableAntiforgery();
}

// ── Query ────────────────────────────────────────────────────────────────────────────────────────────
// The management plane's log viewers, served next to the data instead of across the WAN. Every route is
// the corresponding ILogBackend method; the cluster id is taken from configuration, never from the caller,
// so a query can only ever read this node's own cluster.
MapLogRoutes(app.MapGroup("/api/logs"), sp => sp.GetRequiredService<ILogBackend>());
MapTraceRoutes(app.MapGroup("/api/traces"), sp => sp.GetRequiredService<ITraceQueryService>());

if (node.Role == NodeRole.Indexer)
{
    // The hot tier, for a querier to merge with its own sealed results. Same shape as the public routes so
    // one client type serves both, but bound to the hot-only backend — the scope is fixed by wiring here,
    // never selectable by the caller.
    MapLogRoutes(app.MapGroup("/internal/logs"), sp => sp.GetRequiredKeyedService<ILogBackend>("hot"));
    MapTraceRoutes(app.MapGroup("/internal/traces"), sp => sp.GetRequiredKeyedService<ITraceQueryService>("hot"));

    // The segment list, so a querier knows which archives to fetch from object storage. It is the map, not
    // the data: object keys and time bounds only.
    app.MapGroup("/internal").AddEndpointFilter(RequireQueryToken)
        .MapGet("/segments", async (
            ISegmentCatalog catalog, string signal, DateTime? from, DateTime? to, CancellationToken ct) =>
            Results.Json(await catalog.ListOverlappingAsync(node.TenantId, signal, from, to, ct)));
}

app.Run();

// Constructs a trace service over one index tier. Three call sites need it (all/hot on the indexer,
// sealed on the querier) and it has five dependencies, so it is worth a helper.
static SegmentTraceService NewTraceService(IServiceProvider sp, SegmentScope scope) => new(
    sp.GetRequiredService<SegmentManagerRegistry<SpanSegmentManager>>(),
    sp.GetRequiredService<SegmentManagerRegistry<TraceSummarySegmentManager>>(),
    sp.GetRequiredService<ISegmentCatalog>(),
    sp.GetRequiredService<IClusterTenantResolver>(),
    sp.GetRequiredService<ILogger<SegmentTraceService>>(),
    scope);

// Binds a caller to one route group on the indexer.
NodeHttpApi RemoteApi(IServiceProvider sp, string routePrefix) => new(
    sp.GetRequiredService<IHttpClientFactory>(), IndexerClient, routePrefix, node,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger($"Remote:{routePrefix}"));

// Both the public query API and the indexer's internal hot-tier API expose the same operations over
// different backends, so they are described once. The cluster id always comes from configuration, never
// from the caller — a node can only ever read its own cluster's telemetry.
void MapLogRoutes(RouteGroupBuilder group, Func<IServiceProvider, ILogBackend> resolve)
{
    group.AddEndpointFilter(RequireQueryToken);

    // Does this node actually hold anything? The management plane routes on the answer, and it must be a
    // real question rather than "is the node reachable": a node that exists but has never received a batch
    // answers every other query successfully and empty, which is the one failure shape nothing reports.
    group.MapGet("/has-data", async (IServiceProvider sp, CancellationToken ct) =>
        Results.Json(await resolve(sp).HasDataAsync(node.ClusterId, ct)));

    group.MapGet("/namespaces", async (IServiceProvider sp, int windowMinutes, CancellationToken ct) =>
        Respond(await resolve(sp).GetNamespacesAsync(node.ClusterId, windowMinutes, ct)));

    group.MapGet("/pods", async (IServiceProvider sp, string ns, int windowMinutes, CancellationToken ct) =>
        Respond(await resolve(sp).GetPodsAsync(node.ClusterId, ns, windowMinutes, ct)));

    group.MapGet("/containers", async (IServiceProvider sp, string ns, int windowMinutes, CancellationToken ct) =>
        Respond(await resolve(sp).GetContainersAsync(node.ClusterId, ns, windowMinutes, ct)));

    group.MapPost("/search", async (IServiceProvider sp, LogSearchBody request, CancellationToken ct) =>
        Respond(await resolve(sp).QueryAsync(
            node.ClusterId, request.ToFilter(), request.From, request.To, request.Limit, ct)))
        .DisableAntiforgery();

    // GET twins of the body-bearing routes. The Kubernetes API server's proxy maps methods onto RBAC
    // verbs, so a POST through it needs `create` on services/proxy while a GET needs only `get` — and a
    // read-only kubeconfig has the latter. The management plane therefore uses these; the POST routes stay
    // for callers that reach the node directly, like a querier talking to its indexer.
    group.MapGet("/search", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        LogSearchBody request = NodeQuery.Decode<LogSearchBody>(q)!;
        return Respond(await resolve(sp).QueryAsync(
            node.ClusterId, request.ToFilter(), request.From, request.To, request.Limit, ct));
    });

    group.MapPost("/histogram", async (IServiceProvider sp, LogSearchBody request, CancellationToken ct) =>
        Respond(await resolve(sp).GetHistogramAsync(
            node.ClusterId, request.ToFilter(), request.From, request.To, request.Buckets, ct)))
        .DisableAntiforgery();

    group.MapGet("/histogram", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        LogSearchBody request = NodeQuery.Decode<LogSearchBody>(q)!;
        return Respond(await resolve(sp).GetHistogramAsync(
            node.ClusterId, request.ToFilter(), request.From, request.To, request.Buckets, ct));
    });

    group.MapPost("/count", async (IServiceProvider sp, LogSearchBody request, CancellationToken ct) =>
        Respond(await resolve(sp).CountAsync(
            node.ClusterId, request.Namespaces?.FirstOrDefault(), request.Text, request.MinLevel,
            request.From, request.To, ct)))
        .DisableAntiforgery();

    group.MapGet("/count", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        LogSearchBody request = NodeQuery.Decode<LogSearchBody>(q)!;
        return Respond(await resolve(sp).CountAsync(
            node.ClusterId, request.Namespaces?.FirstOrDefault(), request.Text, request.MinLevel,
            request.From, request.To, ct));
    });

    group.MapGet("/by-trace", async (IServiceProvider sp, string traceId, int limit, CancellationToken ct) =>
        Respond(await resolve(sp).QueryByTraceAsync(node.ClusterId, traceId, limit, ct)));
}

void MapTraceRoutes(RouteGroupBuilder group, Func<IServiceProvider, ITraceQueryService> resolve)
{
    group.AddEndpointFilter(RequireQueryToken);

    // GET twins, for the same reason as the log routes: a POST through the API server's proxy needs the
    // `create` verb on services/proxy, a GET needs only `get`.
    group.MapGet("/has-data", async (IServiceProvider sp, CancellationToken ct) =>
        Results.Json(await resolve(sp).HasDataAsync(node.ClusterId, ct)));

    group.MapGet("/services", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        TraceQueryBody b = NodeQuery.Decode<TraceQueryBody>(q)!;
        return Respond(await resolve(sp).GetServicesAsync(node.ClusterId, ct, b.Namespaces, b.PodPattern, b.WindowMinutes));
    });
    group.MapGet("/list", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        TraceQueryBody b = NodeQuery.Decode<TraceQueryBody>(q)!;
        return Respond(await resolve(sp).ListTracesAsync(
            node.ClusterId, b.Service, b.From, b.To, b.MinDurationMs, b.ErrorsOnly, b.Limit, ct,
            b.Namespaces, b.PodPattern));
    });
    group.MapGet("/trace", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        TraceQueryBody b = NodeQuery.Decode<TraceQueryBody>(q)!;
        return Respond(await resolve(sp).GetTraceAsync(node.ClusterId, b.TraceId ?? "", ct, b.Namespaces));
    });
    group.MapGet("/red", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        TraceQueryBody b = NodeQuery.Decode<TraceQueryBody>(q)!;
        return Respond(await resolve(sp).GetServiceRedAsync(
            node.ClusterId, b.Service ?? "", b.From, b.To, b.Buckets, ct, b.Namespaces, b.PodPattern));
    });
    group.MapGet("/map", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        TraceQueryBody b = NodeQuery.Decode<TraceQueryBody>(q)!;
        return Respond(await resolve(sp).GetServiceMapAsync(node.ClusterId, b.From, b.To, ct, b.Namespaces, b.PodPattern));
    });
    group.MapGet("/stats", async (IServiceProvider sp, string q, CancellationToken ct) =>
    {
        TraceQueryBody b = NodeQuery.Decode<TraceQueryBody>(q)!;
        return Respond(await resolve(sp).GetServiceStatsAsync(node.ClusterId, b.Service ?? "", b.From, b.To, ct));
    });

    group.MapPost("/services", async (IServiceProvider sp, TraceQueryBody q, CancellationToken ct) =>
        Respond(await resolve(sp).GetServicesAsync(node.ClusterId, ct, q.Namespaces, q.PodPattern, q.WindowMinutes)))
        .DisableAntiforgery();

    group.MapPost("/list", async (IServiceProvider sp, TraceQueryBody q, CancellationToken ct) =>
        Respond(await resolve(sp).ListTracesAsync(
            node.ClusterId, q.Service, q.From, q.To, q.MinDurationMs, q.ErrorsOnly, q.Limit, ct,
            q.Namespaces, q.PodPattern)))
        .DisableAntiforgery();

    group.MapPost("/trace", async (IServiceProvider sp, TraceQueryBody q, CancellationToken ct) =>
        Respond(await resolve(sp).GetTraceAsync(node.ClusterId, q.TraceId ?? "", ct, q.Namespaces)))
        .DisableAntiforgery();

    group.MapPost("/red", async (IServiceProvider sp, TraceQueryBody q, CancellationToken ct) =>
        Respond(await resolve(sp).GetServiceRedAsync(
            node.ClusterId, q.Service ?? "", q.From, q.To, q.Buckets, ct, q.Namespaces, q.PodPattern)))
        .DisableAntiforgery();

    group.MapPost("/map", async (IServiceProvider sp, TraceQueryBody q, CancellationToken ct) =>
        Respond(await resolve(sp).GetServiceMapAsync(node.ClusterId, q.From, q.To, ct, q.Namespaces, q.PodPattern)))
        .DisableAntiforgery();

    group.MapPost("/stats", async (IServiceProvider sp, TraceQueryBody q, CancellationToken ct) =>
        Respond(await resolve(sp).GetServiceStatsAsync(node.ClusterId, q.Service ?? "", q.From, q.To, ct)))
        .DisableAntiforgery();
}

async ValueTask<object?> RequireQueryToken(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    => NodeIngest.IsAuthorized(ctx.HttpContext, node.QueryToken) ? await next(ctx) : Results.Unauthorized();

// A failed query is the node's problem, not the caller's request being wrong, so it surfaces as a 500
// carrying the engine's own message — which is what makes a misconfigured bucket diagnosable from the
// management plane instead of showing up as an empty log view.
static IResult Respond<T>(KubernetesOperationResult<T> result) =>
    result.IsSuccess
        ? Results.Json(result.Data)
        : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status500InternalServerError);
