using System.Net;
using System.Text;
using EntKube.Contracts.Catalog;
using EntKube.Contracts.Delivery;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// A cluster with a Prometheus on it, in process: the API server's answers by path, and
/// Prometheus's own answers through the pod proxy. Nothing leaves the process.
///
/// <para>It answers 404 to every path nobody served. That is what a cluster without the resource
/// says, so a test has to state each thing it wants to be there.</para>
/// </summary>
internal sealed class ScrapedClusterFake
{
    public const string PrometheusNamespace = "monitoring";
    private const string Proxy = "/api/v1/namespaces/monitoring/pods/prom-0:9090/proxy";

    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> answers = new(StringComparer.Ordinal);

    /// <summary>What Prometheus answers to a PromQL query. Nothing has any series unless a test says so.</summary>
    public Func<string, (HttpStatusCode Status, string Body)> Query { get; set; } = _ => NoSeries;

    /// <summary>What Prometheus answers on <c>/api/v1/targets</c>.</summary>
    public string Targets { get; set; } = """{"status":"success","data":{"activeTargets":[]}}""";

    /// <summary>A path whose request never comes back, as when the HTTP client gives up on it.</summary>
    public string? Hanging { get; set; }

    /// <summary>The vault (or database) cannot be read, so the cluster does not even resolve.</summary>
    public bool VaultIsDown { get; set; }

    /// <summary>Every path the API server was asked for.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Every PromQL query Prometheus was asked.</summary>
    public List<string> Queries { get; } = [];

    public Guid TenantId { get; } = Guid.NewGuid();

    public Guid ClusterId { get; } = Guid.NewGuid();

    public PrometheusService Prometheus { get; }

    public static (HttpStatusCode, string) NoSeries =>
        (HttpStatusCode.OK, """{"status":"success","data":{"resultType":"vector","result":[]}}""");

    /// <summary>One series with the given labels (JSON members) and value.</summary>
    public static (HttpStatusCode, string) Series(string value, string labels = "") =>
        (HttpStatusCode.OK, "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{"
            + labels + "},\"value\":[1764000000,\"" + value + "\"]}]}}");

