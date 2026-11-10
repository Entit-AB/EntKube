namespace EntKube.Web.Data;

/// <summary>How a virtual machine's port is published.</summary>
public enum VmRouteKind
{
    /// <summary>A hostname on the cluster's HTTPS gateway, TLS terminated there, to a port in the VM.</summary>
    Http,

    /// <summary>A raw TCP port on the dedicated L4 gateway (SSH, RDP…). Istio clusters only.</summary>
    Tcp
}

/// <summary>
/// One port of a KubeVirt virtual machine published outside the cluster.
///
/// <para><b>Why this is a table when the VMs are not.</b> The VM is the cluster's record and is read
/// live. Publishing is EntKube's intent, and both Gateways are regenerated wholesale from the
/// database: a hostname or port that exists only on the cluster loses its listener the next time any
/// app or component route is applied, and the L4 gateway (with its LoadBalancer) is deleted outright
/// when its last stored route goes. So the route has to be stored, and every regeneration has to
/// read it.</para>
///
/// <para>Carries its own <see cref="TenantId"/>, unlike the app routes, which reach a tenant through
/// App → Customer. A VM has no app.</para>
/// </summary>
public class VmRoute
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ClusterId { get; set; }

    /// <summary>The VM's namespace — also where its Service and route objects live.</summary>
    public required string Namespace { get; set; }

    public required string VmName { get; set; }

    public VmRouteKind Kind { get; set; }

    /// <summary>The port the guest listens on.</summary>
    public int VmPort { get; set; }

    /// <summary>Http: the public hostname.</summary>
    public string? Hostname { get; set; }

    /// <summary>Http: the cert-manager ClusterIssuer that issues the hostname's certificate.</summary>
    public string? ClusterIssuerName { get; set; }

    /// <summary>Http: per-request timeout; null is the gateway default, 0 disables it (streams).</summary>
    public int? RequestTimeoutSeconds { get; set; }

    /// <summary>Tcp: the port on the L4 gateway's address.</summary>
    public int? ExternalPort { get; set; }

    /// <summary>When the route last reached the cluster; null until the first successful apply.</summary>
    public DateTime? ClusterAppliedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster Cluster { get; set; } = null!;
}
