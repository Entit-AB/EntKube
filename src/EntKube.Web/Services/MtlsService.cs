using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Inbound mutual TLS for customer app routes: manages the CA trust anchors that client
/// certificates are validated against, and renders the cluster-side objects that enforce it.
///
/// The shape of this feature is dictated by how client-certificate validation actually works in
/// Gateway API and Istio, which is worth stating once because it is counter-intuitive:
///
/// <list type="bullet">
/// <item>Validation is configured on the <em>Gateway</em> (<c>spec.tls.frontend</c>), not on a
/// listener. GEP-91 deliberately moved it off listeners because HTTP/2 connection coalescing lets
/// a connection opened for one hostname carry requests for another, which would bypass any
/// per-hostname setting.</item>
/// <item>It is resolved <em>per port</em>: Istio's <c>resolveGatewayTLS(port, …)</c> looks for a
/// matching <c>perPort</c> entry and otherwise falls back to <c>default</c>. So the listener port
/// — not the hostname — is the unit of isolation, and that is why a route requiring a client
/// certificate is published on its trust anchor's port rather than on 443.</item>
/// <item>Istio 1.28 does not implement the <c>mode</c> field (the conversion code carries a
/// literal <c>// TODO: add 'Mode'</c> and hardcodes <c>MUTUAL</c>). <c>AllowInsecureFallback</c> is
/// therefore <em>not</em> a way to make validation optional: configuring any CA on a port makes a
/// valid client certificate mandatory for every hostname on it. Putting a CA on 443 would break
/// every other customer sharing the gateway.</item>
/// <item>Only one <c>caCertificateRef</c> per port is supported, so all bundles sharing a port are
/// concatenated into a single ConfigMap.</item>
/// </list>
///
/// EntKube does not issue client certificates. The customer supplies the CA that signs them (or
/// points at an existing PKI); we store the public CA material and trust it.
/// </summary>
public class MtlsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<MtlsService> logger)
{
    /// <summary>
    /// Default port for mTLS listeners. Not 443: client-certificate validation applies to every
    /// hostname on a port, so 443 must stay free of it for the routes that don't want mTLS.
    /// Using a second port on the same Gateway keeps the existing LoadBalancer, IP and DNS.
    /// </summary>
    public const int DefaultListenerPort = 8443;

    /// <summary>
    /// Lowest port we accept for an mTLS listener. Keeps operators away from 443 (which would
    /// impose client certificates on every co-hosted hostname) and from the ports Istio reserves
    /// for its own plumbing (15000-15100).
    /// </summary>
    public static bool IsUsableListenerPort(int port) =>
        port is > 0 and < 65536 && port != 443 && port != 80 && (port < 15000 || port > 15100);

    /// <summary>Name of the ConfigMap holding the concatenated trust store for one listener port.</summary>
    public static string CaConfigMapName(int port) => $"entkube-client-ca-{port}";

    /// <summary>Listener/section name for a hostname's mTLS listener on a given port.</summary>
    public static string MtlsListenerName(string hostname, int port)
    {
        // Reuse the hostname sanitiser so the name matches the plain listener's, then suffix the
        // port — a hostname can legitimately appear on both 443 and its mTLS port, and listener
        // names must be unique within a Gateway.
        string baseName = ExternalRouteService.ToListenerName(hostname);
        string suffix = $"-mtls-{port}";
        int room = 63 - suffix.Length;
        if (baseName.Length > room) baseName = baseName[..room].TrimEnd('-');
        return baseName + suffix;
    }

    // ──────── Trust anchors (CRUD) ────────

