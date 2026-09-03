using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace EntKube.Web.Services.Adoption;

/// <summary>The outcome of turning one live object into something safe to store as desired state.</summary>
public sealed record SanitisedResource
{
    public required string Kind { get; init; }
    public required string Name { get; init; }

    /// <summary>Manifest YAML, or null when this object must not be adopted at all.</summary>
    public string? Yaml { get; init; }

    /// <summary>Set when the object cannot safely become desired state. <see cref="Yaml"/> is then null.</summary>
    public string? Refusal { get; init; }

    /// <summary>Things removed or worth knowing about, shown alongside the proposal.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsAdoptable => Yaml is not null;
}

/// <summary>
/// Turns a live Kubernetes object into a manifest that can be stored as desired state.
///
/// A live object is not a manifest. The API server adds identity (uid, resourceVersion,
/// generation), history (creationTimestamp, managedFields), a whole status subtree, and
/// cluster-bound allocations like a Service's clusterIP. Storing that verbatim produces
/// desired state that is unreadable, that re-applies with immutable-field errors, and
/// that cannot be applied to a second cluster at all — which is exactly what a stored
/// manifest is for.
///
/// The guiding rule is to remove only what is unambiguously server-owned or bound to
/// this cluster. Over-stripping silently discards something the operator actually set,
/// and a manifest that quietly lost a field is worse than a verbose one — so anything
/// ambiguous is kept, and anything removed is reported.
/// </summary>
public static class LiveStateSanitiser
{
    /// <summary>metadata keys the API server owns. None of them describe intent.</summary>
    private static readonly string[] ServerOwnedMetadata =
    [
        "uid", "resourceVersion", "generation", "creationTimestamp",
        "managedFields", "selfLink", "deletionTimestamp", "deletionGracePeriodSeconds",
        // An owned object (a ReplicaSet under a Deployment) is created by its controller.
        // Adopting one would store a resource nobody should be applying directly.
        "ownerReferences",
    ];

    private static readonly string[] ServerOwnedAnnotations =
    [
        // Contains a full copy of the previous manifest. Keeping it nests the whole
        // object inside itself, and it grows on every apply.
        "kubectl.kubernetes.io/last-applied-configuration",
        "deployment.kubernetes.io/revision",
        "autoscaling.alpha.kubernetes.io/conditions",
        "control-plane.alpha.kubernetes.io/leader",
        "pv.kubernetes.io/bind-completed",
        "pv.kubernetes.io/bound-by-controller",
        "volume.beta.kubernetes.io/storage-provisioner",
        "volume.kubernetes.io/storage-provisioner",
    ];

    /// <summary>
    /// Sanitises one live object supplied as kubectl JSON.
    /// </summary>
    public static SanitisedResource Sanitise(string? liveJson)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(liveJson ?? "") as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            return new SanitisedResource
            {
                Kind = "?", Name = "?",
                Refusal = "The live object could not be read.",
            };
        }

        string kind = root["kind"]?.GetValue<string>() ?? "?";
        string name = (root["metadata"] as JsonObject)?["name"]?.GetValue<string>() ?? "?";
        List<string> notes = [];

        // A Secret's data is the secret. Copying it into a stored manifest would put
        // credentials in the deployment's YAML — readable in the editor, in the database
        // and in any export — when EntKube has a vault precisely so that never happens.
        // Adopting the object with the data stripped would be worse still: the next apply
        // would overwrite the live Secret with an empty one.
        if (string.Equals(kind, "Secret", StringComparison.Ordinal))
        {
            return new SanitisedResource
            {
                Kind = kind, Name = name,
                Refusal = "A Secret's data cannot be adopted into a stored manifest. "
                        + "Adopting it with the data stripped would blank the live Secret on the next "
                        + "apply. Manage its values in the vault instead; this manifest is left as it is.",
            };
        }

        root.Remove("status");

        if (root["metadata"] is JsonObject metadata)
        {
            foreach (string key in ServerOwnedMetadata)
            {
                if (metadata.Remove(key) && key == "ownerReferences")
                {
                    notes.Add("Dropped ownerReferences — this object is created by a controller.");
                }
            }

            if (metadata["annotations"] is JsonObject annotations)
            {
                foreach (string key in ServerOwnedAnnotations)
                {
                    annotations.Remove(key);
                }

                // An empty annotations map is noise, and re-applying it is a no-op.
                if (annotations.Count == 0)
                {
                    metadata.Remove("annotations");
                }
            }

            if (metadata["labels"] is JsonObject { Count: 0 })
            {
                metadata.Remove("labels");
            }
        }

        SanitiseKindSpecific(root, kind, notes);

        return new SanitisedResource
        {
            Kind = kind,
            Name = name,
            Yaml = ToYaml(root),
            Notes = notes,
        };
    }

    /// <summary>
    /// Removes values the cluster allocated rather than the operator chose. These are the
    /// fields that make a manifest refuse to apply anywhere else: a clusterIP already
    /// belongs to this cluster, a nodePort is taken from its range, a bound volumeName
    /// names a PV that exists here and nowhere else.
    /// </summary>
    private static void SanitiseKindSpecific(JsonObject root, string kind, List<string> notes)
    {
        if (root["spec"] is not JsonObject spec)
        {
            return;
        }

        switch (kind)
        {
            case "Service":
                bool removedIp = spec.Remove("clusterIP") | spec.Remove("clusterIPs");
                if (removedIp)
                {
                    notes.Add("Dropped the assigned clusterIP so the manifest can apply to any cluster.");
                }

                spec.Remove("healthCheckNodePort");

                if (spec["ports"] is JsonArray ports)
                {
                    bool removedNodePort = false;
                    foreach (JsonNode? port in ports)
                    {
                        if (port is JsonObject p && p.Remove("nodePort"))
                        {
                            removedNodePort = true;
                        }
                    }

                    if (removedNodePort)
                    {
                        notes.Add("Dropped allocated nodePort values.");
                    }
                }
                break;

            case "PersistentVolumeClaim":
                if (spec.Remove("volumeName"))
                {
                    notes.Add("Dropped the bound volumeName — it names a PersistentVolume in this cluster only.");
                }
                break;

            case "ServiceAccount":
                // The API server populates this with auto-created token Secrets.
                if (root.Remove("secrets"))
                {
                    notes.Add("Dropped auto-created token secret references.");
                }
                break;
        }
    }

    /// <summary>Renders the pruned object as manifest YAML.</summary>
    private static string ToYaml(JsonObject root)
    {
        ISerializer serializer = new SerializerBuilder()
            .WithIndentedSequences()
            .Build();

        return serializer.Serialize(ToPlain(root)!);
    }

    /// <summary>
    /// Converts the JSON tree into plain objects YamlDotNet can serialise.
    ///
    /// Numbers are narrowed to long where they are integral: a replica count rendered as
    /// "3.0" is not valid for an integer field, and Kubernetes rejects the manifest.
    /// </summary>
    private static object? ToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(p => p.Key, p => ToPlain(p.Value)),
        JsonArray array => array.Select(ToPlain).ToList(),
        JsonValue value => ToScalar(value),
        _ => node.ToString(),
    };

    private static object? ToScalar(JsonValue value)
    {
        if (value.TryGetValue(out bool boolean)) return boolean;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out double number))
        {
            return number == Math.Floor(number) && !double.IsInfinity(number) ? (long)number : number;
        }

        return value.TryGetValue(out string? text) ? text : value.ToString();
    }
}
