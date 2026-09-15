using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Covers the three places an app's ServiceAccount used to be able to acquire more than its
/// namespace: an unvalidated governance rule, a deployment manifest carrying its own RBAC, and
/// RBAC arriving by a path that never touches either — caught at admission instead.
///
/// The deny-lists in <see cref="AppRbacRuleValidator"/> and the <c>restrict-rbac</c> Kyverno
/// policy are asserted against each other here on purpose: they are two enforcement points for
/// one decision, and a change to either that does not reach the other is the failure this is
/// meant to catch.
/// </summary>
public class AppRbacHardeningTests
{
    // ── Rule validation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("*", "pods", "get")]
    [InlineData("", "*", "get")]
    [InlineData("", "pods", "*")]
    public void Wildcards_are_blocked_in_every_field(string groups, string resources, string verbs)
    {
        AppRbacRuleValidator.Validate(groups, resources, verbs)
            .Should().Contain(v => v.Severity == RbacRuleSeverity.Blocked);
    }

    [Theory]
    [InlineData("escalate")]
    [InlineData("bind")]
    [InlineData("impersonate")]
    public void Escalation_verbs_are_blocked(string verb)
    {
        AppRbacRuleValidator.Validate("rbac.authorization.k8s.io", "roles", verb)
            .Should().Contain(v => v.Severity == RbacRuleSeverity.Blocked);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("rolebindings")]
    [InlineData("clusterroles")]
    [InlineData("clusterrolebindings")]
    [InlineData("pods/exec")]
    [InlineData("pods/attach")]
    [InlineData("pods/portforward")]
    public void Escalation_resources_are_blocked(string resource)
    {
        AppRbacRuleValidator.Validate("", resource, "create")
            .Should().Contain(v => v.Severity == RbacRuleSeverity.Blocked);
    }

    [Fact]
    public void Secrets_warn_but_do_not_block()
    {
        IReadOnlyList<RbacRuleViolation> violations =
            AppRbacRuleValidator.Validate("", "secrets", "get,list");

        violations.Should().NotBeEmpty();
        violations.Should().OnlyContain(v => v.Severity == RbacRuleSeverity.Warning);
    }

    [Fact]
    public void An_ordinary_read_rule_is_clean()
    {
        AppRbacRuleValidator.Validate("", "pods,services,configmaps", "get,list,watch")
            .Should().BeEmpty();
    }

