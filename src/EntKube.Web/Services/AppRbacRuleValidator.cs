namespace EntKube.Web.Services;

/// <summary>How seriously a rule departs from least privilege.</summary>
public enum RbacRuleSeverity
{
    /// <summary>Allowed, but the operator should know what they granted.</summary>
    Warning,

    /// <summary>Refused. The grant escapes the app's namespace boundary, or makes escaping it trivial.</summary>
    Blocked
}

/// <summary>One problem found in a proposed or stored RBAC rule.</summary>
public sealed record RbacRuleViolation(RbacRuleSeverity Severity, string Message);

/// <summary>
/// Checks the rules of an <see cref="Data.AppRbacPolicy"/> against a fixed deny-list.
///
/// The Role an app's ServiceAccount receives is namespace-scoped, so it is tempting to treat
/// anything inside that namespace as already contained. It is not: a handful of verbs turn a
/// namespaced Role into a way out of the namespace, and one of them (<c>create pods/exec</c>)
/// yields whatever token the target pod is carrying.
///
/// The same deny-list is enforced a second time at admission by the <c>restrict-rbac</c> Kyverno
/// policy, so a Role that reaches a cluster by some path that does not run through this service —
/// a Helm chart, a hand-applied manifest — is still refused. The two lists are deliberately
/// identical; changing one without the other leaves an inconsistency that is hard to see from
/// either end.
/// </summary>
public static class AppRbacRuleValidator
{
    /// <summary>
    /// Verbs that grant RBAC escalation directly. <c>escalate</c> lifts the cap that normally stops
    /// a subject granting permissions it does not itself hold; <c>bind</c> lets it attach an
    /// existing privileged Role; <c>impersonate</c> lets it act as another subject entirely.
    /// </summary>
    public static readonly IReadOnlySet<string> DeniedVerbs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "escalate", "bind", "impersonate" };

    /// <summary>
    /// Resources an app's own ServiceAccount has no business managing. Writing RBAC objects is
    /// self-escalation; the subresources hand over another pod's identity or its network position.
    /// </summary>
    public static readonly IReadOnlySet<string> DeniedResources =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "roles", "rolebindings", "clusterroles", "clusterrolebindings",
            "pods/exec", "pods/attach", "pods/portforward"
        };

    /// <summary>
    /// Resources that are legitimate but worth saying out loud. Reading Secrets is the app's own
    /// credentials in the clear — sometimes exactly what is wanted, never something to grant by
    /// accident.
    /// </summary>
    public static readonly IReadOnlySet<string> SensitiveResources =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "secrets" };

    /// <summary>
    /// Validates one rule's three fields, each a comma-separated list as stored on
    /// <see cref="Data.AppRbacRule"/>. Returns every violation found, not just the first, so the
    /// operator can fix the rule in one pass instead of discovering problems one at a time.
    /// </summary>
    public static IReadOnlyList<RbacRuleViolation> Validate(string? apiGroups, string? resources, string? verbs)
    {
        List<RbacRuleViolation> violations = [];

        string[] groupList = Split(apiGroups);
        string[] resourceList = Split(resources);
        string[] verbList = Split(verbs);

        // An empty apiGroups means the core group (""), which is legitimate and how the UI
        // expresses it. Empty resources or verbs are not — the rule would grant nothing, and
        // silently storing it makes the policy list lie about what is in force.
        if (resourceList.Length == 0)
            violations.Add(new(RbacRuleSeverity.Blocked, "A rule must name at least one resource."));

        if (verbList.Length == 0)
            violations.Add(new(RbacRuleSeverity.Blocked, "A rule must name at least one verb."));

        foreach ((string field, string[] values) in
                 new[] { ("apiGroups", groupList), ("resources", resourceList), ("verbs", verbList) })
        {
            if (values.Any(v => v == "*"))
            {
                violations.Add(new(RbacRuleSeverity.Blocked,
                    $"Wildcard '*' is not allowed in {field}. List what the app actually needs — "
                    + "a wildcard here grants every current and future item, including ones that do not exist yet."));
            }
        }

        foreach (string verb in verbList.Where(v => DeniedVerbs.Contains(v)))
        {
            violations.Add(new(RbacRuleSeverity.Blocked,
                $"The verb '{verb}' allows RBAC escalation and cannot be granted to an app's ServiceAccount."));
        }

        foreach (string resource in resourceList.Where(r => DeniedResources.Contains(r)))
        {
            violations.Add(new(RbacRuleSeverity.Blocked,
                $"'{resource}' cannot be granted to an app's ServiceAccount — it defeats the namespace boundary."));
        }

        foreach (string resource in resourceList.Where(r => SensitiveResources.Contains(r)))
        {
            violations.Add(new(RbacRuleSeverity.Warning,
                $"This rule grants access to '{resource}'. The app will be able to read its own credentials "
                + "in the clear through the Kubernetes API."));
        }

        return violations;
    }

    /// <summary>
    /// Validates a rule and throws if anything is <see cref="RbacRuleSeverity.Blocked"/>.
    /// Warnings pass — they are for the UI to show, not for this to refuse.
    /// </summary>
    public static void ThrowIfBlocked(string? apiGroups, string? resources, string? verbs)
    {
        string[] blocked = Validate(apiGroups, resources, verbs)
            .Where(v => v.Severity == RbacRuleSeverity.Blocked)
            .Select(v => v.Message)
            .ToArray();

        if (blocked.Length > 0)
            throw new InvalidOperationException(string.Join(" ", blocked));
    }

    /// <summary>
    /// Validates rules that are already stored. Rules written before validation existed can hold
    /// anything, so the UI surfaces them rather than the apply path refusing them — failing an
    /// apply would take a running app down to enforce a rule it has had all along.
    /// </summary>
    public static IReadOnlyList<RbacRuleViolation> ValidateExisting(IEnumerable<Data.AppRbacRule> rules) =>
        rules.SelectMany(r => Validate(r.ApiGroups, r.Resources, r.Verbs)).ToList();

    private static string[] Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
