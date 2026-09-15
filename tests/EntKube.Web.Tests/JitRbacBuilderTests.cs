using EntKube.Web.Data;
using EntKube.Web.Services.Jit;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The RBAC a grant actually creates.
///
/// These are mostly absence assertions. Every level is defined by what it withholds — a rule that
/// crept in would not fail anything obvious at runtime, it would just quietly widen every grant
/// already issued, so the verbs that must never appear are asserted at every level rather than
/// only at the one where they were most tempting.
/// </summary>
public class JitRbacBuilderTests
{
    // ── Naming ────────────────────────────────────────────────────────────────

    [Fact]
    public void Object_names_are_per_grant_and_fit_a_dns_label()
    {
        Guid grantId = Guid.NewGuid();

        string sa = JitRbacBuilder.ServiceAccountName(grantId);

        sa.Should().StartWith("jit-");
        sa.Length.Should().BeLessThanOrEqualTo(63);
        JitRbacBuilder.RoleName(grantId).Length.Should().BeLessThanOrEqualTo(63);
        JitRbacBuilder.RoleBindingName(grantId).Length.Should().BeLessThanOrEqualTo(63);
        sa.Should().MatchRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$");
    }

    [Fact]
    public void Two_grants_get_two_sets_of_objects()
    {
        // A shared ServiceAccount would make early revocation impossible: a bound token cannot be
        // invalidated, so revoking means deleting the binding, and that only works when the
        // binding belongs to one grant.
        JitRbacBuilder.ServiceAccountName(Guid.NewGuid())
            .Should().NotBe(JitRbacBuilder.ServiceAccountName(Guid.NewGuid()));
    }

    // ── What every level withholds ────────────────────────────────────────────

    [Theory]
    [InlineData(JitAccessLevel.Observe)]
    [InlineData(JitAccessLevel.Troubleshoot)]
    [InlineData(JitAccessLevel.Operate)]
    public void No_level_can_read_secrets(JitAccessLevel level)
    {
        Resources(level).Should().NotContain("secrets");
    }

    [Theory]
    [InlineData(JitAccessLevel.Observe)]
    [InlineData(JitAccessLevel.Troubleshoot)]
    [InlineData(JitAccessLevel.Operate)]
    public void No_level_can_port_forward(JitAccessLevel level)
    {
        // Port-forward reaches anything the pod can reach, which includes other namespaces.
        Resources(level).Should().NotContain("pods/portforward");
    }

    [Theory]
    [InlineData(JitAccessLevel.Observe)]
    [InlineData(JitAccessLevel.Troubleshoot)]
    [InlineData(JitAccessLevel.Operate)]
    public void No_level_can_manage_rbac_or_impersonate(JitAccessLevel level)
    {
        Resources(level).Should().NotIntersectWith(
            ["roles", "rolebindings", "clusterroles", "clusterrolebindings"]);
        Verbs(level).Should().NotIntersectWith(["escalate", "bind", "impersonate"]);
    }

    [Theory]
    [InlineData(JitAccessLevel.Observe)]
    [InlineData(JitAccessLevel.Troubleshoot)]
    [InlineData(JitAccessLevel.Operate)]
    public void No_level_uses_a_wildcard(JitAccessLevel level)
    {
        Resources(level).Should().NotContain("*");
        Verbs(level).Should().NotContain("*");
        JitRbacBuilder.RulesFor(level).Select(r => r.ApiGroup).Should().NotContain("*");
    }

    [Theory]
    [InlineData(JitAccessLevel.Observe)]
    [InlineData(JitAccessLevel.Troubleshoot)]
    [InlineData(JitAccessLevel.Operate)]
    public void No_level_reaches_a_cluster_scoped_resource(JitAccessLevel level)
    {
        // Nodes and namespaces are shared; listing them enumerates other customers. A Role cannot
        // grant them at all, but naming one would produce a rule that silently does nothing.
        Resources(level).Should().NotIntersectWith(
            ["nodes", "namespaces", "persistentvolumes", "storageclasses"]);
    }

    // ── What each level grants ────────────────────────────────────────────────

    [Fact]
    public void Observe_reads_the_workload_and_its_logs()
    {
        IReadOnlyList<JitRule> rules = JitRbacBuilder.RulesFor(JitAccessLevel.Observe);

        Resources(JitAccessLevel.Observe).Should()
            .Contain(["pods", "services", "events", "deployments", "ingresses", "pods/log"]);

        // Logs are read with `get`. `list` on pods/log is not a thing, so asking for it would
        // render a rule that grants less than it appears to.
        rules.Single(r => r.Resources.Contains("pods/log")).Verbs.Should().Equal("get");
    }

    [Fact]
    public void Observe_cannot_change_anything()
    {
        Verbs(JitAccessLevel.Observe).Should().BeSubsetOf(["get", "list", "watch"]);
    }