    [Fact]
    public void Core_api_group_is_expressed_as_empty_and_is_not_a_violation()
    {
        // The UI stores the core group as "", not as a missing field. Treating that as an
        // empty rule would make every core-group grant unsaveable.
        AppRbacRuleValidator.Validate("", "pods", "get")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "", "get")]
    [InlineData("", "pods", "")]
    public void A_rule_granting_nothing_is_blocked(string groups, string resources, string verbs)
    {
        AppRbacRuleValidator.Validate(groups, resources, verbs)
            .Should().Contain(v => v.Severity == RbacRuleSeverity.Blocked);
    }

    [Fact]
    public void Every_blocked_reason_is_reported_at_once()
    {
        // One violation at a time would mean fixing a rule by repeated rejection.
        AppRbacRuleValidator.Validate("*", "pods/exec", "escalate")
            .Where(v => v.Severity == RbacRuleSeverity.Blocked)
            .Should().HaveCountGreaterThan(2);
    }

    [Fact]
    public void ThrowIfBlocked_passes_a_warning_and_refuses_a_block()
    {
        Action warning = () => AppRbacRuleValidator.ThrowIfBlocked("", "secrets", "get");
        warning.Should().NotThrow();

        Action blocked = () => AppRbacRuleValidator.ThrowIfBlocked("", "pods", "*");
        blocked.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Stored_rules_are_reported_rather_than_rejected()
    {
        // Rules written before the deny-list existed must stay visible without breaking an apply.
        List<AppRbacRule> existing =
        [
            new() { Id = Guid.NewGuid(), ApiGroups = "*", Resources = "*", Verbs = "*" },
            new() { Id = Guid.NewGuid(), ApiGroups = "", Resources = "pods", Verbs = "get" },
        ];

        AppRbacRuleValidator.ValidateExisting(existing)
            .Should().Contain(v => v.Severity == RbacRuleSeverity.Blocked);
    }

    // ── Manifest kinds ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rbac.authorization.k8s.io/v1", "ClusterRoleBinding")]
    [InlineData("rbac.authorization.k8s.io/v1", "ClusterRole")]
    [InlineData("rbac.authorization.k8s.io/v1", "Role")]
    [InlineData("rbac.authorization.k8s.io/v1", "RoleBinding")]
    [InlineData("admissionregistration.k8s.io/v1", "ValidatingWebhookConfiguration")]
    [InlineData("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration")]
    [InlineData("apiregistration.k8s.io/v1", "APIService")]
    public void Denied_kinds_are_refused_in_app_manifests(string apiVersion, string kind)
    {
        string yaml = $"""
            apiVersion: {apiVersion}
            kind: {kind}
            metadata:
              name: escalate-me
            """;

        ManifestKindPolicy.CheckYaml([yaml], "acme-billing")
            .Should().NotBeNull().And.Contain(kind);
    }

    [Fact]
    public void A_cluster_scoped_binding_is_caught_even_though_it_names_no_namespace()
    {
        // This is the gap the kind check exists for: the namespace governance check looks for a
        // namespace field pointing elsewhere, and a ClusterRoleBinding has none to find.
        string yaml = """
            apiVersion: rbac.authorization.k8s.io/v1
            kind: ClusterRoleBinding
            metadata:
              name: billing-cluster-admin
            roleRef:
              apiGroup: rbac.authorization.k8s.io
              kind: ClusterRole
              name: cluster-admin
            subjects:
              - kind: ServiceAccount
                name: billing-api
                namespace: acme-billing
            """;

        ManifestKindPolicy.CheckYaml([yaml], "acme-billing").Should().NotBeNull();
    }

    [Fact]
    public void An_ordinary_workload_manifest_passes()
    {
        string yaml = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: billing-api
              namespace: acme-billing
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: billing-api
              namespace: acme-billing
            ---
            apiVersion: v1
            kind: ServiceAccount
            metadata:
              name: billing-api
              namespace: acme-billing
            """;

        ManifestKindPolicy.CheckYaml([yaml], "acme-billing").Should().BeNull();
    }

    [Fact]
    public void The_refusal_names_every_offending_resource()
    {
        string yaml = """
            apiVersion: rbac.authorization.k8s.io/v1
            kind: Role
            metadata:
              name: first
              namespace: acme-billing
            ---
            apiVersion: rbac.authorization.k8s.io/v1
            kind: ClusterRole
            metadata:
              name: second
            """;

        string? error = ManifestKindPolicy.CheckYaml([yaml], "acme-billing");
        error.Should().NotBeNull();
        error.Should().Contain("first").And.Contain("second");
    }

    // ── Kyverno policy ────────────────────────────────────────────────────────

    [Fact]
    public void Restrict_rbac_policy_renders_for_the_namespace()
    {
        string yaml = Render();

        yaml.Should().Contain("name: restrict-rbac");
        yaml.Should().Contain("namespace: acme-billing");
        yaml.Should().Contain("- Role");
        yaml.Should().Contain("- RoleBinding");
    }

    [Fact]
    public void Restrict_rbac_defaults_every_jmespath_projection()
    {
        // A Role that omits apiGroups projects to null, and a rule that fails to evaluate denies
        // the resource — so a missing default would block ordinary Roles instead of dangerous ones.
        string yaml = Render();

        foreach (string path in new[] { "apiGroups[]", "resources[]", "verbs[]" })
            yaml.Should().Contain($"request.object.rules[].{path} || `[]`");
    }

    [Fact]
    public void Restrict_rbac_denies_the_privileged_built_in_cluster_roles()
    {
        string yaml = Render();

        yaml.Should().Contain("roleRef.kind");
        foreach (string role in new[] { "cluster-admin", "admin", "edit" })
            yaml.Should().Contain(role);
    }

    [Fact]
    public void The_admission_policy_and_the_validator_deny_the_same_things()
    {
        // Two enforcement points, one decision. If a term is added to the validator's deny-list
        // and not to the policy, a Role refused in the UI is still admitted from a Helm chart.
        string yaml = Render();

        foreach (string verb in AppRbacRuleValidator.DeniedVerbs)
            yaml.Should().Contain($"\"{verb}\"", $"the policy must deny the verb '{verb}' too");

        foreach (string resource in AppRbacRuleValidator.DeniedResources)
            yaml.Should().Contain($"\"{resource}\"", $"the policy must deny '{resource}' too");
    }

    [Fact]
    public void Secrets_are_not_denied_at_admission_because_they_are_only_a_warning()
    {
        // The validator warns on secrets rather than blocking. Denying them here would refuse a
        // Role that the governance UI had just saved without complaint.
        Render().Should().NotContain("\"secrets\"");
    }

    [Fact]
    public void Mode_follows_the_policy_setting()
    {
        Render(KyvernoValidationFailureAction.Enforce)
            .Should().Contain("validationFailureAction: Enforce");

        Render().Should().Contain("validationFailureAction: Audit");
    }

    private static string Render(
        KyvernoValidationFailureAction mode = KyvernoValidationFailureAction.Audit)
    {
        KyvernoPolicy policy = new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EnvironmentId = Guid.NewGuid(),
            PolicyType = KyvernoPolicyType.RestrictRbac,
            ValidationFailureAction = mode,
        };

        return KyvernoPolicyService.BuildManifest([policy], "acme-billing");
    }
}
