using EntKube.Contracts.Catalog;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Letting the cluster's Prometheus through a NetworkPolicy that would otherwise shut it out.
///
/// <para><b>Why it exists.</b> EntKube writes policies that close a namespace to everything it was
/// not told about: a governance "Deny all", a Connectivity tab default-deny. Prometheus was never one
/// of the things they were told about, so the moment one of them was applied the app's metrics
/// stopped, the target went down and the stack's TargetDown alert turned that into an incident —
/// for a policy that was doing exactly what it said.</para>
///
/// <para>Every writer asks the same two questions here, so the SeaweedFS rule, the governance policy
/// and the connectivity plan cannot disagree about who Prometheus is.</para>
/// </summary>
public static class PrometheusScrapeAllowance
{
    /// <summary>
    /// The namespace a NetworkPolicy rule lets Prometheus in from: the one the monitor engine judges admission
    /// against, <see cref="PrometheusService.GetPrometheusConfig(InstalledComponent)"/>, whose Configuration
    /// override (set for adopted stacks) wins over the column. Two answers would open the port to
    /// prometheus-labelled pods of a namespace that is not Prometheus's and leave the real one out.
    ///
    /// <para>Except that a blank column is unknown, never the engine's <c>monitoring</c> default: a guessed
    /// namespace would admit somebody else's pods to a port that serves pprof. Only an explicit override then
    /// names it, and with none the answer is blank and nothing is opened.</para>
    /// </summary>
    public static string? NamespaceToAdmit(InstalledComponent? prometheus)
    {
        if (prometheus is null)
        {
            return null;
        }

        // "" rather than null: GetPrometheusConfig fills only a null column with its default.
        string column = string.IsNullOrWhiteSpace(prometheus.Namespace) ? "" : prometheus.Namespace;
        return PrometheusService.GetPrometheusConfig(prometheus with { Namespace = column }).Namespace;
    }

    /// <summary>
    /// A NetworkPolicy that lets the cluster's Prometheus into every pod of a namespace.
    ///
    /// <para><b>Who gets in.</b> Pods the Prometheus Operator labels as a Prometheus, AND only from the
    /// namespace Prometheus runs in. Both, in one peer: either alone would admit a tenant's own pod
    /// that merely calls itself prometheus.</para>
    ///
    /// <para><b>Every port, deliberately.</b> An app names its metrics port in its own
    /// <c>prometheus.io/port</c> annotation, which EntKube does not know when it writes the policy,
    /// and under Istio the scrape goes to the sidecar's merged port (15020) instead. A list of ports
    /// guessed here would quietly keep the target down; the narrowness is in WHO, not where.</para>
    ///
    /// <para>⚠️ An ingress policy over every pod is itself what closes a namespace. Write this only
    /// beside a policy that already closes the same pods to ingress — on its own it would shut
    /// out everything except Prometheus.</para>
    /// </summary>
    public static string BuildNetworkPolicy(string name, string ns, string prometheusNamespace) => string.Join("\n",
        "apiVersion: networking.k8s.io/v1",
        "kind: NetworkPolicy",
        "metadata:",
        $"  name: {name}",
        $"  namespace: {ns}",
        "  labels:",
        "    app.kubernetes.io/managed-by: entkube",
        "spec:",
        "  podSelector: {}",
        "  policyTypes:",
        "    - Ingress",
        "  ingress:",
        "    - from:",
        "        - namespaceSelector:",
        "            matchLabels:",

        // Quoted, because a namespace may be called 2026 and a label value that is a number is refused.
        $"              kubernetes.io/metadata.name: \"{prometheusNamespace.Trim()}\"",
        "          podSelector:",
        "            matchLabels:",
        "              app.kubernetes.io/name: prometheus");
}
