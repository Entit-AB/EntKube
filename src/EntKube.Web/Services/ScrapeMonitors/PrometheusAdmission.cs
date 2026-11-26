using System.Globalization;
using k8s.Models;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Whether a namespace's NetworkPolicies let Prometheus reach a pod's port.
///
/// <para><b>Why it is asked at all.</b> A monitor for a port that is closed to Prometheus is a target
/// that is down for ever, which the stack's own TargetDown alert turns into an incident.</para>
///
/// <para><b>Where it cannot tell, it answers yes.</b> An <c>ipBlock</c> that may or may not cover the
/// pod network, a selector it does not understand: the monitor is then created, and if the door was
/// shut after all there is a visible down target. Answering no would be a metric silently missing
/// with nobody told why.</para>
/// </summary>
public sealed class PrometheusAdmission(
    string prometheusNamespace,
    IReadOnlyDictionary<string, string>? prometheusNamespaceLabels,
    IReadOnlyList<IReadOnlyDictionary<string, string>> prometheusPodLabels)
{
    /// <summary>Whether Prometheus is let in to this endpoint.</summary>
    public bool Admits(ScrapedEndpoint endpoint, LiveNamespace ns)
    {
        // A pod on the host's network is outside what NetworkPolicies govern.
        if (endpoint.Pod.Spec?.HostNetwork is true) return true;

        IReadOnlyDictionary<string, string> labels = ScrapedEndpoints.Labels(endpoint.Pod);

        List<V1NetworkPolicy> isolating =
        [
            .. ns.NetworkPolicies.Where(p => RestrictsIngress(p) && Governs(p, labels))
        ];

        // Policies add up: a pod that any of them selects is closed except for what some policy opens.
        return isolating.Count == 0
            || isolating.SelectMany(p => p.Spec?.Ingress ?? [])
                .Any(rule => Opens(rule, endpoint) && LetsIn(rule, ns.Name));
    }

    /// <summary>A policy that names no types is an ingress policy: that is the default.</summary>
    private static bool RestrictsIngress(V1NetworkPolicy policy)
        => policy.Spec?.PolicyTypes is not { Count: > 0 } types || types.Contains("Ingress");

    /// <summary>A policy whose pod selector is empty, or not there, governs every pod of its namespace.</summary>
    private static bool Governs(V1NetworkPolicy policy, IReadOnlyDictionary<string, string> podLabels)
        => policy.Spec?.PodSelector is null || LabelSelector.From(policy.Spec.PodSelector)!.Matches(podLabels) is true;

    private static bool Opens(V1NetworkPolicyIngressRule rule, ScrapedEndpoint endpoint)
        => rule.Ports is not { Count: > 0 } ports
           || ports.Any(p => p.Protocol is null or "TCP" && IsPort(p, endpoint));

    private static bool IsPort(V1NetworkPolicyPort allowed, ScrapedEndpoint endpoint)
    {
        string? port = allowed.Port?.Value;
        if (string.IsNullOrEmpty(port)) return true;

        if (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return endpoint.Port >= number && endpoint.Port <= (allowed.EndPort ?? number);
        }

        // A name is looked up on the pod being reached, like a Service's.
        return ScrapedEndpoints.DeclaredPorts(endpoint.Pod)
            .Any(p => p.Name == port && p.ContainerPort == endpoint.Port);
    }

    private bool LetsIn(V1NetworkPolicyIngressRule rule, string policyNamespace)
        => rule.FromProperty is not { Count: > 0 } peers || peers.Any(p => IsPrometheus(p, policyNamespace));

    private bool IsPrometheus(V1NetworkPolicyPeer peer, string policyNamespace)
    {
        // Addresses: whether Prometheus's fall inside cannot be told from here.
        if (peer.IpBlock is not null) return true;

        // No namespace selector means the policy's own namespace, not every namespace.
        bool fromNamespace = peer.NamespaceSelector is null
            ? policyNamespace == prometheusNamespace
            : prometheusNamespaceLabels is null
              || LabelSelector.From(peer.NamespaceSelector)!.Matches(prometheusNamespaceLabels) is not false;

        if (!fromNamespace) return false;

        return peer.PodSelector is null
            || prometheusPodLabels.Count == 0
            || prometheusPodLabels.Any(labels => LabelSelector.From(peer.PodSelector)!.Matches(labels) is not false);
    }
}
