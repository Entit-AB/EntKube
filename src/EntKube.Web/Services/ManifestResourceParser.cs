using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Services;

/// <summary>Identity of one Kubernetes resource parsed from a deployment manifest.</summary>
public sealed record ManifestResourceRef(
    string Group, string Version, string Kind, string Name, string? Namespace, bool Prunable)
{
    /// <summary>Version-agnostic identity used to diff manifest sets (a resource is not identified by its apiVersion's version).</summary>
    public (string Group, string Kind, string? Namespace, string Name) Key => (Group, Kind, Namespace, Name);
}

/// <summary>
/// Parses raw manifest YAML (one or more documents) into resource identities so the
/// apply path can diff the current manifest set against the last-applied inventory
/// and prune what was removed. Reads apiVersion/kind/metadata and the prune opt-out
/// annotations (<c>helm.sh/resource-policy: keep</c>, <c>entkube.io/prune: disabled</c>).
/// </summary>
public static class ManifestResourceParser
{
    // Common cluster-scoped kinds — these carry no namespace even when the manifest omits one.
    private static readonly HashSet<string> ClusterScopedKinds = new(StringComparer.Ordinal)
    {
        "Namespace", "Node", "PersistentVolume", "ClusterRole", "ClusterRoleBinding",
        "CustomResourceDefinition", "StorageClass", "ClusterIssuer", "IngressClass",
        "PriorityClass", "ValidatingWebhookConfiguration", "MutatingWebhookConfiguration",
        "ClusterPolicy", "APIService"
    };

    public static IReadOnlyList<ManifestResourceRef> Parse(string yaml, string defaultNamespace)
    {
        List<ManifestResourceRef> refs = [];
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return refs;
        }

        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch
        {
            return refs; // unparseable YAML — nothing to prune against
        }

        foreach (YamlDocument doc in stream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode map)
            {
                continue;
            }

            string? kind = Scalar(map, "kind");
            YamlMappingNode? metadata = Child(map, "metadata");
            string? name = metadata is null ? null : Scalar(metadata, "name");
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            (string group, string version) = SplitApiVersion(Scalar(map, "apiVersion"));

            string? ns = metadata is null ? null : Scalar(metadata, "namespace");
            if (string.IsNullOrWhiteSpace(ns))
            {
                ns = ClusterScopedKinds.Contains(kind) ? null : defaultNamespace;
            }

            bool prunable = true;
            if (metadata is not null && Child(metadata, "annotations") is YamlMappingNode annotations)
            {
                if (string.Equals(Scalar(annotations, "helm.sh/resource-policy"), "keep", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Scalar(annotations, "entkube.io/prune"), "disabled", StringComparison.OrdinalIgnoreCase))
                {
                    prunable = false;
                }
            }

            refs.Add(new ManifestResourceRef(group, version, kind, name, ns, prunable));
        }

        return refs;
    }

    private static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key) is { } keyNode
            && map.Children[keyNode] is YamlScalarNode value
            ? value.Value
            : null;

    private static YamlMappingNode? Child(YamlMappingNode map, string key) =>
        map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key) is { } keyNode
            ? map.Children[keyNode] as YamlMappingNode
            : null;

    private static (string Group, string Version) SplitApiVersion(string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return ("", "");
        }
        int slash = apiVersion.IndexOf('/');
        return slash < 0 ? ("", apiVersion) : (apiVersion[..slash], apiVersion[(slash + 1)..]);
    }
}
