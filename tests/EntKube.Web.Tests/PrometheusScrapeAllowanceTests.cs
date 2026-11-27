using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;
using k8s.Models;

namespace EntKube.Web.Tests;

/// <summary>
/// The NetworkPolicies a governance apply writes: that they are policies at all, and that a
/// namespace they close stays open to the cluster's Prometheus and to nobody else.
///
/// <para><b>Judged, not string-matched.</b> Every manifest is parsed back into
/// <see cref="V1NetworkPolicy"/> objects the way the API server would read them, and
/// <see cref="PrometheusAdmission"/> — the engine the monitor planner trusts to decide whether a
/// target is reachable — answers whether Prometheus gets in. A policy whose YAML does not parse
/// fails here exactly as it failed on the cluster.</para>
/// </summary>
public class PrometheusScrapeAllowanceTests
{
    private const string AppNamespace = "billing";

    private static AppGovernanceData With(params AppNetworkPolicy[] policies) => new() { NetworkPolicies = [.. policies] };

    private static AppNetworkPolicy Policy(AppNetworkPolicyType type, string name = "policy", string? fromNamespace = null) => new()
    {
        Name = name,
        PolicyType = type,
        AllowFromNamespace = fromNamespace,
    };

    // ──────── Every built-in policy is a policy ────────

    /// <summary>
    /// The three allow types were written as the header followed directly by their ingress block,
    /// with no line break between, so <c>podSelector: {}</c> and <c>ingress:</c> landed on one line.
    /// kubectl refused the document, and with it the whole governance apply.
    /// </summary>
    [Theory]
    [InlineData(AppNetworkPolicyType.AllowFromIngress, null)]
    [InlineData(AppNetworkPolicyType.AllowFromSameNamespace, null)]
    [InlineData(AppNetworkPolicyType.AllowFromNamespace, "partners")]
    public void An_allow_policy_parses_as_a_NetworkPolicy_with_its_ingress_rule(
        AppNetworkPolicyType type, string? fromNamespace)
    {
        string manifest = AppGovernanceService.BuildManifest(
            With(Policy(type, "allow", fromNamespace)), AppNamespace, prometheusNamespace: null);

        V1NetworkPolicy policy = PrometheusJudge.NetworkPolicies(manifest).Should().ContainSingle().Subject;

        policy.Metadata.Name.Should().Be("allow");
        policy.Spec.PodSelector.Should().NotBeNull();
        policy.Spec.Ingress.Should().ContainSingle()
            .Which.FromProperty.Should().ContainSingle("each allow type names exactly one source");
    }

    [Fact]
    public void Deny_all_parses_and_closes_both_directions()
    {
        string manifest = AppGovernanceService.BuildManifest(
            With(Policy(AppNetworkPolicyType.DenyAll, "deny")), AppNamespace, prometheusNamespace: null);

        V1NetworkPolicy deny = PrometheusJudge.NetworkPolicies(manifest).Should().ContainSingle().Subject;

        deny.Spec.PolicyTypes.Should().BeEquivalentTo("Ingress", "Egress");
        deny.Spec.Ingress.Should().BeNullOrEmpty();
    }

    // ──────── Prometheus is let in, and only Prometheus ────────

    /// <summary>
    /// Every built-in type selects every pod and restricts ingress, so each one on its own shuts
    /// Prometheus out of the namespace — not only "Deny all".
    /// </summary>
    [Theory]
    [InlineData(AppNetworkPolicyType.DenyAll, null)]
    [InlineData(AppNetworkPolicyType.AllowFromIngress, null)]
    [InlineData(AppNetworkPolicyType.AllowFromSameNamespace, null)]
    [InlineData(AppNetworkPolicyType.AllowFromNamespace, "partners")]
    public void A_namespace_governance_closes_stays_open_to_the_clusters_Prometheus(
        AppNetworkPolicyType type, string? fromNamespace)
    {
        string manifest = AppGovernanceService.BuildManifest(
            With(Policy(type, "policy", fromNamespace)), AppNamespace, prometheusNamespace: "monitoring");

        PrometheusJudge.LetsIn(manifest, AppNamespace, prometheusLivesIn: "monitoring").Should().BeTrue(
            "Prometheus has to reach the app's metrics port, or the target is down for as long as the policy stands");
    }

