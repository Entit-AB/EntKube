using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Services;

/// <summary>
/// Represents a single context parsed from a kubeconfig file.
/// A context ties together a cluster (server URL) and user (credentials).
/// </summary>
public record KubeconfigContext(string Name, string ClusterServer, bool IsCurrent);

/// <summary>
/// The result of choosing a context in the kubeconfig picker: the raw kubeconfig YAML
/// plus the selected context name and its cluster server URL.
/// </summary>
public record KubeconfigSelection(string Yaml, string ContextName, string ServerUrl);

/// <summary>
/// Parses kubeconfig YAML to extract available contexts and their associated
/// cluster server URLs. This lets users paste or upload a kubeconfig and pick
/// which context (cluster) to register.
/// </summary>
public static class KubeconfigParser
{
    /// <summary>
    /// Parses a kubeconfig YAML string and returns all valid contexts.
    /// Each context includes the resolved cluster server URL so the user
    /// can make an informed choice. Invalid or unresolvable contexts are skipped.
    /// </summary>
    public static List<KubeconfigContext> ParseContexts(string kubeconfigYaml)
    {
        if (string.IsNullOrWhiteSpace(kubeconfigYaml))
        {
            return [];
        }

        try
        {
            YamlStream yaml = new();
            using (StringReader reader = new(kubeconfigYaml))
            {
                yaml.Load(reader);
            }

            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                return [];
            }

            // Build a lookup of cluster name → server URL from the "clusters" array.
            Dictionary<string, string> clusterServers = BuildClusterServerMap(root);

            if (clusterServers.Count == 0)
            {
                return [];
            }

            // Determine which context is marked as current.
            string currentContext = GetScalarValue(root, "current-context") ?? "";

            // Walk the "contexts" array and resolve each one against the cluster map.
            YamlSequenceNode? contextsNode = GetSequenceNode(root, "contexts");

            if (contextsNode is null)
            {
                return [];
            }

            List<KubeconfigContext> results = [];

            foreach (YamlNode node in contextsNode)
            {
                if (node is not YamlMappingNode contextEntry)
                {
                    continue;
                }

                string? name = GetScalarValue(contextEntry, "name");

                if (name is null)
                {
                    continue;
                }

                // The "context" sub-object holds the cluster reference.
                YamlMappingNode? contextBody = GetMappingNode(contextEntry, "context");

                if (contextBody is null)
                {
                    continue;
                }

                string? clusterRef = GetScalarValue(contextBody, "cluster");

                if (clusterRef is null || !clusterServers.TryGetValue(clusterRef, out string? serverUrl))
                {
                    continue;
                }

                bool isCurrent = string.Equals(name, currentContext, StringComparison.Ordinal);
                results.Add(new KubeconfigContext(name, serverUrl, isCurrent));
            }

            return results;
        }
        catch
        {
            // Any YAML parsing failure — return empty rather than crash.
            return [];
        }
    }

    /// <summary>
    /// Builds a dictionary mapping cluster name to its server URL
    /// from the "clusters" array in the kubeconfig root.
    /// </summary>
    private static Dictionary<string, string> BuildClusterServerMap(YamlMappingNode root)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        YamlSequenceNode? clustersNode = GetSequenceNode(root, "clusters");

        if (clustersNode is null)
        {
            return map;
        }

        foreach (YamlNode node in clustersNode)
        {
            if (node is not YamlMappingNode clusterEntry)
            {
                continue;
            }

            string? name = GetScalarValue(clusterEntry, "name");
            YamlMappingNode? clusterBody = GetMappingNode(clusterEntry, "cluster");

            if (name is null || clusterBody is null)
            {
                continue;
            }

            string? server = GetScalarValue(clusterBody, "server");

            if (server is not null)
            {
                map[name] = server;
            }
        }

        return map;
    }

    // --- YAML navigation helpers ---

    private static string? GetScalarValue(YamlMappingNode mapping, string key)
    {
        YamlScalarNode keyNode = new(key);

        if (mapping.Children.TryGetValue(keyNode, out YamlNode? value) && value is YamlScalarNode scalar)
        {
            return scalar.Value;
        }

        return null;
    }

    private static YamlSequenceNode? GetSequenceNode(YamlMappingNode mapping, string key)
    {
        YamlScalarNode keyNode = new(key);

        if (mapping.Children.TryGetValue(keyNode, out YamlNode? value) && value is YamlSequenceNode seq)
        {
            return seq;
        }

        return null;
    }

    private static YamlMappingNode? GetMappingNode(YamlMappingNode mapping, string key)
    {
        YamlScalarNode keyNode = new(key);

        if (mapping.Children.TryGetValue(keyNode, out YamlNode? value) && value is YamlMappingNode map)
        {
            return map;
        }

        return null;
    }
}
