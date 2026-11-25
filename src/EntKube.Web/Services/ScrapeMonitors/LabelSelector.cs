using System.Text.Json;
using System.Text.Json.Nodes;
using k8s.Models;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>One <c>matchExpressions</c> entry: a label, how it is compared, and with what.</summary>
public sealed record LabelRequirement(string Key, string Operator, IReadOnlyList<string> Values);

/// <summary>
/// A Kubernetes label selector, and whether a set of labels satisfies it.
///
/// <para><b>Three answers, not two.</b> A selector can carry an operator this class does not know:
/// Kubernetes adds them, and a custom resource's field may hold anything. Reading that as "does not
/// match" would let EntKube create a monitor beside one that already scrapes the pod, and reading it
/// as "matches" would let it remove one. So the answer is then null, "cannot tell", and every caller
/// has to say what it does with that.</para>
///
/// <para><b>Absent is not empty.</b> In the Prometheus Operator's monitor selectors a missing
/// selector selects nothing and an empty one selects everything, which is the opposite of what it
/// looks like. <see cref="Selects"/> is where that rule lives. The places where absent means
/// something else (a namespace selector, a NetworkPolicy peer) handle it before they ask.</para>
/// </summary>
public sealed record LabelSelector(
    IReadOnlyDictionary<string, string> MatchLabels,
    IReadOnlyList<LabelRequirement> MatchExpressions)
{
    /// <summary>An operator no cluster sends: it makes a selector that could not be read answer "cannot tell".</summary>
    private const string Unreadable = "(unreadable)";

    /// <summary>A selector on labels alone, the usual case.</summary>
    public static LabelSelector Of(params (string Key, string Value)[] labels)
        => new(labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal), []);

    /// <summary>This selector with one more requirement: the label is one of these values.</summary>
    public LabelSelector In(string key, params string[] values)
        => this with { MatchExpressions = [.. MatchExpressions, new LabelRequirement(key, "In", values)] };

    /// <summary>This selector with one more requirement: the label is none of these values, or absent.</summary>
    public LabelSelector NotIn(string key, params string[] values)
        => this with { MatchExpressions = [.. MatchExpressions, new LabelRequirement(key, "NotIn", values)] };

    /// <summary>
    /// The selector in a custom resource's field, or null when the field is absent. A field that is
    /// present but not shaped like a selector becomes one that answers "cannot tell".
    /// </summary>
    public static LabelSelector? From(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject selector) return CannotBeRead();

        Dictionary<string, string> labels = new(StringComparer.Ordinal);

        if (selector["matchLabels"] is { } matchLabels)
        {
            if (matchLabels is not JsonObject pairs) return CannotBeRead();

            foreach ((string key, JsonNode? value) in pairs)
            {
                if (TextOf(value) is not { } text) return CannotBeRead();
                labels[key] = text;
            }
        }

        List<LabelRequirement> expressions = [];

        if (selector["matchExpressions"] is { } matchExpressions)
        {
            if (matchExpressions is not JsonArray entries) return CannotBeRead();

            foreach (JsonNode? entry in entries)
            {
                if (entry is not JsonObject requirement
                    || TextOf(requirement["key"]) is not { } key
                    || TextOf(requirement["operator"]) is not { } comparison
                    || requirement["values"] is not (null or JsonArray))
                {
                    return CannotBeRead();
                }

                List<string> values = [];
                foreach (JsonNode? value in requirement["values"] as JsonArray ?? [])
                {
                    if (TextOf(value) is not { } text) return CannotBeRead();
                    values.Add(text);
                }

                expressions.Add(new LabelRequirement(key, comparison, values));
            }
        }

        return new LabelSelector(labels, expressions);
    }

    /// <summary>The same for the typed selectors on NetworkPolicies.</summary>
    public static LabelSelector? From(V1LabelSelector? selector)
        => selector is null
            ? null
            : new LabelSelector(
                (selector.MatchLabels ?? new Dictionary<string, string>())
                    .ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal),
                [.. (selector.MatchExpressions ?? [])
                    .Select(e => new LabelRequirement(e.Key, e.OperatorProperty, [.. e.Values ?? []]))]);

    /// <summary>
    /// Whether <paramref name="selector"/> selects something carrying <paramref name="labels"/>:
    /// an absent selector selects nothing, an empty one everything, and null is "cannot tell".
    /// </summary>
    public static bool? Selects(LabelSelector? selector, IReadOnlyDictionary<string, string> labels)
        => selector is null ? false : selector.Matches(labels);

    /// <summary>
    /// Whether these labels satisfy every requirement. One requirement that is definitely unmet
    /// decides it, whatever else cannot be read: an unknown operator can only narrow a selection.
    /// </summary>
    public bool? Matches(IReadOnlyDictionary<string, string> labels)
    {
        foreach ((string key, string value) in MatchLabels)
        {
            if (!labels.TryGetValue(key, out string? have) || have != value) return false;
        }

        bool certain = true;

        foreach (LabelRequirement requirement in MatchExpressions)
        {
            bool present = labels.TryGetValue(requirement.Key, out string? have);

            bool? met = requirement.Operator switch
            {
                "In" => present && requirement.Values.Contains(have!, StringComparer.Ordinal),
                // An absent label satisfies NotIn. That is Kubernetes' rule, and it is what lets
                // "component not in (gateway)" select a pod with no component label at all.
                "NotIn" => !present || !requirement.Values.Contains(have!, StringComparer.Ordinal),
                "Exists" => present,
                "DoesNotExist" => !present,
                _ => null,
            };

            if (met is false) return false;
            if (met is null) certain = false;
        }

        return certain ? true : null;
    }

    /// <summary>The selector as a manifest writes it.</summary>
    public JsonObject ToJson()
    {
        JsonObject json = [];

        if (MatchLabels.Count > 0)
        {
            JsonObject labels = [];
            foreach ((string key, string value) in MatchLabels) labels[key] = value;
            json["matchLabels"] = labels;
        }

        if (MatchExpressions.Count > 0)
        {
            JsonArray expressions = [];
            foreach (LabelRequirement requirement in MatchExpressions)
            {
                JsonArray values = [];
                foreach (string value in requirement.Values) values.Add(value);

                expressions.Add(new JsonObject
                {
                    ["key"] = requirement.Key,
                    ["operator"] = requirement.Operator,
                    ["values"] = values,
                });
            }

            json["matchExpressions"] = expressions;
        }

        return json;
    }

    /// <summary>How a sentence names it: <c>app=redis, role in (leader, follower)</c>.</summary>
    public string Describe() => string.Join(", ",
        MatchLabels.Select(l => $"{l.Key}={l.Value}").Concat(MatchExpressions.Select(e => e.Operator switch
        {
            "In" => $"{e.Key} in ({string.Join(", ", e.Values)})",
            "NotIn" => $"{e.Key} not in ({string.Join(", ", e.Values)})",
            _ => $"{e.Key} {e.Operator}",
        })));

    private static LabelSelector CannotBeRead()
        => new(new Dictionary<string, string>(), [new LabelRequirement("", Unreadable, [])]);

    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
