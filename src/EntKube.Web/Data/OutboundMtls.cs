namespace EntKube.Web.Data;

/// <summary>How a client certificate reaches the partner endpoint.</summary>
public enum OutboundMtlsMode
{
    /// <summary>
    /// The Istio sidecar performs the TLS handshake and presents the client certificate, so the
    /// application itself needs no TLS code and never handles the private key.
    ///
    /// Requires the app to call the partner over <em>plain HTTP</em>: the sidecar can only add a
    /// client certificate to a handshake it performs itself. If the app opens its own TLS
    /// connection, the sidecar sees an opaque byte stream and forwards it untouched.
    /// </summary>
    MeshOriginated = 0,

    /// <summary>
    /// Only the Kubernetes Secret is created; the application mounts it and does mTLS itself.
    /// For workloads outside the mesh, or apps that must control the TLS handshake.
    /// </summary>
    SecretOnly = 1,
}

/// <summary>
/// A client certificate a customer app presents when calling an external partner API that requires
/// mutual TLS — the outbound counterpart to <see cref="ClientCaBundle"/>.
///
/// The certificate itself lives in the tenant's vault as a <see cref="VaultSecretType.Certificate"/>
/// secret, so it inherits the encryption, expiry tracking and rotation notifications already built
/// around vault certificates. This record is the binding: which app, calling which host, with which
/// certificate, and whether the mesh or the app performs the handshake.
/// </summary>
public class OutboundMtlsCredential
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The customer app that calls the partner.</summary>
    public Guid AppId { get; set; }

    /// <summary>
    /// Restrict to one environment (null = every environment of the app). A partner usually issues
    /// separate certificates for test and production, so this is normally set.
    /// </summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>Short name; also the name of the generated Secret, ServiceEntry and DestinationRule.</summary>
    public required string Name { get; set; }

    /// <summary>The partner hostname, e.g. <c>api.partner.com</c>.</summary>
    public required string Host { get; set; }

    /// <summary>The port the partner serves mTLS on.</summary>
    public int Port { get; set; } = 443;

    /// <summary>The vault certificate (client certificate + private key, optionally the partner's CA).</summary>
    public Guid VaultSecretId { get; set; }

    public OutboundMtlsMode Mode { get; set; } = OutboundMtlsMode.MeshOriginated;

    /// <summary>
    /// JSON object of pod labels selecting the workloads allowed to use this certificate.
    ///
    /// Not optional for <see cref="OutboundMtlsMode.MeshOriginated"/>: Istio honours
    /// <c>credentialName</c> in a DestinationRule only when the rule carries a
    /// <c>workloadSelector</c>. It is also the blast radius — every pod matching these labels can
    /// present this certificate to the partner.
    /// </summary>
    public string? WorkloadSelectorJson { get; set; }

    /// <summary>Set when the generated resources were last applied. Null means not yet applied.</summary>
    public DateTime? AppliedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
