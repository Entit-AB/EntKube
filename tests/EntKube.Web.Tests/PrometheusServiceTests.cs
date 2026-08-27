using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly PrometheusService sut;

    public PrometheusServiceTests()
    {
        // Mirrors production: contexts resolve cluster.Kubeconfig from the vault via the interceptor.
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vaultService = testDb.CreateVaultService();

        sut = new PrometheusService(
            testDb.Factory,
            new KubernetesProxyClientPool(NullLogger<KubernetesProxyClientPool>.Instance),
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
            await sut.GetClusterHealthAsync(nonExistentId);

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
            await sut.GetClusterHealthAsync(cluster.Id);

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
            await sut.GetClusterHealthAsync(cluster.Id);

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
}
