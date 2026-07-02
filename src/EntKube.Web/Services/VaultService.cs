using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages the per-tenant secrets vault: initializing vaults, storing/retrieving
/// encrypted secrets for apps and cluster components, and configuring Kubernetes
/// sync. Each operation unseals the vault transparently (auto-unseal via root key).
/// </summary>
public class VaultService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultEncryptionService encryption,
    // Optional: used only to evict the cached kubeconfig plaintext after an update. Absent in
    // unit tests, where there is no resolver cache to invalidate.
    KubeconfigResolver? kubeconfigResolver = null)
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
        Guid tenantId, Guid appId, string name, string value,
        CancellationToken ct = default, Guid? environmentId = null)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Unseal the vault to get the tenant's DEK.
        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        // Check if a secret with this name already exists for this app in the
        // same environment scope (a shared secret and an env-bound secret with
        // the same name are distinct entries).
        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.AppId == appId
                && s.EnvironmentId == environmentId && s.Name == name, ct);

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
            AppId = appId,
            EnvironmentId = environmentId
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

    /// <summary>
    /// Lists the app secrets visible within a single environment: secrets bound
    /// to that environment plus "shared" secrets (null EnvironmentId). Secrets
    /// bound to other environments are never returned, so e.g. prod secrets are
    /// invisible while operating in the test environment. Values stay encrypted.
    /// </summary>
    public async Task<List<VaultSecret>> GetAppSecretsForEnvironmentAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.AppId == appId
                && (s.EnvironmentId == null || s.EnvironmentId == environmentId))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    // --- App Certificate Secrets ---

    private static readonly JsonSerializerOptions CertificateJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Stores or updates a TLS certificate secret for an app. The bundle (certificate,
    /// optional key, optional CA, optional chain) is validated, serialized to JSON, and
    /// stored as a single encrypted value with <see cref="VaultSecretType.Certificate"/>.
    /// Upserts by (app, environment, name) like <see cref="SetAppSecretAsync"/>.
    /// </summary>
    /// <returns>(true, secret) on success; (false, null) with the reason in <paramref name="error"/> on validation failure.</returns>
    public async Task<(bool Ok, string? Error)> SetAppCertificateAsync(
        Guid tenantId, Guid appId, string name, CertificateBundle bundle,
        Guid? environmentId = null, string? updatedBy = null, CancellationToken ct = default)
    {
        (bool valid, string? validationError) = CertificateParser.Validate(bundle);
        if (!valid)
        {
            return (false, validationError);
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.AppId == appId
                && s.EnvironmentId == environmentId && s.Name == name, ct);

        string json = JsonSerializer.Serialize(bundle, CertificateJsonOptions);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, json);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.SecretType = VaultSecretType.Certificate;
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = updatedBy;
            await db.SaveChangesAsync(ct);
            return (true, null);
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            SecretType = VaultSecretType.Certificate,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            AppId = appId,
            EnvironmentId = environmentId,
            UpdatedBy = updatedBy
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>
    /// Decrypts a certificate secret and returns its parsed bundle, or null when the
    /// secret does not exist or is not a certificate.
    /// </summary>
    public async Task<CertificateBundle?> GetCertificateBundleByIdAsync(Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        if (secret is null || secret.SecretType != VaultSecretType.Certificate)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(secret.Vault.TenantId, ct);
        string json = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
        return DeserializeBundle(json);
    }

    /// <summary>
    /// Decrypts a certificate secret and returns the parsed metadata of its leaf
    /// certificate (subject, validity, etc.), or null when it is not a parseable
    /// certificate secret. Used to show expiry status in lists.
    /// </summary>
    public async Task<CertificateInfo?> GetCertificateInfoByIdAsync(Guid secretId, CancellationToken ct = default)
    {
        CertificateBundle? bundle = await GetCertificateBundleByIdAsync(secretId, ct);
        return bundle is null ? null : CertificateParser.TryParse(bundle.Certificate);
    }

    private static CertificateBundle DeserializeBundle(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CertificateBundle>(json, CertificateJsonOptions) ?? new CertificateBundle();
        }
        catch
        {
            return new CertificateBundle();
        }
    }

    // --- App OAuth/OIDC Client Secrets ---

    /// <summary>
    /// Stores or updates an OAuth/OIDC client credential (app registration) for an app.
    /// The bundle is validated, serialized to JSON, and stored as a single encrypted
    /// value with <see cref="VaultSecretType.OAuthClient"/>. Upserts by (app, environment, name).
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetAppOAuthClientAsync(
        Guid tenantId, Guid appId, string name, OAuthClientBundle bundle,
        Guid? environmentId = null, string? updatedBy = null, CancellationToken ct = default)
    {
        (bool valid, string? validationError) = OAuthClientHelper.Validate(bundle);
        if (!valid)
        {
            return (false, validationError);
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.AppId == appId
                && s.EnvironmentId == environmentId && s.Name == name, ct);

        string json = JsonSerializer.Serialize(bundle, CertificateJsonOptions);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, json);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.SecretType = VaultSecretType.OAuthClient;
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = updatedBy;
            await db.SaveChangesAsync(ct);
            return (true, null);
        }

        SecretVault vault = (await GetVaultAsync(tenantId, ct))!;

        VaultSecret secret = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            Name = name,
            SecretType = VaultSecretType.OAuthClient,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            AppId = appId,
            EnvironmentId = environmentId,
            UpdatedBy = updatedBy
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>
    /// Decrypts an OAuth client secret and returns its full bundle (including the
    /// client secret), or null when the secret does not exist or is not an OAuth client.
    /// </summary>
    public async Task<OAuthClientBundle?> GetOAuthClientBundleByIdAsync(Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        if (secret is null || secret.SecretType != VaultSecretType.OAuthClient)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(secret.Vault.TenantId, ct);
        string json = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
        return DeserializeOAuthBundle(json);
    }

    /// <summary>
    /// Decrypts an OAuth client secret and returns its non-secret metadata (provider,
    /// client id, issuer, scopes, expiry) for list display. Excludes the client secret.
    /// </summary>
    public async Task<OAuthClientInfo?> GetOAuthClientInfoByIdAsync(Guid secretId, CancellationToken ct = default)
    {
        OAuthClientBundle? bundle = await GetOAuthClientBundleByIdAsync(secretId, ct);
        return bundle?.ToInfo();
    }

    private static OAuthClientBundle DeserializeOAuthBundle(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OAuthClientBundle>(json, CertificateJsonOptions) ?? new OAuthClientBundle();
        }
        catch
        {
            return new OAuthClientBundle();
        }
    }

    // --- Cluster Kubeconfigs ---

    /// <summary>
    /// The conventional secret name for a cluster's kubeconfig. There is one kubeconfig
    /// secret per cluster, scoped via <see cref="VaultSecret.OwnerClusterId"/>.
    /// </summary>
    public const string KubeconfigSecretName = "kubeconfig";

    /// <summary>
    /// Stores or updates a registered cluster's kubeconfig in the tenant vault. The bundle
    /// is validated, serialized to JSON, and stored as a single encrypted value with
    /// <see cref="VaultSecretType.Kubeconfig"/>, scoped to the cluster. The cluster's
    /// <see cref="KubernetesCluster.KubeconfigSecretId"/> is set to point at the secret so
    /// the materialization interceptor can resolve it on load. Upserts by (cluster, name)
    /// and archives the previous value as a version. Returns the secret id on success.
    /// </summary>
    public async Task<(bool Ok, string? Error, Guid? SecretId)> SetClusterKubeconfigAsync(
        Guid tenantId, Guid clusterId, KubeconfigBundle bundle,
        string? updatedBy = null, CancellationToken ct = default)
    {
        (bool valid, string? validationError) = KubeconfigHelper.Validate(bundle);
        if (!valid)
        {
            return (false, validationError, null);
        }

        // A kubeconfig may be the first secret a tenant ever stores, so ensure the vault exists.
        await InitializeVaultAsync(tenantId, ct);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        KubernetesCluster? cluster = await db.Set<KubernetesCluster>()
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
        if (cluster is null)
        {
            return (false, "Cluster not found.", null);
        }

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.OwnerClusterId == clusterId && s.Name == KubeconfigSecretName, ct);

        // Serialize with the bundle's own (camelCase) options so it round-trips with
        // KubeconfigBundle.Deserialize used by the reader, resolver, and expiry scanner.
        string json = bundle.Serialize();
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, json);

        Guid secretId;
        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.SecretType = VaultSecretType.Kubeconfig;
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = updatedBy;
            secretId = existing.Id;
        }
        else
        {
            SecretVault vault = (await GetVaultAsync(tenantId, ct))!;
            secretId = Guid.NewGuid();
            db.Set<VaultSecret>().Add(new VaultSecret
            {
                Id = secretId,
                VaultId = vault.Id,
                Name = KubeconfigSecretName,
                SecretType = VaultSecretType.Kubeconfig,
                EncryptedValue = ciphertext,
                Nonce = nonce,
                OwnerClusterId = clusterId,
                UpdatedBy = updatedBy
            });
        }

        cluster.KubeconfigSecretId = secretId;
        await db.SaveChangesAsync(ct);

        // Drop the cached plaintext so the next materialization re-decrypts the new value.
        kubeconfigResolver?.Invalidate(secretId);

        return (true, null, secretId);
    }

    /// <summary>
    /// Decrypts a cluster kubeconfig secret and returns its full bundle (including the
    /// kubeconfig YAML), or null when the secret does not exist or is not a kubeconfig.
    /// </summary>
    public async Task<KubeconfigBundle?> GetKubeconfigBundleByIdAsync(Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        if (secret is null || secret.SecretType != VaultSecretType.Kubeconfig)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(secret.Vault.TenantId, ct);
        string json = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
        return KubeconfigBundle.Deserialize(json);
    }

    /// <summary>
    /// Decrypts a cluster kubeconfig secret and returns its non-secret metadata (context,
    /// API server, expiry) for list/badge display. Excludes the kubeconfig YAML.
    /// </summary>
    public async Task<KubeconfigInfo?> GetKubeconfigInfoByIdAsync(Guid secretId, CancellationToken ct = default)
    {
        KubeconfigBundle? bundle = await GetKubeconfigBundleByIdAsync(secretId, ct);
        return bundle?.ToInfo();
    }

    /// <summary>
    /// Lists every cluster kubeconfig secret in the tenant's vault (metadata only, with the
    /// owning cluster loaded), ordered by cluster name. Used by the vault admin UI to browse
    /// kubeconfigs alongside the other secret scopes.
    /// </summary>
    public async Task<List<VaultSecret>> GetClusterKubeconfigSecretsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Include(s => s.OwnerCluster)
            .Where(s => s.Vault.TenantId == tenantId && s.SecretType == VaultSecretType.Kubeconfig)
            .OrderBy(s => s.OwnerCluster!.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Enumerates every certificate and OAuth/OIDC client secret in the tenant's vault,
    /// decrypting each just far enough to read its expiry, and projects the non-secret
    /// metadata (scope, expiry, days remaining) used by the expiry-notification scanner
    /// and the management UI. Returns an empty list when the tenant has no vault.
    /// When <paramref name="customerId"/> is supplied, only secrets belonging to that
    /// customer's apps are returned (used by the customer portal).
    /// </summary>
    public async Task<List<ExpiringSecretInfo>> GetExpiringSecretCandidatesAsync(
        Guid tenantId, Guid? customerId = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        SecretVault? vault = await db.Set<SecretVault>()
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);
        if (vault is null)
        {
            return [];
        }

        List<VaultSecret> secrets = await db.Set<VaultSecret>()
            .Include(s => s.App)
            .Include(s => s.Environment)
            .Include(s => s.OwnerCluster)
            .Where(s => s.VaultId == vault.Id
                && (s.SecretType == VaultSecretType.Certificate
                    || s.SecretType == VaultSecretType.OAuthClient
                    // Kubeconfigs are cluster-scoped (no app), so they are only surfaced
                    // in the tenant-wide scan, never when filtering to a customer's apps.
                    || (s.SecretType == VaultSecretType.Kubeconfig && customerId == null))
                && (customerId == null || (s.AppId != null && s.App!.CustomerId == customerId)))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        if (secrets.Count == 0)
        {
            return [];
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        List<ExpiringSecretInfo> result = new(secrets.Count);

        foreach (VaultSecret secret in secrets)
        {
            DateTime? expiresAt = null;
            string? detail = null;

            try
            {
                string json = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);

                if (secret.SecretType == VaultSecretType.Certificate)
                {
                    CertificateInfo? info = CertificateParser.TryParse(DeserializeBundle(json).Certificate);
                    if (info is not null)
                    {
                        expiresAt = info.NotAfter;
                        detail = info.Subject;
                    }
                }
                else if (secret.SecretType == VaultSecretType.OAuthClient)
                {
                    OAuthClientBundle bundle = DeserializeOAuthBundle(json);
                    expiresAt = bundle.ExpiresAt;
                    detail = $"{OAuthClientHelper.DisplayName(bundle.Provider)}{(string.IsNullOrWhiteSpace(bundle.ClientId) ? "" : $" · {bundle.ClientId}")}";
                }
                else // Kubeconfig
                {
                    KubeconfigBundle bundle = KubeconfigBundle.Deserialize(json);
                    expiresAt = bundle.ExpiresAt;
                    detail = string.IsNullOrWhiteSpace(bundle.ContextName) ? bundle.ApiServerUrl : bundle.ContextName;
                }
            }
            catch
            {
                // A secret that fails to decrypt/parse is surfaced with no expiry rather
                // than aborting the whole scan.
            }

            result.Add(new ExpiringSecretInfo
            {
                SecretId = secret.Id,
                Name = secret.Name,
                SecretType = secret.SecretType,
                AppId = secret.AppId,
                AppName = secret.App?.Name,
                EnvironmentId = secret.EnvironmentId,
                EnvironmentName = secret.Environment?.Name,
                OwnerClusterId = secret.OwnerClusterId,
                ClusterName = secret.OwnerCluster?.Name,
                ExpiresAt = expiresAt,
                Detail = detail
            });
        }

        return result;
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

    /// <summary>
    /// Changes the environment scope of an app-scoped secret. Pass
    /// <paramref name="environmentId"/> = null to make the secret "shared" across
    /// every environment, or an environment id to bind it to that environment only.
    /// </summary>
    /// <returns>
    /// (true, null) on success; (false, reason) when the secret is not app-scoped,
    /// already in the requested scope, or another secret with the same name already
    /// occupies the target scope (the unique (VaultId, AppId, EnvironmentId, Name)
    /// index would be violated).
    /// </returns>
    public async Task<(bool Ok, string? Reason)> ChangeAppSecretScopeAsync(
        Guid secretId, Guid? environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        if (secret is null)
        {
            return (false, "Secret not found.");
        }

        if (secret.AppId is null)
        {
            return (false, "Only app secrets have an environment scope.");
        }

        if (secret.EnvironmentId == environmentId)
        {
            return (false, "Secret is already in that scope.");
        }

        // Guard the unique (VaultId, AppId, EnvironmentId, Name) index: refuse if
        // another secret already occupies the target scope under the same name.
        bool collision = await db.Set<VaultSecret>()
            .AnyAsync(s => s.Id != secret.Id
                && s.VaultId == secret.VaultId
                && s.AppId == secret.AppId
                && s.EnvironmentId == environmentId
                && s.Name == secret.Name, ct);

        if (collision)
        {
            return (false, $"A secret named '{secret.Name}' already exists in the target scope.");
        }

        // If the secret syncs to a cluster, an environment-bound scope must match
        // that cluster's environment — otherwise the sync guard would later refuse
        // it. Clear the now-mismatched sync target rather than leave it broken.
        if (secret.SyncToKubernetes && environmentId is not null && secret.KubernetesClusterId is not null)
        {
            Guid? clusterEnv = await db.Set<KubernetesCluster>()
                .Where(c => c.Id == secret.KubernetesClusterId)
                .Select(c => (Guid?)c.EnvironmentId)
                .FirstOrDefaultAsync(ct);

            if (clusterEnv != environmentId)
            {
                secret.SyncToKubernetes = false;
                secret.KubernetesClusterId = null;
                secret.KubernetesSecretName = null;
                secret.KubernetesNamespace = null;
            }
        }

        secret.EnvironmentId = environmentId;
        secret.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (true, null);
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
    /// Re-reads an app secret's value from the live Kubernetes Secret and updates the
    /// stored vault value. Used before taking ownership of an observed/imported secret
    /// so EntKube pushes the current live value (e.g. whatever ArgoCD/Flux last set)
    /// rather than the stale import-time snapshot. Returns true when a value was read
    /// and updated; false if there is no sync target or the live key is absent.
    /// </summary>
    public async Task<bool> RefreshAppSecretFromClusterAsync(Guid secretId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);

        if (secret is null
            || secret.KubernetesClusterId is null
            || string.IsNullOrEmpty(secret.KubernetesSecretName))
        {
            return false;
        }

        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == secret.KubernetesClusterId, ct);

        if (cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig))
        {
            return false;
        }

        string ns = secret.KubernetesNamespace ?? "default";
        string? liveValue = await ReadLiveSecretValueAsync(
            secret.KubernetesSecretName!, secret.Name, ns, cluster.Kubeconfig!, ct);

        if (liveValue is null)
        {
            return false;
        }

        byte[] dataKey = await UnsealVaultAsync(secret.Vault.TenantId, ct);

        // Skip the write (and version-history churn) when the live value is unchanged —
        // important because the background refresher polls these on an interval.
        string currentValue = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
        if (string.Equals(currentValue, liveValue, StringComparison.Ordinal))
        {
            return false;
        }

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, liveValue);

        await ArchiveVersionAsync(db, secret, ct);
        secret.EncryptedValue = ciphertext;
        secret.Nonce = nonce;
        secret.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Returns the IDs of app secrets that are "observed": Kubernetes sync is disabled
    /// but a target cluster + Secret name are recorded — the state imported secrets land
    /// in. The background refresher re-reads their live values so the vault copy tracks
    /// whatever ArgoCD/Flux last set, until the operator takes ownership (enables sync).
    /// </summary>
    public async Task<List<Guid>> GetObservedAppSecretIdsAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.AppId != null
                && !s.SyncToKubernetes
                && s.KubernetesClusterId != null
                && s.KubernetesSecretName != null)
            .Select(s => s.Id)
            .ToListAsync(ct);
    }

    /// <summary>Reads one key from a live Secret via kubectl and base64-decodes it.</summary>
    private static async Task<string?> ReadLiveSecretValueAsync(
        string secretName, string key, string ns, string kubeconfig, CancellationToken ct)
    {
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, kubeconfig, ct);

            HelmExecutionResult result = await RunProcessAsync(
                "kubectl", $"get secret {secretName} -n {ns} --kubeconfig {tempKubeconfig} -o json", ct);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                return null;
            }

            // Index the data map by the literal key so dotted keys (e.g. tls.crt) work.
            System.Text.Json.Nodes.JsonNode? node = System.Text.Json.Nodes.JsonNode.Parse(result.Output);
            string? base64 = node?["data"]?[key]?.GetValue<string>();

            if (string.IsNullOrEmpty(base64))
            {
                return null;
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (File.Exists(tempKubeconfig))
            {
                File.Delete(tempKubeconfig);
            }
        }
    }

    /// <summary>
    /// Syncs all app-scoped secrets that have <see cref="VaultSecret.SyncToKubernetes"/>
    /// enabled to their target Kubernetes cluster. Secrets are grouped by
    /// (KubernetesSecretName, KubernetesNamespace) and written as Opaque Secrets.
    /// Secrets without a <see cref="VaultSecret.KubernetesClusterId"/> are reported
    /// in the output as needing cluster configuration but do not block the rest.
    ///
    /// When <paramref name="environmentId"/> is supplied, only secrets visible in
    /// that environment (shared secrets plus secrets bound to it) are synced.
    /// Regardless of the entry point, an environment-bound secret is only ever
    /// written to a cluster belonging to its own environment — cross-environment
    /// targets are refused so e.g. a prod secret can never land on a test cluster.
    /// </summary>
    public async Task<HelmExecutionResult> SyncAppSecretsToKubernetesAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default, Guid? environmentId = null)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Load ALL sync-enabled secrets — including those missing a cluster reference,
        // so we can report them clearly instead of silently skipping. When scoped to
        // an environment, only that environment's secrets and shared secrets apply.
        List<VaultSecret> allSecrets = await db.Set<VaultSecret>()
            .Include(s => s.KubernetesCluster)
            .Where(s => s.AppId == appId && s.SyncToKubernetes
                && (environmentId == null || s.EnvironmentId == null || s.EnvironmentId == environmentId))
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

        // Safety invariant: an environment-bound secret may only be written to a
        // cluster in that same environment. Refuse (don't silently skip) any
        // misconfigured cross-environment target so prod secrets never reach test.
        foreach (VaultSecret s in allSecrets.Where(s =>
            s.EnvironmentId != null && s.KubernetesCluster != null
            && s.KubernetesCluster.EnvironmentId != s.EnvironmentId))
        {
            results.Add($"✗ '{s.Name}' is bound to a different environment than its target cluster '{s.KubernetesCluster!.Name}' — refusing to sync (cross-environment).");
        }

        // Only process secrets that are fully configured AND whose target cluster
        // is in the secret's environment (shared secrets — null EnvironmentId — may
        // target any of the app's clusters).
        List<VaultSecret> actionable = allSecrets
            .Where(s => s.KubernetesClusterId != null && !string.IsNullOrWhiteSpace(s.KubernetesSecretName)
                && (s.EnvironmentId == null || s.KubernetesCluster?.EnvironmentId == s.EnvironmentId))
            .ToList();

        if (actionable.Count == 0)
        {
            return new HelmExecutionResult
            {
                Success = results.Count == 0,
                Output = results.Count > 0 ? string.Join("\n", results) : "No app secrets with both a K8s Secret name and a target cluster."
            };
        }

        // Unseal once; we decrypt each secret directly from its entity below
        // (a name lookup would be ambiguous now that shared and environment-bound
        // secrets can share a name).
        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

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

                // Within this cluster, group OPAQUE secrets by (K8sSecretName, Namespace)
                // so multiple values land as keys in one generic Secret. Certificate
                // secrets are handled separately below — each is its own kubernetes.io/tls
                // Secret and is never merged with opaque keys.
                IEnumerable<IGrouping<(string SecretName, string Namespace), VaultSecret>> secretGroups =
                    clusterGroup
                        .Where(s => s.SecretType == VaultSecretType.Opaque)
                        .GroupBy(s => (
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

                    // Decrypt each secret value. If a shared and an environment-bound
                    // secret share the same key name in this target, the environment
                    // -bound value wins (an explicit per-environment override).
                    List<string> literals = [];

                    IEnumerable<VaultSecret> effective = group
                        .GroupBy(s => s.Name)
                        .Select(g => g.OrderByDescending(s => s.EnvironmentId.HasValue).First());

                    foreach (VaultSecret vaultSecret in effective)
                    {
                        string plainValue = encryption.Decrypt(dataKey, vaultSecret.EncryptedValue, vaultSecret.Nonce);

                        // Shell-safe: write value to a temp file so we avoid quoting issues.
                        string tmpVal = Path.Combine(Path.GetTempPath(), $"entkube-val-{Guid.NewGuid()}");
                        await File.WriteAllTextAsync(tmpVal, plainValue, ct);
                        literals.Add($"--from-file={vaultSecret.Name}={tmpVal}");
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
                        await LabelManagedSecretAsync(k8sSecretName, ns, tempKubeconfig, ct);
                        results.Add($"✓ Secret '{k8sSecretName}' synced to '{ns}' on '{cluster.Name}' ({group.Count()} keys)");
                    }
                    else
                    {
                        results.Add($"✗ Secret '{k8sSecretName}' failed on '{cluster.Name}': {createResult.Output}");
                    }
                }

                // Certificate secrets → one kubernetes.io/tls Secret each.
                foreach (VaultSecret certSecret in clusterGroup.Where(s => s.SecretType == VaultSecretType.Certificate))
                {
                    await SyncCertificateSecretAsync(certSecret, dataKey, cluster!, tempKubeconfig, results, ct);
                }

                // OAuth/OIDC client secrets → one Opaque Secret each (named keys).
                foreach (VaultSecret oauthSecret in clusterGroup.Where(s => s.SecretType == VaultSecretType.OAuthClient))
                {
                    await SyncOAuthClientSecretAsync(oauthSecret, dataKey, cluster!, tempKubeconfig, results, ct);
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

    /// <summary>
    /// Label key/value stamped on every Secret EntKube writes to a cluster.
    /// Lets other parts of the platform (notably the deployment importer) recognize
    /// an EntKube-managed Secret and avoid re-adopting it into the vault.
    /// </summary>
    public const string ManagedByLabelKey = "app.kubernetes.io/managed-by";
    public const string ManagedByLabelValue = "entkube";

    /// <summary>
    /// Stamps the EntKube managed-by labels onto a freshly-synced Secret. Best-effort:
    /// a labeling failure must not fail the sync, so the result is not inspected.
    /// </summary>
    private async Task LabelManagedSecretAsync(string name, string ns, string tempKubeconfig, CancellationToken ct)
    {
        await RunProcessAsync("kubectl",
            $"label secret {name} --namespace {ns} {ManagedByLabelKey}={ManagedByLabelValue} entkube.io/managed=true --overwrite --kubeconfig {tempKubeconfig}", ct);
    }

    /// <summary>
    /// Writes a single certificate secret to its target cluster as a
    /// <c>kubernetes.io/tls</c> Secret: <c>tls.crt</c> (leaf + chain), <c>tls.key</c>
    /// (private key), and <c>ca.crt</c> when a CA is present. A certificate with no
    /// private key cannot form a valid TLS Secret and is reported, not synced.
    /// </summary>
    private async Task SyncCertificateSecretAsync(
        VaultSecret certSecret, byte[] dataKey, KubernetesCluster cluster,
        string tempKubeconfig, List<string> results, CancellationToken ct)
    {
        string ns = certSecret.KubernetesNamespace ?? "default";
        string k8sSecretName = certSecret.KubernetesSecretName!;

        CertificateBundle bundle;
        try
        {
            string json = encryption.Decrypt(dataKey, certSecret.EncryptedValue, certSecret.Nonce);
            bundle = DeserializeBundle(json);
        }
        catch (Exception ex)
        {
            results.Add($"✗ Certificate '{certSecret.Name}' could not be decrypted: {ex.Message}");
            return;
        }

        if (!bundle.HasCertificate)
        {
            results.Add($"⚠ Certificate '{certSecret.Name}' has no certificate body — skipping.");
            return;
        }

        if (!bundle.HasPrivateKey)
        {
            results.Add($"⚠ Certificate '{certSecret.Name}' has no private key — a kubernetes.io/tls Secret requires tls.key, skipping.");
            return;
        }

        // Ensure namespace exists.
        await RunProcessAsync("kubectl", $"create namespace {ns} --kubeconfig {tempKubeconfig}", ct);

        string crtFile = Path.Combine(Path.GetTempPath(), $"entkube-tls-crt-{Guid.NewGuid()}");
        string keyFile = Path.Combine(Path.GetTempPath(), $"entkube-tls-key-{Guid.NewGuid()}");
        string fullChainFile = Path.Combine(Path.GetTempPath(), $"entkube-tls-fullchain-{Guid.NewGuid()}");
        string? caFile = bundle.HasCaCertificate ? Path.Combine(Path.GetTempPath(), $"entkube-tls-ca-{Guid.NewGuid()}") : null;

        try
        {
            await File.WriteAllTextAsync(crtFile, bundle.CombinedCertificateChain, ct);
            await File.WriteAllTextAsync(keyFile, bundle.PrivateKey!.Trim() + "\n", ct);
            // fullchain.crt = leaf + intermediates + CA (everything but the private key),
            // for consumers that want the complete chain in a single file.
            await File.WriteAllTextAsync(fullChainFile, bundle.FullChain, ct);

            List<string> fromFiles =
            [
                $"--from-file=tls.crt={crtFile}",
                $"--from-file=tls.key={keyFile}",
                $"--from-file=fullchain.crt={fullChainFile}",
            ];
            if (caFile is not null)
            {
                await File.WriteAllTextAsync(caFile, bundle.CaCertificate!.Trim() + "\n", ct);
                fromFiles.Add($"--from-file=ca.crt={caFile}");
            }

            await RunProcessAsync("kubectl",
                $"delete secret {k8sSecretName} --namespace {ns} --ignore-not-found --kubeconfig {tempKubeconfig}", ct);

            HelmExecutionResult createResult = await RunProcessAsync("kubectl",
                $"create secret generic {k8sSecretName} --namespace {ns} --type=kubernetes.io/tls {string.Join(" ", fromFiles)} --kubeconfig {tempKubeconfig}", ct);

            if (createResult.Success)
            {
                await LabelManagedSecretAsync(k8sSecretName, ns, tempKubeconfig, ct);
                results.Add($"✓ Certificate '{k8sSecretName}' synced to '{ns}' on '{cluster.Name}' (kubernetes.io/tls, tls.crt + tls.key + fullchain.crt{(caFile is not null ? " + ca.crt" : "")})");
            }
            else
            {
                results.Add($"✗ Certificate '{k8sSecretName}' failed on '{cluster.Name}': {createResult.Output}");
            }
        }
        finally
        {
            if (File.Exists(crtFile)) File.Delete(crtFile);
            if (File.Exists(keyFile)) File.Delete(keyFile);
            if (File.Exists(fullChainFile)) File.Delete(fullChainFile);
            if (caFile is not null && File.Exists(caFile)) File.Delete(caFile);
        }
    }

    /// <summary>
    /// Writes a single OAuth/OIDC client secret to its target cluster as an Opaque
    /// Secret with named keys: <c>client-id</c>, <c>client-secret</c>, <c>issuer</c>,
    /// <c>tenant-id</c>, and <c>scopes</c> (only the keys that have values are written;
    /// client-secret is required).
    /// </summary>
    private async Task SyncOAuthClientSecretAsync(
        VaultSecret oauthSecret, byte[] dataKey, KubernetesCluster cluster,
        string tempKubeconfig, List<string> results, CancellationToken ct)
    {
        string ns = oauthSecret.KubernetesNamespace ?? "default";
        string k8sSecretName = oauthSecret.KubernetesSecretName!;

        OAuthClientBundle bundle;
        try
        {
            string json = encryption.Decrypt(dataKey, oauthSecret.EncryptedValue, oauthSecret.Nonce);
            bundle = DeserializeOAuthBundle(json);
        }
        catch (Exception ex)
        {
            results.Add($"✗ OAuth client '{oauthSecret.Name}' could not be decrypted: {ex.Message}");
            return;
        }

        if (!bundle.HasClientSecret)
        {
            results.Add($"⚠ OAuth client '{oauthSecret.Name}' has no client secret — skipping.");
            return;
        }

        // Map the bundle to K8s data keys, skipping any that are empty.
        Dictionary<string, string> data = new()
        {
            ["client-secret"] = bundle.ClientSecret!.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(bundle.ClientId)) data["client-id"] = bundle.ClientId.Trim();
        if (!string.IsNullOrWhiteSpace(bundle.EffectiveIssuer)) data["issuer"] = bundle.EffectiveIssuer!;
        if (!string.IsNullOrWhiteSpace(bundle.TenantId)) data["tenant-id"] = bundle.TenantId.Trim();
        if (!string.IsNullOrWhiteSpace(bundle.Scopes)) data["scopes"] = bundle.Scopes.Trim();

        // Ensure namespace exists.
        await RunProcessAsync("kubectl", $"create namespace {ns} --kubeconfig {tempKubeconfig}", ct);

        List<string> fromFiles = [];
        List<string> tmpFiles = [];
        try
        {
            foreach ((string key, string value) in data)
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"entkube-oauth-{Guid.NewGuid()}");
                await File.WriteAllTextAsync(tmp, value, ct);
                tmpFiles.Add(tmp);
                fromFiles.Add($"--from-file={key}={tmp}");
            }

            await RunProcessAsync("kubectl",
                $"delete secret {k8sSecretName} --namespace {ns} --ignore-not-found --kubeconfig {tempKubeconfig}", ct);

            HelmExecutionResult createResult = await RunProcessAsync("kubectl",
                $"create secret generic {k8sSecretName} --namespace {ns} {string.Join(" ", fromFiles)} --kubeconfig {tempKubeconfig}", ct);

            if (createResult.Success)
            {
                await LabelManagedSecretAsync(k8sSecretName, ns, tempKubeconfig, ct);
                results.Add($"✓ OAuth client '{k8sSecretName}' synced to '{ns}' on '{cluster.Name}' ({data.Count} keys)");
            }
            else
            {
                results.Add($"✗ OAuth client '{k8sSecretName}' failed on '{cluster.Name}': {createResult.Output}");
            }
        }
        finally
        {
            foreach (string tmp in tmpFiles)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
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

    // ── Kafka cluster secrets ────────────────────────────────────────────────────

    public async Task<VaultSecret> SetKafkaClusterSecretAsync(
        Guid tenantId, Guid clusterId, string name, string value, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.KafkaClusterId == clusterId
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
            KafkaClusterId = clusterId,
            SyncToKubernetes = false
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task<string?> GetKafkaClusterSecretValueAsync(
        Guid tenantId, Guid clusterId, string name, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.KafkaClusterId == clusterId
                && s.Name == name, ct);

        if (secret is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
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
