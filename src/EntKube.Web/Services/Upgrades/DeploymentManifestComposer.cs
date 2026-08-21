using EntKube.Web.Data;

namespace EntKube.Web.Services.Upgrades;

/// <summary>
/// Builds the exact manifest document that EntKube applies for a YAML-defined
/// deployment.
///
/// Extracted so that apply and drift detection cannot diverge: drift is only
/// meaningful if it diffs the same bytes the apply would send. If this rendering
/// lived in the apply path alone and drift re-implemented it, every difference
/// between the two implementations would show up as permanent phantom drift that
/// no amount of re-applying would clear.
/// </summary>
public static class DeploymentManifestComposer
{
    /// <summary>
    /// Combines a deployment's manifests, in apply order, into one multi-document YAML
    /// string prefixed with a Namespace document (mirroring Helm's --create-namespace).
    /// </summary>
    public static string Combine(string namespaceName, IEnumerable<DeploymentManifest> manifests)
        => Combine(namespaceName, manifests.OrderBy(m => m.SortOrder).Select(m => m.YamlContent));

    /// <summary>
    /// Combines raw manifest bodies, already in apply order, into one multi-document
    /// YAML string prefixed with a Namespace document.
    /// </summary>
    public static string Combine(string namespaceName, IEnumerable<string> manifestBodies)
    {
        string nsManifest =
            $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {namespaceName}";

        return nsManifest + "\n---\n" + string.Join("\n---\n", manifestBodies.Select(StripLeadingMarker));
    }

    /// <summary>
    /// Drops a leading "---" document marker. Some Git-managed files start with one, and
    /// keeping it would produce a "---\n---" separator in the combined output — an empty
    /// document that kubectl rejects on some versions.
    /// </summary>
    private static string StripLeadingMarker(string yaml)
    {
        string content = yaml.TrimStart();
        return content.StartsWith("---", StringComparison.Ordinal)
            ? content["---".Length..].TrimStart('\n', '\r')
            : content;
    }
}
