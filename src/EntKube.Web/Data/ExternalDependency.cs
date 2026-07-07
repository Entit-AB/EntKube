namespace EntKube.Web.Data;

/// <summary>
/// An off-cluster endpoint an app needs to reach on the internet (or elsewhere
/// outside the mesh), identified by FQDN + port — e.g. api.stripe.com:443.
///
/// EntKube has no egress modelling today, so a blanket deny-egress NetworkPolicy
/// silently breaks these calls. Capturing them as first-class rows lets the
/// analyzer warn that outbound access is required, and lets later phases generate
/// the right enforcement (an Istio ServiceEntry, or a CNI FQDN/CIDR egress rule —
/// which mechanism is available depends on the cluster's CNI and mesh).
///
/// Scoped per app + environment.
/// </summary>
public class ExternalDependency
{
    public Guid Id { get; set; }

    public Guid AppId { get; set; }

    /// <summary>Which environment needs this egress. External deps are per-environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>The destination host (FQDN), e.g. "api.stripe.com". Wildcards ("*.stripe.com") allowed.</summary>
    public required string Host { get; set; }

    /// <summary>The destination port, e.g. 443.</summary>
    public int Port { get; set; }

    /// <summary>Transport protocol.</summary>
    public L4Protocol Protocol { get; set; } = L4Protocol.Tcp;

    /// <summary>Whether the connection is TLS (informational; drives ServiceEntry tls settings later).</summary>
    public bool Tls { get; set; } = true;

    public ConnectivitySource Source { get; set; } = ConnectivitySource.Declared;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
