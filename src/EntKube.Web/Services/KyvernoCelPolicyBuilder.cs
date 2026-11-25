using System.Text;
using System.Text.Json;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Renders EntKube's built-in policies as Kyverno CEL policies (<c>policies.kyverno.io/v1</c>), the
/// only kinds Kyverno 1.20 and later execute. <see cref="KyvernoPolicyService.BuildManifest"/> still
/// renders the <c>kyverno.io</c> kinds for clusters too old to serve these.
///
/// <para><b>Names are the legacy names.</b> The kinds differ, so a replacement can exist beside the
/// policy it replaces, and its name is what says which one that is.</para>
///
/// <para><b>Expressions match Pods and say <c>object.spec</c>.</b> Kyverno derives the Deployment,
/// StatefulSet, DaemonSet, ReplicaSet, Job and CronJob versions by replacing the text
/// <c>object.spec</c> and <c>object.metadata</c> (and the <c>oldObject</c> forms). Reaching the spec
/// any other way — a macro variable over <c>object</c>, an index — is left alone by that rewrite, and
/// the rule then reads a Deployment as if it were a Pod. The rewrite covers the whole policy on 1.18
/// and 1.19, messages included, so no message may contain either phrase.</para>
///
/// <para><b>Updates to something already in violation are let through.</b> That was the legacy
/// default (<c>allowExistingViolations</c>) and CEL policies have no such field. Without it, moving a
/// cluster over would refuse every later edit to a workload that predates its policy — including the
/// finalizer removal that lets a pod be deleted. So each rule passes when the object complies or when
/// <c>oldObject</c> already did not.</para>
///
/// <para><b>Every field is tested with <c>has()</c> first.</b> An expression that errors denies the
/// request under the default failure policy, so an unguarded optional field blocks the workloads the
/// rule had nothing to say about. Role rules and binding subjects serialize as null when empty, which
/// <c>has()</c> counts as present, so those are compared to null as well.</para>
/// </summary>
public static class KyvernoCelPolicyBuilder
{
    public const string ApiVersion = "policies.kyverno.io/v1";

    private const string Validating = "NamespacedValidatingPolicy";

    /// <summary>One policy, or null when its configuration is too incomplete to mean anything.</summary>
    public static string? Build(KyvernoPolicy policy, string ns)
    {
        Rule[]? rules = policy.PolicyType switch
        {
            KyvernoPolicyType.DisallowPrivilegedContainers =>
            [
                new("Privileged containers are not allowed.", s =>
                    AllContainers(s, "!has(c.securityContext) || !has(c.securityContext.privileged) || c.securityContext.privileged == false")),
            ],
            KyvernoPolicyType.DisallowRootUser =>
            [
                new("Containers must not run as root. Set runAsNonRoot: true or runAsUser > 0.", s =>
                    $"has({s}.spec.securityContext) && ("
                    + $"(has({s}.spec.securityContext.runAsNonRoot) && {s}.spec.securityContext.runAsNonRoot == true)"
                    + $" || (has({s}.spec.securityContext.runAsUser) && {s}.spec.securityContext.runAsUser > 0))"),
            ],
            KyvernoPolicyType.RequireReadOnlyRootFilesystem =>
            [
                new("Containers must use a read-only root filesystem.", s =>
                    MainContainers(s, "has(c.securityContext) && has(c.securityContext.readOnlyRootFilesystem) && c.securityContext.readOnlyRootFilesystem == true")),
            ],
            KyvernoPolicyType.DisallowPrivilegeEscalation =>
            [
                new("Privilege escalation is not allowed.", s =>
                    AllContainers(s, "has(c.securityContext) && has(c.securityContext.allowPrivilegeEscalation) && c.securityContext.allowPrivilegeEscalation == false")),
            ],
            KyvernoPolicyType.DisallowHostNetwork =>
            [
                new("Host networking is not allowed.", s => $"!has({s}.spec.hostNetwork) || {s}.spec.hostNetwork == false"),
            ],
            KyvernoPolicyType.DisallowHostPID =>
            [
                new("Host process ID namespace sharing is not allowed.", s => $"!has({s}.spec.hostPID) || {s}.spec.hostPID == false"),
            ],
            KyvernoPolicyType.DisallowHostPath =>
            [
                // A Pod with no volumes is the ordinary case and must pass; see KyvernoHostPathPolicyTests.
                new("HostPath volumes are not allowed.", s => $"!has({s}.spec.volumes) || {s}.spec.volumes.all(v, !has(v.hostPath))"),
            ],
            KyvernoPolicyType.RequireResourceLimits =>
            [
                new("All containers must specify CPU and memory limits.", s => MainContainers(s, HasQuantities("limits"))),
            ],
            KyvernoPolicyType.RequireResourceRequests =>
            [
                new("All containers must specify CPU and memory requests.", s => MainContainers(s, HasQuantities("requests"))),
            ],
            KyvernoPolicyType.RequireSeccompProfile =>
            [
                new("Pods must have a seccomp profile set to RuntimeDefault or Localhost.", Seccomp),
            ],
            KyvernoPolicyType.RestrictImageRegistries => Registries(policy),
            KyvernoPolicyType.RequirePodLabels => Labels(policy),
            _ => null,
        };

        if (rules is not null)
        {
            // Registry rules never ran in the background; the rest did.
            return Render(Validating, NameOf(policy.PolicyType)!, ns, policy.ValidationFailureAction,
                background: policy.PolicyType != KyvernoPolicyType.RestrictImageRegistries,
                [("", "v1", ["pods"])], rules);
        }

        return policy.PolicyType switch
        {
            KyvernoPolicyType.RestrictRbac => Render(Validating, "restrict-rbac", ns, policy.ValidationFailureAction,
                background: true, [(RbacGroup, "v1", ["roles", "rolebindings"])], RbacRules()),
            KyvernoPolicyType.VerifyImageSignatures => VerifyImages(policy, ns),
            _ => null,
        };
    }

