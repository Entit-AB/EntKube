namespace EntKube.Web.Data;

/// <summary>
/// A scoped bearer token for EntKube's public API.
///
/// Unlike the ingest token (a stateless HMAC capability), an API token is stored:
/// it needs individual revocation, named scopes, expiry and last-used tracking,
/// none of which a self-contained signed token can provide without a lookup anyway.
///
/// Only the SHA-256 hash of the token is persisted. The plaintext is shown exactly
/// once, at creation, and cannot be recovered afterwards — so a database leak does
/// not hand over working credentials.
/// </summary>
public class ApiToken
{
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant this token can act within. Every request authenticated by this token is
    /// scoped to this tenant, so a token can never reach another tenant's data.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>Operator-supplied label, e.g. "CI pipeline" — shown in the token list.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// SHA-256 of the token, hex-encoded. SHA-256 rather than a password hash on purpose:
    /// the token is 256 bits of CSPRNG output, so there is no dictionary to attack and a
    /// deliberately slow hash would only add latency to every API request.
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// First few characters of the token, stored in the clear so the UI can show
    /// "ekp_7Fq2…" and an operator can tell two tokens apart without revealing either.
    /// </summary>
    public required string DisplayPrefix { get; set; }

    /// <summary>Space-separated scope list, e.g. "fleet:read ops:read".</summary>
    public string Scopes { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Username of the operator who created the token, for audit.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Null means the token never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Stamped on each successful authentication, so unused tokens can be found and removed.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Set when revoked. A revoked token is kept, not deleted, so the audit trail survives.</summary>
    public DateTime? RevokedAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
