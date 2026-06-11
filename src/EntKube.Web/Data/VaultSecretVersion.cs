namespace EntKube.Web.Data;

/// <summary>
/// A historical snapshot of a VaultSecret value. Created automatically each time
/// a secret is updated; at most 10 versions are retained per secret (oldest pruned).
/// </summary>
public class VaultSecretVersion
{
    public Guid Id { get; set; }

    public Guid SecretId { get; set; }

    /// <summary>
    /// Monotonically increasing version counter within the secret. Version 1 is the
    /// first value that was later replaced.
    /// </summary>
    public int VersionNumber { get; set; }

    /// <summary>
    /// The secret value at this version, encrypted with the tenant's DEK.
    /// Combined format: ciphertext + 16-byte GCM authentication tag.
    /// </summary>
    public required byte[] EncryptedValue { get; set; }

    /// <summary>
    /// The nonce used when encrypting this version's value.
    /// </summary>
    public required byte[] Nonce { get; set; }

    /// <summary>
    /// The identity (email) of the user who set this value.
    /// Null for versions created before audit tracking was added.
    /// </summary>
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public VaultSecret Secret { get; set; } = null!;
}