    /// <summary>
    /// The resource name of a built-in namespaced policy — the one the legacy rendering used, and the
    /// one discovery recognises. Null for the kinds that have no namespaced object of their own.
    /// </summary>
    public static string? NameOf(KyvernoPolicyType type) => type switch
    {
        KyvernoPolicyType.DisallowPrivilegedContainers => "disallow-privileged-containers",
        KyvernoPolicyType.DisallowRootUser => "disallow-root-user",
        KyvernoPolicyType.RequireReadOnlyRootFilesystem => "require-readonly-rootfs",
        KyvernoPolicyType.DisallowPrivilegeEscalation => "disallow-privilege-escalation",
        KyvernoPolicyType.DisallowHostNetwork => "disallow-host-network",
        KyvernoPolicyType.DisallowHostPID => "disallow-host-pid",
        KyvernoPolicyType.DisallowHostPath => "disallow-host-path",
        KyvernoPolicyType.RestrictImageRegistries => "restrict-image-registries",
        KyvernoPolicyType.VerifyImageSignatures => "verify-image-signatures",
        KyvernoPolicyType.RequireResourceLimits => "require-resource-limits",
        KyvernoPolicyType.RequireResourceRequests => "require-resource-requests",
        KyvernoPolicyType.RequireSeccompProfile => "require-seccomp-profile",
        KyvernoPolicyType.RequirePodLabels => "require-pod-labels",
        KyvernoPolicyType.RestrictRbac => "restrict-rbac",
        _ => null,
    };

