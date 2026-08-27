using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Outbound mutual TLS: the client certificates a customer app presents to a partner API that
/// requires one.
///
/// Two ways to get the certificate onto the wire, and the difference matters:
///
/// <list type="bullet">
/// <item><b>Mesh-originated.</b> The sidecar performs the handshake using a Secret it reads via
/// SDS, so the app never holds the private key. The catch is that the app must call the partner
/// over plain HTTP — a sidecar can only add a client certificate to a handshake it performs
/// itself. An app that opens its own TLS connection hands the sidecar an opaque byte stream,
/// which it forwards unchanged and unauthenticated. Istio also honours <c>credentialName</c>
/// only on a DestinationRule carrying a <c>workloadSelector</c>, so the selector is required
/// rather than optional.</item>
/// <item><b>Secret only.</b> The Secret is created and the app mounts it and does mTLS itself.
/// The fallback for workloads outside the mesh. EntKube does not edit the customer's manifests to
/// mount it — rewriting someone's deployment under their feet is how a platform breaks a workload
/// it does not own — so the mount stays with whoever owns the manifest.</item>
/// </list>
/// </summary>
public class OutboundMtlsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    ILogger<OutboundMtlsService> logger)
{
    // ──────── CRUD ────────

    public async Task<List<OutboundMtlsCredential>> GetForAppAsync(
        Guid appId, Guid? environmentId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.OutboundMtlsCredentials
            .Where(c => c.AppId == appId
                     && (environmentId == null || c.EnvironmentId == null || c.EnvironmentId == environmentId))
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<OutboundMtlsCredential?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.OutboundMtlsCredentials.FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<OutboundMtlsCredential> CreateAsync(
        OutboundMtlsCredential credential, CancellationToken ct = default)
    {
        Validate(credential);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        string name = SanitizeName(credential.Name);
        bool duplicate = await db.OutboundMtlsCredentials
            .AnyAsync(c => c.AppId == credential.AppId && c.Name == name, ct);
        if (duplicate)
            throw new InvalidOperationException($"A credential named '{name}' already exists for this app.");

        // The vault secret must actually carry a client certificate *and* its key — a cert-only
        // secret produces a Secret the partner's handshake can never complete with.
        CertificateBundle? bundle = await vault.GetCertificateBundleByIdAsync(credential.VaultSecretId, ct);
        if (bundle is null || !bundle.HasCertificate)
            throw new InvalidOperationException("The selected vault secret has no certificate.");
        if (!bundle.HasPrivateKey)
            throw new InvalidOperationException(
                "The selected vault certificate has no private key. A client certificate is only usable with " +
                "its key — upload the key alongside it in the vault.");

        credential.Id = Guid.NewGuid();
        credential.Name = name;
        credential.Host = credential.Host.Trim().ToLowerInvariant();

        db.OutboundMtlsCredentials.Add(credential);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Outbound mTLS credential {Name} created for app {AppId} → {Host}",
            name, credential.AppId, credential.Host);

        return credential;
    }

    public async Task UpdateAsync(OutboundMtlsCredential credential, CancellationToken ct = default)
    {
        Validate(credential);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OutboundMtlsCredential existing = await db.OutboundMtlsCredentials
            .FirstOrDefaultAsync(c => c.Id == credential.Id, ct)
            ?? throw new InvalidOperationException("Credential not found.");

        existing.Name = SanitizeName(credential.Name);
        existing.Host = credential.Host.Trim().ToLowerInvariant();
        existing.Port = credential.Port;
        existing.VaultSecretId = credential.VaultSecretId;
        existing.Mode = credential.Mode;
        existing.EnvironmentId = credential.EnvironmentId;
        existing.WorkloadSelectorJson = credential.WorkloadSelectorJson;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        OutboundMtlsCredential? credential = await db.OutboundMtlsCredentials
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null) return;

