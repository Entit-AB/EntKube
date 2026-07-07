namespace EntKube.Web.Data;

/// <summary>
/// A single directed edge in an app's connectivity graph: "this app may talk to
/// (or accept traffic from) a peer, on a port". This is the least-privilege
/// intent a customer declares — the source of truth from which both a Kubernetes
/// NetworkPolicy (L3/L4 via namespace/pod selectors + ports) and an Istio
/// AuthorizationPolicy (L7 via workload identity + protocol) are generated in
/// later phases, and against which the pre-apply analyzer predicts breakage.
///
/// Rules are scoped per app + environment. Inferred rules are recreated from
/// bindings/routes on each graph build; Declared rules are authored by a user
/// and persist. External (internet) egress is modelled separately by
/// <see cref="ExternalDependency"/>; a rule with <see cref="PeerType"/> External
/// simply points at one of those.
/// </summary>
public class ConnectivityRule
{
    public Guid Id { get; set; }

    /// <summary>The owning app — the subject of the rule.</summary>
    public Guid AppId { get; set; }

    /// <summary>Which environment this rule applies to. Rules are per-environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// Egress = the owning app initiates to the peer; Ingress = the owning app
    /// accepts from the peer. Most intents are authored as egress.
    /// </summary>
    public ConnectivityDirection Direction { get; set; } = ConnectivityDirection.Egress;

    public ConnectivityPeerType PeerType { get; set; }

    /// <summary>For PeerType App: the app on the other end of the edge.</summary>
    public Guid? PeerAppId { get; set; }

    /// <summary>For PeerType Namespace (or an App resolved to a namespace): the namespace name.</summary>
    public string? PeerNamespace { get; set; }

    /// <summary>For PeerType Selector: a JSON pod label selector (e.g. {"app":"api"}).</summary>
    public string? PeerSelector { get; set; }

    /// <summary>For PeerType Cidr: an IP block, e.g. "10.0.0.0/16".</summary>
    public string? PeerCidr { get; set; }

    /// <summary>The destination port. Null means "any port".</summary>
    public int? Port { get; set; }

    /// <summary>Transport protocol for the port.</summary>
    public L4Protocol Protocol { get; set; } = L4Protocol.Tcp;

    /// <summary>
    /// Application-layer protocol (http/grpc…) used only when generating an L7
    /// Istio AuthorizationPolicy. Null for pure L3/L4 rules.
    /// </summary>
    public string? AppProtocol { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ConnectivitySource Source { get; set; } = ConnectivitySource.Declared;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;

    /// <summary>The peer app, when PeerType is App. No cascade — restricted delete.</summary>
    public App? PeerApp { get; set; }
}
