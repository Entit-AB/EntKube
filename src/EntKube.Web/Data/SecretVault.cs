namespace EntKube.Web.Data;

/// <summary>
/// A per-tenant secrets vault. Each tenant gets exactly one vault when created.
/// The vault stores an encrypted Data Encryption Key (DEK) that protects all
/// secrets within it. The DEK is sealed with the platform root key — providing
/// automatic unsealing without manual key ceremonies like HashiCorp Vault requires.
/// </summary>
public class SecretVault
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The tenant's Data Encryption Key, encrypted (sealed) with the platform root key.
    /// Combined format: ciphertext + 16-byte GCM authentication tag.
    /// </summary>
    public required byte[] EncryptedDataKey { get; set; }

    /// <summary>
    /// The nonce (IV) used when sealing the DEK. Needed for decryption.
    /// </summary>
    public required byte[] Nonce { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<VaultSecret> Secrets { get; set; } = [];
}