    /// <summary>
    /// The allowance names Prometheus's namespace and Prometheus's pods. A tenant who runs a pod
    /// labelled <c>app.kubernetes.io/name: prometheus</c> in a namespace of their own is still shut out.
    /// </summary>
    [Fact]
    public void A_pod_that_only_calls_itself_prometheus_is_still_shut_out()
    {
        string manifest = AppGovernanceService.BuildManifest(
            With(Policy(AppNetworkPolicyType.DenyAll)), AppNamespace, prometheusNamespace: "monitoring");

        PrometheusJudge.LetsIn(manifest, AppNamespace, prometheusLivesIn: "tenant-b").Should().BeFalse();
    }

    /// <summary>
    /// With no Prometheus on the cluster, or one whose namespace nobody recorded, nothing is opened:
    /// a guessed namespace would admit somebody else's pods.
    /// </summary>
    [Fact]
    public void Without_a_known_Prometheus_nothing_is_opened()
    {
        string manifest = AppGovernanceService.BuildManifest(
            With(Policy(AppNetworkPolicyType.DenyAll)), AppNamespace, prometheusNamespace: null);

        PrometheusJudge.LetsIn(manifest, AppNamespace, prometheusLivesIn: "monitoring").Should().BeFalse();
    }

    /// <summary>
    /// ⚠️ An ingress allowance is itself an ingress policy: written into a namespace nothing else
    /// closes, it would CLOSE it to everything but Prometheus. So it goes only where a built-in
    /// policy already shuts the namespace — never on its own, and never beside a custom policy whose
    /// selector and types it cannot see.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_namespace_no_built_in_policy_closes_gets_no_policy_from_this(bool withCustomPolicy)
    {
        AppGovernanceData data = withCustomPolicy
            ? With(new AppNetworkPolicy
            {
                Name = "egress-only",
                PolicyType = AppNetworkPolicyType.Custom,
                CustomYaml = """
                    apiVersion: networking.k8s.io/v1
                    kind: NetworkPolicy
                    metadata:
                      name: egress-only
                    spec:
                      podSelector: {}
                      policyTypes:
                        - Egress
                    """,
            })
            : With();

        string manifest = AppGovernanceService.BuildManifest(data, AppNamespace, prometheusNamespace: "monitoring");

        PrometheusJudge.NetworkPolicies(manifest).Select(p => p.Metadata.Name)
            .Should().BeEquivalentTo(withCustomPolicy ? ["egress-only"] : Array.Empty<string>());
    }
}

/// <summary>
/// Parses a multi-document manifest and asks <see cref="PrometheusAdmission"/> whether the
/// NetworkPolicies in it let a Prometheus living in a given namespace scrape an ordinary app pod.
/// </summary>
internal static class PrometheusJudge
{
    /// <summary>The app's metrics port. The allowance must not depend on knowing it.</summary>
    public const int MetricsPort = 9464;

    public static List<V1NetworkPolicy> NetworkPolicies(string manifest)
        => [.. manifest.Split("\n---\n")
            .Where(doc => doc.Contains("kind: NetworkPolicy"))
            .Select(doc => k8s.KubernetesYaml.Deserialize<V1NetworkPolicy>(doc))];

    /// <param name="prometheusLivesIn">
    /// Where the pod asking to scrape actually runs. Its pods carry the labels the Prometheus
    /// Operator gives every Prometheus, so the only thing that can tell it apart is its namespace.
    /// </param>
    public static bool LetsIn(string manifest, string appNamespace, string prometheusLivesIn)
    {
        V1Pod app = new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = "app-0",
                NamespaceProperty = appNamespace,
                Labels = new Dictionary<string, string> { ["app"] = "app" },
            },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "app",
                        Ports = [new V1ContainerPort { Name = "metrics", ContainerPort = MetricsPort }],
                    },
                ],
            },
            Status = new V1PodStatus { PodIP = "10.0.0.9" },
        };

        PrometheusAdmission engine = new(
            prometheusLivesIn,
            new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = prometheusLivesIn },
            [
                new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = "prometheus",
                    ["app.kubernetes.io/managed-by"] = "prometheus-operator",
                    ["operator.prometheus.io/name"] = "kps-kube-prometheus-stack-prometheus",
                },
            ]);

        return engine.Admits(
            new ScrapedEndpoint(app, MetricsPort, "/metrics"),
            new LiveNamespace(appNamespace, [app], [], NetworkPolicies(manifest)));
    }
}
