namespace EntKube.Web.Data;

/// <summary>
/// A trust anchor for inbound client-certificate authentication (mTLS) on customer app routes.
///
/// EntKube never issues client certificates — the customer supplies the CA that signs them, or
/// points at an existing corporate PKI. This entity holds only public CA material, so nothing
/// here is encrypted (same reasoning as <see cref="CaTrustBundleSource"/>).
///
/// A bundle is rendered to a single ConfigMap in the gateway namespace, keyed <c>ca.crt</c>, and
/// referenced from the Gateway's <c>spec.tls.frontend</c> block. Istio accepts exactly one
/// caCertificateRef per port, so every bundle sharing a <see cref="ListenerPort"/> is concatenated
/// into one ConfigMap — see <see cref="Services.MtlsService.BuildCaConfigMapYaml"/>.
/// </summary>
public class ClientCaBundle
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Friendly name shown when picking a trust anchor for a route (e.g. "Acme Corp Client CA").</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The gateway port that serves routes trusting this bundle. Client-certificate validation is a
    /// per-port property of the Gateway (Istio resolves it via <c>resolveGatewayTLS(port, …)</c>), not
    /// a per-hostname one — so the port, not the hostname, is what isolates one CA from another.
    ///
    /// Bundles sharing a port are merged into one trust store: any client cert signed by <em>any</em>
    /// of those CAs completes the handshake on <em>any</em> hostname on that port, and only the
    /// generated AuthorizationPolicy keeps tenants apart. Give a bundle its own port when that L7
    /// check should not be the only thing standing between two customers.
    ///
    /// Every port used here must be exposed on the ingress gateway's Service; see
    /// <see cref="Services.MtlsService.DefaultListenerPort"/>.
    /// </summary>
    public int ListenerPort { get; set; } = Services.MtlsService.DefaultListenerPort;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ClientCaCertificate> Certificates { get; set; } = [];

    /// <summary>Routes that authenticate their clients against this bundle.</summary>
    public ICollection<AppRoute> Routes { get; set; } = [];
}

/// <summary>
/// One CA certificate (PEM, public material) contributing to a <see cref="ClientCaBundle"/>.
/// A bundle usually holds one root, but a PKI mid-rotation legitimately has two — both are
/// concatenated into the trust store so certificates from either chain validate.
/// </summary>
public class ClientCaCertificate
{
    public Guid Id { get; set; }

    public Guid BundleId { get; set; }

    /// <summary>Friendly label for this CA (e.g. "Acme Root CA 2026").</summary>
    public required string Name { get; set; }

    /// <summary>The CA certificate in PEM format. May contain a chain (root + intermediates).</summary>
    public required string Pem { get; set; }

    /// <summary>
    /// Subject of the parsed certificate, stored at upload time so the list renders without
    /// re-parsing PEM on every read. Null when the PEM could not be parsed.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>Not-after of the parsed certificate — drives expiry warnings. Null when unparsed.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>SHA-256 fingerprint of the parsed certificate, for operators comparing against their PKI.</summary>
    public string? Fingerprint { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ClientCaBundle Bundle { get; set; } = null!;
}