    /// <summary>
    /// The CEL form of <see cref="KyvernoPolicyService.BuildClusterRbacPolicy"/>, with the same list of
    /// app namespaces and the same reasoning: what is refused is the subject, not the creator.
    /// </summary>
    public static string? BuildClusterRbacPolicy(
        IReadOnlyCollection<string> appNamespaces, KyvernoValidationFailureAction mode)
    {
        string[] namespaces = appNamespaces
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Against no namespaces the rule matches nothing while looking like protection.
        if (namespaces.Length == 0) return null;

        IEnumerable<string> groups = KyvernoPolicyService.AlwaysDeniedGroups
            .Concat(namespaces.Select(n => $"system:serviceaccounts:{n}"));

        Rule rule = new(
            "A ClusterRoleBinding may not grant cluster-wide permissions to a customer app namespace.",
            s => $"!has({s}.subjects) || {s}.subjects == null || {s}.subjects.all(u, "
                + $"!(u.kind == 'ServiceAccount' && has(u.namespace) && u.namespace in {CelList(namespaces)})"
                + $" && !(u.kind == 'Group' && u.name in {CelList(groups)}))");

        return Render("ValidatingPolicy", KyvernoPolicyService.ClusterRbacPolicyName, ns: null, mode,
            background: true, [(RbacGroup, "v1", ["clusterrolebindings"])], [rule],
            matchConditions: [("not-a-system-binding", "!object.metadata.name.startsWith('system:')")]);
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    private const string RbacGroup = "rbac.authorization.k8s.io";

    /// <summary>
    /// A message, and whether a subject complies. The subject is <c>object</c> or <c>oldObject</c>, so
    /// the same expression answers "is it compliant" and "was it already not".
    /// </summary>
    private sealed record Rule(string Message, Func<string, string> Complies);

    private static string MainContainers(string s, string test) => $"{s}.spec.containers.all(c, {test})";

    private static string AllContainers(string s, string test) =>
        $"{MainContainers(s, test)} && (!has({s}.spec.initContainers) || {s}.spec.initContainers.all(c, {test}))";

    private static string HasQuantities(string field) =>
        $"has(c.resources) && has(c.resources.{field})"
        + $" && 'cpu' in c.resources.{field} && c.resources.{field}['cpu'] != ''"
        + $" && 'memory' in c.resources.{field} && c.resources.{field}['memory'] != ''";

    private static string Seccomp(string s)
    {
        const string profile = "has(c.securityContext.seccompProfile) && has(c.securityContext.seccompProfile.type)"
            + " && c.securityContext.seccompProfile.type in ['RuntimeDefault', 'Localhost']";

        // Pod level, or on every container. An init container with no securityContext passes, as it
        // always has: refusing those would be a new rule rather than the same one moved.
        return $"(has({s}.spec.securityContext) && has({s}.spec.securityContext.seccompProfile)"
            + $" && has({s}.spec.securityContext.seccompProfile.type)"
            + $" && {s}.spec.securityContext.seccompProfile.type in ['RuntimeDefault', 'Localhost'])"
            + $" || ({MainContainers(s, $"has(c.securityContext) && {profile}")}"
            + $" && (!has({s}.spec.initContainers) || {s}.spec.initContainers.all(c, !has(c.securityContext) || ({profile}))))";
    }

    private static Rule[]? Registries(KyvernoPolicy policy)
    {
        List<string> registries = KyvernoPolicyService.GetConfigList(policy);
        if (registries.Count == 0) return null;

        string pattern = "^(" + string.Join("|", registries.Select(r =>
            WildcardToRe2(r.TrimEnd('/') + (r.Contains('*') ? "" : "/*")))) + ")$";

        return
        [
            new($"Images must come from an approved registry: {string.Join(", ", registries)}",
                s => AllContainers(s, $"c.image.matches({CelString(pattern)})")),
        ];
    }

    private static Rule[]? Labels(KyvernoPolicy policy)
    {
        List<string> labels = KyvernoPolicyService.GetConfigList(policy);
        if (labels.Count == 0) return null;

        return
        [
            new($"Pods must have all required labels: {string.Join(", ", labels)}", s =>
                $"has({s}.metadata.labels)" + string.Concat(labels.Select(l =>
                    $" && {CelString(l)} in {s}.metadata.labels && {s}.metadata.labels[{CelString(l)}] != ''"))),
        ];
    }

    /// <summary>
    /// The admission half of <see cref="AppRbacRuleValidator"/>'s deny-list, read from it so that the
    /// two cannot drift apart.
    ///
    /// <para>The legacy rendering compared with <c>AnyIn</c>, which treats a <c>*</c> on either side as
    /// a wildcard, so its <c>"*"</c> matched every verb and it refused every Role that had a rule. Here
    /// <c>*</c> means the literal wildcard. The subresource wildcards are spelled out because
    /// <c>pods/*</c> and <c>*/exec</c> grant exec as surely as <c>pods/exec</c> does.</para>
    /// </summary>
    private static Rule[] RbacRules()
    {
        IEnumerable<string> deniedResources = AppRbacRuleValidator.DeniedResources
            .Concat(["*", "pods/*"])
            .Concat(AppRbacRuleValidator.DeniedResources
                .Where(r => r.StartsWith("pods/", StringComparison.Ordinal))
                .Select(r => "*" + r["pods".Length..]))
            .Order(StringComparer.Ordinal);

        IEnumerable<string> deniedVerbs = AppRbacRuleValidator.DeniedVerbs.Append("*").Order(StringComparer.Ordinal);

        return
        [
            new("A Role may not use wildcards, manage RBAC, or grant exec, attach or port-forward.", s =>
                $"{s}.kind != 'Role' || !has({s}.rules) || {s}.rules == null || {s}.rules.all(r, "
                + "(!has(r.apiGroups) || !('*' in r.apiGroups))"
                + $" && (!has(r.resources) || !r.resources.exists(x, x in {CelList(deniedResources)}))"
                + $" && (!has(r.verbs) || !r.verbs.exists(x, x in {CelList(deniedVerbs)})))"),
            new("A RoleBinding may not bind to a privileged built-in ClusterRole.", s =>
                $"{s}.kind != 'RoleBinding' || {s}.roleRef.kind != 'ClusterRole'"
                + $" || !({s}.roleRef.name in ['cluster-admin', 'admin', 'edit'])"),
        ];
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private static string Render(
        string kind, string name, string? ns, KyvernoValidationFailureAction mode, bool background,
        IReadOnlyList<(string Group, string Version, string[] Resources)> matches,
        IReadOnlyList<Rule> rules,
        IReadOnlyList<(string Name, string Expression)>? matchConditions = null)
    {
        StringBuilder yaml = Header(kind, name, ns, mode, background, matches);

        if (matchConditions is { Count: > 0 })
        {
            yaml.Append("  matchConditions:\n");
            foreach ((string conditionName, string expression) in matchConditions)
            {
                yaml.Append($"    - name: {conditionName}\n");
                yaml.Append($"      expression: {Quoted(expression)}\n");
            }
        }

        // As variables, so a rule reads as the decision it makes. They are evaluated only when used.
        yaml.Append("  variables:\n");
        for (int i = 0; i < rules.Count; i++)
        {
            yaml.Append($"    - name: complies{i}\n");
            yaml.Append($"      expression: {Quoted(rules[i].Complies("object"))}\n");
            yaml.Append($"    - name: complied{i}\n");
            // Kyverno hands a create an empty oldObject in one path and null in another. `kind` is not
            // a path the pod-controller rewrite touches, so this holds for every kind.
            yaml.Append($"      expression: {Quoted($"oldObject == null || !has(oldObject.kind) || ({rules[i].Complies("oldObject")})")}\n");
        }

        yaml.Append("  validations:\n");
        for (int i = 0; i < rules.Count; i++)
        {
            yaml.Append($"    - expression: {Quoted($"variables.complies{i} || !variables.complied{i}")}\n");
            yaml.Append($"      message: {Quoted(rules[i].Message)}\n");
        }

        return yaml.ToString();
    }

    private static StringBuilder Header(
        string kind, string name, string? ns, KyvernoValidationFailureAction mode, bool background,
        IReadOnlyList<(string Group, string Version, string[] Resources)> matches)
    {
        StringBuilder yaml = new();
        yaml.Append($"apiVersion: {ApiVersion}\n");
        yaml.Append($"kind: {kind}\n");
        yaml.Append("metadata:\n");
        yaml.Append($"  name: {name}\n");
        if (ns is not null) yaml.Append($"  namespace: {ns}\n");
        yaml.Append("spec:\n");
        yaml.Append("  validationActions:\n");
        yaml.Append($"    - {(mode == KyvernoValidationFailureAction.Enforce ? "Deny" : "Audit")}\n");
        yaml.Append("  evaluation:\n");
        yaml.Append("    background:\n");
        yaml.Append($"      enabled: {(background ? "true" : "false")}\n");
        yaml.Append("  matchConstraints:\n");
        yaml.Append("    resourceRules:\n");
        foreach ((string group, string version, string[] resources) in matches)
        {
            yaml.Append($"      - apiGroups: [{Quoted(group)}]\n");
            yaml.Append($"        apiVersions: [{Quoted(version)}]\n");
            yaml.Append("        operations: [\"CREATE\", \"UPDATE\"]\n");
            yaml.Append($"        resources: [{string.Join(", ", resources.Select(Quoted))}]\n");
        }

        return yaml;
    }

    private static string? VerifyImages(KyvernoPolicy policy, string ns)
    {
        KyvernoPolicyService.VerifyImagesConfig config = KyvernoPolicyService.GetVerifyImagesConfig(policy);
        if (!config.IsUsable) return null;

        List<string> patterns = config.ImagePatterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (patterns.Count == 0) return null;

        StringBuilder yaml = Header("NamespacedImageValidatingPolicy", "verify-image-signatures", ns,
            policy.ValidationFailureAction, background: false, [("", "v1", ["pods"])]);

        yaml.Append("  validationConfigurations:\n");
        yaml.Append($"    required: {(config.Required ? "true" : "false")}\n");
        // The admitted digest is the verified one, so a tag cannot be moved afterwards.
        yaml.Append("    mutateDigest: true\n");
        yaml.Append("    verifyDigest: true\n");

        yaml.Append("  matchImageReferences:\n");
        foreach (string pattern in patterns)
        {
            yaml.Append($"    - glob: {Quoted(pattern)}\n");
        }

        List<string> attestors = [];
        yaml.Append("  attestors:\n");

        if (!string.IsNullOrWhiteSpace(config.PublicKey))
        {
            // The legacy field took several keys in one value and accepted a signature by any of them.
            // Here a key is one attestor, and the count below accepts any.
            foreach (string key in SplitKeys(config.PublicKey))
            {
                attestors.Add($"key{attestors.Count}");
                yaml.Append($"    - name: {attestors[^1]}\n");
                yaml.Append("      cosign:\n");
                yaml.Append("        key:\n");
                yaml.Append($"          {(key.StartsWith("-----", StringComparison.Ordinal) || !key.Contains("://") ? "data" : "kms")}: {Quoted(key)}\n");
            }
        }
        else
        {
            attestors.Add("keyless");
            yaml.Append("    - name: keyless\n");
            yaml.Append("      cosign:\n");
            yaml.Append("        keyless:\n");
            yaml.Append("          identities:\n");
            // Legacy issuer and subject were wildcard patterns; these fields are exact, with a regular
            // expression as the alternative. Copying a pattern across would match no identity at all.
            yaml.Append($"            - {Identity("issuer", config.Issuer!)}\n");
            yaml.Append($"              {Identity("subject", config.Subject!)}\n");
            yaml.Append("        ctlog:\n");
            yaml.Append("          url: https://rekor.sigstore.dev\n");
        }

        string verified = $"verifyImageSignatures(image, [{string.Join(", ", attestors.Select(a => $"attestors.{a}"))}]) > 0";

        // `images` is already narrowed to the patterns above, so an image outside them is not judged.
        yaml.Append("  validations:\n");
        foreach (string list in new[] { "containers", "initContainers", "ephemeralContainers" })
        {
            yaml.Append($"    - expression: {Quoted($"images.{list}.all(image, {verified})")}\n");
            yaml.Append("      message: \"Image signature could not be verified.\"\n");
        }

        return yaml.ToString();
    }

    private static string Identity(string field, string value) =>
        value.Contains('*') || value.Contains('?')
            ? $"{field}RegExp: {Quoted($"^{WildcardToRe2(value)}$")}"
            : $"{field}: {Quoted(value)}";

    private static IEnumerable<string> SplitKeys(string keys)
    {
        const string end = "-----END PUBLIC KEY-----";
        string rest = keys.Replace("\r\n", "\n").Trim();

        while (rest.IndexOf(end, StringComparison.Ordinal) is int at and >= 0)
        {
            yield return rest[..(at + end.Length)].Trim();
            rest = rest[(at + end.Length)..].Trim();
        }

        if (rest.Length > 0) yield return rest;
    }

    // ── Escaping ──────────────────────────────────────────────────────────────

    /// <summary>A Kyverno wildcard (<c>*</c> any run, <c>?</c> one character) as an RE2 fragment.</summary>
    private static string WildcardToRe2(string wildcard)
    {
        StringBuilder re = new();
        foreach (char c in wildcard)
        {
            re.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ when @"\.+()|[]{}^$".Contains(c) => $"\\{c}",
                _ => c.ToString(),
            });
        }

        return re.ToString();
    }

    private static string CelString(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r") + "'";

    private static string CelList(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(CelString))}]";

    /// <summary>A YAML double-quoted scalar. JSON strings are a subset of those.</summary>
    private static string Quoted(string value) => JsonSerializer.Serialize(value, Relaxed);

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