    [Fact]
    public void Troubleshoot_adds_configmaps_but_not_secrets()
    {
        // The two sit next to each other in every example of this rule, which is exactly why the
        // omission is worth asserting rather than assuming.
        IReadOnlyList<string> resources = Resources(JitAccessLevel.Troubleshoot);

        resources.Should().Contain("configmaps");
        resources.Should().NotContain("secrets");
    }

    [Fact]
    public void Troubleshoot_adds_exec_as_a_create()
    {
        JitRule exec = JitRbacBuilder.RulesFor(JitAccessLevel.Troubleshoot)
            .Single(r => r.Resources.Contains("pods/exec"));

        exec.Verbs.Should().Equal("create");
        Resources(JitAccessLevel.Observe).Should().NotContain("pods/exec");
    }

    [Fact]
    public void Operate_adds_restart_and_scale()
    {
        IReadOnlyList<JitRule> rules = JitRbacBuilder.RulesFor(JitAccessLevel.Operate);

        rules.Should().Contain(r => r.Resources.Contains("pods") && r.Verbs.Contains("delete"));
        rules.Should().Contain(r => r.Resources.Contains("deployments/scale") && r.Verbs.Contains("patch"));

        Verbs(JitAccessLevel.Troubleshoot).Should().NotContain("delete");
    }

    [Fact]
    public void Each_level_contains_the_one_below_it()
    {
        // Levels are built by addition. A level defined by a copied-and-edited list drifts from
        // the one it was copied from the first time either changes.
        Resources(JitAccessLevel.Troubleshoot).Should().Contain(Resources(JitAccessLevel.Observe));
        Resources(JitAccessLevel.Operate).Should().Contain(Resources(JitAccessLevel.Troubleshoot));
    }

    // ── Manifest ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_manifest_is_three_namespaced_objects()
    {
        string yaml = JitRbacBuilder.BuildManifest(Grant(JitAccessLevel.Observe));

        yaml.Should().Contain("kind: ServiceAccount");
        yaml.Should().Contain("kind: Role\n");
        yaml.Should().Contain("kind: RoleBinding");

        // Never a ClusterRole or ClusterRoleBinding — the grant is confined to one namespace and
        // a cluster-scoped object would be exactly the escape it exists to prevent.
        yaml.Should().NotContain("ClusterRole");
        yaml.Split("\n---\n").Should().HaveCount(3);
    }

    [Fact]
    public void Every_object_names_the_grants_namespace()
    {
        string yaml = JitRbacBuilder.BuildManifest(Grant(JitAccessLevel.Operate));

        yaml.Split("\n---\n").Should().OnlyContain(doc => doc.Contains("namespace: acme-billing"));
    }

    [Fact]
    public void The_service_account_does_not_automount()
    {
        // Nothing mounts this into a pod — the only token it issues is the bound one from mint.
        // Saying so means a pod that named it by accident does not acquire the grant.
        JitRbacBuilder.BuildManifest(Grant(JitAccessLevel.Observe))
            .Should().Contain("automountServiceAccountToken: false");
    }

    [Fact]
    public void Every_object_is_labelled_with_its_grant()
    {
        // The orphan sweep finds objects by label, not by name, so an object whose name cannot be
        // parsed back to a grant is still identifiable as ours.
        JitGrant grant = Grant(JitAccessLevel.Observe);

        string yaml = JitRbacBuilder.BuildManifest(grant);

        yaml.Split("\n---\n").Should().OnlyContain(doc =>
            doc.Contains($"{JitRbacBuilder.ManagedLabel}: {JitRbacBuilder.ManagedValue}")
            && doc.Contains($"{JitRbacBuilder.GrantLabel}: \"{grant.Id}\""));
    }

    [Fact]
    public void The_binding_points_at_the_grants_own_role()
    {
        JitGrant grant = Grant(JitAccessLevel.Observe);

        string binding = JitRbacBuilder.BuildManifest(grant).Split("\n---\n")[2];

        binding.Should().Contain("kind: Role\n");
        binding.Should().Contain($"name: {JitRbacBuilder.RoleName(grant.Id)}");
        binding.Should().Contain($"name: {JitRbacBuilder.ServiceAccountName(grant.Id)}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> Resources(JitAccessLevel level) =>
        JitRbacBuilder.RulesFor(level).SelectMany(r => r.Resources).Distinct().ToList();

    private static IReadOnlyList<string> Verbs(JitAccessLevel level) =>
        JitRbacBuilder.RulesFor(level).SelectMany(r => r.Verbs).Distinct().ToList();

    private static JitGrant Grant(JitAccessLevel level) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        AppId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        KubernetesClusterId = Guid.NewGuid(),
        Namespace = "acme-billing",
        UserId = "customer@acme.example",
        Reason = "debugging",
        RequestedBy = "customer@acme.example",
        Level = level,
    };
}