    public async Task<List<ClientCaBundle>> GetBundlesAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.ClientCaBundles
            .Include(b => b.Certificates)
            .Where(b => b.TenantId == tenantId)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
    }

    public async Task<ClientCaBundle?> GetBundleAsync(Guid bundleId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.ClientCaBundles
            .Include(b => b.Certificates)
            .Include(b => b.Routes)
            .FirstOrDefaultAsync(b => b.Id == bundleId, ct);
    }

    public async Task<ClientCaBundle> CreateBundleAsync(
        Guid tenantId, string name, string? description, int listenerPort = DefaultListenerPort,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name is required.");

        if (!IsUsableListenerPort(listenerPort))
            throw new InvalidOperationException(
                $"Port {listenerPort} can't serve mTLS. Ports 80 and 443 carry hostnames that don't require " +
                "client certificates — putting a CA there would demand one from all of them — and 15000-15100 " +
                "is reserved by Istio.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        string trimmed = name.Trim();
        bool duplicate = await db.ClientCaBundles.AnyAsync(b => b.TenantId == tenantId && b.Name == trimmed, ct);
        if (duplicate)
            throw new InvalidOperationException($"A trust anchor named '{trimmed}' already exists.");

        ClientCaBundle bundle = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = trimmed,
            Description = description?.Trim(),
            ListenerPort = listenerPort
        };

        db.ClientCaBundles.Add(bundle);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Client CA bundle {Name} created for tenant {TenantId} on port {Port}",
            trimmed, tenantId, listenerPort);

        return bundle;
    }

    public async Task UpdateBundleAsync(
        Guid bundleId, string name, string? description, int listenerPort, CancellationToken ct = default)
    {
        if (!IsUsableListenerPort(listenerPort))
            throw new InvalidOperationException($"Port {listenerPort} can't serve mTLS.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClientCaBundle bundle = await db.ClientCaBundles.FirstOrDefaultAsync(b => b.Id == bundleId, ct)
            ?? throw new InvalidOperationException("Trust anchor not found.");

        bundle.Name = name.Trim();
        bundle.Description = description?.Trim();
        bundle.ListenerPort = listenerPort;
        bundle.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a trust anchor. Refused while routes still authenticate against it — dropping it
    /// would leave those routes published on an mTLS port with nothing validating clients.
    /// </summary>
    public async Task DeleteBundleAsync(Guid bundleId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClientCaBundle bundle = await db.ClientCaBundles
            .Include(b => b.Routes)
            .FirstOrDefaultAsync(b => b.Id == bundleId, ct)
            ?? throw new InvalidOperationException("Trust anchor not found.");

        if (bundle.Routes.Count > 0)
            throw new InvalidOperationException(
                $"{bundle.Routes.Count} route(s) still authenticate clients against this trust anchor. " +
                "Turn off client certificates on them (or point them at another anchor) first.");

        db.ClientCaBundles.Remove(bundle);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds a CA certificate to a bundle. The PEM is parsed here so a malformed or non-CA
    /// certificate is rejected at upload time rather than silently producing a trust store that
    /// rejects every client.
    /// </summary>
    public async Task<ClientCaCertificate> AddCertificateAsync(
        Guid bundleId, string name, string pem, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name is required.");

        ParsedCa parsed = ParseCa(pem);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        bool exists = await db.ClientCaBundles.AnyAsync(b => b.Id == bundleId, ct);
        if (!exists)
            throw new InvalidOperationException("Trust anchor not found.");

        ClientCaCertificate cert = new()
        {
            Id = Guid.NewGuid(),
            BundleId = bundleId,
            Name = name.Trim(),
            Pem = parsed.NormalizedPem,
            Subject = parsed.Subject,
            ExpiresAt = parsed.ExpiresAt,
            Fingerprint = parsed.Fingerprint
        };

        db.ClientCaCertificates.Add(cert);

        ClientCaBundle? bundle = await db.ClientCaBundles.FirstOrDefaultAsync(b => b.Id == bundleId, ct);
        if (bundle is not null) bundle.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return cert;
    }

    public async Task RemoveCertificateAsync(Guid certificateId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClientCaCertificate? cert = await db.ClientCaCertificates
            .FirstOrDefaultAsync(c => c.Id == certificateId, ct);
        if (cert is null) return;

        db.ClientCaCertificates.Remove(cert);
        await db.SaveChangesAsync(ct);
    }

    // ──────── PEM parsing ────────

    /// <summary>A CA certificate parsed out of uploaded PEM.</summary>
    public sealed record ParsedCa(string NormalizedPem, string Subject, DateTime ExpiresAt, string Fingerprint, bool IsCertificateAuthority);

    /// <summary>
    /// Parses (and validates) uploaded CA PEM. Accepts a chain — a PKI mid-rotation legitimately
    /// hands over root + intermediate — and reports on the first certificate, which is the one an
    /// operator identifies the anchor by.
    /// </summary>
    public static ParsedCa ParseCa(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidOperationException("Certificate is required.");

        X509Certificate2Collection collection = [];
        try
        {
            collection.ImportFromPem(pem);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"Could not parse the certificate: {ex.Message}", ex);
        }

        if (collection.Count == 0)
            throw new InvalidOperationException(
                "No certificate found. Paste PEM text including the -----BEGIN CERTIFICATE----- lines.");

        X509Certificate2 first = collection[0];

        // A client certificate validates against the CA that signed it, so a leaf pasted here by
        // mistake would produce a trust store that rejects every client. Catch it now.
        bool isCa = first.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Any(e => e.CertificateAuthority);

        if (!isCa)
            throw new InvalidOperationException(
                $"'{first.Subject}' is not a CA certificate (no basicConstraints CA:TRUE). Upload the CA that " +
                "signs your client certificates, not a client certificate itself.");

        StringBuilder normalized = new();
        foreach (X509Certificate2 cert in collection)
        {
            normalized.AppendLine(cert.ExportCertificatePem());
        }

        return new ParsedCa(
            normalized.ToString().Trim() + "\n",
            first.Subject,
            first.NotAfter.ToUniversalTime(),
            Convert.ToHexString(SHA256.HashData(first.RawData)),
            isCa);
    }

    // ──────── Cluster-side rendering ────────

    /// <summary>
    /// The mTLS shape of one cluster: which ports carry client-certificate validation, which
    /// hostnames are published on them, and anything an operator needs to know before applying.
    /// </summary>
    public sealed class MtlsClusterPlan
    {
        /// <summary>Port → the trust anchors merged into that port's trust store.</summary>
        public Dictionary<int, List<ClientCaBundle>> BundlesByPort { get; } = [];

        /// <summary>Hostname → the port its mTLS listener is published on.</summary>
        public Dictionary<string, int> HostPorts { get; } = [];

        /// <summary>Hostnames reachable over mTLS only (no plain 443 listener).</summary>
        public HashSet<string> MtlsOnlyHosts { get; } = [];

        /// <summary>ConfigMap documents (one per port) carrying the concatenated trust stores.</summary>
        public List<string> CaConfigMaps { get; } = [];

        /// <summary>Things that will not work, or will surprise someone, once this is applied.</summary>
        public List<string> Warnings { get; } = [];

        public bool IsEmpty => HostPorts.Count == 0;
    }

    /// <summary>
    /// Builds the mTLS plan for a cluster from the routes that require client certificates.
    /// <paramref name="routes"/> must have <see cref="AppRoute.ClientCaBundle"/> loaded.
    /// </summary>
    public static MtlsClusterPlan BuildPlan(IEnumerable<AppRoute> routes, string gatewayNamespace)
    {
        MtlsClusterPlan plan = new();

        List<AppRoute> mtlsRoutes = routes
            .Where(r => r is { IsEnabled: true, RequireClientCertificate: true })
            .ToList();

        foreach (AppRoute route in mtlsRoutes)
        {
            // Fail loudly rather than emit a Gateway that quietly stops requiring certificates:
            // an unloaded navigation would otherwise look exactly like "no mTLS configured".
            ClientCaBundle bundle = route.ClientCaBundle
                ?? throw new InvalidOperationException(
                    $"Route '{route.Hostname}' requires a client certificate but its trust anchor was not " +
                    "loaded. Include AppRoute.ClientCaBundle before generating the Gateway.");

            int port = bundle.ListenerPort;

            plan.HostPorts[route.Hostname] = port;
            if (route.ClientCertificateOnly) plan.MtlsOnlyHosts.Add(route.Hostname);

            if (!plan.BundlesByPort.TryGetValue(port, out List<ClientCaBundle>? bundles))
            {
                bundles = [];
                plan.BundlesByPort[port] = bundles;
            }

            if (bundles.All(b => b.Id != bundle.Id)) bundles.Add(bundle);
        }

        foreach ((int port, List<ClientCaBundle> bundles) in plan.BundlesByPort)
        {
            plan.CaConfigMaps.Add(BuildCaConfigMapYaml(gatewayNamespace, port, bundles));

            // Sharing a port means sharing a trust store: a certificate signed by any of these CAs
            // completes the handshake for every hostname on the port. That is a real cross-tenant
            // exposure, and the only fix is a port per anchor.
            if (bundles.Count > 1)
            {
                IEnumerable<string> hostsOnPort = plan.HostPorts
                    .Where(h => h.Value == port)
                    .Select(h => h.Key);

                plan.Warnings.Add(
                    $"Port {port} merges {bundles.Count} trust anchors ({string.Join(", ", bundles.Select(b => b.Name))}) " +
                    $"into one trust store. A client certificate from any of them is accepted on every hostname on " +
                    $"that port ({string.Join(", ", hostsOnPort)}). Give an anchor its own port to keep them apart, " +
                    $"or have the app authorise on the X-Forwarded-Client-Cert header.");
            }

            foreach (ClientCaBundle bundle in bundles)
            {
                if (bundle.Certificates.Count == 0)
                {
                    plan.Warnings.Add(
                        $"Trust anchor '{bundle.Name}' has no CA certificate. Istio rejects a Gateway whose " +
                        $"caCertificateRef resolves to an empty trust store, so port {port} would stop serving.");
                    continue;
                }

                foreach (ClientCaCertificate cert in bundle.Certificates)
                {
                    if (cert.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow)
                    {
                        plan.Warnings.Add(
                            $"CA '{cert.Name}' in anchor '{bundle.Name}' expired on {expiry:yyyy-MM-dd}. " +
                            "Client certificates it signed no longer validate.");
                    }
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Renders the trust store for one listener port as a ConfigMap keyed <c>ca.crt</c>.
    ///
    /// A ConfigMap (not a Secret) because that is Gateway API's Core-supported kind for
    /// caCertificateRefs, and because a CA certificate is public material. It lives in the gateway's
    /// own namespace so no ReferenceGrant is needed. All anchors on the port are concatenated:
    /// Istio accepts exactly one caCertificateRef per port.
    /// </summary>
    public static string BuildCaConfigMapYaml(string gatewayNamespace, int port, IEnumerable<ClientCaBundle> bundles)
    {
        IEnumerable<string> pems = bundles
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .SelectMany(b => b.Certificates.OrderBy(c => c.Name, StringComparer.Ordinal))
            .Select(c => c.Pem.Trim())
            .Where(p => p.Length > 0);

        string bundlePem = string.Join("\n", pems);

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ConfigMap");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {CaConfigMapName(port)}");
        sb.AppendLine($"  namespace: {gatewayNamespace}");
        sb.AppendLine("  annotations:");
        sb.AppendLine("    app.kubernetes.io/managed-by: entkube");
        sb.AppendLine("data:");
        sb.AppendLine("  ca.crt: |");
        foreach (string line in bundlePem.Replace("\r\n", "\n").Split('\n'))
        {
            sb.AppendLine($"    {line}");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Renders the Gateway's <c>spec.tls</c> block: an empty <c>default</c> so ports without an
    /// override (notably 443) keep serving without client certificates, plus one <c>perPort</c>
    /// entry per mTLS port.
    ///
    /// The empty default is load-bearing. <c>frontend.default</c> is a required field, and Istio
    /// falls back to it for any port with no <c>perPort</c> match — so leaving it out, or putting a
    /// CA in it, is what turns "mTLS for one customer" into "client certificates for everyone".
    /// Returns an empty string when no port needs validation, so a cluster without mTLS emits the
    /// Gateway it always did.
    /// </summary>
    public static string BuildGatewayTlsBlock(IEnumerable<int> mtlsPorts)
    {
        List<int> ports = mtlsPorts.Distinct().OrderBy(p => p).ToList();
        if (ports.Count == 0) return "";

        StringBuilder sb = new();
        sb.AppendLine("  tls:");
        sb.AppendLine("    frontend:");
        sb.AppendLine("      default: {}");
        sb.AppendLine("      perPort:");
        foreach (int port in ports)
        {
            sb.AppendLine($"        - port: {port}");
            sb.AppendLine($"          tls:");
            sb.AppendLine($"            validation:");
            sb.AppendLine($"              caCertificateRefs:");
            sb.AppendLine($"                - group: \"\"");
            sb.AppendLine($"                  kind: ConfigMap");
            sb.AppendLine($"                  name: {CaConfigMapName(port)}");
            // Spelled out even though Istio 1.28 ignores it and always enforces MUTUAL: the field is
            // the spec's contract, it is what other Gateway API implementations read, and it states
            // the intent for whoever reads the applied manifest.
            sb.AppendLine($"              mode: AllowValidOnly");
        }

        return sb.ToString();
    }
}
