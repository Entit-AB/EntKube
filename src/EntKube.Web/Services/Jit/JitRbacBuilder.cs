using System.Text;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Jit;

/// <summary>One rule in a grant's Role — the same shape as one entry in a Kubernetes Role's rules[].</summary>
public sealed record JitRule(string ApiGroup, string[] Resources, string[] Verbs);

/// <summary>
/// Renders the ServiceAccount, Role and RoleBinding for one grant.
///
/// Objects are per-grant, named <c>jit-{first 12 hex of the grant id}</c>, rather than a shared
/// "customer-readonly" ServiceAccount reused across grants. That is not tidiness: a bound
/// ServiceAccount token cannot be invalidated before it expires, so revoking early has to mean
/// deleting the RoleBinding — which only works if the binding belongs to exactly one grant. It
/// also makes the API server's own audit log legible, since the subject names the grant.
///
/// Every level is defined by what it withholds. The verbs left out — <c>get secrets</c>,
/// <c>pods/portforward</c>, <c>impersonate</c>, and anything cluster-scoped — each escape the
/// namespace on their own, so none of them appears at any level.
/// </summary>
public static class JitRbacBuilder
{
    /// <summary>Prefix every grant's cluster objects share, and what the reaper sweeps for.</summary>
    public const string NamePrefix = "jit-";

    /// <summary>Label carrying the grant id, so a live object can be traced back to its row.</summary>
    public const string GrantLabel = "entkube.io/jit-grant";

    /// <summary>Label marking an object as JIT-managed, independent of the name.</summary>
    public const string ManagedLabel = "entkube.io/managed-by";

    public const string ManagedValue = "entkube-jit";

    /// <summary>
    /// Derives the ServiceAccount name for a grant. Twelve hex characters of the id: short enough
    /// to leave room inside the 63-character DNS label limit, wide enough that a collision between
    /// two live grants in one namespace is not a thing that happens.
    /// </summary>
    public static string ServiceAccountName(Guid grantId) =>
        NamePrefix + grantId.ToString("N")[..12];

    public static string RoleName(Guid grantId) => ServiceAccountName(grantId) + "-role";

    public static string RoleBindingName(Guid grantId) => ServiceAccountName(grantId) + "-binding";

    /// <summary>
    /// The rules a level grants, in the order they are rendered.
    ///
    /// Each level is a superset of the one below, built by addition rather than by restating the
    /// whole set — a level defined by a copied-and-edited list drifts from the one it was copied
    /// from the first time either changes.
    /// </summary>
    public static IReadOnlyList<JitRule> RulesFor(JitAccessLevel level)
    {
        List<JitRule> rules =
        [
            // Reading the workload. Events are included because "why is this pod not starting"
            // is answered by events far more often than by anything else here.
            new("", ["pods", "services", "events", "endpoints"], ["get", "list", "watch"]),
            new("apps", ["deployments", "replicasets", "statefulsets", "daemonsets"], ["get", "list", "watch"]),
            new("networking.k8s.io", ["ingresses"], ["get", "list", "watch"]),

            // Logs are a subresource of pods, and read with `get` — `list` on pods/log is not a
            // thing, so asking for it would render a Role that grants less than it appears to.
            new("", ["pods/log"], ["get"]),
        ];

        if (level >= JitAccessLevel.Troubleshoot)
        {
            // ConfigMaps, deliberately without Secrets. The two sit next to each other in every
            // example of this rule, which is exactly why the omission is worth stating.
            rules.Add(new("", ["configmaps"], ["get", "list"]));

            // Exec is `create` on the subresource, not `get`. It yields the target pod's own
            // ServiceAccount token, which is why it is a level of its own rather than part of
            // reading logs.
            rules.Add(new("", ["pods/exec"], ["create"]));
        }

        if (level >= JitAccessLevel.Operate)
        {
            // Enough to restart a stuck workload and to scale it, and nothing else. `delete` on
            // pods is safe under a Deployment — the ReplicaSet puts it back.
            rules.Add(new("", ["pods"], ["delete"]));
            rules.Add(new("apps", ["deployments", "statefulsets"], ["patch"]));
            rules.Add(new("apps", ["deployments/scale", "statefulsets/scale"], ["get", "patch"]));
        }

        return rules;
    }

    /// <summary>
    /// Renders the three objects as one multi-document manifest, ready for
    /// <see cref="IKubernetesClientFactory.ApplyManifestAsync"/>.
    /// </summary>
    public static string BuildManifest(JitGrant grant)
    {
        string sa = ServiceAccountName(grant.Id);
        string ns = grant.Namespace;

        StringBuilder rules = new();
        foreach (JitRule rule in RulesFor(grant.Level))
        {
            rules.AppendLine($"  - apiGroups: [\"{rule.ApiGroup}\"]");
            rules.AppendLine($"    resources: [{Quote(rule.Resources)}]");
            rules.AppendLine($"    verbs: [{Quote(rule.Verbs)}]");
        }

        string labels = string.Join("\n", [
            "  labels:",
            $"    {ManagedLabel}: {ManagedValue}",
            $"    {GrantLabel}: \"{grant.Id}\"",
        ]);

        string serviceAccount = string.Join("\n", [
            "apiVersion: v1",
            "kind: ServiceAccount",
            "metadata:",
            $"  name: {sa}",
            $"  namespace: {ns}",
            labels,
            // Nothing mounts this ServiceAccount into a pod — the only token it ever issues is the
            // bound one requested at mint time. Saying so explicitly means a pod that named this
            // ServiceAccount by accident would not silently acquire the grant's permissions.
            "automountServiceAccountToken: false",
        ]);

        string role = string.Join("\n", [
            "apiVersion: rbac.authorization.k8s.io/v1",
            "kind: Role",
            "metadata:",
            $"  name: {RoleName(grant.Id)}",
            $"  namespace: {ns}",
            labels,
            "rules:",
            rules.ToString().TrimEnd(),
        ]);

        string binding = string.Join("\n", [
            "apiVersion: rbac.authorization.k8s.io/v1",
            "kind: RoleBinding",
            "metadata:",
            $"  name: {RoleBindingName(grant.Id)}",
            $"  namespace: {ns}",
            labels,
            "subjects:",
            "  - kind: ServiceAccount",
            $"    name: {sa}",
            $"    namespace: {ns}",
            "roleRef:",
            "  kind: Role",
            $"  name: {RoleName(grant.Id)}",
            "  apiGroup: rbac.authorization.k8s.io",
        ]);

        return string.Join("\n---\n", [serviceAccount, role, binding]);
    }

    private static string Quote(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => $"\"{v}\""));
}