    public ScrapedClusterFake()
    {
        Mock<IClusterClient> client = new(MockBehavior.Strict);
        client.SetupGet(c => c.ClusterId).Returns(ClusterId);
        client.SetupGet(c => c.ClusterName).Returns("prod");
        client.Setup(c => c.GetPooledSdkClient()).Returns(() => new k8s.Kubernetes(
            new k8s.KubernetesClientConfiguration { Host = "https://k8s.example.com" }, new AnsweredBy(this)));

        Mock<IClusterClientFactory> clusters = new();
        clusters.Setup(c => c.ForAsync(TenantId, ClusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => VaultIsDown ? throw new InvalidOperationException("the vault is sealed") : client.Object);

        Mock<ICatalogApi> catalog = new();
        catalog.Setup(c => c.FindPrometheusAsync(TenantId, ClusterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstalledComponent(
                Guid.NewGuid(), TenantId, ClusterId, "prod", Guid.NewGuid(),
                "kube-prometheus-stack", "HelmChart", ComponentStatus.Installed, PrometheusNamespace,
                "prom", "kube-prometheus-stack", null, null, null, null, DateTime.UnixEpoch));

        Prometheus = new PrometheusService(
            new Mock<IDeliveryApi>().Object, clusters.Object, catalog.Object,
            new PromQueryCache(), NullLogger<PrometheusService>.Instance);

        Serve($"/api/v1/namespaces/{PrometheusNamespace}/endpoints/prom-prometheus", """
            {"apiVersion":"v1","kind":"Endpoints","metadata":{"name":"prom-prometheus","namespace":"monitoring"},
             "subsets":[{"addresses":[{"ip":"10.0.0.9","targetRef":{"kind":"Pod","name":"prom-0","namespace":"monitoring"}}],
                         "ports":[{"port":9090,"protocol":"TCP"}]}]}
            """);
    }

    public void Serve(string path, string body) => answers[path] = (HttpStatusCode.OK, body);

    public void Refuse(string path, HttpStatusCode status) =>
        answers[path] = (status, $$"""{"kind":"Status","apiVersion":"v1","status":"Failure","code":{{(int)status}}}""");

    // ──────── The objects the scrape path is made of ────────

    public const string PrometheusesPath = "/apis/monitoring.coreos.com/v1/prometheuses";
    public const string PodMonitorsPath = "/apis/monitoring.coreos.com/v1/podmonitors";
    public const string ServiceMonitorsPath = "/apis/monitoring.coreos.com/v1/servicemonitors";

    public void ServeList(string path, params string[] items) =>
        Serve(path, "{\"apiVersion\":\"v1\",\"kind\":\"List\",\"metadata\":{},\"items\":[" + string.Join(",", items) + "]}");

    /// <summary>The component's own Prometheus, reading every monitor in every namespace.</summary>
    public static string ReadsEverything => Prom("prom-prometheus", """
        "podMonitorSelector":{},"podMonitorNamespaceSelector":{},"serviceMonitorSelector":{},"serviceMonitorNamespaceSelector":{}
        """);

    public static string Prom(string name, string spec, string ns = PrometheusNamespace) =>
        $$$"""{"metadata":{"name":"{{{name}}}","namespace":"{{{ns}}}"},"spec":{{{{spec}}}}}""";

    public static string PodMonitor(
        string ns, string name, string selector, string labels = "", string endpoints = """{"port":"metrics"}""", string more = "") =>
        $$$"""{"metadata":{"name":"{{{name}}}","namespace":"{{{ns}}}","labels":{{{{labels}}}}},"spec":{"selector":{{{selector}}},"podMetricsEndpoints":[{{{endpoints}}}]{{{more}}}}}""";

    public static string ServiceMonitor(
        string ns, string name, string selector, string labels = "", string endpoints = """{"port":"prometheus"}""", string more = "") =>
        $$$"""{"metadata":{"name":"{{{name}}}","namespace":"{{{ns}}}","labels":{{{{labels}}}}},"spec":{"selector":{{{selector}}},"endpoints":[{{{endpoints}}}]{{{more}}}}}""";

    public static string Labelled(string ns, string name, string labels) =>
        $$$"""{"metadata":{"name":"{{{name}}}","namespace":"{{{ns}}}","labels":{{{{labels}}}} }}""";

    /// <summary>The instance pods of one CNPG database, as the operator labels them.</summary>
    public void ServeDatabasePods(string ns, string database, int instances = 1) =>
        ServeList($"/api/v1/namespaces/{ns}/pods", [.. Enumerable.Range(1, instances).Select(i =>
            Labelled(ns, $"{database}-{i}", $"\"cnpg.io/cluster\":\"{database}\",\"cnpg.io/podRole\":\"instance\",\"team\":\"shop\""))]);

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);

        if (path == $"{Proxy}/-/healthy") return Reply(HttpStatusCode.OK, "Prometheus Server is Healthy.");
        if (path == $"{Proxy}/api/v1/targets") return Reply(HttpStatusCode.OK, Targets);
        if (path == $"{Proxy}/api/v1/query")
        {
            string query = Uri.UnescapeDataString(request.RequestUri.Query["?query=".Length..]);
            lock (Queries) Queries.Add(query);
            (HttpStatusCode status, string body) = Query(query);
            return Reply(status, body);
        }

        lock (Requests) Requests.Add(path);
        return answers.TryGetValue(path, out (HttpStatusCode Status, string Body) served)
            ? Reply(served.Status, served.Body)
            : Reply(HttpStatusCode.NotFound, """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""");
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The last handler in an SDK client's chain. One per client: a handler's inner handler cannot be set twice.</summary>
    private sealed class AnsweredBy(ScrapedClusterFake cluster) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == cluster.Hanging)
                throw new TaskCanceledException("A task was canceled.");

            HttpResponseMessage response = cluster.Answer(request);
            response.RequestMessage = request;   // the SDK builds its exceptions from it
            return Task.FromResult(response);
        }
    }
}
