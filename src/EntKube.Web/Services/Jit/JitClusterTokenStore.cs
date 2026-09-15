using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Jit;

/// <summary>
/// Holds the bound ServiceAccount token for a grant, encrypted on the grant's own row.
///
/// The token is what the proxy presents to the API server, so it is a live cluster credential and
/// cannot sit in the clear. It uses the same envelope as the vault — a per-grant data key sealed
/// with the platform root key — but keeps the key on the grant rather than in a shared tenant DEK,
/// for two reasons. A tenant need not have a vault for JIT to work, and clearing the grant's
/// columns destroys the credential outright rather than leaving ciphertext whose key still exists
/// somewhere else.
/// </summary>
public static class JitClusterTokenStore
{
    /// <summary>Encrypts and stores the bound token for a grant, replacing anything already there.</summary>
    public static async Task StoreAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        VaultEncryptionService encryption,
        Guid grantId,
        string token,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        JitGrant grant = await db.JitGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct)
            ?? throw new InvalidOperationException("Grant not found.");

        byte[] dataKey = encryption.GenerateDataKey();
        (byte[] sealedKey, byte[] keyNonce) = encryption.SealDataKey(dataKey);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, token);

        grant.ClusterTokenKey = sealedKey;
        grant.ClusterTokenKeyNonce = keyNonce;
        grant.EncryptedClusterToken = ciphertext;
        grant.ClusterTokenNonce = nonce;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the bound token for a grant, or null when there is none — a grant that was never
    /// minted, or one whose credential has already been destroyed.
    /// </summary>
    public static string? Read(VaultEncryptionService encryption, JitGrant grant)
    {
        if (grant.EncryptedClusterToken is null || grant.ClusterTokenNonce is null
            || grant.ClusterTokenKey is null || grant.ClusterTokenKeyNonce is null)
        {
            return null;
        }

        byte[] dataKey = encryption.UnsealDataKey(grant.ClusterTokenKey, grant.ClusterTokenKeyNonce);
        return encryption.Decrypt(dataKey, grant.EncryptedClusterToken, grant.ClusterTokenNonce);
    }

    /// <summary>
    /// Destroys the stored credential. Called at teardown, so an expired grant's row carries the
    /// history of the access without carrying the means to repeat it.
    /// </summary>
    public static async Task ForgetAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, Guid grantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        JitGrant? grant = await db.JitGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null) return;

        grant.EncryptedClusterToken = null;
        grant.ClusterTokenNonce = null;
        grant.ClusterTokenKey = null;
        grant.ClusterTokenKeyNonce = null;

        await db.SaveChangesAsync(ct);
    }
}
