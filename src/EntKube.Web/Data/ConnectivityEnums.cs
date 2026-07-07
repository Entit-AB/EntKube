namespace EntKube.Web.Data;

/// <summary>
/// Where a piece of the connectivity model came from. Inferred entries are
/// derived automatically from the app's manifests, bindings, and routes and are
/// refreshed on every graph build; Declared entries are authored by an operator
/// (or customer) and are the source of truth for least-privilege intent.
/// </summary>
public enum ConnectivitySource
{
    /// <summary>Derived automatically from existing config (Service manifests, bindings, routes).</summary>
    Inferred,

    /// <summary>Explicitly declared by a user — a deliberate least-privilege intent.</summary>
    Declared
}

/// <summary>
/// Direction of a connectivity rule relative to the owning app.
/// Egress = the owning app initiates the connection to the peer.
/// Ingress = the owning app accepts a connection from the peer.
/// A single logical "A talks to B" intent is modelled once (as A's egress);
/// the generator later derives the matching NetworkPolicy ingress rule on B.
/// </summary>
public enum ConnectivityDirection
{
    Egress,
    Ingress
}

/// <summary>
/// What the far end of a connectivity rule is. Kept deliberately broad so the
/// same edge can feed both a Kubernetes NetworkPolicy (namespace/pod selectors,
/// CIDR) and an Istio AuthorizationPolicy (workload identity) in later phases.
/// </summary>
public enum ConnectivityPeerType
{
    /// <summary>Another EntKube-managed app (resolved to its namespace/labels/identity).</summary>
    App,

    /// <summary>A whole namespace, matched by name.</summary>
    Namespace,

    /// <summary>An arbitrary pod label selector (JSON), same or cross namespace.</summary>
    Selector,

    /// <summary>The cluster ingress gateway (north-south entry point).</summary>
    Ingress,

    /// <summary>A raw IP block (CIDR) — for on-prem ranges or fixed external IPs.</summary>
    Cidr,

    /// <summary>An off-cluster host reached by FQDN — see ExternalDependency.</summary>
    External
}
