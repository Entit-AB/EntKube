using System.Security.Cryptography;
using System.Text;

namespace EntKube.Web.Services;

/// <summary>
/// Mints and verifies the per-cluster bearer token an OpenTelemetry Collector presents when
/// pushing telemetry to <c>/ingest/otlp/*</c>. The token is a stateless, HMAC-signed capability
/// that binds a (clusterId, tenantId) pair — no DB lookup on the hot path. The ingest endpoint
/// trusts the ids inside a valid token and stamps them onto every row, so tenant/cluster identity
/// can never be spoofed by the payload.
///
/// Format: <c>ek1.&lt;base64url(payload)&gt;.&lt;base64url(hmac-sha256(payload))&gt;</c>, payload =
/// <c>"{clusterId:N}:{tenantId:N}"</c>.
///
/// Signing key: <c>Telemetry:IngestSigningKey</c> (base64) if set, otherwise derived from
/// <c>Vault:RootKey</c> with domain separation so the root key is not used directly.
///
/// Revocation is currently coarse — rotating the signing key invalidates all tokens. Per-cluster
/// revocation (a token version stored on the cluster) is a later refinement.
/// </summary>
public sealed class IngestTokenService
{
    private const string Prefix = "ek1";
    private readonly byte[] _key;

    public IngestTokenService(IConfiguration config)
    {
        string? explicitKey = config.GetValue<string>("Telemetry:IngestSigningKey");
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            _key = Convert.FromBase64String(explicitKey);
            return;
        }

        string rootKeyB64 = config.GetValue<string>("Vault:RootKey")
            ?? throw new InvalidOperationException(
                "Telemetry:IngestSigningKey or Vault:RootKey must be configured to sign ingest tokens.");
        byte[] root = Convert.FromBase64String(rootKeyB64);
        // Domain-separate so the ingest key is not literally the vault root key.
        _key = HMACSHA256.HashData(root, "entkube-telemetry-ingest-v1"u8.ToArray());
    }

    /// <summary>Issues an ingest token for a cluster. Given to the collector at configure time.</summary>
    public string Mint(Guid clusterId, Guid tenantId)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"{clusterId:N}:{tenantId:N}");
        return $"{Prefix}.{Base64Url(payload)}.{Base64Url(Sign(payload))}";
    }

    /// <summary>
    /// Verifies a token and extracts the bound identity. Returns false (and zeroed ids) on any
    /// tampering, malformed input, or signature mismatch.
    /// </summary>
    public bool TryValidate(string? token, out Guid tenantId, out Guid clusterId)
    {
        tenantId = Guid.Empty;
        clusterId = Guid.Empty;
        if (string.IsNullOrEmpty(token)) return false;

        string[] parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix) return false;

        byte[] payload, sig;
        try
        {
            payload = FromBase64Url(parts[1]);
            sig = FromBase64Url(parts[2]);
        }
        catch { return false; }

        if (!CryptographicOperations.FixedTimeEquals(sig, Sign(payload))) return false;

        string[] ids = Encoding.UTF8.GetString(payload).Split(':');
        if (ids.Length != 2) return false;
        return Guid.TryParseExact(ids[0], "N", out clusterId)
            && Guid.TryParseExact(ids[1], "N", out tenantId);
    }

    /// <summary>Pulls the token from an ingest request: <c>Authorization: Bearer</c> or <c>X-EntKube-Ingest-Key</c>.</summary>
    public static string? ExtractToken(HttpRequest req)
    {
        string? auth = req.Headers.Authorization.FirstOrDefault();
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        string? key = req.Headers["X-EntKube-Ingest-Key"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(_key, payload);

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string p = s.Replace('-', '+').Replace('_', '/');
        p = (p.Length % 4) switch { 2 => p + "==", 3 => p + "=", _ => p };
        return Convert.FromBase64String(p);
    }
}
