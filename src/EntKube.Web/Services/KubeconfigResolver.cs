using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Resolves a registered cluster's kubeconfig YAML from the tenant vault, decrypting
/// on demand. Used by <see cref="KubeconfigMaterializationInterceptor"/> to transparently
/// populate <see cref="KubernetesCluster.Kubeconfig"/> when a cluster is loaded, so the
/// many consumers that read <c>cluster.Kubeconfig</c> synchronously keep working after
/// the plaintext column was dropped in favour of vault storage.
///
/// Registered as a singleton. All work is done synchronously (the materialization hook is
/// synchronous) and cached aggressively: the per-tenant DEK and the per-secret decrypted
/// YAML are memoised. Cache entries are invalidated when a kubeconfig is updated.
/// </summary>
public sealed class KubeconfigResolver(IServiceProvider services, VaultEncryptionService encryption)
{
    // tenantId -> unsealed DEK
    private readonly ConcurrentDictionary<Guid, byte[]> dekCache = new();
    // secretId -> decrypted kubeconfig YAML
    private readonly ConcurrentDictionary<Guid, string> planCache = new();

    /// <summary>
    /// Returns the decrypted kubeconfig YAML for the given vault secret, or null when it
    /// cannot be resolved (missing secret, wrong type, decryption failure). Never throws —
    /// a failure to resolve degrades to "no kubeconfig", matching the previous behaviour of
    /// a null column, rather than breaking every cluster operation.
    /// </summary>
    public string? Resolve(Guid tenantId, Guid secretId)
    {
        if (planCache.TryGetValue(secretId, out string? cached))
        {
            return cached;
        }

        try
        {
            // A fresh context (its own connection) — safe to query from inside the
            // materialization of a different context. No KubernetesCluster is materialized
            // here, so the interceptor is a no-op and there is no re-entrancy.
            using ApplicationDbContext db = services
                .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                .CreateDbContext();

            VaultSecret? secret = db.Set<VaultSecret>()
                .AsNoTracking()
                .FirstOrDefault(s => s.Id == secretId && s.SecretType == VaultSecretType.Kubeconfig);
            if (secret is null)
            {
                return null;
            }

            byte[] dek = GetDataKey(db, tenantId);
            string json = encryption.Decrypt(dek, secret.EncryptedValue, secret.Nonce);
            string? yaml = KubeconfigBundle.Deserialize(json).ConfigYaml;

            if (!string.IsNullOrEmpty(yaml))
            {
                planCache[secretId] = yaml;
            }

            return yaml;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops the cached plaintext for a secret so the next load re-decrypts it.</summary>
    public void Invalidate(Guid secretId) => planCache.TryRemove(secretId, out _);

    private byte[] GetDataKey(ApplicationDbContext db, Guid tenantId)
    {
        if (dekCache.TryGetValue(tenantId, out byte[]? dek))
        {
            return dek;
        }

        SecretVault vault = db.Set<SecretVault>()
            .AsNoTracking()
            .First(v => v.TenantId == tenantId);
        dek = encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
        dekCache[tenantId] = dek;
        return dek;
    }
}
