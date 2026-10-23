using System.Collections.Concurrent;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
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
public sealed class KubeconfigResolver(
    IServiceProvider services,
    VaultEncryptionService encryption,
    TimeSpan? freshness = null)
{
    // tenantId -> unsealed DEK, with the moment it stops being trusted
    private readonly ConcurrentDictionary<Guid, (byte[] Dek, DateTime FreshUntil)> dekCache = new();
    // secretId -> decrypted kubeconfig YAML, with the moment it stops being trusted
    private readonly ConcurrentDictionary<Guid, (string Yaml, DateTime FreshUntil)> planCache = new();

    /// <summary>
    /// ⭐ How long a decrypted kubeconfig is reused before it is read from the vault again.
    ///
    /// <para><b>This cache had no expiry at all, and that only became wrong at more than one
    /// instance.</b> The sole eviction is <see cref="Invalidate"/>, called from the write path on
    /// whichever instance performed the write (<c>VaultService.RotateClusterKubeconfigAsync</c>
    /// and friends). Rotation upserts in place, so the cache key — the secret id — does not change.
    /// An operator rotating or revoking a cluster credential on instance A therefore left
    /// instances B and C serving the OLD plaintext <em>until their process restarted</em>, across
    /// every call site that reaches <c>cluster.Kubeconfig</c> through the materialization
    /// interceptor. A revoked credential that still works is the one outcome a vault must not
    /// have.</para>
    ///
    /// <para>⚠️ A TTL <b>bounds</b> that; it does not coordinate. Cross-instance invalidation
    /// needs a bus, and the Redis connection that <c>Presence:Provider</c> already optionally
    /// registers is the obvious place for it — recorded in docs/decomposition.md §9.5 as its own
    /// step. Thirty seconds is chosen so the untrusted window is shorter than a human notices
    /// while the decrypt stays amortised: the cost of a miss is one indexed row read plus one
    /// AES-GCM open, and it is paid once per secret per instance per window, not per operation.</para>
    ///
    /// <para>The in-process <see cref="Invalidate"/> remains and still matters: on the instance
    /// that did the rotation the new value is visible immediately rather than up to a window
    /// late.</para>
    /// </summary>
    /// <summary>
    /// The shipped window. Public because a test pins it: the behaviour tests run with a window
    /// nothing ships with, so a fix whose production value was left at zero — or at a day — would
    /// pass all of them.
    /// </summary>
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ⚠️ Injectable so it can be TESTED, which is the only reason it is not a constant. A
    /// 30-second window cannot be exercised by a test that has to finish, and the property worth
    /// pinning — "a rotation on another instance is picked up without anyone calling
    /// <see cref="Invalidate"/>" — is exactly what the window exists for. A test that asserted
    /// only the dictionary's contents would pass against a cache that never expires.
    /// </summary>
    private readonly TimeSpan planFreshness = freshness ?? DefaultFreshness;

    /// <summary>
    /// Returns the decrypted kubeconfig YAML for the given vault secret, or null when it
    /// cannot be resolved (missing secret, wrong type, decryption failure). Never throws —
    /// a failure to resolve degrades to "no kubeconfig", matching the previous behaviour of
    /// a null column, rather than breaking every cluster operation.
    /// </summary>
    public string? Resolve(Guid tenantId, Guid secretId)
    {
        // ⚠️ Compared against the clock, not merely present. An entry past its window is treated
        // as absent and re-read, which is what bounds how long a rotated or revoked credential
        // can go on being served by an instance that did not perform the rotation.
        if (planCache.TryGetValue(secretId, out (string Yaml, DateTime FreshUntil) cached)
            && cached.FreshUntil > DateTime.UtcNow)
        {
            return cached.Yaml;
        }

        // ⚠️ Resolved OUTSIDE the catch below, deliberately. The catch degrades any failure to
        // "no kubeconfig", which is right for a data problem — a missing secret, ciphertext that
        // will not decrypt — and badly wrong for a container that cannot supply a context. That
        // is a wiring fault, and swallowing it would turn one bad registration into every cluster
        // in the installation silently losing its kubeconfig, reported as
        // "no kubeconfig configured" per operation. GetRequiredService throwing here is the loud
        // failure that belongs at startup. (Found the hard way: moving this off
        // ApplicationDbContext failed ten tests with that exact misleading message, because the
        // test harness registered only the one factory.)
        IDbContextFactory<SecretsDbContext> factory =
            services.GetRequiredService<IDbContextFactory<SecretsDbContext>>();

        try
        {
            // A fresh context (its own connection) — safe to query from inside the
            // materialization of a different context. No KubernetesCluster is materialized
            // here, so the interceptor is a no-op and there is no re-entrancy.
            using SecretsDbContext db = factory.CreateDbContext();

            VaultSecret? secret = db.VaultSecrets
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
                planCache[secretId] = (yaml, DateTime.UtcNow + planFreshness);
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

    private byte[] GetDataKey(SecretsDbContext db, Guid tenantId)
    {
        // ⚠️ Expires for a DIFFERENT reason than the plan cache, and the difference is worth
        // keeping in mind. A stale plaintext kubeconfig is a security problem — a revoked
        // credential that still works. A stale DEK is an availability one: it cannot decrypt
        // anything sealed with the new key, so Resolve's catch degrades every secret for this
        // tenant to "no kubeconfig" on that instance. With no expiry that state was PERMANENT
        // until a restart, so a vault rotation would quietly take out every instance that did not
        // perform it. Re-reading on a window makes it self-heal.
        if (dekCache.TryGetValue(tenantId, out (byte[] Dek, DateTime FreshUntil) cached)
            && cached.FreshUntil > DateTime.UtcNow)
        {
            return cached.Dek;
        }

        SecretVault vault = db.SecretVaults
            .AsNoTracking()
            .First(v => v.TenantId == tenantId);
        byte[] dek = encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
        dekCache[tenantId] = (dek, DateTime.UtcNow + planFreshness);
        return dek;
    }
}
