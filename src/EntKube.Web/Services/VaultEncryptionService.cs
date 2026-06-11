using System.Security.Cryptography;
using System.Text;

namespace EntKube.Web.Services;

/// <summary>
/// Provides envelope encryption for the secrets vault. Two layers of AES-256-GCM:
///
/// Layer 1 — Root key (from platform config) seals/unseals per-tenant Data Encryption Keys.
/// Layer 2 — Each tenant's DEK encrypts/decrypts individual secret values.
///
/// This gives us: auto-unseal (root key always available from config), per-tenant isolation
/// (unique DEK per tenant), and envelope encryption (DB compromise alone doesn't expose secrets).
/// The root key can be rotated by re-encrypting all DEKs — without touching individual secrets.
/// </summary>
public class VaultEncryptionService
{
    private readonly byte[] rootKey;

    public VaultEncryptionService(byte[] rootKey)
    {
        // The root key must be exactly 256 bits for AES-256.
        if (rootKey.Length != 32)
        {
            throw new ArgumentException("Root key must be exactly 32 bytes (256 bits).", nameof(rootKey));
        }

        this.rootKey = rootKey;
    }

    /// <summary>
    /// Generates a cryptographically random 256-bit data encryption key.
    /// Called once when creating a new tenant vault.
    /// </summary>
    public byte[] GenerateDataKey()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Seals (encrypts) a tenant's data key using the platform root key.
    /// The encrypted key and nonce are stored in the database alongside the vault.
    /// </summary>
    public (byte[] encryptedKey, byte[] nonce) SealDataKey(byte[] dataKey)
    {
        return EncryptBytes(rootKey, dataKey);
    }

    /// <summary>
    /// Unseals (decrypts) a tenant's data key using the platform root key.
    /// This is the "auto-unseal" — the root key is always available from config,
    /// so no manual intervention or key ceremony is needed.
    /// </summary>
    public byte[] UnsealDataKey(byte[] encryptedKey, byte[] nonce)
    {
        return DecryptBytes(rootKey, encryptedKey, nonce);
    }

    /// <summary>
    /// Encrypts a secret value using the tenant's data encryption key.
    /// Each call generates a unique nonce, so identical plaintexts produce different ciphertexts.
    /// </summary>
    public (byte[] ciphertext, byte[] nonce) Encrypt(byte[] dataKey, string plaintext)
    {
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        return EncryptBytes(dataKey, plaintextBytes);
    }

    /// <summary>
    /// Decrypts a secret value using the tenant's data encryption key.
    /// Returns the original plaintext string.
    /// </summary>
    public string Decrypt(byte[] dataKey, byte[] ciphertext, byte[] nonce)
    {
        byte[] plaintextBytes = DecryptBytes(dataKey, ciphertext, nonce);
        return Encoding.UTF8.GetString(plaintextBytes);
    }

    // --- Private helpers for AES-256-GCM ---

    private static (byte[] ciphertext, byte[] nonce) EncryptBytes(byte[] key, byte[] plaintext)
    {
        // AES-GCM uses a 12-byte nonce (96 bits) per NIST recommendation.
        byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);

        // The tag is 16 bytes (128 bits) — appended to the ciphertext for storage simplicity.
        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];
        byte[] ciphertext = new byte[plaintext.Length];

        using (AesGcm aes = new(key, AesGcm.TagByteSizes.MaxSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Combine ciphertext + tag into a single byte array for simple storage.
        byte[] combined = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);

        return (combined, nonce);
    }

    private static byte[] DecryptBytes(byte[] key, byte[] combinedCiphertext, byte[] nonce)
    {
        // Split the combined array back into ciphertext and tag.
        int tagLength = AesGcm.TagByteSizes.MaxSize;
        int ciphertextLength = combinedCiphertext.Length - tagLength;

        byte[] ciphertext = new byte[ciphertextLength];
        byte[] tag = new byte[tagLength];

        Buffer.BlockCopy(combinedCiphertext, 0, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(combinedCiphertext, ciphertextLength, tag, 0, tagLength);

        byte[] plaintext = new byte[ciphertextLength];

        using (AesGcm aes = new(key, AesGcm.TagByteSizes.MaxSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }
}
