using EntKube.Web.Services.Jit;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The proxy's namespace check.
///
/// Redundant with the grant's RoleBinding by design — the RBAC would already refuse most of this —
/// but it fails closed where RBAC cannot, and a path shape nobody anticipated should be refused
/// rather than forwarded. So the assertions that matter most are the ones about paths this code
/// has never seen.
/// </summary>
public class JitPathPolicyTests
{
    private const string Ns = "acme-billing";

    // ── Allowed ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/v1/namespaces/acme-billing/pods")]
    [InlineData("api/v1/namespaces/acme-billing/pods")]
    [InlineData("/api/v1/namespaces/acme-billing/pods/billing-7d9f/log")]
    [InlineData("/api/v1/namespaces/acme-billing/events")]
    [InlineData("/apis/apps/v1/namespaces/acme-billing/deployments")]
    [InlineData("/apis/apps/v1/namespaces/acme-billing/deployments/billing/scale")]
    [InlineData("/apis/networking.k8s.io/v1/namespaces/acme-billing/ingresses")]
    [InlineData("/api/v1/namespaces/acme-billing")]
    public void Requests_inside_the_grants_namespace_are_allowed(string path)
    {
        JitPathPolicy.Check(path, Ns).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("/version")]
    [InlineData("/api")]
    [InlineData("/api/v1")]
    [InlineData("/apis")]
    [InlineData("/apis/apps")]
    [InlineData("/apis/apps/v1")]
    [InlineData("/openapi/v2")]
    [InlineData("/openapi/v3/apis/apps/v1")]
    [InlineData("/healthz")]
    public void Discovery_is_allowed_because_kubectl_cannot_work_without_it(string path)
    {
        // None of these is namespaced and none says anything about another tenant — they describe
        // what the server can do, not what is running on it.
        JitPathPolicy.Check(path, Ns).Allowed.Should().BeTrue();
    }

    // ── Refused ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/v1/namespaces/globex-api/pods")]
    [InlineData("/apis/apps/v1/namespaces/globex-api/deployments")]
    [InlineData("/api/v1/namespaces/globex-api")]
    public void Another_namespace_is_refused(string path)
    {
        JitPathVerdict verdict = JitPathPolicy.Check(path, Ns);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain(Ns);
    }

    [Theory]
    [InlineData("/api/v1/pods")]
    [InlineData("/apis/apps/v1/deployments")]
    [InlineData("/api/v1/namespaces")]
    public void A_request_spanning_all_namespaces_is_refused(string path)
    {
        // This is what `kubectl get pods --all-namespaces` sends. It would enumerate every
        // customer on the cluster.
        JitPathPolicy.Check(path, Ns).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/v1/nodes")]
    [InlineData("/api/v1/persistentvolumes")]
    [InlineData("/apis/rbac.authorization.k8s.io/v1/clusterroles")]
    [InlineData("/apis/storage.k8s.io/v1/storageclasses")]
    public void Cluster_scoped_resources_are_refused(string path)
    {
        // Nodes and volumes are shared. Listing them enumerates other customers' workloads by
        // implication even when their own objects stay hidden.
        JitPathPolicy.Check(path, Ns).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/v1/namespaces/acme-billing/../globex-api/pods")]
    [InlineData("/api/v1/namespaces/./acme-billing/pods")]
    public void Path_traversal_is_refused_rather_than_normalised(string path)
    {
        // Normalising would mean the check ran against a different path than the one forwarded.
        JitPathPolicy.Check(path, Ns).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData(null)]
    public void An_empty_path_is_refused(string? path)
    {
        JitPathPolicy.Check(path, Ns).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("/logs")]
    [InlineData("/metrics")]
    [InlineData("/debug/pprof")]
    [InlineData("/.well-known/openid-configuration")]
    public void Anything_outside_the_api_surface_is_refused(string path)
    {
        // An allow-list, not a deny-list: the API server grows endpoints every release, and
        // /logs in particular serves the control plane's own log files.
        JitPathPolicy.Check(path, Ns).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_namespace_that_merely_starts_the_same_is_refused()
    {
        // Prefix matching here would hand "acme-billing-staging" to a grant for "acme-billing".
        JitPathPolicy.Check("/api/v1/namespaces/acme-billing-staging/pods", Ns)
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public void Namespace_matching_is_case_sensitive()
    {
        // Kubernetes names are case-sensitive, so "Acme-Billing" is a different namespace, not
        // the same one spelled differently.
        JitPathPolicy.Check("/api/v1/namespaces/ACME-BILLING/pods", Ns).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_second_namespaces_segment_is_refused()
    {
        // A shape this check has not reasoned about. Refusing beats assuming the first one governs.
        JitPathPolicy.Check("/api/v1/namespaces/acme-billing/pods/namespaces/globex-api", Ns)
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_grant_with_no_namespace_can_reach_nothing()
    {
        // Fail closed: a grant in this state is a bug, and the safe reading is that it grants
        // nothing rather than everything.
        JitPathPolicy.Check("/api/v1/namespaces/acme-billing/pods", "").Allowed.Should().BeFalse();
        JitPathPolicy.Check("/version", "").Allowed.Should().BeFalse();
    }
}
