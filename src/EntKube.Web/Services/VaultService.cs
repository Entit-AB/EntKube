using System.Diagnostics;
using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages the per-tenant secrets vault: initializing vaults, storing/retrieving
/// encrypted secrets for apps and cluster components, and configuring Kubernetes
/// sync. Each operation unseals the vault transparently (auto-unseal via root key).
/// </summary>
public class VaultService(IDbContextFactory<ApplicationDbContext> dbFactory, VaultEncryptionService encryption)
{
    // --- Vault Lifecycle ---

    /// <summary>
    /// Creates a new vault for the tenant if one doesn't already exist.
    /// Generates a fresh DEK and seals it with the platform root key.
    /// Idempotent — returns the existing vault if already initialized.
    /// </summary>
    public async Task<SecretVault> InitializeVaultAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // If the tenant already has a vault, just return it.
        SecretVault? existing = await db.Set<SecretVault>()
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);

        if (existing is not null)
        {
            return existing;
        }

        // Generate a brand new data encryption key and seal it with the root key.
        byte[] dataKey = encryption.GenerateDataKey();
        (byte[] encryptedKey, byte[] nonce) = encryption.SealDataKey(dataKey);

        SecretVault vault = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EncryptedDataKey = encryptedKey,
            Nonce = nonce
        };

        db.Set<SecretVault>().Add(vault);
        await db.SaveChangesAsync(ct);
        return vault;
    }

    /// <summary>
    /// Retrieves the vault for a tenant, or null if not yet initialized.
    /// </summary>
    public async Task<SecretVault?> GetVaultAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<SecretVault>()
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);
    }

    // --- App Secrets ---

    /// <summary>
    /// Stores or updates a secret for a customer app. If a secret with the same
    /// name already exists for this app, its value is updated in place.
    /// </summary>
    public async Task<VaultSecret> SetAppSecretAsync(
        Guid tenantId, Guid appId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Unseal the vault to get the tenant's DEK.
        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        // Check if a secret with this name already exists for this app.
        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.AppId == appId && s.Name == name, ct);

        // Encrypt the value with the tenant's DEK.
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            // Update the existing secret's encrypted value.
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        // Create a new secret entry.
        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            AppId = appId
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Decrypts and returns a specific app secret value.
    /// </summary>
    public async Task<string?> GetAppSecretValueAsync(
        Guid tenantId, Guid appId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.AppId == appId && s.Name == name, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Lists all secrets for an app (metadata only — values remain encrypted).
    /// </summary>
    public async Task<List<VaultSecret>> GetAppSecretsAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.AppId == appId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    // --- Component Secrets ---

    /// <summary>
    /// Stores or updates a secret for a cluster component. When k8sSecretName is
    /// provided, the vault sync service will mirror the value into that Kubernetes Secret.
    /// </summary>
    public async Task<VaultSecret> SetComponentSecretAsync(
        Guid tenantId, Guid componentId, string name, string value,
        CancellationToken ct = default,
        string? k8sSecretName = null, string? k8sNamespace = null)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.ComponentId == componentId && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            if (k8sSecretName is not null)
            {
                existing.SyncToKubernetes = true;
                existing.KubernetesSecretName = k8sSecretName;
                existing.KubernetesNamespace = k8sNamespace;
            }
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            ComponentId = componentId,
            SyncToKubernetes = k8sSecretName is not null,
            KubernetesSecretName = k8sSecretName,
            KubernetesNamespace = k8sNamespace
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Decrypts and returns a specific component secret value.
    /// </summary>
    public async Task<string?> GetComponentSecretValueAsync(
        Guid tenantId, Guid componentId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.ComponentId == componentId && s.Name == name, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Lists all secrets for a component (metadata only).
    /// </summary>
    public async Task<List<VaultSecret>> GetComponentSecretsAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.ComponentId == componentId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    // --- Storage Link Secrets ---

    /// <summary>
    /// Creates or updates a secret scoped to a storage link.
    /// </summary>
    public async Task<VaultSecret> SetStorageLinkSecretAsync(
        Guid tenantId, Guid storageLinkId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.StorageLinkId == storageLinkId && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            StorageLinkId = storageLinkId
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Returns all secrets scoped to a storage link.
    /// </summary>
    public async Task<List<VaultSecret>> GetStorageLinkSecretsAsync(
        Guid tenantId, Guid storageLinkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.StorageLinkId == storageLinkId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gets a single decrypted secret value for a storage link by name.
    /// Returns null if the secret doesn't exist.
    /// </summary>
    public async Task<string?> GetStorageLinkSecretValueAsync(
        Guid tenantId, Guid storageLinkId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.StorageLinkId == storageLinkId && s.Name == name, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    // --- OpenStack Connection Secrets ---

    /// <summary>
    /// Creates or updates a secret scoped to an OpenStack connection.
    /// </summary>
    public async Task<VaultSecret> SetOpenStackSecretAsync(
        Guid tenantId, Guid openStackConnectionId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.OpenStackConnectionId == openStackConnectionId && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            OpenStackConnectionId = openStackConnectionId
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Returns all secrets scoped to an OpenStack connection.
    /// </summary>
    public async Task<List<VaultSecret>> GetOpenStackSecretsAsync(
        Guid tenantId, Guid openStackConnectionId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.OpenStackConnectionId == openStackConnectionId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Decrypts and returns a specific OpenStack connection secret by name.
    /// </summary>
    public async Task<string?> GetOpenStackSecretValueAsync(
        Guid tenantId, Guid openStackConnectionId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.OpenStackConnectionId == openStackConnectionId && s.Name == name, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    // --- CNPG Database Secrets ---

    /// <summary>
    /// Creates or updates a secret scoped to a CNPG database, pre-configured
    /// for Kubernetes sync so applications can consume connection credentials
    /// as a standard K8s Secret.
    /// </summary>
    public async Task<VaultSecret> SetCnpgDatabaseSecretAsync(
        Guid tenantId, Guid cnpgDatabaseId, string name, string value,
        string k8sSecretName, string k8sNamespace, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.CnpgDatabaseId == cnpgDatabaseId && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.SyncToKubernetes = true;
            existing.KubernetesSecretName = k8sSecretName;
            existing.KubernetesNamespace = k8sNamespace;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            CnpgDatabaseId = cnpgDatabaseId,
            SyncToKubernetes = true,
            KubernetesSecretName = k8sSecretName,
            KubernetesNamespace = k8sNamespace
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Returns all secrets scoped to a CNPG database.
    /// </summary>
    public async Task<List<VaultSecret>> GetCnpgDatabaseSecretsAsync(
        Guid tenantId, Guid cnpgDatabaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.CnpgDatabaseId == cnpgDatabaseId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<List<VaultSecret>> GetMongoDatabaseSecretsAsync(
        Guid tenantId, Guid mongoDatabaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.MongoDatabaseId == mongoDatabaseId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the decrypted password for a CNPG database. Used when another
    /// service (e.g. Keycloak) needs to read the DB password to propagate it.
    /// </summary>
    public async Task<string?> GetCnpgDatabasePasswordAsync(
        Guid tenantId, Guid cnpgDatabaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.CnpgDatabaseId == cnpgDatabaseId
                && s.Name == "PASSWORD", ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Stores or updates a CNPG-database-scoped secret that is NOT synced to Kubernetes.
    /// Used for administrative credentials (e.g. superuser password) that should live
    /// in the vault for reference but must never be pushed to a K8s Secret.
    /// </summary>
    public async Task<VaultSecret> SetCnpgDatabaseAdminSecretAsync(
        Guid tenantId, Guid cnpgDatabaseId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.CnpgDatabaseId == cnpgDatabaseId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.SyncToKubernetes = false;
            existing.KubernetesSecretName = null;
            existing.KubernetesNamespace = null;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            CnpgDatabaseId = cnpgDatabaseId,
            SyncToKubernetes = false
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Decrypts and returns a CNPG-database-scoped admin secret value by name.
    /// </summary>
    public async Task<string?> GetCnpgDatabaseAdminSecretValueAsync(
        Guid tenantId, Guid cnpgDatabaseId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.CnpgDatabaseId == cnpgDatabaseId
                && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    public async Task<VaultSecret> SetCnpgClusterSecretAsync(
        Guid tenantId, Guid cnpgClusterId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.CnpgClusterId == cnpgClusterId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            CnpgClusterId = cnpgClusterId,
            SyncToKubernetes = false
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task<string?> GetCnpgClusterSecretValueAsync(
        Guid tenantId, Guid cnpgClusterId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.CnpgClusterId == cnpgClusterId
                && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    public async Task<List<VaultSecret>> GetCnpgClusterSecretsAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.CnpgClusterId == cnpgClusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns all vault secrets belonging to a CNPG cluster: cluster-level secrets
    /// (e.g. superuser credentials) plus secrets for every database in the cluster.
    /// Database secrets include the CnpgDatabase navigation property so callers can
    /// group or label them by database name.
    /// </summary>
    public async Task<List<VaultSecret>> GetAllCnpgSecretsForClusterAsync(
        Guid tenantId, Guid cnpgClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> clusterSecrets = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.CnpgClusterId == cnpgClusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        List<VaultSecret> databaseSecrets = await db.Set<VaultSecret>()
            .Include(s => s.CnpgDatabase)
            .Where(s => s.Vault.TenantId == tenantId
                && s.CnpgDatabase != null
                && s.CnpgDatabase.CnpgClusterId == cnpgClusterId)
            .OrderBy(s => s.CnpgDatabase!.Name)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);

        return [.. clusterSecrets, .. databaseSecrets];
    }

    public async Task<List<VaultSecret>> GetAllMongoSecretsForClusterAsync(
        Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> clusterSecrets = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.MongoClusterId == mongoClusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        List<VaultSecret> databaseSecrets = await db.Set<VaultSecret>()
            .Include(s => s.MongoDatabase)
            .Where(s => s.Vault.TenantId == tenantId
                && s.MongoDatabase != null
                && s.MongoDatabase.MongoClusterId == mongoClusterId)
            .OrderBy(s => s.MongoDatabase!.Name)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);

        return [.. clusterSecrets, .. databaseSecrets];
    }

    public async Task<List<VaultSecret>> GetAllRegisteredPostgresSecretsForInstanceAsync(
        Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Include(s => s.RegisteredPostgresDatabase)
            .Where(s => s.Vault.TenantId == tenantId
                && s.RegisteredPostgresDatabase != null
                && s.RegisteredPostgresDatabase.RegisteredPostgresInstanceId == instanceId)
            .OrderBy(s => s.RegisteredPostgresDatabase!.Name)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates or updates a vault secret scoped to a MongoDB database.
    /// Mirrors SetCnpgDatabaseSecretAsync but uses MongoDatabaseId.
    /// </summary>
    public async Task<VaultSecret> SetMongoDatabaseSecretAsync(
        Guid tenantId, Guid mongoDatabaseId, string name, string value,
        string k8sSecretName, string k8sNamespace, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.MongoDatabaseId == mongoDatabaseId && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.SyncToKubernetes = true;
            existing.KubernetesSecretName = k8sSecretName;
            existing.KubernetesNamespace = k8sNamespace;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            MongoDatabaseId = mongoDatabaseId,
            SyncToKubernetes = true,
            KubernetesSecretName = k8sSecretName,
            KubernetesNamespace = k8sNamespace
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Creates or updates a vault secret scoped to a managed MongoDB cluster.
    /// When SyncToKubernetes is true the vault sync service will keep the K8s Secret
    /// up to date, but callers that need the Secret immediately should also apply
    /// the manifest directly via IKubernetesClientFactory.
    /// </summary>
    public async Task<VaultSecret> SetMongoClusterSecretAsync(
        Guid tenantId, Guid mongoClusterId, string name, string value,
        string k8sSecretName, string k8sNamespace, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.MongoClusterId == mongoClusterId && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.SyncToKubernetes = true;
            existing.KubernetesSecretName = k8sSecretName;
            existing.KubernetesNamespace = k8sNamespace;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            MongoClusterId = mongoClusterId,
            SyncToKubernetes = true,
            KubernetesSecretName = k8sSecretName,
            KubernetesNamespace = k8sNamespace
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Decrypts and returns the value of a secret scoped to a managed MongoDB cluster.
    /// Returns null if no matching secret is found.
    /// </summary>
    public async Task<string?> GetMongoClusterSecretValueAsync(
        Guid tenantId, Guid mongoClusterId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.MongoClusterId == mongoClusterId && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    // --- Registered Postgres Database Secrets ---

    /// <summary>
    /// Creates or updates a connection credential secret scoped to a registered
    /// (non-CNPG) PostgreSQL database, pre-configured for Kubernetes sync.
    /// </summary>
    public async Task<VaultSecret> SetRegisteredPostgresDatabaseSecretAsync(
        Guid tenantId, Guid registeredPostgresDatabaseId, string name, string value,
        string k8sSecretName, string k8sNamespace, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.RegisteredPostgresDatabaseId == registeredPostgresDatabaseId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.SyncToKubernetes = true;
            existing.KubernetesSecretName = k8sSecretName;
            existing.KubernetesNamespace = k8sNamespace;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            RegisteredPostgresDatabaseId = registeredPostgresDatabaseId,
            SyncToKubernetes = true,
            KubernetesSecretName = k8sSecretName,
            KubernetesNamespace = k8sNamespace
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Returns all secrets scoped to a registered Postgres database.
    /// </summary>
    public async Task<List<VaultSecret>> GetRegisteredPostgresDatabaseSecretsAsync(
        Guid tenantId, Guid registeredPostgresDatabaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId
                && s.RegisteredPostgresDatabaseId == registeredPostgresDatabaseId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the decrypted password for a registered Postgres database.
    /// </summary>
    public async Task<string?> GetRegisteredPostgresDatabasePasswordAsync(
        Guid tenantId, Guid registeredPostgresDatabaseId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.RegisteredPostgresDatabaseId == registeredPostgresDatabaseId
                && s.Name == "PASSWORD", ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Creates or updates the admin password secret for a RegisteredPostgresInstance.
    /// Stored as a named app-independent vault secret using a unique key per instance.
    /// </summary>
    public async Task SetRegisteredPostgresAdminPasswordAsync(
        Guid tenantId, Guid instanceId, string password, CancellationToken ct = default)
    {
        await InitializeVaultAsync(tenantId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        string secretName = AdminPasswordSecretName(instanceId);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.Name == secretName, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, password);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        db.Set<VaultSecret>().Add(new VaultSecret
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = secretName,
            EncryptedValue = ciphertext,
            Nonce = nonce
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Retrieves the decrypted admin password for a RegisteredPostgresInstance.
    /// Returns null if not yet set.
    /// </summary>
    public async Task<string?> GetRegisteredPostgresAdminPasswordAsync(
        Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        string secretName = AdminPasswordSecretName(instanceId);

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.Name == secretName, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    private static string AdminPasswordSecretName(Guid instanceId) =>
        $"__reg-pg-{instanceId:N}__admin";

    // --- Secret Management ---

    /// <summary>
    /// Decrypts and returns the value of any secret by its ID, regardless of scope.
    /// Used by the UI to reveal a secret value on demand.
    /// </summary>
    public async Task<string?> GetSecretValueByIdAsync(Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(secret.Vault.TenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Updates the encrypted value of an existing secret by its ID.
    /// Archives the previous value as a version before overwriting.
    /// </summary>
    public async Task<bool> UpdateSecretValueAsync(
        Guid secretId, string newValue, string? updatedBy = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        if (secret is null)
        {
            return false;
        }

        byte[] dataKey = await UnsealVaultAsync(secret.Vault.TenantId, ct);

        await ArchiveVersionAsync(db, secret, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, newValue);

        secret.EncryptedValue = ciphertext;
        secret.Nonce = nonce;
        secret.UpdatedAt = DateTime.UtcNow;
        secret.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // --- Version History ---

    /// <summary>
    /// Returns the version history for a secret (metadata only — values remain encrypted).
    /// Ordered newest first.
    /// </summary>
    public async Task<List<VaultSecretVersion>> GetSecretVersionsAsync(
        Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecretVersion>()
            .Where(v => v.SecretId == secretId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Decrypts and returns the value of a specific historical version.
    /// </summary>
    public async Task<string?> GetSecretVersionValueAsync(
        Guid secretId, Guid versionId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecretVersion? version = await db.Set<VaultSecretVersion>()
            .Include(v => v.Secret).ThenInclude(s => s.Vault)
            .FirstOrDefaultAsync(v => v.Id == versionId && v.SecretId == secretId, ct);

        if (version is null) return null;

        byte[] dataKey = await UnsealVaultAsync(version.Secret.Vault.TenantId, ct);
        return encryption.Decrypt(dataKey, version.EncryptedValue, version.Nonce);
    }

    /// <summary>
    /// Restores a secret's value to a specific historical version.
    /// Archives the current value before overwriting.
    /// </summary>
    public async Task<bool> RollbackToVersionAsync(
        Guid secretId, Guid versionId, string? updatedBy = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        VaultSecretVersion? version = await db.Set<VaultSecretVersion>()
            .FirstOrDefaultAsync(v => v.Id == versionId && v.SecretId == secretId, ct);

        if (secret is null || version is null) return false;

        await ArchiveVersionAsync(db, secret, ct);

        secret.EncryptedValue = version.EncryptedValue;
        secret.Nonce = version.Nonce;
        secret.UpdatedAt = DateTime.UtcNow;
        secret.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Checks whether a secret can be safely deleted. A secret is protected from
    /// deletion when it belongs to a StorageLink that has active StorageBindings
    /// (i.e. a workload depends on those credentials being available).
    /// </summary>
    public async Task<(bool CanDelete, string? Reason)> CanDeleteSecretAsync(
        Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>().FindAsync([secretId], ct);

        if (secret is null)
        {
            return (true, null);
        }

        // If the secret belongs to a storage link, check for active bindings.

        if (secret.StorageLinkId is not null)
        {
            bool hasBindings = await db.Set<StorageBinding>()
                .AnyAsync(b => b.StorageLinkId == secret.StorageLinkId && b.SyncEnabled, ct);

            if (hasBindings)
            {
                return (false, "This secret is linked to a storage binding that syncs to Kubernetes. Unbind the storage first.");
            }
        }

        // If the secret is synced to K8s, warn but still allow deletion.

        return (true, null);
    }

    /// <summary>
    /// Deletes a secret by ID regardless of its scope.
    /// </summary>
    public async Task<bool> DeleteSecretAsync(Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>().FindAsync([secretId], ct);

        if (secret is null)
        {
            return false;
        }

        db.Set<VaultSecret>().Remove(secret);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Configures Kubernetes sync for a secret — enabling or disabling sync
    /// and setting the target Secret name, namespace, and cluster.
    /// Pass <paramref name="clusterId"/> for app-scoped secrets where the cluster
    /// cannot be derived from the secret's owner relationship.
    /// </summary>
    public async Task ConfigureKubernetesSyncAsync(
        Guid secretId, bool syncEnabled, string? secretName, string? ns,
        CancellationToken ct = default, Guid? clusterId = null)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>().FindAsync([secretId], ct);

        if (secret is null)
        {
            return;
        }

        secret.SyncToKubernetes = syncEnabled;
        secret.KubernetesSecretName = secretName;
        secret.KubernetesNamespace = ns;
        if (clusterId.HasValue)
        {
            secret.KubernetesClusterId = clusterId;
        }
        else if (!syncEnabled)
        {
            secret.KubernetesClusterId = null;
        }
        secret.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Syncs all app-scoped secrets that have <see cref="VaultSecret.SyncToKubernetes"/>
    /// enabled to their target Kubernetes cluster. Secrets are grouped by
    /// (KubernetesSecretName, KubernetesNamespace) and written as Opaque Secrets.
    /// Secrets without a <see cref="VaultSecret.KubernetesClusterId"/> are reported
    /// in the output as needing cluster configuration but do not block the rest.
    /// </summary>
    public async Task<HelmExecutionResult> SyncAppSecretsToKubernetesAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load ALL sync-enabled secrets — including those missing a cluster reference,
        // so we can report them clearly instead of silently skipping.
        List<VaultSecret> allSecrets = await db.Set<VaultSecret>()
            .Include(s => s.KubernetesCluster)
            .Where(s => s.AppId == appId && s.SyncToKubernetes)
            .ToListAsync(ct);

        if (allSecrets.Count == 0)
        {
            return new HelmExecutionResult
            {
                Success = true,
                Output = "No app secrets marked for Kubernetes sync."
            };
        }

        List<string> results = [];

        // Secrets without a K8s Secret name or cluster are not actionable — tell the user.
        foreach (VaultSecret s in allSecrets.Where(s => string.IsNullOrWhiteSpace(s.KubernetesSecretName)))
        {
            results.Add($"⚠ '{s.Name}' has no K8s Secret name configured — open K8s sync settings to assign one.");
        }
        foreach (VaultSecret s in allSecrets.Where(s => !string.IsNullOrWhiteSpace(s.KubernetesSecretName) && s.KubernetesClusterId == null))
        {
            results.Add($"⚠ '{s.Name}' → '{s.KubernetesSecretName}' has no target cluster — open K8s sync settings to assign one.");
        }

        // Only process secrets that are fully configured.
        List<VaultSecret> actionable = allSecrets
            .Where(s => s.KubernetesClusterId != null && !string.IsNullOrWhiteSpace(s.KubernetesSecretName))
            .ToList();

        if (actionable.Count == 0)
        {
            return new HelmExecutionResult
            {
                Success = results.Count == 0,
                Output = results.Count > 0 ? string.Join("\n", results) : "No app secrets with both a K8s Secret name and a target cluster."
            };
        }

        // Group by cluster so we only write one kubeconfig temp file per cluster.
        IEnumerable<IGrouping<Guid, VaultSecret>> clusterGroups =
            actionable.GroupBy(s => s.KubernetesClusterId!.Value);

        foreach (IGrouping<Guid, VaultSecret> clusterGroup in clusterGroups)
        {
            KubernetesCluster? cluster = clusterGroup.First().KubernetesCluster;
            if (cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                results.Add($"✗ Cluster '{clusterGroup.Key}' has no kubeconfig — skipping {clusterGroup.Count()} secret(s).");
                continue;
            }

            string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");

            try
            {
                await File.WriteAllTextAsync(tempKubeconfig, cluster.Kubeconfig, ct);

                // Within this cluster, group secrets by (K8sSecretName, Namespace).
                IEnumerable<IGrouping<(string SecretName, string Namespace), VaultSecret>> secretGroups =
                    clusterGroup.GroupBy(s => (
                        SecretName: s.KubernetesSecretName!,
                        Namespace: s.KubernetesNamespace ?? "default"
                    ));

                foreach (IGrouping<(string SecretName, string Namespace), VaultSecret> group in secretGroups)
                {
                    string k8sSecretName = group.Key.SecretName;
                    string ns = group.Key.Namespace;

                    // Ensure namespace exists.
                    await RunProcessAsync("kubectl",
                        $"create namespace {ns} --kubeconfig {tempKubeconfig}", ct);

                    // Decrypt each secret value.
                    List<string> literals = [];

                    foreach (VaultSecret vaultSecret in group)
                    {
                        string? plainValue = await GetAppSecretValueAsync(tenantId, appId, vaultSecret.Name, ct);

                        if (plainValue is not null)
                        {
                            // Shell-safe: write value to a temp file so we avoid quoting issues.
                            string tmpVal = Path.Combine(Path.GetTempPath(), $"entkube-val-{Guid.NewGuid()}");
                            await File.WriteAllTextAsync(tmpVal, plainValue, ct);
                            literals.Add($"--from-file={vaultSecret.Name}={tmpVal}");
                        }
                    }

                    if (literals.Count == 0)
                    {
                        results.Add($"  (skipped '{k8sSecretName}/{ns}' — no decryptable values)");
                        continue;
                    }

                    // Delete and recreate for clean state.
                    await RunProcessAsync("kubectl",
                        $"delete secret {k8sSecretName} --namespace {ns} --ignore-not-found --kubeconfig {tempKubeconfig}", ct);

                    HelmExecutionResult createResult = await RunProcessAsync("kubectl",
                        $"create secret generic {k8sSecretName} --namespace {ns} {string.Join(" ", literals)} --kubeconfig {tempKubeconfig}", ct);

                    // Clean up temp value files.
                    foreach (string lit in literals)
                    {
                        // Extract file path from "--from-file=KEY=PATH"
                        int eq2 = lit.IndexOf('=', lit.IndexOf('=') + 1);
                        if (eq2 >= 0)
                        {
                            string tmpFile = lit[(eq2 + 1)..];
                            if (File.Exists(tmpFile)) File.Delete(tmpFile);
                        }
                    }

                    if (createResult.Success)
                    {
                        results.Add($"✓ Secret '{k8sSecretName}' synced to '{ns}' on '{cluster.Name}' ({group.Count()} keys)");
                    }
                    else
                    {
                        results.Add($"✗ Secret '{k8sSecretName}' failed on '{cluster.Name}': {createResult.Output}");
                    }
                }
            }
            finally
            {
                if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
            }
        }

        bool allSucceeded = results.All(r => r.StartsWith("✓"));
        return new HelmExecutionResult
        {
            Success = allSucceeded,
            Output = string.Join("\n", results)
        };
    }

    // --- Git Repository Secrets ---

    /// <summary>
    /// Stores or updates a secret scoped to a Git repository credential.
    /// Conventional key names: "PAT" (HttpsPat auth), "PASSWORD" (HttpsPassword auth),
    /// "SSH_PRIVATE_KEY" (SshKey auth).
    /// </summary>
    public async Task<VaultSecret> SetGitRepositorySecretAsync(
        Guid tenantId, Guid gitRepositoryId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.GitRepositoryId == gitRepositoryId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            GitRepositoryId = gitRepositoryId
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Decrypts and returns a specific git repository secret value.
    /// Returns null if not found.
    /// </summary>
    public async Task<string?> GetGitRepositorySecretValueAsync(
        Guid tenantId, Guid gitRepositoryId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.GitRepositoryId == gitRepositoryId
                && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Lists all vault secrets scoped to a git repository (metadata only).
    /// </summary>
    public async Task<List<VaultSecret>> GetGitRepositorySecretsAsync(
        Guid tenantId, Guid gitRepositoryId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.GitRepositoryId == gitRepositoryId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    // ── Customer git credential secrets ─────────────────────────────────────────

    /// <summary>
    /// Stores or updates a secret scoped to a customer git credential.
    /// Conventional key names: "PAT", "PASSWORD", "SSH_PRIVATE_KEY".
    /// </summary>
    public async Task<VaultSecret> SetCustomerGitCredentialSecretAsync(
        Guid tenantId, Guid credentialId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.CustomerGitCredentialId == credentialId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            CustomerGitCredentialId = credentialId
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Decrypts and returns a specific customer git credential secret value.
    /// Returns null if not found.
    /// </summary>
    public async Task<string?> GetCustomerGitCredentialSecretValueAsync(
        Guid tenantId, Guid credentialId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.CustomerGitCredentialId == credentialId
                && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    // --- Private Helpers ---

    private static async Task<HelmExecutionResult> RunProcessAsync(
        string executable, string arguments, CancellationToken ct = default)
    {
        ProcessStartInfo psi = new(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = new() { StartInfo = psi };
        StringBuilder output = new();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return new HelmExecutionResult
        {
            Success = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            Output = output.ToString().TrimEnd()
        };
    }

    // --- Cluster Components ---

    /// <summary>
    /// Adds a new component (helm chart, deployment, etc.) to a cluster.
    /// </summary>
    public async Task<ClusterComponent> CreateComponentAsync(
        Guid clusterId, string name, string componentType, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = name,
            ComponentType = componentType
        };

        db.Set<ClusterComponent>().Add(component);
        await db.SaveChangesAsync(ct);
        return component;
    }

    /// <summary>
    /// Lists all components for a cluster.
    /// </summary>
    public async Task<List<ClusterComponent>> GetComponentsAsync(Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<ClusterComponent>()
            .Where(c => c.ClusterId == clusterId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Deletes a component by ID. Cascade will also remove its secrets.
    /// </summary>
    public async Task<bool> DeleteComponentAsync(Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? component = await db.Set<ClusterComponent>().FindAsync([componentId], ct);

        if (component is null)
        {
            return false;
        }

        db.Set<ClusterComponent>().Remove(component);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    // --- RabbitMQ Cluster Secrets ---

    public async Task<VaultSecret> SetRabbitMQClusterSecretAsync(
        Guid tenantId, Guid rabbitMQClusterId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.RabbitMQClusterId == rabbitMQClusterId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            RabbitMQClusterId = rabbitMQClusterId,
            SyncToKubernetes = false
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task<string?> GetRabbitMQClusterSecretValueAsync(
        Guid tenantId, Guid rabbitMQClusterId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.RabbitMQClusterId == rabbitMQClusterId
                && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    public async Task<List<VaultSecret>> GetRabbitMQClusterSecretsAsync(
        Guid tenantId, Guid rabbitMQClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.RabbitMQClusterId == rabbitMQClusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task DeleteRabbitMQClusterSecretAsync(
        Guid tenantId, Guid rabbitMQClusterId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.RabbitMQClusterId == rabbitMQClusterId
                && s.Name == name, ct);

        if (secret is not null)
        {
            db.Set<VaultSecret>().Remove(secret);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Redis cluster secrets ────────────────────────────────────────────────────

    public async Task<VaultSecret> SetRedisClusterSecretAsync(
        Guid tenantId, Guid clusterId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.RedisClusterId == clusterId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            RedisClusterId = clusterId,
            SyncToKubernetes = false
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task<string?> GetRedisClusterSecretValueAsync(
        Guid tenantId, Guid clusterId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.RedisClusterId == clusterId
                && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    public async Task<List<VaultSecret>> GetRedisClusterSecretsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.RedisClusterId == clusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    // ── VPN remote endpoint secrets ──────────────────────────────────────────────

    public async Task<VaultSecret> SetVpnRemoteEndpointSecretAsync(
        Guid tenantId, Guid endpointId, string name, string value,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, value);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.VpnRemoteEndpointId == endpointId && s.Name == name, ct);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            VpnRemoteEndpointId = endpointId
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task<string?> GetVpnRemoteEndpointSecretValueAsync(
        Guid tenantId, Guid endpointId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.VpnRemoteEndpointId == endpointId && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    private const int MaxVersionsPerSecret = 10;

    /// <summary>
    /// Archives the current encrypted value of a secret as a new version row, then
    /// prunes old versions so at most MaxVersionsPerSecret are kept. The caller is
    /// responsible for calling SaveChangesAsync after this returns.
    /// </summary>
    private static async Task ArchiveVersionAsync(
        ApplicationDbContext db, VaultSecret secret, CancellationToken ct)
    {
        int nextVersion = await db.Set<VaultSecretVersion>()
            .Where(v => v.SecretId == secret.Id)
            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;

        nextVersion++;

        db.Set<VaultSecretVersion>().Add(new VaultSecretVersion
        {
            Id = Guid.NewGuid(),
            SecretId = secret.Id,
            VersionNumber = nextVersion,
            EncryptedValue = secret.EncryptedValue,
            Nonce = secret.Nonce,
            CreatedBy = secret.UpdatedBy,
            CreatedAt = secret.UpdatedAt
        });

        // Prune excess versions: delete the oldest ones beyond the cap.
        List<Guid> toDelete = await db.Set<VaultSecretVersion>()
            .Where(v => v.SecretId == secret.Id)
            .OrderByDescending(v => v.VersionNumber)
            .Skip(MaxVersionsPerSecret)
            .Select(v => v.Id)
            .ToListAsync(ct);

        if (toDelete.Count > 0)
        {
            await db.Set<VaultSecretVersion>()
                .Where(v => toDelete.Contains(v.Id))
                .ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>Unseals the tenant's vault and returns the decrypted DEK.</summary>
    private async Task<byte[]> UnsealVaultAsync(Guid tenantId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        SecretVault vault = await db.Set<SecretVault>()
            .FirstAsync(v => v.TenantId == tenantId, ct);

        return encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
    }
}