        db.OutboundMtlsCredentials.Remove(credential);
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(OutboundMtlsCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Name))
            throw new InvalidOperationException("Name is required.");

        if (string.IsNullOrWhiteSpace(credential.Host))
            throw new InvalidOperationException("Partner hostname is required.");

        if (credential.Host.Contains("://"))
            throw new InvalidOperationException("Enter a hostname (api.partner.com), not a URL.");

        if (credential.Port is < 1 or > 65535)
            throw new InvalidOperationException("Port must be between 1 and 65535.");

        if (credential.VaultSecretId == Guid.Empty)
            throw new InvalidOperationException("Pick the vault certificate to present to the partner.");

        if (credential.Mode == OutboundMtlsMode.MeshOriginated
            && ParseSelector(credential.WorkloadSelectorJson).Count == 0)
        {
            // Not a style preference: Istio silently ignores credentialName on a DestinationRule
            // without a workloadSelector, which yields a rule that looks applied and originates
            // no client certificate at all.
            throw new InvalidOperationException(
                "Mesh-originated mTLS needs pod labels selecting the workloads allowed to use this certificate. " +
                "Istio ignores credentialName on a DestinationRule with no workloadSelector.");
        }
    }

    /// <summary>Sanitises a name into something usable as a Kubernetes resource name.</summary>
    public static string SanitizeName(string name) =>
        TrustBundleService.SanitizeName(name, "entkube-outbound-mtls");

    /// <summary>Parses the stored label selector; an empty/invalid document yields no labels.</summary>
    public static Dictionary<string, string> ParseSelector(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ──────── Rendering ────────

    /// <summary>
    /// The Secret the sidecar (or the app) reads the client certificate from.
    ///
    /// Deliberately <c>Opaque</c>, not <c>kubernetes.io/tls</c>: Istio's SDS reads a generic secret
    /// carrying <c>tls.crt</c>, <c>tls.key</c> and — when the partner's CA is known — <c>ca.crt</c>,
    /// and a kubernetes.io/tls Secret cannot hold that third key.
    /// </summary>
    public static string BuildSecretYaml(string ns, string name, CertificateBundle bundle)
    {
        List<(string Key, string Value)> data =
        [
            ("tls.crt", bundle.CombinedCertificateChain),
            ("tls.key", bundle.PrivateKey ?? "")
        ];

        // Without a CA the sidecar falls back to the system trust store for verifying the partner,
        // which is right for a publicly-trusted endpoint and wrong for a private one.
        if (bundle.HasCaCertificate) data.Add(("ca.crt", bundle.CaCertificate!));

        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        foreach ((string key, string value) in data)
            sb.AppendLine($"  {key}: {Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}");

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Registers the partner host with the mesh.
    ///
    /// Two ports are declared: the plaintext port the app calls, and the TLS port the sidecar
    /// actually connects to. <c>targetPort</c> is what redirects one to the other — it is the piece
    /// that lets the app speak HTTP while the partner sees mTLS.
    /// </summary>
    public static string BuildServiceEntryYaml(string ns, string name, string host, int port, int plainPort)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: networking.istio.io/v1");
        sb.AppendLine("kind: ServiceEntry");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine("spec:");
        sb.AppendLine("  hosts:");
        sb.AppendLine($"    - {host}");
        sb.AppendLine("  ports:");
        sb.AppendLine($"    - number: {plainPort}");
        sb.AppendLine("      name: http-port");
        sb.AppendLine("      protocol: HTTP");
        sb.AppendLine($"      targetPort: {port}");
        sb.AppendLine($"    - number: {port}");
        sb.AppendLine("      name: https-port");
        sb.AppendLine("      protocol: HTTPS");
        sb.AppendLine("  resolution: DNS");

        return sb.ToString();
    }

    /// <summary>
    /// The DestinationRule that makes the sidecar present the client certificate.
    ///
    /// The <c>workloadSelector</c> is load-bearing twice over: Istio ignores <c>credentialName</c>
    /// without it, and it bounds which pods can use the certificate. <c>sni</c> is set explicitly
    /// because the partner selects its certificate by it, and the plaintext request the app sends
    /// carries no SNI of its own.
    /// </summary>
    public static string BuildDestinationRuleYaml(
        string ns, string name, string host, int plainPort, string secretName,
        IReadOnlyDictionary<string, string> selectorLabels)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: networking.istio.io/v1");
        sb.AppendLine("kind: DestinationRule");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    entkube.io/managed: \"true\"");
        sb.AppendLine("spec:");
        sb.AppendLine("  workloadSelector:");
        sb.AppendLine("    matchLabels:");
        foreach ((string key, string value) in selectorLabels.OrderBy(l => l.Key, StringComparer.Ordinal))
            sb.AppendLine($"      {key}: {value}");
        sb.AppendLine($"  host: {host}");
        sb.AppendLine("  trafficPolicy:");
        sb.AppendLine("    portLevelSettings:");
        sb.AppendLine("      - port:");
        sb.AppendLine($"          number: {plainPort}");
        sb.AppendLine("        tls:");
        sb.AppendLine("          mode: MUTUAL");
        sb.AppendLine($"          credentialName: {secretName}");
        sb.AppendLine($"          sni: {host}");

        return sb.ToString();
    }

    /// <summary>
    /// The plaintext port the app calls for a given partner port. 443 → 80 keeps the familiar
    /// pairing; anything else reuses the partner's own port number on the plaintext side, which
    /// keeps the app's configuration recognisable.
    /// </summary>
    public static int PlainPortFor(int partnerPort) => partnerPort == 443 ? 80 : partnerPort;

    /// <summary>
    /// Renders everything for one credential in one namespace: the Secret always, plus the mesh
    /// resources when the mesh performs the handshake.
    /// </summary>
    public static string BuildManifest(
        OutboundMtlsCredential credential, CertificateBundle bundle, string ns)
    {
        string name = credential.Name;
        List<string> docs = [BuildSecretYaml(ns, name, bundle)];

        if (credential.Mode == OutboundMtlsMode.MeshOriginated)
        {
            int plainPort = PlainPortFor(credential.Port);
            docs.Add(BuildServiceEntryYaml(ns, name, credential.Host, credential.Port, plainPort));
            docs.Add(BuildDestinationRuleYaml(
                ns, name, credential.Host, plainPort, name, ParseSelector(credential.WorkloadSelectorJson)));
        }

        return string.Join("---\n", docs);
    }

    /// <summary>
    /// How the app must address the partner for this credential to be used. Worth showing
    /// verbatim: with mesh origination the app calls <em>http</em>, which looks wrong until you
    /// know the sidecar upgrades it.
    /// </summary>
    public static string CallHint(OutboundMtlsCredential credential) =>
        credential.Mode == OutboundMtlsMode.MeshOriginated
            ? $"http://{credential.Host}:{PlainPortFor(credential.Port)}"
            : $"https://{credential.Host}:{credential.Port}";
}
