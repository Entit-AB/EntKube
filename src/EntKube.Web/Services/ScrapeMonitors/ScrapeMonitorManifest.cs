using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>How a live monitor compares with the one it should be.</summary>
public enum MonitorDifference
{
    /// <summary>It already says everything it should.</summary>
    None,

    /// <summary>An apply makes it right.</summary>
    Apply,

    /// <summary>
    /// Its selector says more than it should, and an apply cannot take that away: kubectl only
    /// removes a field it remembers having written. It has to be deleted and created again.
    /// </summary>
    Replace,
}

/// <summary>
/// The monitors EntKube writes: what one looks like, whether a live one already is that, and the
/// text kubectl is handed.
/// </summary>
public static partial class ScrapeMonitorManifest
{
    /// <summary>The label that makes a monitor EntKube's, carrying the id of the cluster it was written for.</summary>
    /// <remarks>
    /// The id and not just "yes": one physical cluster can be registered under two tenants, or in two
    /// EntKube installations, and each would otherwise prune the other's monitors.
    /// </remarks>
    public const string OwnershipLabel = "entkube.io/scrape-monitor";

    /// <summary>What the monitor is for: the target's key, for a person reading the object.</summary>
    public const string OwnerAnnotation = "entkube.io/scrape-owner";

    private const string ApiVersion = "monitoring.coreos.com/v1";

    private static readonly JsonSerializerOptions Quoted = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The monitor as it should be.</summary>
    /// <param name="monitor">What it selects and scrapes.</param>
    /// <param name="owner">The target's key.</param>
    /// <param name="labels">Every label it carries: ownership, and what Prometheus selects by.</param>
    /// <param name="overTls">Whether an endpoint is scraped over TLS, which a live pod may decide.</param>
    public static JsonObject Build(
        MonitorSpec monitor, string owner, IReadOnlyDictionary<string, string> labels,
        Func<ScrapeEndpointSpec, bool> overTls)
    {
        JsonObject labelNode = [];
        foreach ((string key, string value) in labels) labelNode[key] = value;

        JsonArray endpoints = [];
        foreach (ScrapeEndpointSpec endpoint in monitor.Endpoints) endpoints.Add(Endpoint(endpoint, overTls(endpoint)));

        return new JsonObject
        {
            ["apiVersion"] = ApiVersion,
            ["kind"] = monitor.Identity.Kind.ToString(),
            ["metadata"] = new JsonObject
            {
                ["name"] = monitor.Identity.Name,
                ["namespace"] = monitor.Identity.Namespace,
                ["labels"] = labelNode,
                ["annotations"] = new JsonObject { [OwnerAnnotation] = owner },
            },
            ["spec"] = new JsonObject
            {
                ["selector"] = monitor.Selector.ToJson(),
                [EndpointsField(monitor.Identity.Kind)] = endpoints,
            },
        };
    }

    /// <summary>
    /// Whether <paramref name="live"/> already says everything <paramref name="desired"/> does.
    ///
    /// <para><b>Structure, not text.</b> The API server fills in defaults the manifest leaves out
    /// (every relabelling gets <c>action: replace</c>), so an equality check would find a difference
    /// on every pass and write an audit row for each, for ever. Desired has to be <em>contained</em>
    /// in live: every key with its value, lists of the same length element by element. An extra
    /// endpoint is a longer list and is noticed; an extra default is not.</para>
    ///
    /// <para><b>The selectors are the exception: nothing may be added to those.</b> A key added to
    /// <c>matchLabels</c> by hand leaves everything desired in place and selects nothing, so it is
    /// the one edit that stops the scraping without looking like a difference. The API server
    /// defaults nothing inside them.</para>
    /// </summary>
    public static MonitorDifference Compare(JsonObject desired, LiveMonitor live)
    {
        JsonNode? spec = desired["spec"];

        if (NarrowedByHand(LabelSelector.From(spec?["selector"])!, LabelSelector.From(live.Spec["selector"]))
            || live.Spec["namespaceSelector"] is JsonObject { Count: > 0 })
        {
            return MonitorDifference.Replace;
        }

        bool same = Contains(AsNode(live.Labels), desired["metadata"]?["labels"])
            && Contains(AsNode(live.Annotations), desired["metadata"]?["annotations"])
            && Contains(live.Spec, spec);

        return same ? MonitorDifference.None : MonitorDifference.Apply;
    }

    /// <summary>One input for <c>kubectl apply -f</c> holding all of them.</summary>
    public static string Render(IEnumerable<JsonObject> monitors)
        => string.Join("---\n", monitors.Select(m => Yaml(m)));

    /// <summary>One input for <c>kubectl delete -f</c> naming all of them and nothing else.</summary>
    public static string RenderNames(IEnumerable<MonitorIdentity> monitors)
        => Render(monitors.Select(m => new JsonObject
        {
            ["apiVersion"] = ApiVersion,
            ["kind"] = m.Kind.ToString(),
            ["metadata"] = new JsonObject { ["name"] = m.Name, ["namespace"] = m.Namespace },
        }));

    /// <summary>Where each kind keeps its endpoints.</summary>
    private static string EndpointsField(MonitorKind kind)
        => kind == MonitorKind.PodMonitor ? "podMetricsEndpoints" : "endpoints";

