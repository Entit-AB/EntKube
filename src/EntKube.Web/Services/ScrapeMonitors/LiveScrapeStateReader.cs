using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Services.Clusters;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Reads a cluster for the monitor reconcile, in two halves because what is scraped has to be read
/// before the database and what runs can only be read after it (that is when the namespaces are
/// known).
///
/// <para>Both halves read everything or throw. There is one exception, and it is not a failure:
/// a cluster that does not have the monitor kinds at all.</para>
/// </summary>
public interface ILiveScrapeStateReader
{
    /// <summary>
    /// The Prometheus objects, every monitor and Prometheus's own targets; or null when the cluster
    /// does not know the monitor kinds, so there is nothing to write and nothing to remove.
    /// </summary>
    Task<LiveMonitoring?> ReadMonitoringAsync(Guid tenantId, IClusterClient cluster, CancellationToken ct);

    /// <summary>Every namespace's labels, and what runs in the ones named.</summary>
    Task<LiveWorkloads> ReadWorkloadsAsync(
        IClusterClient cluster, IReadOnlyCollection<string> namespaces, CancellationToken ct);
}

/// <summary>
/// <see cref="ILiveScrapeStateReader"/> over the Kubernetes SDK.
///
/// <para><b>Why not the helpers that already list these objects.</b> They answer an empty list for a
/// refusal, a timeout and a cluster without the kind alike, which is right for a page that shows
/// what it can and wrong here: an empty list of monitors means "create them all", and an empty list
/// of pods means "this is not running". The SDK says which it was, and stops when it is told to.</para>
/// </summary>
/// <param name="prometheus">Asks Prometheus what it scrapes.</param>
/// <param name="time">
/// The clock the answer is stamped with; the system's unless a test says otherwise. It is not
/// registered as a service, so the container leaves it to the default.
/// </param>
public sealed class LiveScrapeStateReader(PrometheusService prometheus, TimeProvider? time = null)
    : ILiveScrapeStateReader
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private const string Group = "monitoring.coreos.com";
    private const string Version = "v1";

    /// <summary>How many objects are asked for at a time. The rest come in further pages, to the last.</summary>
    private const int PageSize = 250;

    public async Task<LiveMonitoring?> ReadMonitoringAsync(
        Guid tenantId, IClusterClient cluster, CancellationToken ct)
    {
        Kubernetes k8s = cluster.GetPooledSdkClient();

        IReadOnlyList<JsonObject>? prometheuses = await ListIfKnownAsync(k8s, "prometheuses", ct);
        if (prometheuses is null) return null;

        IReadOnlyList<JsonObject>? serviceMonitors = await ListIfKnownAsync(k8s, "servicemonitors", ct);
        if (serviceMonitors is null) return null;

        IReadOnlyList<JsonObject>? podMonitors = await ListIfKnownAsync(k8s, "podmonitors", ct);
        if (podMonitors is null) return null;

        string targets = await prometheus.ReadActiveTargetsAsync(tenantId, cluster.ClusterId, ct);
        DateTimeOffset readAt = clock.GetUtcNow();

        return new LiveMonitoring(
            [.. prometheuses.Select(ReadPrometheus)],
            [
                .. serviceMonitors.Select(m => ReadMonitor(MonitorKind.ServiceMonitor, m)),
                .. podMonitors.Select(m => ReadMonitor(MonitorKind.PodMonitor, m)),
            ],
            ReadTargets(targets),
            readAt);
    }

    public async Task<LiveWorkloads> ReadWorkloadsAsync(
        IClusterClient cluster, IReadOnlyCollection<string> namespaces, CancellationToken ct)
    {
        Kubernetes k8s = cluster.GetPooledSdkClient();

        IReadOnlyList<V1Namespace> all = await EveryPageAsync<V1Namespace>(async next =>
        {
            V1NamespaceList page = await k8s.CoreV1.ListNamespaceAsync(
                limit: PageSize, continueParameter: next, cancellationToken: ct);
            return (page.Items, page.Metadata?.ContinueProperty);
        });

        // A namespace on its way out takes no new objects, and an apply into it fails the whole set.
        Dictionary<string, IReadOnlyDictionary<string, string>> labels = all
            .Where(n => n.Metadata.DeletionTimestamp is null && n.Status?.Phase != "Terminating")
            .ToDictionary(n => n.Metadata.Name, n => ScrapedEndpoints.AsReadOnly(n.Metadata.Labels), StringComparer.Ordinal);

        LiveNamespace[] contents = await Task.WhenAll(namespaces
            .Distinct(StringComparer.Ordinal)
            .Where(labels.ContainsKey)
            .Select(ns => ReadNamespaceAsync(k8s, ns, ct)));

        return new LiveWorkloads(labels, contents.ToDictionary(n => n.Name, StringComparer.Ordinal));
    }

    private static async Task<LiveNamespace> ReadNamespaceAsync(Kubernetes k8s, string ns, CancellationToken ct)
    {
        IReadOnlyList<V1Pod> pods = await EveryPageAsync<V1Pod>(async next =>
        {
            V1PodList page = await k8s.CoreV1.ListNamespacedPodAsync(
                ns, limit: PageSize, continueParameter: next, cancellationToken: ct);
            return (page.Items, page.Metadata?.ContinueProperty);
        });

        IReadOnlyList<V1Service> services = await EveryPageAsync<V1Service>(async next =>
        {
            V1ServiceList page = await k8s.CoreV1.ListNamespacedServiceAsync(
                ns, limit: PageSize, continueParameter: next, cancellationToken: ct);
            return (page.Items, page.Metadata?.ContinueProperty);
        });

        IReadOnlyList<V1NetworkPolicy> policies = await EveryPageAsync<V1NetworkPolicy>(async next =>
        {
            V1NetworkPolicyList page = await k8s.NetworkingV1.ListNamespacedNetworkPolicyAsync(
                ns, limit: PageSize, continueParameter: next, cancellationToken: ct);
            return (page.Items, page.Metadata?.ContinueProperty);
        });

        return new LiveNamespace(ns, pods, services, policies);
    }

    /// <summary>
    /// Every object of a monitor kind, or null when the API server answers that it has no such kind.
    /// Only that answer: refused, timed out and unreachable are exceptions, as they are elsewhere.
    /// </summary>
    private static async Task<IReadOnlyList<JsonObject>?> ListIfKnownAsync(
        Kubernetes k8s, string plural, CancellationToken ct)
    {
        try
        {
            return await EveryPageAsync<JsonObject>(async next =>
            {
                object raw = await k8s.CustomObjects.ListCustomObjectForAllNamespacesAsync(
                    Group, Version, plural, limit: PageSize, continueParameter: next, cancellationToken: ct);

                JsonNode? list = JsonNode.Parse(JsonSerializer.Serialize(raw));

                return list?["items"] is JsonArray items
                    ? (items.Select(i => i as JsonObject ?? throw Unreadable(plural)), TextOf(list["metadata"]?["continue"]))
                    : throw Unreadable(plural);
            });
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Asks for page after page until the server says there are no more.</summary>
    private static async Task<IReadOnlyList<T>> EveryPageAsync<T>(
        Func<string?, Task<(IEnumerable<T> Items, string? Next)>> readPage)
    {
        List<T> all = [];
        string? next = null;

        do
        {
            (IEnumerable<T> items, string? following) = await readPage(next);
            all.AddRange(items);
            next = string.IsNullOrEmpty(following) ? null : following;
        }
        while (next is not null);

        return all;
    }

    // ── What the objects say ──────────────────────────────────────────────────

    private static LivePrometheus ReadPrometheus(JsonObject item)
        => new(NamespaceOf(item), NameOf(item), Pairs(item["metadata"]?["labels"]), SpecOf(item));

    private static LiveMonitor ReadMonitor(MonitorKind kind, JsonObject item)
        => new(
            new MonitorIdentity(kind, NamespaceOf(item), NameOf(item)),
            Pairs(item["metadata"]?["labels"]),
            Pairs(item["metadata"]?["annotations"]),
            SpecOf(item),
            CreatedOf(item),
            ChangedOf(item),
            GenerationOf(item));

    /// <summary>
    /// When the API server made it. It always says; one that says something that is not a time is an
    /// answer that cannot be read, not a monitor of no age.
    /// </summary>
    private static DateTimeOffset? CreatedOf(JsonObject item)
    {
        JsonNode? created = item["metadata"]?["creationTimestamp"];
        return created is null ? null : TimeOf(created);
    }

    /// <summary>
    /// When anybody last wrote it: the latest time in the API server's record of its writers
    /// (<c>metadata.managedFields</c>), leaving out what was written to its status, which is the operator
    /// reporting on it and changes nothing it selects. Null where there is no record, or no time in it.
    /// A record that is there and cannot be read throws, like any other answer that cannot be.
    /// </summary>
    private static DateTimeOffset? ChangedOf(JsonObject item)
    {
        JsonNode? record = item["metadata"]?["managedFields"];
        if (record is null) return null;
        if (record is not JsonArray writers) throw Unreadable("monitors");

        DateTimeOffset? latest = null;
        foreach (JsonNode? writer in writers)
        {
            if (writer is not JsonObject entry) throw Unreadable("monitors");
            if (TextOf(entry["subresource"]) is { Length: > 0 }) continue;
            if (entry["time"] is not { } time) continue;

            DateTimeOffset at = TimeOf(time);
            if (latest is null || at > latest) latest = at;
        }

        return latest;
    }

    /// <summary>How many times its spec has been written; null where it was not said.</summary>
    private static long? GenerationOf(JsonObject item)
    {
        JsonNode? generation = item["metadata"]?["generation"];
        if (generation is null) return null;

        return generation is JsonValue value && value.GetValueKind() == JsonValueKind.Number
               && value.TryGetValue(out long number)
            ? number
            : throw Unreadable("monitors");
    }

    private static DateTimeOffset TimeOf(JsonNode node)
        => TextOf(node) is { } text && DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
            ? at
            : throw Unreadable("monitors");

    /// <summary>
    /// Prometheus's own list of what it scrapes. Every part of it has to be there: this is what says
    /// nothing else scrapes a pod, and a target skipped for being odd would say so of one that is.
    /// </summary>
    public static IReadOnlyList<ActiveTarget> ReadTargets(string json)
    {
        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Prometheus's list of targets is not JSON.", ex);
        }

        if (answer is not JsonObject root
            || TextOf(root["status"]) != "success"
            || root["data"] is not JsonObject data
            || data["activeTargets"] is not JsonArray targets)
        {
            throw Unreadable("targets");
        }

        List<ActiveTarget> read = [];

        foreach (JsonNode? target in targets)
        {
            if (target is not JsonObject found
                || TextOf(found["scrapePool"]) is not { Length: > 0 } pool
                || !Uri.TryCreate(TextOf(found["scrapeUrl"]), UriKind.Absolute, out Uri? url))
            {
                throw Unreadable("targets");
            }

            read.Add(new ActiveTarget(
                pool, url.Host.Trim('[', ']'), url.Port, url.AbsolutePath, TextOf(found["health"]) == "up",
                url.Query));
        }

        return read;
    }

    private static FormatException Unreadable(string what)
        => new($"The cluster's answer about its {what} could not be read.");

    private static string NameOf(JsonObject item)
        => TextOf(item["metadata"]?["name"]) ?? throw Unreadable("monitors");

    private static string NamespaceOf(JsonObject item)
        => TextOf(item["metadata"]?["namespace"]) ?? throw Unreadable("monitors");

    private static JsonObject SpecOf(JsonObject item)
        => item["spec"] as JsonObject ?? throw Unreadable("monitors");

    private static IReadOnlyDictionary<string, string> Pairs(JsonNode? node)
    {
        Dictionary<string, string> pairs = new(StringComparer.Ordinal);

        foreach ((string key, JsonNode? value) in node as JsonObject ?? [])
        {
            pairs[key] = TextOf(value) ?? throw Unreadable("monitors");
        }

        return pairs;
    }

    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
