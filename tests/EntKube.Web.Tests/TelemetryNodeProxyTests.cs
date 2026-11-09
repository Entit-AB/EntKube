using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Reaching a cluster's telemetry node without holding the cluster's credential.
///
/// <para>This file used to build an authenticated <c>Kubernetes</c> client from the kubeconfig in
/// order to address the API server's <c>/proxy</c> endpoint — a <em>connection</em>, not a command,
/// which is why every earlier custody batch left it alone. The seam gained
/// <c>ProxyServiceAsync(ns, service, port, …)</c>: the narrow capability actually in use, rather
/// than a configured client, which would have moved the credential out of the caller's hands while
/// granting the same authority.</para>
/// </summary>
public class TelemetryNodeProxyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly TelemetryNodeClient sut;

    private readonly List<(string Ns, string Service, int Port, string Path)> proxied = [];

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public TelemetryNodeProxyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();

        Guid envId = Guid.NewGuid();
        Guid componentId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "primary", ApiServerUrl = "https://c:6443",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "entkube-telemetry-query",
            ComponentType = "HelmChart", HelmChartName = "entkube-telemetry",
            Status = ComponentStatus.Installed, Namespace = "monitoring",
        });
        db.SaveChanges();

        VaultService vault = testDb.CreateVaultService();
        vault.InitializeVaultAsync(tenantId).GetAwaiter().GetResult();
        testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid)
            .GetAwaiter().GetResult();

        // The Service list the seam answers with: a querier and an indexer, so the preference is
        // exercised rather than assumed.
        k8s.Setup(x => x.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
                {"items":[
                  {"metadata":{"name":"tel-indexer","labels":{"app.kubernetes.io/component":"indexer"}},
                   "spec":{"ports":[{"name":"http","port":8080}]}},
                  {"metadata":{"name":"tel-querier","labels":{"app.kubernetes.io/component":"querier"}},
                   "spec":{"ports":[{"name":"metrics","port":9090},{"name":"http","port":8081}]}}
                ]}
                """);

        k8s.Setup(x => x.ProxyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback((string ns, string svc, int port, HttpMethod _, string path, string _2,
                    object? _3, IReadOnlyDictionary<string, string>? _4, CancellationToken _5)
                => proxied.Add((ns, svc, port, path)))
            .ReturnsAsync(new ServiceProxyResponse(200, "OK", "true"));

        sut = new TelemetryNodeClient(
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new IngestTokenService(TestServices.TestConfiguration()),
            NullLogger<TelemetryNodeClient>.Instance);

        TelemetryNodeClient.Invalidate(clusterId);
    }

    public void Dispose()
    {
        TelemetryNodeClient.Invalidate(clusterId);
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_query_goes_to_the_queriers_http_port()
    {
        KubernetesOperationResult<bool> result =
            await sut.GetAsync<bool>(tenantId, clusterId, "api/logs/has-data");

        result.IsSuccess.Should().BeTrue(result.Error);

        (string ns, string service, int port, string path) = proxied.Should().ContainSingle().Subject;

        ns.Should().Be("monitoring");
        service.Should().Be("tel-querier", "a querier is preferred — offloading reads is why it exists");
        port.Should().Be(8081, "the port NAMED http, not the first one listed");
        path.Should().Be("api/logs/has-data");
    }

    /// <summary>
    /// The cluster is not the stranger's, so nothing may be reached — and the message says both
    /// possible reasons, because the seam's refusal does not distinguish them.
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_query_the_node()
    {
        Guid stranger = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        KubernetesOperationResult<bool> result =
            await sut.GetAsync<bool>(stranger, clusterId, "api/logs/has-data");

        result.IsSuccess.Should().BeFalse();
        proxied.Should().BeEmpty("a stranger's read must not reach another tenant's node");
    }

    /// <summary>
    /// ⚠️ The endpoint memo is keyed by (tenant, cluster), and this is the <b>third</b> time that
    /// trap has been hit in this codebase. Resolving is tenant-scoped, so a stranger resolves "no
    /// node"; under a cluster-only key that answer would then be served to the owner, whose
    /// telemetry reads would silently fall back to a management-plane store that holds nothing for
    /// a cut-over cluster. Asking in the dangerous order is the only way to see it.
    /// </summary>
    [Fact]
    public async Task A_strangers_refusal_is_not_cached_for_the_owner()
    {
        Guid stranger = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        // Stranger FIRST, so a cluster-keyed memo would hold their "no".
        (await sut.IsAvailableAsync(stranger, clusterId)).Should().BeFalse();

        (await sut.IsAvailableAsync(tenantId, clusterId)).Should().BeTrue(
            "the owner's node is there; the stranger's refusal was an answer to a different question");
    }

    /// <summary>And it still memoizes — a re-key that defeated the cache would be its own bug.</summary>
    [Fact]
    public async Task The_endpoint_is_resolved_once_per_tenant_and_cluster()
    {
        await sut.IsAvailableAsync(tenantId, clusterId);
        await sut.IsAvailableAsync(tenantId, clusterId);
        await sut.IsAvailableAsync(tenantId, clusterId);

        k8s.Verify(x => x.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Deadlines ──
    // Every node call crosses the cluster's API server, and none had a deadline of its own, so a node that
    // never answered held the page until the HTTP client gave up 100 seconds later. Each test makes the
    // seam hang until cancelled and asserts the call comes back on its own bound, with a message.

    /// <summary>
    /// Hangs until the CALLER's deadline cancels it, and no longer than <see cref="Ceiling"/>. Without the
    /// ceiling a removed deadline would hang the test rather than fail it, which a suite reports as
    /// nothing at all until the job times out.
    /// </summary>
    private static async Task<T> HangUntilCancelled<T>(CancellationToken ct)
    {
        await Task.Delay(Ceiling, ct);
        throw new TimeoutException("the client never cancelled the call; nothing bounded it");
    }

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    private TelemetryNodeClient ClientWith(TimeSpan lookup, TimeSpan probe, TimeSpan read) =>
        new(new ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new IngestTokenService(TestServices.TestConfiguration()),
            NullLogger<TelemetryNodeClient>.Instance)
        {
            LookupTimeout = lookup, ProbeTimeout = probe, ReadTimeout = read,
        };

    private void ProxyHangs() =>
        k8s.Setup(x => x.ProxyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, int _, HttpMethod _, string _, string _, object? _,
                    IReadOnlyDictionary<string, string>? _, CancellationToken ct)
                => HangUntilCancelled<ServiceProxyResponse>(ct));

    [Fact]
    public async Task A_read_from_a_node_that_never_answers_fails_on_the_read_deadline()
    {
        ProxyHangs();
        TelemetryNodeClient client = ClientWith(
            lookup: TimeSpan.FromSeconds(30), probe: TimeSpan.FromSeconds(30), read: TimeSpan.FromMilliseconds(300));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        KubernetesOperationResult<List<string>> result =
            await client.GetAsync<List<string>>(tenantId, clusterId, "api/traces/services");

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("did not answer within", "a hung node must say so, not just fail");
    }

    [Fact]
    public async Task A_has_data_probe_answers_no_on_the_probe_deadline_not_the_read_one()
    {
        ProxyHangs();
        TelemetryNodeClient client = ClientWith(
            lookup: TimeSpan.FromSeconds(30), probe: TimeSpan.FromMilliseconds(300), read: TimeSpan.FromMinutes(5));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        bool holds = await new NodeTraceService(tenantId, client).HasDataAsync(clusterId);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "the routing probe runs in front of every view, so it must not wait as long as a search may");
        holds.Should().BeFalse("silence is not \"I hold data\"");
    }

    [Fact]
    public async Task A_service_lookup_that_never_answers_counts_as_no_node()
    {
        k8s.Setup(x => x.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, string _, CancellationToken ct) => HangUntilCancelled<string>(ct));
        TelemetryNodeClient client = ClientWith(
            lookup: TimeSpan.FromMilliseconds(300), probe: TimeSpan.FromMinutes(5), read: TimeSpan.FromMinutes(5));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        bool present = await client.IsAvailableAsync(tenantId, clusterId);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        present.Should().BeFalse();
    }

    /// <summary>
    /// The port fallbacks, which decide whether an installed node is reachable at all. Exercised
    /// directly because a wrong port presents as a healthy node that answers nothing.
    /// </summary>
    [Fact]
    public void The_port_falls_back_to_the_first_then_to_the_charts_default()
    {
        TelemetryNodeClient.ReadServices(
            """{"items":[{"metadata":{"name":"a"},"spec":{"ports":[{"name":"grpc","port":9095}]}}]}""")
            .Should().ContainSingle().Which.Port.Should().Be(9095, "no http port, so the first one");

        TelemetryNodeClient.ReadServices("""{"items":[{"metadata":{"name":"a"},"spec":{}}]}""")
            .Should().ContainSingle().Which.Port.Should().Be(8080, "no ports at all, so the chart's default");

        TelemetryNodeClient.ReadServices("""{"items":[]}""").Should().BeEmpty();
    }
}
