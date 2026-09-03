namespace EntKube.Web.Data;

/// <summary>
/// How a route pins a client to one backend pod ("sticky sessions").
///
/// Istio expresses this as <c>trafficPolicy.loadBalancer.consistentHash</c> on the
/// DestinationRule for the backend Service — the gateway hashes one attribute of each
/// request and picks the endpoint from that hash, so the same value keeps landing on the
/// same pod for as long as the endpoint set is stable.
///
/// It is deliberately per-route rather than a cluster-wide default. Consistent hashing
/// costs even load balancing, and it silently defeats the point of running several replicas
/// when the hash key has low cardinality — only a workload that actually needs it (in-memory
/// sessions, a websocket-backed UI, a cache that is not shared between pods) should pay that.
/// </summary>
public enum SessionAffinityMode
{
    /// <summary>No affinity — the gateway load balances freely. The default.</summary>
    None,

    /// <summary>
    /// Hash on an HTTP cookie. Envoy issues the cookie itself when the request does not
    /// carry one, so this works without the application knowing anything about it — the
    /// usual choice for a browser-facing app with server-side session state.
    /// </summary>
    Cookie,

    /// <summary>
    /// Hash on a request header (e.g. <c>x-tenant-id</c>, <c>authorization</c>). Requests
    /// missing the header are load balanced normally rather than all colliding on one pod.
    /// </summary>
    Header,

    /// <summary>Hash on a query-string parameter — for clients that cannot set headers or hold cookies.</summary>
    QueryParameter,

    /// <summary>
    /// Hash on the client source IP. The bluntest option: everything behind one NAT or one
    /// corporate egress lands on a single pod, so prefer a cookie or a header where the
    /// client can carry one.
    /// </summary>
    SourceIp
}