    private static JsonObject Endpoint(ScrapeEndpointSpec endpoint, bool overTls)
    {
        JsonObject json = [];

        if (endpoint.Port is not null) json["port"] = endpoint.Port;
        // The deprecated field, on purpose: portNumber, which replaces it, is missing from the CRDs
        // of the older stacks still out there, and this one works on both.
        else json["targetPort"] = endpoint.ContainerPort;

        json["path"] = endpoint.Path;
        json["scheme"] = overTls ? "https" : "http";

        if (overTls) json["tlsConfig"] = new JsonObject { ["insecureSkipVerify"] = true };
        if (endpoint.BearerTokenFile is not null) json["bearerTokenFile"] = endpoint.BearerTokenFile;
        if (endpoint.HonorLabels) json["honorLabels"] = true;

        if (endpoint.Relabelings is { Count: > 0 } relabelings)
        {
            JsonArray rules = [];
            foreach (ScrapeRelabeling relabeling in relabelings)
            {
                JsonObject rule = [];

                if (relabeling.Action is not null) rule["action"] = relabeling.Action;
                if (relabeling.Regex is not null) rule["regex"] = relabeling.Regex;

                if (relabeling.SourceLabels is { } sources)
                {
                    JsonArray names = [];
                    foreach (string source in sources) names.Add(source);
                    rule["sourceLabels"] = names;
                }

                if (relabeling.TargetLabel is not null) rule["targetLabel"] = relabeling.TargetLabel;

                rules.Add(rule);
            }

            json["relabelings"] = rules;
        }

        return json;
    }

    /// <summary>Whether every part of <paramref name="desired"/> is in <paramref name="live"/>.</summary>
    private static bool Contains(JsonNode? live, JsonNode? desired)
    {
        switch (desired)
        {
            case null:
                return true;

            case JsonObject pairs:
                return live is JsonObject have
                    && pairs.All(p => have.TryGetPropertyValue(p.Key, out JsonNode? value) && Contains(value, p.Value));

            case JsonArray items:
                return live is JsonArray list
                    && list.Count == items.Count
                    && items.Zip(list).All(pair => Contains(pair.Second, pair.First));

            default:
                return JsonNode.DeepEquals(live, desired);
        }
    }

    /// <summary>
    /// Whether the live selector asks for something the desired one does not mention at all, which
    /// an apply leaves in place. A requirement that only CHANGED is not this: a label's new value
    /// replaces the old one, and a list of expressions is replaced whole.
    /// </summary>
    private static bool NarrowedByHand(LabelSelector desired, LabelSelector? live)
        => live is not null
           && (live.MatchLabels.Keys.Except(desired.MatchLabels.Keys, StringComparer.Ordinal).Any()
               || (desired.MatchExpressions.Count == 0 && live.MatchExpressions.Count > 0));

    private static JsonObject AsNode(IReadOnlyDictionary<string, string> pairs)
    {
        JsonObject node = [];
        foreach ((string key, string value) in pairs) node[key] = value;
        return node;
    }

    // ── YAML ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The document as YAML, with every string in double quotes. A label value of true or 3 has to
    /// reach the API server as the string it is, and YAML reads it as something else unquoted.
    /// </summary>
    private static string Yaml(JsonObject document)
    {
        StringBuilder text = new();
        WriteMap(text, document, 0);
        return text.ToString();
    }

    private static void WriteMap(StringBuilder text, JsonObject map, int indent)
    {
        foreach ((string key, JsonNode? value) in map)
        {
            text.Append(' ', indent);
            WriteEntry(text, key, value, indent);
        }
    }

    private static void WriteEntry(StringBuilder text, string key, JsonNode? value, int indent)
    {
        text.Append(PlainKey().IsMatch(key) ? key : JsonSerializer.Serialize(key, Quoted)).Append(':');

        switch (value)
        {
            case JsonObject { Count: > 0 } map:
                text.Append('\n');
                WriteMap(text, map, indent + 2);
                break;

            case JsonArray { Count: > 0 } list:
                text.Append('\n');
                WriteList(text, list, indent + 2);
                break;

            default:
                text.Append(' ').Append(Scalar(value)).Append('\n');
                break;
        }
    }

    private static void WriteList(StringBuilder text, JsonArray list, int indent)
    {
        foreach (JsonNode? item in list)
        {
            text.Append(' ', indent).Append("- ");

            if (item is JsonObject { Count: > 0 } map)
            {
                bool first = true;
                foreach ((string key, JsonNode? value) in map)
                {
                    if (!first) text.Append(' ', indent + 2);
                    WriteEntry(text, key, value, indent + 2);
                    first = false;
                }
            }
            else
            {
                text.Append(Scalar(item)).Append('\n');
            }
        }
    }

    private static string Scalar(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "{}",
        JsonArray => "[]",
        _ => value.ToJsonString(Quoted),
    };

    /// <summary>A key YAML reads as the string it is, without quotes.</summary>
    [GeneratedRegex(@"^(?!(?i:y|n|yes|no|on|off|true|false|null)$)[A-Za-z][A-Za-z0-9_./-]*$")]
    private static partial Regex PlainKey();
}
