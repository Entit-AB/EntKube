using System.Security.Cryptography;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.PublicApi;

/// <summary>The identity a valid API token resolves to, used by the endpoints for authorization.</summary>
public sealed record ApiTokenPrincipal
{
    public required Guid TokenId { get; init; }
    public required Guid TenantId { get; init; }
    public required string TokenName { get; init; }
    public required IReadOnlySet<string> Scopes { get; init; }

    public bool HasScope(string scope) => Scopes.Contains(scope);
}

/// <summary>Result of minting a token — the plaintext is available here and nowhere else, ever again.</summary>
public sealed record CreatedApiToken
{
    public required ApiToken Token { get; init; }

    /// <summary>The full token. Shown once at creation; only its hash is stored.</summary>
    public required string Plaintext { get; init; }
}

/// <summary>
/// Mints, verifies and revokes public API tokens.
///
/// Tokens are 256 bits of CSPRNG output, stored only as a SHA-256 hash. Lookup is by
/// hash, so a stolen database yields no usable credential, and verification stays a
/// single indexed read rather than a scan-and-compare over every row.
/// </summary>
public class ApiTokenService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ApiTokenService> logger)
{
    /// <summary>
    /// Prefix on every token. Makes a leaked credential greppable in logs and lets
    /// secret scanners recognise it, which is why the convention exists at all.
    /// </summary>
    public const string TokenPrefix = "ekp_";

    /// <summary>Characters of the token kept in the clear for display ("ekp_7Fq2KpL9…").</summary>
    private const int DisplayPrefixLength = 12;

    /// <summary>
    /// Creates a token for a tenant and returns the plaintext exactly once.
    /// </summary>
    public async Task<CreatedApiToken> CreateAsync(
        Guid tenantId, string name, IEnumerable<string> scopes,
        string? createdBy, DateTime? expiresAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Token name is required.", nameof(name));
        }

        string serializedScopes = ApiScopes.Serialize(scopes);
        if (serializedScopes.Length == 0)
        {
            // A token with no scopes can do nothing; minting one is always a mistake and
            // would sit in the list looking like working access.
            throw new ArgumentException("At least one valid scope is required.", nameof(scopes));
        }

        string plaintext = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

        ApiToken token = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            TokenHash = Hash(plaintext),
            DisplayPrefix = plaintext[..DisplayPrefixLength],
            Scopes = serializedScopes,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
            ExpiresAt = expiresAt,
        };

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "API token {TokenId} ({Name}) created for tenant {TenantId} with scopes [{Scopes}]",
            token.Id, token.Name, tenantId, serializedScopes);

        return new CreatedApiToken { Token = token, Plaintext = plaintext };
    }

    /// <summary>
    /// Verifies a presented token. Returns null for anything that is not a live,
    /// unexpired, unrevoked token — the caller cannot distinguish the reasons, which is
    /// deliberate: telling a caller "expired" vs "unknown" leaks whether a token existed.
    /// </summary>
    public async Task<ApiTokenPrincipal?> ValidateAsync(string? presented, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string hash = Hash(presented.Trim());

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ApiToken? token = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null || token.RevokedAt is not null)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        if (token.ExpiresAt is DateTime expiry && expiry <= now)
        {
            return null;
        }

        // Coarse last-used stamping: only write when the value would move by more than a
        // minute, so a busy integration doesn't turn every read into a database write.
        if (token.LastUsedAt is null || now - token.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = now;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Last-used tracking is a convenience for finding unused tokens. It must
                // never be able to fail authentication: if the row is revoked or deleted
                // between the read above and this write, the caller should get a clean
                // result from the token they presented, not a 500 from our bookkeeping.
                logger.LogDebug(ex, "Could not stamp last-used on API token {TokenId}", token.Id);
            }
        }

        return new ApiTokenPrincipal
        {
            TokenId = token.Id,
            TenantId = token.TenantId,
            TokenName = token.Name,
            Scopes = ApiScopes.Parse(token.Scopes),
        };
    }

    public async Task<List<ApiToken>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiTokens
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Revokes a token. The row is kept rather than deleted so the audit trail — who
    /// created it, when it was last used — survives the revocation.
    /// </summary>
    public async Task<bool> RevokeAsync(Guid tenantId, Guid tokenId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Scoped by tenant as well as id so a tenant can never revoke another's token.
        ApiToken? token = await db.ApiTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.TenantId == tenantId, ct);

        if (token is null || token.RevokedAt is not null)
        {
            return false;
        }

        token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("API token {TokenId} revoked for tenant {TenantId}", tokenId, tenantId);
        return true;
    }

    /// <summary>Extracts a bearer token from the request's Authorization header.</summary>
    public static string? ExtractToken(HttpRequest request)
    {
        string? header = request.Headers.Authorization.FirstOrDefault();
        return header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>SHA-256, hex-encoded. Exposed for tests that need to seed a known token.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
