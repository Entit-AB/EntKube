namespace EntKube.Web.Services;

/// <summary>
/// Refuses kinds that a customer app's manifests have no legitimate reason to carry.
///
/// The namespace lock is what confines an app, and it is enforced by
/// <c>CheckNamespaceGovernance</c> — but that check works by looking for a <c>namespace:</c> field
/// that points somewhere else. Cluster-scoped resources have no such field, so they pass it
/// untouched and are then applied with the cluster's stored kubeconfig, which is an admin
/// credential. A manifest carrying a ClusterRoleBinding therefore used to be a supported way for
/// an app to grant itself anything at all.
///
/// Namespaced RBAC is refused too. A RoleBinding is confined to its namespace, so it cannot reach
/// another tenant — but it can bind the app's ServiceAccount to <c>cluster-admin</c> *within* that
/// namespace, which re-grants everything <see cref="AppRbacRuleValidator"/> exists to withhold and
/// does it where nobody is looking. RBAC for an app belongs in the governance RBAC section, which
/// validates what it is asked to grant and shows it in one place.
/// </summary>
public static class ManifestKindPolicy
{
    /// <summary>
    /// Kinds refused in a customer app's deployment manifests, with the reason each is refused —
    /// the message an operator gets has to say why, or the only available next step is guessing.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DeniedKinds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ClusterRole"] =
                "cluster-scoped RBAC escapes the app's namespace entirely",
            ["ClusterRoleBinding"] =
                "cluster-scoped RBAC escapes the app's namespace entirely",
            ["Role"] =
                "namespaced RBAC belongs in the governance RBAC section, where what it grants is validated",
            ["RoleBinding"] =
                "a RoleBinding can bind to a privileged ClusterRole, re-granting inside the namespace what the RBAC rules withhold",
            ["ValidatingWebhookConfiguration"] =
                "an admission webhook intercepts API traffic cluster-wide, including other tenants'",
            ["MutatingWebhookConfiguration"] =
                "an admission webhook can rewrite other tenants' resources as they are admitted",
            ["APIService"] =
                "an APIService redirects part of the cluster's API surface to a pod of the app's choosing",
        };

    /// <summary>
    /// Checks parsed manifest resources against the deny-list. Returns a non-null error string
    /// naming every refused resource, or null when the manifest set is acceptable.
    /// </summary>
    public static string? Check(IEnumerable<ManifestResourceRef> resources)
    {
        List<string> refused = [];

        foreach (ManifestResourceRef r in resources)
        {
            if (DeniedKinds.TryGetValue(r.Kind, out string? reason))
                refused.Add($"{r.Kind}/{r.Name} — {reason}");
        }

        if (refused.Count == 0) return null;

        return "Manifest contains resource kinds that are not allowed in an app deployment: "
            + string.Join("; ", refused)
            + ". Grant the app's ServiceAccount what it needs under Governance → RBAC instead.";
    }

    /// <summary>
    /// Parses each manifest document and checks the result. <paramref name="defaultNamespace"/> is
    /// only used to resolve namespaced resources that omit one; it does not affect the verdict.
    /// </summary>
    public static string? CheckYaml(IEnumerable<string> manifestContents, string defaultNamespace) =>
        Check(manifestContents.SelectMany(y => ManifestResourceParser.Parse(y, defaultNamespace)));
}
