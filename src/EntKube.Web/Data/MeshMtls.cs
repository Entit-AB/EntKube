namespace EntKube.Web.Data;

/// <summary>How workloads in a namespace accept traffic from other workloads in the mesh.</summary>
public enum MeshMtlsMode
{
    /// <summary>
    /// Accept both mTLS and plaintext. The platform default, and the mode EntKube has always
    /// applied when publishing a route: an ingress gateway dialling a sidecar-less pod would
    /// otherwise fail its TLS handshake.
    /// </summary>
    Permissive = 0,

    /// <summary>
    /// Accept mTLS only. Every caller must present a mesh identity, so plaintext from anywhere —
    /// including a pod that simply has no sidecar — is refused. Safe only once every workload in
    /// the namespace is in the mesh, which is why applying it goes through a readiness check.
    /// </summary>
    Strict = 1,
}

/// <summary>
/// Service-to-service mTLS posture for one namespace on one cluster, rendered to an Istio
/// namespace-wide <c>PeerAuthentication</c>.
///
/// This is the counterpart to <see cref="ClientCaBundle"/>: that authenticates clients arriving
/// from outside, this authenticates workloads calling each other inside the cluster. The identities
/// here are issued by Istio itself (SPIFFE, rotated automatically) — there is no CA to upload.
///
/// A row exists only for a namespace whose posture an operator has decided. Absence means
/// <see cref="MeshMtlsMode.Permissive"/>, which is what the platform applies by default.
/// </summary>
public class MeshMtlsPolicy
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Scalar FK to <see cref="KubernetesCluster"/> (no nav), as with other cluster-scoped rows.</summary>
    public Guid ClusterId { get; set; }

    /// <summary>The Kubernetes namespace this posture applies to.</summary>
    public required string Namespace { get; set; }

    public MeshMtlsMode Mode { get; set; } = MeshMtlsMode.Permissive;

    /// <summary>Set when the PeerAuthentication was last successfully applied. Null means not yet applied.</summary>
    public DateTime? AppliedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
