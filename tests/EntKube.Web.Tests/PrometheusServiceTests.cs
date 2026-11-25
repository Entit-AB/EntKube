using System.Net;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for PrometheusService — the service that connects to a kube-prometheus-stack
/// running in a cluster and retrieves health/status metrics. These tests verify the
/// data-layer plumbing (finding clusters, validating configuration) and response
/// parsing. Actual Prometheus queries require a live cluster.
/// </summary>
public class PrometheusServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vaultService;
    private readonly FakeApiServer apiServer = new();
    private readonly PrometheusService sut;

    public PrometheusServiceTests()
    {
        // Mirrors production: contexts resolve cluster.Kubeconfig from the vault via the interceptor.
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vaultService = testDb.CreateVaultService();

        // The real factory still decides whether a cluster resolves (tenant, kubeconfig); only the
        // SDK client it hands out talks to `apiServer` instead of https://k8s.example.com. The
        // aggregate now probes component-less clusters, and without this every such test would make
        // a real request — and by default this server serves nothing, which is what a cluster
        // without Prometheus looks like.
        sut = new PrometheusService(
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
            new SdkClientsAnsweredBy(
                new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, new Mock<IKubernetesClientFactory>().Object),
                apiServer),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new PromQueryCache(),
            NullLogger<PrometheusService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }

    // ──────── GetClusterHealthAsync — failure paths ────────

    [Fact]
    public async Task GetClusterHealthAsync_ClusterNotFound_ReturnsFailure()
    {
        // Arrange — use a random cluster ID that doesn't exist in the database.

        Guid nonExistentId = Guid.NewGuid();

        // Act

        KubernetesOperationResult<ClusterHealthSummary> result =
            await sut.GetClusterHealthAsync(Guid.NewGuid(), nonExistentId);

        // Assert — should fail gracefully with a clear message.

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task GetClusterHealthAsync_NoPrometheusComponent_ReturnsFailure()
    {
        // Arrange — cluster exists but has no prometheus component configured.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestTenant", Slug = "test" };
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "prod" };
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com",
        };

        db.Set<Tenant>().Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        // The cluster has a kubeconfig (stored in the vault), so the health check gets past the
        // kubeconfig gate and fails on the missing prometheus component.
        await vaultService.InitializeVaultAsync(tenant.Id);
        await testDb.SeedKubeconfigAsync(vaultService, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        // Act

        KubernetesOperationResult<ClusterHealthSummary> result =
            await sut.GetClusterHealthAsync(tenant.Id, cluster.Id);

        // Assert — no prometheus component means we can't query metrics.

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("prometheus");
    }

    [Fact]
    public async Task GetClusterHealthAsync_NoKubeconfig_ReturnsFailure()
    {
        // Arrange — cluster has prometheus component but no kubeconfig stored.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestTenant2", Slug = "test2" };
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "prod" };
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "no-kubeconfig-cluster",
            ApiServerUrl = "https://k8s.example.com",
            Kubeconfig = null
        };

        ClusterComponent prometheusComponent = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = "kube-prometheus-stack",
            ComponentType = "HelmChart",
            Configuration = """{"namespace":"monitoring","serviceName":"prometheus-kube-prometheus-prometheus","servicePort":9090}"""
        };

        db.Set<Tenant>().Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        db.ClusterComponents.Add(prometheusComponent);
        await db.SaveChangesAsync();

        // Act

        KubernetesOperationResult<ClusterHealthSummary> result =
            await sut.GetClusterHealthAsync(tenant.Id, cluster.Id);

        // Assert — without kubeconfig we can't connect to the cluster.

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
    }

    // ──────── ParsePrometheusResponse — parsing metric results ────────

    [Fact]
    public void ParseInstantQueryResult_ValidVectorResponse_ReturnsValues()
    {
        // Arrange — a typical Prometheus instant query response for `up` metric.

        string json = """
        {
            "status": "success",
            "data": {
                "resultType": "vector",
                "result": [
                    {"metric": {"job": "node-exporter", "instance": "node1:9100"}, "value": [1700000000, "1"]},
                    {"metric": {"job": "node-exporter", "instance": "node2:9100"}, "value": [1700000000, "1"]},
                    {"metric": {"job": "node-exporter", "instance": "node3:9100"}, "value": [1700000000, "0"]}
                ]
            }
        }
        """;

        // Act

        List<PrometheusMetricResult> results = PrometheusService.ParseInstantQueryResult(json);

        // Assert — three results parsed, with correct values.

        results.Should().HaveCount(3);
        results[0].Value.Should().Be(1.0);
        results[1].Value.Should().Be(1.0);
        results[2].Value.Should().Be(0.0);
        results[0].Labels.Should().ContainKey("instance").WhoseValue.Should().Be("node1:9100");
    }

    [Fact]
    public void ParseInstantQueryResult_EmptyResult_ReturnsEmptyList()
    {
        // Arrange — Prometheus returns success but no matching series.

        string json = """
        {
            "status": "success",
            "data": {
                "resultType": "vector",
                "result": []
            }
        }
        """;

        // Act

        List<PrometheusMetricResult> results = PrometheusService.ParseInstantQueryResult(json);

        // Assert

        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseInstantQueryResult_ErrorResponse_ReturnsEmptyList()
    {
        // Arrange — Prometheus returns an error status.

        string json = """
        {
            "status": "error",
            "errorType": "bad_data",
            "error": "invalid expression"
        }
        """;

        // Act

        List<PrometheusMetricResult> results = PrometheusService.ParseInstantQueryResult(json);

        // Assert — graceful degradation, no exception thrown.

        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseInstantQueryResult_ScalarResponse_ReturnsSingleValue()
    {
        // Arrange — a scalar query result (e.g. `count(up)`).

        string json = """
        {
            "status": "success",
            "data": {
                "resultType": "scalar",
                "result": [1700000000, "42"]
            }
        }
        """;

        // Act

        List<PrometheusMetricResult> results = PrometheusService.ParseInstantQueryResult(json);

        // Assert

        results.Should().HaveCount(1);
        results[0].Value.Should().Be(42.0);
    }

    // ──────── GetPrometheusConfig — extracting config from component ────────

    [Fact]
    public void GetPrometheusConfig_ValidJson_ReturnsConfig()
    {
        // Arrange — a component with properly formatted configuration JSON.

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = Guid.NewGuid(),
            Name = "kube-prometheus-stack",
            ComponentType = "HelmChart",
            Configuration = """{"namespace":"monitoring","serviceName":"prometheus-kube-prometheus-prometheus","servicePort":9090}"""
        };

        // Act

        PrometheusConfig? config = PrometheusService.GetPrometheusConfig(component);

        // Assert

        config.Should().NotBeNull();
        config!.Namespace.Should().Be("monitoring");
        config.ServiceName.Should().Be("prometheus-kube-prometheus-prometheus");
        config.ServicePort.Should().Be(9090);
    }

    [Fact]
    public void GetPrometheusConfig_NullConfiguration_ReturnsDefaultConfig()
    {
        // Arrange — component exists but has no configuration set yet.
        // We should return sensible defaults for kube-prometheus-stack.

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = Guid.NewGuid(),
            Name = "kube-prometheus-stack",
            ComponentType = "HelmChart",
            Configuration = null
        };

        // Act

        PrometheusConfig? config = PrometheusService.GetPrometheusConfig(component);

        // Assert — service names are derived from the release name
        // (component name "kube-prometheus-stack" → "{release}-prometheus").

        config.Should().NotBeNull();
        config!.Namespace.Should().Be("monitoring");
        config.ServiceName.Should().Be("kube-prometheus-stack-prometheus");
        config.ServicePort.Should().Be(9090);
    }

    [Fact]
    public void GetPrometheusConfig_InvalidJson_ReturnsDefaultConfig()
    {
        // Arrange — configuration is corrupt JSON.

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = Guid.NewGuid(),
            Name = "kube-prometheus-stack",
            ComponentType = "HelmChart",
            Configuration = "not valid json {"
        };

        // Act

        PrometheusConfig? config = PrometheusService.GetPrometheusConfig(component);

        // Assert — graceful fallback to defaults.

        config.Should().NotBeNull();
        config!.Namespace.Should().Be("monitoring");
    }

    // ──────── ParseRangeQueryResult — time-series parsing ────────

    [Fact]
    public void ParseRangeQueryResult_ValidMatrixResponse_ReturnsTimeSeries()
    {
        // Arrange — a typical Prometheus range query response with matrix data.
        // Each result is a series with multiple [timestamp, value] pairs over time.

        string json = """
        {
            "status": "success",
            "data": {
                "resultType": "matrix",
                "result": [
                    {
                        "metric": {"instance": "node1:9100"},
                        "values": [
                            [1700000000, "45.2"],
                            [1700000060, "47.1"],
                            [1700000120, "43.8"],
                            [1700000180, "50.3"]
                        ]
                    }
                ]
            }
        }
        """;

        // Act

        List<PrometheusTimeSeries> results = PrometheusService.ParseRangeQueryResult(json);

        // Assert — one series with four data points.

        results.Should().HaveCount(1);
        results[0].Labels.Should().ContainKey("instance").WhoseValue.Should().Be("node1:9100");
        results[0].DataPoints.Should().HaveCount(4);
        results[0].DataPoints[0].Value.Should().BeApproximately(45.2, 0.01);
        results[0].DataPoints[3].Value.Should().BeApproximately(50.3, 0.01);
        results[0].DataPoints[0].Timestamp.Should().BeAfter(DateTime.UnixEpoch);
    }

    [Fact]
    public void ParseRangeQueryResult_MultipleSeriesResponse_ReturnsAll()
    {
        // Arrange — two series (e.g. CPU per node) in a range query.

        string json = """
        {
            "status": "success",
            "data": {
                "resultType": "matrix",
                "result": [
                    {
                        "metric": {"node": "node1"},
                        "values": [[1700000000, "30.0"], [1700000060, "35.0"]]
                    },
                    {
                        "metric": {"node": "node2"},
                        "values": [[1700000000, "60.0"], [1700000060, "62.5"]]
                    }
                ]
            }
        }
        """;

        // Act

        List<PrometheusTimeSeries> results = PrometheusService.ParseRangeQueryResult(json);

        // Assert

        results.Should().HaveCount(2);
        results[0].Labels["node"].Should().Be("node1");
        results[1].Labels["node"].Should().Be("node2");
        results[1].DataPoints[1].Value.Should().BeApproximately(62.5, 0.01);
    }

    [Fact]
    public void ParseRangeQueryResult_EmptyResult_ReturnsEmptyList()
    {
        // Arrange

        string json = """
        {
            "status": "success",
            "data": {
                "resultType": "matrix",
                "result": []
            }
        }
        """;

        // Act

        List<PrometheusTimeSeries> results = PrometheusService.ParseRangeQueryResult(json);

        // Assert

        results.Should().BeEmpty();
    }

    // ──────── ParseAlertmanagerAlerts — alertmanager response parsing ────────

    [Fact]
    public void ParseAlertmanagerAlerts_ValidResponse_ReturnsAlerts()
    {
        // Arrange — Alertmanager /api/v2/alerts returns an array of alert objects.

        string json = """
        [
            {
                "labels": {"alertname": "HighCPU", "severity": "warning", "instance": "node1:9100"},
                "annotations": {"summary": "CPU usage is above 90%", "description": "Node node1 has high CPU load"},
                "startsAt": "2026-05-16T10:30:00Z",
                "endsAt": "0001-01-01T00:00:00Z",
                "status": {"state": "active"},
                "fingerprint": "abc123"
            },
            {
                "labels": {"alertname": "PodCrashLooping", "severity": "critical", "namespace": "default", "pod": "api-7f8b9-xyz"},
                "annotations": {"summary": "Pod is crash looping"},
                "startsAt": "2026-05-16T09:15:00Z",
                "endsAt": "0001-01-01T00:00:00Z",
                "status": {"state": "active"},
                "fingerprint": "def456"
            }
        ]
        """;

        // Act

        List<AlertInfo> alerts = PrometheusService.ParseAlertmanagerAlerts(json);

        // Assert — two alerts parsed with correct severity and labels.

        alerts.Should().HaveCount(2);
        alerts[0].Name.Should().Be("HighCPU");
        alerts[0].Severity.Should().Be("warning");
        alerts[0].Summary.Should().Contain("CPU usage");
        alerts[0].State.Should().Be("active");
        alerts[0].Labels.Should().ContainKey("instance");
        alerts[1].Name.Should().Be("PodCrashLooping");
        alerts[1].Severity.Should().Be("critical");
    }

    [Fact]
    public void ParseAlertmanagerAlerts_EmptyArray_ReturnsEmptyList()
    {
        // Arrange

        string json = "[]";

        // Act

        List<AlertInfo> alerts = PrometheusService.ParseAlertmanagerAlerts(json);

        // Assert

        alerts.Should().BeEmpty();
    }

    [Fact]
    public void ParseAlertmanagerAlerts_InvalidJson_ReturnsEmptyList()
    {
        // Arrange — corrupt JSON should degrade gracefully.

        string json = "not valid json {";

        // Act

        List<AlertInfo> alerts = PrometheusService.ParseAlertmanagerAlerts(json);

        // Assert

        alerts.Should().BeEmpty();
    }

    // ──────── ParseAlertmanagerSilences — silence parsing ────────

    [Fact]
    public void ParseAlertmanagerSilences_ValidResponse_ReturnsSilences()
    {
        // Arrange — Alertmanager /api/v2/silences response.

        string json = """
        [
            {
                "id": "silence-001",
                "status": {"state": "active"},
                "comment": "Maintenance window for node upgrade",
                "createdBy": "ops-team",
                "startsAt": "2026-05-16T08:00:00Z",
                "endsAt": "2026-05-16T12:00:00Z",
                "matchers": [
                    {"name": "instance", "value": "node1:9100", "isRegex": false, "isEqual": true}
                ]
            },
            {
                "id": "silence-002",
                "status": {"state": "expired"},
                "comment": "Past maintenance",
                "createdBy": "admin",
                "startsAt": "2026-05-15T00:00:00Z",
                "endsAt": "2026-05-15T04:00:00Z",
                "matchers": [
                    {"name": "alertname", "value": "Watchdog", "isRegex": false, "isEqual": true}
                ]
            }
        ]
        """;

        // Act

        List<SilenceInfo> silences = PrometheusService.ParseAlertmanagerSilences(json);

        // Assert — two silences, one active and one expired.

        silences.Should().HaveCount(2);
        silences[0].Id.Should().Be("silence-001");
        silences[0].State.Should().Be("active");
        silences[0].Comment.Should().Contain("Maintenance");
        silences[0].CreatedBy.Should().Be("ops-team");
        silences[0].Matchers.Should().HaveCount(1);
        silences[0].Matchers[0].Name.Should().Be("instance");
        silences[1].State.Should().Be("expired");
    }

    // ════════════════════════════════════════════════════════════════
    //  CNPG metric selectors
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CnpgSelector_KeepsEveryMatcherInOneBraceBlock()
    {
        // Two adjacent brace blocks — metric{a="1"}{b="2"} — do not narrow the query, they fail
        // to parse, and Prometheus rejects the whole request with 400 Bad Request.
        string query = PrometheusService.CnpgSelector("cnpg_pg_stat_activity_count", "postgres", "state=\"active\"");

        query.Should().NotContain("}{");
        query.Should().Contain("cnpg_pg_stat_activity_count{cluster=\"postgres\",state=\"active\"}");
    }

    [Fact]
    public void CnpgSelector_MatchesEitherClusterLabelSpelling()
    {
        // CNPG's exporter labels series with `cluster`; some scrape setups relabel to
        // `cluster_name`. Matching both keeps the panel working on either.
        string query = PrometheusService.CnpgSelector("cnpg_backends_total", "postgres");

        query.Should().Be(
            "cnpg_backends_total{cluster=\"postgres\"} or cnpg_backends_total{cluster_name=\"postgres\"}");
    }

    [Fact]
    public void CnpgSelector_OmitsTheSeparatorWhenThereIsNoExtraMatcher()
    {
        PrometheusService.CnpgSelector("cnpg_pg_replication_lag", "postgres")
            .Should().NotContain(",}");
    }

    // ════════════════════════════════════════════════════════════════
    //  CNPG scrape diagnosis — selector matching
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CnpgSelector_IsUsedForEveryMetricQuery()
    {
        // Guards the shape the diagnosis explains to the operator: one brace block per metric.
        foreach (string metric in new[]
                 { "cnpg_pg_replication_lag", "cnpg_backends_total", "cnpg_pg_database_size_bytes" })
        {
            PrometheusService.CnpgSelector(metric, "postgres").Should().NotContain("}{");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  RabbitMQ query construction
    //
    //  A metric name that does not exist returns an empty vector rather than an
    //  error, so a typo surfaces as a tile that reads zero forever — which looks
    //  exactly like an idle broker. These names are therefore pinned against the
    //  rabbitmq_prometheus plugin's published metrics reference.
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("unacked",     "rabbitmq_queue_messages_unacked")]
    [InlineData("ready",       "rabbitmq_queue_messages_ready")]
    [InlineData("total",       "rabbitmq_queue_messages")]
    [InlineData("deliver",     "rabbitmq_channel_messages_delivered_total")]
    [InlineData("ack",         "rabbitmq_channel_messages_acked_total")]
    [InlineData("publish",     "rabbitmq_channel_messages_published_total")]
    [InlineData("redeliver",   "rabbitmq_channel_messages_redelivered_total")]
    [InlineData("unconfirmed", "rabbitmq_channel_messages_unconfirmed")]
    [InlineData("consumers",   "rabbitmq_consumers")]
    [InlineData("memLimit",    "rabbitmq_resident_memory_limit_bytes")]
    [InlineData("diskLimit",   "rabbitmq_disk_space_available_limit_bytes")]
    public void BuildRabbitMQQueries_UsesThePublishedMetricName(string key, string expectedMetric)
    {
        PrometheusService.BuildRabbitMQQueries("broker")[key].Should().Contain(expectedMetric);
    }

    [Fact]
    public void BuildRabbitMQQueries_DoesNotUseNamesThePluginNeverExported()
    {
        // Both of these were queried previously and always answered with nothing:
        // the unacknowledged gauge is spelled "_unacked", and delivery is counted on
        // the channel rather than the queue.
        string all = string.Join("\n", PrometheusService.BuildRabbitMQQueries("broker").Values);

        all.Should().NotContain("rabbitmq_queue_messages_unacknowledged");
        all.Should().NotContain("rabbitmq_queue_messages_delivered_total");
    }

    [Fact]
    public void BuildRabbitMQQueries_ScopesEveryQueryToTheNamedCluster()
    {
        // Without the cluster label every broker on the cluster would be summed into one card.
        foreach (KeyValuePair<string, string> q in PrometheusService.BuildRabbitMQQueries("orders"))
        {
            q.Value.Should().Contain("rabbitmq_cluster=\"orders\"", $"query '{q.Key}' must be scoped");
        }
    }

    [Fact]
    public void BuildRabbitMQQueries_ReachTheClusterNameByJoiningIdentityInfo()
    {
        // The plugin emits its aggregated families with no labels whatsoever — rabbitmq_cluster
        // exists only on rabbitmq_identity_info. Writing metric{rabbitmq_cluster="x"} directly
        // matches no series and reports zero forever, so every non-identity query has to join.
        Dictionary<string, string> queries = PrometheusService.BuildRabbitMQQueries("orders");

        foreach (KeyValuePair<string, string> q in queries.Where(q => q.Key != "nodes"))
        {
            q.Value.Should().Contain("group_left", $"query '{q.Key}' must join rabbitmq_identity_info");
            q.Value.Should().Contain("rabbitmq_identity_info", $"query '{q.Key}' must join identity info");
        }
    }

    [Fact]
    public void BuildRabbitMQQueries_PinTheIdentityEndpointSoNodesAreNotDoubleCounted()
    {
        // The plugin emits identity info once per scrape path. If /metrics/detailed is also
        // scraped, an unpinned join finds two identity series per node — Prometheus rejects the
        // ambiguity, and the node count doubles.
        foreach (KeyValuePair<string, string> q in PrometheusService.BuildRabbitMQQueries("orders"))
        {
            q.Value.Should().Contain("rabbitmq_endpoint=\"aggregated\"",
                $"query '{q.Key}' must pin the identity endpoint");
        }
    }

    [Fact]
    public void BuildRabbitMQQueries_CountDeliveriesInBothAcknowledgementModes()
    {
        // "_delivered_total" counts auto-ack deliveries only; manual-ack ones — the normal case —
        // land in "_delivered_ack_total". Querying one alone reports zero for real workloads.
        string deliver = PrometheusService.BuildRabbitMQQueries("orders")["deliver"];

        deliver.Should().Contain("rabbitmq_channel_messages_delivered_ack_total");
        deliver.Should().Contain("rabbitmq_channel_messages_delivered_total");
    }

    [Fact]
    public void BuildRabbitMQQueries_CompareAckRateAgainstManualAckDeliveriesOnly()
    {
        // Auto-ack deliveries are never acknowledged, so including them would show permanent
        // ack lag on a healthy auto-ack consumer.
        string deliverAck = PrometheusService.BuildRabbitMQQueries("orders")["deliverAck"];

        deliverAck.Should().Contain("rabbitmq_channel_messages_delivered_ack_total");
        deliverAck.Should().NotContain("rabbitmq_channel_messages_delivered_total");
    }

    [Fact]
    public void BuildRabbitMQQueries_KeepsPerNodeResourceQueriesUnaggregated()
    {
        // One node at its memory watermark blocks publishers cluster-wide; summing across
        // nodes would hide which node, and comparing a sum to a single limit is meaningless.
        // The join must also carry rabbitmq_node, since the node families carry no labels.
        foreach (string key in new[] { "mem", "memLimit", "disk", "diskLimit", "fds", "fdsMax" })
        {
            string q = PrometheusService.BuildRabbitMQQueries("broker")[key];
            q.Should().NotStartWith("sum(");
            q.Should().Contain("rabbitmq_node", $"query '{key}' must carry the node identity");
        }
    }

    [Fact]
    public void BuildRabbitMQQueries_DefaultSummedRatesToZeroSoOneAbsentCounterCannotBlankThem()
    {
        // Vector arithmetic yields an empty result when either side has no series, which would
        // silently zero the value and the warning that depends on it.
        PrometheusService.BuildRabbitMQQueries("orders")["unroutable"].Should().Contain("or vector(0)");
    }

    // ════════════════════════════════════════════════════════════════
    //  Keycloak query construction
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("requests",   "http_server_requests_seconds_count")]
    [InlineData("latency",    "http_server_requests_seconds_sum")]
    [InlineData("active",     "http_server_active_requests")]
    [InlineData("heapUsed",   "jvm_memory_used_bytes")]
    [InlineData("gcPause",    "jvm_gc_pause_seconds_sum")]
    [InlineData("dbActive",   "agroal_active_count")]
    [InlineData("dbAwait",    "agroal_awaiting_count")]
    [InlineData("dbBlock",    "agroal_blocking_time_average_milliseconds")]
    [InlineData("logins",     "keycloak_user_events_total")]
    public void BuildKeycloakQueries_UsesThePublishedMetricName(string key, string expectedMetric)
    {
        PrometheusService.BuildKeycloakQueries("keycloak")[key].Should().Contain(expectedMetric);
    }

    [Fact]
    public void BuildKeycloakQueries_ScopesEveryQueryToTheReleaseNamespace()
    {
        // Keycloak's metrics carry no instance-identifying label, so the namespace is the
        // only thing separating two Keycloaks on the same cluster.
        foreach (KeyValuePair<string, string> q in PrometheusService.BuildKeycloakQueries("sso"))
        {
            q.Value.Should().Contain("namespace=\"sso\"", $"query '{q.Key}' must be scoped");
        }
    }

    [Fact]
    public void BuildKeycloakQueries_PutsEveryMatcherInOneBraceBlock()
    {
        // Two adjacent brace blocks are a PromQL parse error and Prometheus rejects the
        // whole request with 400 rather than ignoring the second one.
        foreach (KeyValuePair<string, string> q in PrometheusService.BuildKeycloakQueries("sso"))
        {
            q.Value.Should().NotContain("}{", $"query '{q.Key}' must use a single matcher block");
        }
    }

    [Fact]
    public void BuildKeycloakQueries_TreatAFailedLoginAsALoginCarryingAnError()
    {
        // Keycloak has no "login_error" event value: a failed login is event="login" with a
        // non-empty error label. Querying login_error matches nothing, and counting every
        // event="login" as a success folds the failures back into the success count.
        Dictionary<string, string> q = PrometheusService.BuildKeycloakQueries("sso");

        q["loginErr"].Should().Contain("event=\"login\"").And.Contain("error!=\"\"");
        q["logins"].Should().Contain("event=\"login\"").And.Contain("error=\"\"");

        string all = string.Join("\n", q.Values);
        all.Should().NotContain("login_error");
    }

    [Fact]
    public void BuildKeycloakQueries_ReducePerJvmMetricsWithoutSummingAcrossReplicas()
    {
        // These are compared against per-instance thresholds. Summing three healthy replicas'
        // GC time crosses a single JVM's threshold while every JVM is fine, and a summed
        // "active" happily exceeds a maxed "peak used", which is impossible on its face.
        Dictionary<string, string> q = PrometheusService.BuildKeycloakQueries("sso");

        foreach (string key in new[] { "gcPause", "gcOverhead", "dbActive", "dbAwait", "dbMaxUsed" })
        {
            q[key].Should().StartWith("max(", $"query '{key}' is a per-pod quantity");
        }

        // The pod with the fewest spare connections is the one about to block.
        q["dbAvail"].Should().StartWith("min(");
    }

    [Fact]
    public void BuildKeycloakQueries_RestrictsHeapToTheHeapArea()
    {
        // jvm_memory_used_bytes covers non-heap pools too; without the area matcher the
        // "heap used" tile silently includes metaspace and code cache.
        PrometheusService.BuildKeycloakQueries("sso")["heapUsed"].Should().Contain("area=\"heap\"");
        PrometheusService.BuildKeycloakQueries("sso")["heapComm"].Should().Contain("area=\"heap\"");
    }

    // ──────── The refusal the tenant-aware path buys ────────

    /// <summary>
    /// A tenant asking about a cluster that is not theirs is refused.
    ///
    /// <para><b>This could not be asserted before.</b> Every method on this service took a cluster
    /// id and no tenant, and the single resolution point was <c>c.Id == clusterId</c> — so there
    /// was nothing to compare the cluster against and a crafted id answered about any cluster in
    /// the installation. <c>GetCnpgClusterMetricsAsync</c> is the first method moved onto the
    /// tenant-scoped path; fifteen still take the old one, which is why this is one test and not
    /// seventeen.</para>
    ///
    /// <para>The sibling assertion below is what stops this passing for the wrong reason: the same
    /// cluster, asked by its own tenant, gets past the resolution and fails later — on the missing
    /// prometheus component rather than on the tenant.</para>
    /// </summary>
    [Fact]
    public async Task GetCnpgClusterMetricsAsync_AnotherTenantsCluster_IsRefused()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();
        Guid stranger = Guid.NewGuid();
        db.Set<Tenant>().Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        KubernetesOperationResult<CnpgMetricsSummary> result =
            await sut.GetCnpgClusterMetricsAsync(stranger, clusterId, "db-1");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found for this tenant",
            "the seam refuses before anything is asked of the cluster");
    }

    [Fact]
    public async Task GetCnpgClusterMetricsAsync_OwnCluster_GetsPastTheTenantCheck()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();

        KubernetesOperationResult<CnpgMetricsSummary> result =
            await sut.GetCnpgClusterMetricsAsync(tenantId, clusterId, "db-1");

        result.IsSuccess.Should().BeFalse("there is no prometheus component on this cluster");
        result.Error.Should().Contain("No prometheus component",
            "which is a different refusal from the tenant one — if both said the same thing, the "
            + "test above would prove nothing");
    }

    // ──────── The aggregate: skipped is not zero ────────

    /// <summary>
    /// An app whose only deployment is on a cluster belonging to someone else must come back a
    /// FAILURE, not a successful set of zeroes.
    ///
    /// <para><b>This is the risk the conversion introduced.</b> <c>AggregateMetricsAsync</c> sums
    /// across a tenant's clusters, so a cluster it cannot reach has to be skipped rather than
    /// abort the whole answer. Skipping is one line; the danger is that skipping every cluster
    /// then returns <c>Success</c> with 0 CPU and 0 pods — and a dashboard cannot tell that from
    /// an app that is genuinely idle. <c>anySuccess</c> is what stops it, and this is the test
    /// that says so.</para>
    /// </summary>
    [Fact]
    public async Task GetAppMetricsAsync_WhenEveryClusterIsSkipped_FailsRatherThanReturningZeroes()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Contoso" };
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "billing" };
        db.Customers.Add(customer);
        db.Apps.Add(app);
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = app.Id, Name = "prod", Type = DeploymentType.Manual,
            EnvironmentId = db.Set<Data.Environment>().First(e => e.TenantId == tenantId).Id,
            ClusterId = clusterId, Namespace = "billing", IsManaged = true,
        });
        await db.SaveChangesAsync();

        // Asked by a tenant that owns neither the app nor the cluster, so every cluster in the
        // fan-out is skipped.
        Guid stranger = Guid.NewGuid();
        db.Set<Tenant>().Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        KubernetesOperationResult<DeploymentMetricsSummary> result =
            await sut.GetAppMetricsAsync(stranger, app.Id);

        result.IsSuccess.Should().BeFalse(
            "every cluster was skipped, and a skipped cluster must not read as a measured zero");
        result.Data.Should().BeNull();
    }

    // ──────── The deployment path: the id is an input, not permission ────────

    /// <summary>
    /// A deployment id belonging to somebody else is refused.
    ///
    /// <para>This method used to take a deployment id and nothing else, load the deployment's
    /// cluster by that id, and read the kubeconfig straight off it — so the id was the whole
    /// authorization. Then the id became an input and the seam compared the cluster against the
    /// asking tenant, which is what this test was written for.</para>
    ///
    /// <para><b>The refusal has since moved one step earlier, and the assertion with it.</b> The
    /// deployment is read through <c>IDeliveryApi</c> now, and every method on that contract is
    /// tenant-scoped — so a stranger does not learn that the deployment exists at all. Both
    /// refusals are correct; this one says less.</para>
    /// </summary>
    [Fact]
    public async Task GetDeploymentMetricsAsync_DeploymentOnAnotherTenantsCluster_IsRefused()
    {
        (Guid tenantId, Guid deploymentId) = await SeedDeploymentAsync();
        Guid stranger = Guid.NewGuid();
        db.Set<Tenant>().Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        KubernetesOperationResult<DeploymentMetricsSummary> result =
            await sut.GetDeploymentMetricsAsync(stranger, deploymentId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found",
            "a deployment that is not this tenant's does not resolve, so the refusal does not "
            + "disclose that it exists");
    }

    [Fact]
    public async Task GetDeploymentMetricsAsync_OwnDeployment_GetsPastTheTenantCheck()
    {
        (Guid tenantId, Guid deploymentId) = await SeedDeploymentAsync();

        KubernetesOperationResult<DeploymentMetricsSummary> result =
            await sut.GetDeploymentMetricsAsync(tenantId, deploymentId);

        result.IsSuccess.Should().BeFalse("there is no prometheus component on this cluster");
        result.Error.Should().Contain("No prometheus component",
            "a different refusal from the tenant one, so the test above is not passing by accident");
    }

    [Fact]
    public async Task GetDeploymentMetricsAsync_UnknownDeployment_SaysSo()
    {
        (Guid tenantId, _) = await SeedDeploymentAsync();

        KubernetesOperationResult<DeploymentMetricsSummary> result =
            await sut.GetDeploymentMetricsAsync(tenantId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Deployment not found");
    }

    /// <summary>A tenant with a reachable cluster and one deployment on it.</summary>
    private async Task<(Guid TenantId, Guid DeploymentId)> SeedDeploymentAsync()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Contoso" };
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "billing" };
        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(), AppId = app.Id, Name = "prod", Type = DeploymentType.Manual,
            EnvironmentId = db.Set<Data.Environment>().First(e => e.TenantId == tenantId).Id,
            ClusterId = clusterId, Namespace = "billing", IsManaged = true,
        };

        db.Customers.Add(customer);
        db.Apps.Add(app);
        db.AppDeployments.Add(deployment);
        await db.SaveChangesAsync();

        return (tenantId, deployment.Id);
    }

    // ──────── The last two: the raw query surface ────────

    /// <summary>
    /// The range query and the label lookup were the last two methods on the old resolution, and
    /// the ones with the widest reach: every dashboard panel, the metrics explorer's dropdowns,
    /// the cost report, the resource-usage sweep and the ingress traffic sparkline go through
    /// them. A cluster id was the whole question each one asked.
    /// </summary>
    [Fact]
    public async Task GetMetricRangeAsync_AnotherTenantsCluster_IsRefused()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();
        Guid stranger = Guid.NewGuid();
        db.Set<Tenant>().Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        KubernetesOperationResult<List<PrometheusTimeSeries>> result =
            await sut.GetMetricRangeAsync(stranger, clusterId, "up", TimeSpan.FromMinutes(5));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found for this tenant");
    }

    [Fact]
    public async Task GetLabelValuesAsync_AnotherTenantsCluster_IsRefused()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();
        Guid stranger = Guid.NewGuid();
        db.Set<Tenant>().Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        KubernetesOperationResult<List<string>> result = await sut.GetLabelValuesAsync(
            stranger, clusterId, "__name__", null, TimeSpan.FromHours(24));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found for this tenant");
    }

    /// <summary>
    /// And the sibling, so neither test above can pass for the wrong reason: the same cluster
    /// asked by its owner gets past the tenant check and fails on the missing component instead.
    /// </summary>
    [Fact]
    public async Task GetMetricRangeAsync_OwnCluster_GetsPastTheTenantCheck()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();

        KubernetesOperationResult<List<PrometheusTimeSeries>> result =
            await sut.GetMetricRangeAsync(tenantId, clusterId, "up", TimeSpan.FromMinutes(5));

        result.IsSuccess.Should().BeFalse("there is no prometheus component on this cluster");
        result.Error.Should().Contain("No prometheus component");
    }

    // ──────── The environment's Metrics tab ────────

    /// <summary>
    /// An app with a deployment in prod on <c>prod-cluster</c> and one in staging on
    /// <c>staging-cluster</c>. Neither cluster has Prometheus, so each cluster's name surfaces in
    /// the answer — which is how these tests see which deployments were counted without a live
    /// Prometheus.
    /// </summary>
    private async Task<(Guid TenantId, Guid AppId, Guid ProdEnvId, Guid StagingEnvId)> SeedAppInTwoEnvironmentsAsync(
        string prodNamespace = "billing")
    {
        (Guid tenantId, Guid prodClusterId) = await SeedReachableClusterAsync();
        Guid prodEnvId = db.Set<Data.Environment>().First(e => e.TenantId == tenantId).Id;

        Data.Environment staging = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "staging" };
        KubernetesCluster stagingCluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, EnvironmentId = staging.Id,
            Name = "staging-cluster", ApiServerUrl = "https://k8s.example.com",
        };
        db.Set<Data.Environment>().Add(staging);
        db.KubernetesClusters.Add(stagingCluster);
        await db.SaveChangesAsync();
        await testDb.SeedKubeconfigAsync(vaultService, tenantId, stagingCluster.Id, TestKubeconfig.Valid);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Contoso" };
        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "billing" };
        db.Customers.Add(customer);
        db.Apps.Add(app);
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = app.Id, Name = "prod", Type = DeploymentType.Manual,
            EnvironmentId = prodEnvId, ClusterId = prodClusterId, Namespace = prodNamespace, IsManaged = true,
        });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = app.Id, Name = "staging", Type = DeploymentType.Manual,
            EnvironmentId = staging.Id, ClusterId = stagingCluster.Id, Namespace = "billing-staging", IsManaged = true,
        });
        await db.SaveChangesAsync();

        return (tenantId, app.Id, prodEnvId, staging.Id);
    }

    /// <summary>
    /// The tab said "aggregate across all deployments in this environment" and summed every
    /// environment of the app. Production's tab must consult production's cluster and nothing else.
    /// </summary>
    [Fact]
    public async Task GetEnvironmentMetricsAsync_AsksOnlyAboutThisEnvironmentsDeployments()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, Guid stagingEnvId) = await SeedAppInTwoEnvironmentsAsync();

        KubernetesOperationResult<AppMetricsOverview> prod =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);
        KubernetesOperationResult<AppMetricsOverview> staging =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, stagingEnvId);

        prod.IsSuccess.Should().BeTrue(prod.Error);
        prod.Data!.ClustersWithoutPrometheus.Should().Equal(["prod-cluster"],
            "staging's deployment is on staging-cluster and is not this environment's");
        staging.Data!.ClustersWithoutPrometheus.Should().Equal(["staging-cluster"]);
    }

    /// <summary>
    /// A cluster with no Prometheus component is said to have none — an empty state naming the
    /// cluster — rather than queried on a guessed service name, which surfaced as a proxy error
    /// about a service nobody installed.
    /// </summary>
    [Fact]
    public async Task GetEnvironmentMetricsAsync_ClusterWithoutPrometheus_IsAnEmptyStateNotAZero()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();

        KubernetesOperationResult<AppMetricsOverview> result =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);

        result.IsSuccess.Should().BeTrue("'Prometheus is not installed' is an answer, not a failure");
        result.Data!.Totals.Should().BeNull("nothing was measured, so there are no numbers to show — not zeros");
        result.Data.Scopes.Should().BeEmpty();
        result.Data.Problems.Should().BeEmpty();
        result.Data.Describe().Should().Be("Prometheus is not installed on prod-cluster.");
    }

    /// <summary>
    /// The single-number callers turn the same answer into a failure that names the cluster: no
    /// totals is never a successful zero.
    /// </summary>
    [Fact]
    public async Task GetAppMetricsAsync_NoPrometheusAnywhere_FailsNamingEachCluster()
    {
        (Guid tenantId, Guid appId, _, _) = await SeedAppInTwoEnvironmentsAsync();

        KubernetesOperationResult<DeploymentMetricsSummary> result = await sut.GetAppMetricsAsync(tenantId, appId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Prometheus is not installed on prod-cluster.")
            .And.Contain("Prometheus is not installed on staging-cluster.");
    }

    /// <summary>
    /// A group whose deployments have no namespace used to be queried with <c>namespace=~""</c>,
    /// which matches no container series and came back as a successful 0 cores, 0 pods.
    /// </summary>
    [Fact]
    public async Task GetEnvironmentMetricsAsync_DeploymentsWithoutANamespace_FailInsteadOfAnsweringZero()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync(prodNamespace: "");

        KubernetesOperationResult<AppMetricsOverview> result =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("prod-cluster: no namespace is recorded");
        result.Error.Should().NotContain("not installed",
            "the namespace is checked first; a message both paths emit would prove nothing");
    }

    /// <summary>
    /// A cluster EntKube cannot reach at all is not a cluster without Prometheus: it is a failure,
    /// and it is not offered as the empty state.
    /// </summary>
    [Fact]
    public async Task GetEnvironmentMetricsAsync_ClusterWithoutKubeconfig_IsAProblemNotAnEmptyState()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();

        KubernetesCluster unreachable = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, EnvironmentId = prodEnvId,
            Name = "dark-cluster", ApiServerUrl = "https://k8s.example.com",
        };
        db.KubernetesClusters.Add(unreachable);
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = appId, Name = "prod-2", Type = DeploymentType.Manual,
            EnvironmentId = prodEnvId, ClusterId = unreachable.Id, Namespace = "billing", IsManaged = true,
        });
        await db.SaveChangesAsync();

        KubernetesOperationResult<AppMetricsOverview> result =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);

        result.IsSuccess.Should().BeFalse("one cluster could not be asked at all, and none answered");
        result.Error.Should().Contain("Prometheus is not installed on prod-cluster.")
            .And.Contain("dark-cluster: ")
            .And.NotContain("Prometheus is not installed on dark-cluster");
    }

    [Fact]
    public async Task GetEnvironmentMetricsAsync_AnotherTenantsApp_ResolvesNothing()
    {
        (_, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();
        Guid stranger = Guid.NewGuid();
        db.Set<Tenant>().Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        await db.SaveChangesAsync();

        KubernetesOperationResult<AppMetricsOverview> result =
            await sut.GetEnvironmentMetricsAsync(stranger, appId, prodEnvId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("No deployments");
        result.Error.Should().NotContain("prod-cluster", "a stranger does not learn which cluster the app runs on");
    }

    [Fact]
    public async Task GetPvcUsageAsync_EmptyNamespaceMatch_IsRefused()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();

        KubernetesOperationResult<List<PvcUsageRow>> result = await sut.GetPvcUsageAsync(tenantId, clusterId, "");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("No namespace");
    }

    // ── The volume queries, through a real HttpClient ───────────────
    //
    // GetPvcUsageAsync reaches Prometheus through the API server's pod proxy on a concrete
    // Kubernetes client, so these drive ReadPvcUsageAsync — what it runs once there — with a
    // stub handler standing in for Prometheus. The remaining one-line wiring (that
    // GetPvcUsageAsync hands its proxy HttpClient to ReadPvcUsageAsync and passes a failure
    // through) is not covered here.

    private const string VolumeOk = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"namespace":"shop","persistentvolumeclaim":"local"},"value":[1732449600,"2048"]}]}}
        """;

    /// <summary>Answers each query by the metric it names: a status and a body, or a 200 with a result.</summary>
    private sealed class StubPrometheus(Func<string, (System.Net.HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            (System.Net.HttpStatusCode status, string body) = answer(query);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static Task<KubernetesOperationResult<List<PvcUsageRow>>> ReadVolumes(
        Func<string, (System.Net.HttpStatusCode, string)> answer, CancellationToken ct = default)
        => PrometheusService.ReadPvcUsageAsync(
            new HttpClient(new StubPrometheus(answer)), "http://prometheus.test", "shop", ct);

    [Fact]
    public async Task Volume_usage_is_read_when_every_volume_query_answers()
    {
        KubernetesOperationResult<List<PvcUsageRow>> result =
            await ReadVolumes(_ => (System.Net.HttpStatusCode.OK, VolumeOk));

        result.IsSuccess.Should().BeTrue();
        result.Data!.Should().ContainSingle(r => r.Claim == "local" && r.Mounted);
    }

    /// <summary>
    /// ⚠️ With the health probe skipped, a Prometheus answering 503 to the mounted query alone
    /// used to be a successful table in which a claim on local-path storage — no kubelet stats —
    /// read "not mounted". The shared helper turned the 503 into an empty vector.
    /// </summary>
    [Fact]
    public async Task A_volume_query_answering_503_fails_the_volume_table_and_is_named()
    {
        KubernetesOperationResult<List<PvcUsageRow>> result = await ReadVolumes(q =>
            q.Contains("kube_pod_spec_volumes_persistentvolumeclaims_info")
                ? (System.Net.HttpStatusCode.ServiceUnavailable, "upstream unavailable")
                : (System.Net.HttpStatusCode.OK, VolumeOk));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("which claims are mounted").And.Contain("503");
    }

    [Fact]
    public async Task Every_volume_query_failing_is_a_failure_not_an_app_without_claims()
    {
        KubernetesOperationResult<List<PvcUsageRow>> result =
            await ReadVolumes(_ => (System.Net.HttpStatusCode.GatewayTimeout, ""));

        result.IsSuccess.Should().BeFalse(
            "the table's empty state says \"No persistent volume claims\", which nobody measured");
    }

    /// <summary>A 200 whose body is a Prometheus error, or not a query result at all, is not "no series".</summary>
    [Theory]
    [InlineData("""{"status":"error","errorType":"execution","error":"query processing would load too many samples"}""", "too many samples")]
    [InlineData("<html>Bad gateway</html>", "not a query result")]
    public async Task A_volume_query_answered_with_an_error_body_is_a_failure(string body, string reason)
    {
        KubernetesOperationResult<List<PvcUsageRow>> result = await ReadVolumes(q =>
            q.Contains("kube_persistentvolumeclaim_resource_requests_storage_bytes")
                ? (System.Net.HttpStatusCode.OK, body)
                : (System.Net.HttpStatusCode.OK, VolumeOk));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("requested size").And.Contain(reason);
    }

    [Fact]
    public async Task Cancelling_the_volume_read_cancels_it_rather_than_reporting_failed_queries()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> read = () => ReadVolumes(_ => (System.Net.HttpStatusCode.OK, VolumeOk), cts.Token);

        await read.Should().ThrowAsync<OperationCanceledException>(
            "the caller gave up; that is not five queries Prometheus failed to answer");
    }

    /// <summary>A tenant with one cluster whose kubeconfig is in the vault.</summary>
    private async Task<(Guid TenantId, Guid ClusterId)> SeedReachableClusterAsync()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Owner", Slug = $"owner-{Guid.NewGuid():N}" };
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "prod" };
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, EnvironmentId = env.Id,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        };

        db.Set<Tenant>().Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        await vaultService.InitializeVaultAsync(tenant.Id);
        await testDb.SeedKubeconfigAsync(vaultService, tenant.Id, cluster.Id, TestKubeconfig.Valid);

        return (tenant.Id, cluster.Id);
    }

    // ──────── A Prometheus EntKube did not install ────────

    /// <summary>Where <see cref="PrometheusConfig"/> looks when the cluster has no component.</summary>
    private const string DefaultNs = "monitoring";
    private const string DefaultService = "prometheus-kube-prometheus-prometheus";

    /// <summary>
    /// A hand-installed <c>prometheus</c> release in <c>monitoring</c> serves
    /// <c>prometheus-kube-prometheus-prometheus:9090</c> and has no EntKube component. The
    /// aggregate used to read it; when it started requiring a component, the customer tab and
    /// <c>GetAppMetricsAsync</c> called every such cluster "Prometheus is not installed".
    /// </summary>
    [Fact]
    public async Task A_cluster_without_a_component_whose_default_service_answers_contributes_like_any_other()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();
        apiServer.Serve(DefaultNs, DefaultService, 9090, scalar: "2");

        KubernetesOperationResult<AppMetricsOverview> env =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);

        env.IsSuccess.Should().BeTrue(env.Error);
        env.Data!.ClustersWithoutPrometheus.Should().BeEmpty("something answered there");
        env.Data.Problems.Should().BeEmpty();
        env.Data.Scopes.Select(s => s.ClusterName).Should().Equal(["prod-cluster"]);
        env.Data.Totals.Should().NotBeNull();
        env.Data.Totals!.CpuCores.Should().Be(2);
        env.Data.Totals.PodCount.Should().Be(2);

        // The single-number path the customer tab shares: both clusters, summed.
        KubernetesOperationResult<DeploymentMetricsSummary> app = await sut.GetAppMetricsAsync(tenantId, appId);

        app.IsSuccess.Should().BeTrue(app.Error);
        app.Data!.CpuCores.Should().Be(4, "prod-cluster and staging-cluster each answered 2");
        app.Data.RestartCount.Should().Be(4);
    }

    /// <summary>
    /// The honest empty state: no component and nothing serving on the default service. Decided by
    /// asking the cluster — not assumed from the missing component, and not reported as a proxy
    /// error about a service nobody installed.
    /// </summary>
    [Fact]
    public async Task A_cluster_without_a_component_whose_default_service_does_not_answer_is_without_prometheus_not_a_problem()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();

        KubernetesOperationResult<AppMetricsOverview> result =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);

        apiServer.Requests.Should().Contain(
            $"/api/v1/namespaces/{DefaultNs}/endpoints/{DefaultService}",
            "the default service is probed before the cluster is called one without Prometheus");
        result.IsSuccess.Should().BeTrue("'Prometheus is not installed' is an answer, not a failure");
        result.Data!.ClustersWithoutPrometheus.Should().Equal(["prod-cluster"]);
        result.Data.Problems.Should().BeEmpty("a failed probe is the empty state, not a proxy error");
        result.Data.Totals.Should().BeNull("nothing answered, so there is nothing to sum — not a zero");
    }

    /// <summary>
    /// Only "the lookup answered and no ready pod is there" means not installed. A cluster whose API
    /// refuses the lookup (403 here; equally an API server that does not answer, or a timeout) has
    /// told us nothing about Prometheus — calling it "not installed" sent the operator to install
    /// one rather than to the credentials.
    /// </summary>
    [Fact]
    public async Task A_cluster_without_a_component_whose_api_refuses_the_probe_is_a_problem_not_without_prometheus()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();
        apiServer.Forbid();

        KubernetesOperationResult<AppMetricsOverview> result =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);

        result.IsSuccess.Should().BeFalse("the only cluster asked could not be asked");
        result.Error.Should().Contain("prod-cluster: ")
            .And.NotContain("Prometheus is not installed on prod-cluster");
    }

    /// <summary>
    /// A scope found on the default service is charted on the default service. Asked with the
    /// component required, the panel's range and volume calls answered "No prometheus component
    /// found" directly beneath the totals that scope had just produced.
    /// </summary>
    [Fact]
    public async Task A_scope_found_on_the_default_service_is_charted_there_too()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();
        apiServer.Serve(DefaultNs, DefaultService, 9090, scalar: "2");

        AppMetricsScope scope = (await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId)).Data!.Scopes.Single();
        scope.ViaDefaultService.Should().BeTrue();

        KubernetesOperationResult<List<PrometheusTimeSeries>> range = await sut.GetMetricRangeAsync(
            tenantId, scope.ClusterId, "up", TimeSpan.FromMinutes(30), requireComponent: !scope.ViaDefaultService);
        range.IsSuccess.Should().BeTrue(range.Error);
        range.Data!.Should().ContainSingle();

        // The guard the flag lifts is still there for everyone else.
        (await sut.GetMetricRangeAsync(tenantId, scope.ClusterId, "up", TimeSpan.FromMinutes(30)))
            .Error.Should().Contain("No prometheus component found");
    }

    /// <summary>
    /// Once the default service has passed its health check there IS a Prometheus there, so a query
    /// that then fails is a problem with it — calling it "not installed" would send the operator to
    /// install something that is already running.
    /// </summary>
    [Fact]
    public async Task A_cluster_without_a_component_whose_default_service_is_healthy_but_fails_the_query_is_a_problem()
    {
        (Guid tenantId, Guid appId, Guid prodEnvId, _) = await SeedAppInTwoEnvironmentsAsync();
        apiServer.Serve(DefaultNs, DefaultService, 9090, scalar: "2", queriesFail: true);

        KubernetesOperationResult<AppMetricsOverview> result =
            await sut.GetEnvironmentMetricsAsync(tenantId, appId, prodEnvId);

        result.IsSuccess.Should().BeFalse("the only cluster asked did not answer");
        result.Error.Should().Contain("prod-cluster: ")
            .And.NotContain("Prometheus is not installed on prod-cluster");
    }

    /// <summary>
    /// A helm upgrade of a running kube-prometheus-stack holds the component at Installing for the
    /// whole <c>--wait</c>, while the previous revision keeps serving. Every single-cluster view
    /// said "No prometheus component found on this cluster." for the length of every upgrade.
    /// </summary>
    [Fact]
    public async Task A_single_cluster_query_during_a_prometheus_upgrade_still_reaches_prometheus()
    {
        (Guid tenantId, Guid clusterId) = await SeedReachableClusterAsync();
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId, Name = "kube-prometheus-stack", ComponentType = "HelmChart",
            HelmChartName = "kube-prometheus-stack", ReleaseName = "prom", Namespace = "monitoring",
            Status = ComponentStatus.Installing,
            InstalledAt = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        apiServer.Serve("monitoring", "prom-prometheus", 9090, scalar: "1");

        KubernetesOperationResult<List<PrometheusTimeSeries>> result =
            await sut.GetMetricRangeAsync(tenantId, clusterId, "up", TimeSpan.FromMinutes(5));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Data.Should().ContainSingle("the range came from the release's own service, prom-prometheus");
    }

    /// <summary>
    /// The API server, in process. Serves at most one Prometheus — a Service with one ready pod,
    /// its <c>/-/healthy</c>, and instant/range queries through the pod proxy — and answers 404 to
    /// everything else, which is what a cluster without that Service says.
    /// </summary>
    private sealed class FakeApiServer
    {
        private const string Pod = "prom-0";
        private (string Ns, string Service, int Port, string Scalar, bool QueriesFail)? served;
        private bool forbidden;

        /// <summary>Every path asked, in order, without the query string.</summary>
        public List<string> Requests { get; } = [];

        public void Serve(string ns, string service, int port, string scalar, bool queriesFail = false)
            => served = (ns, service, port, scalar, queriesFail);

        /// <summary>Every request refused with 403, as for a kubeconfig without endpoints access.</summary>
        public void Forbid() => forbidden = true;

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Requests.Add(path);

            if (forbidden) return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"Forbidden","code":403}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };

            if (path.EndsWith("/endpoints", StringComparison.Ordinal))
                return Json("""{"apiVersion":"v1","kind":"EndpointsList","metadata":{},"items":[]}""");

            if (served is not { } s) return NotFound();

            if (path == $"/api/v1/namespaces/{s.Ns}/endpoints/{s.Service}")
            {
                return Json($$$"""
                    {"apiVersion":"v1","kind":"Endpoints","metadata":{"name":"{{{s.Service}}}","namespace":"{{{s.Ns}}}"},
                     "subsets":[{"addresses":[{"ip":"10.0.0.9","targetRef":{"kind":"Pod","name":"{{{Pod}}}","namespace":"{{{s.Ns}}}"}}],
                                 "ports":[{"port":{{{s.Port}}},"protocol":"TCP"}]}]}
                    """);
            }

            string proxy = $"/api/v1/namespaces/{s.Ns}/pods/{Pod}:{s.Port}/proxy";
            if (path == $"{proxy}/-/healthy") return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Prometheus Server is Healthy."),
            };

            if (path == $"{proxy}/api/v1/query" || path == $"{proxy}/api/v1/query_range")
            {
                if (s.QueriesFail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);

                return Json(path.EndsWith("_range", StringComparison.Ordinal)
                    ? $$$"""{"status":"success","data":{"resultType":"matrix","result":[{"metric":{"__name__":"up"},"values":[[1764000000,"{{{s.Scalar}}}"]]}]}}"""
                    : $$$"""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1764000000,"{{{s.Scalar}}}"]}]}}""");
            }

            return NotFound();
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// The last handler in an SDK client's chain: answers from <see cref="FakeApiServer"/> and never
    /// calls through, so nothing leaves the process. One per client, because a handler's inner
    /// handler cannot be set again once it has been used.
    /// </summary>
    private sealed class AnsweredBy(FakeApiServer server) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response = server.Answer(request);

            // The SDK builds its result (and its HttpOperationException) from the response's
            // request; a real handler always sets it, and without it every call failed with
            // "Value cannot be null (Parameter 'httpRequest')".
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// The real factory's resolution — so tenant scoping and the kubeconfig gate are the
    /// production ones — with only the SDK client swapped for one that talks to
    /// <see cref="FakeApiServer"/>. That is the one member PrometheusService uses.
    /// </summary>
    private sealed class SdkClientsAnsweredBy(
        EntKube.Web.Services.Clusters.IClusterClientFactory real, FakeApiServer server)
        : EntKube.Web.Services.Clusters.IClusterClientFactory
    {
        public async Task<EntKube.Web.Services.Clusters.IClusterClient?> ForAsync(
            Guid tenantId, Guid clusterId, CancellationToken ct = default)
        {
            EntKube.Web.Services.Clusters.IClusterClient? resolved = await real.ForAsync(tenantId, clusterId, ct);
            if (resolved is null) return null;

            Mock<EntKube.Web.Services.Clusters.IClusterClient> client = new(MockBehavior.Strict);
            client.SetupGet(c => c.ClusterId).Returns(resolved.ClusterId);
            client.SetupGet(c => c.ClusterName).Returns(resolved.ClusterName);
            client.Setup(c => c.GetPooledSdkClient()).Returns(() => new k8s.Kubernetes(
                new k8s.KubernetesClientConfiguration { Host = "https://k8s.example.com" },
                new AnsweredBy(server)));
            return client.Object;
        }
    }
}
