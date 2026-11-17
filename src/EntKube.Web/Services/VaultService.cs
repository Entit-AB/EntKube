using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages the per-tenant secrets vault: initializing vaults, storing/retrieving
/// encrypted secrets for apps and cluster components, and configuring Kubernetes
/// sync. Each operation unseals the vault transparently (auto-unseal via root key).
/// </summary>
public class VaultService(
    IDbContextFactory<SecretsDbContext> dbFactory,
    VaultEncryptionService encryption,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterClients,
    // Optional: used only to evict the cached kubeconfig plaintext after an update. Absent in
    // unit tests, where there is no resolver cache to invalidate.
    KubeconfigResolver? kubeconfigResolver = null,
    // Optional for the same reason, and safe to be absent: BeginOperation only ever suppresses
    // the SECOND and later questions of one action, never a refusal — so a null gate means more
    // dialogs, never fewer, and never a change that went unasked.
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate? gate = null)
{
    // --- Vault Lifecycle ---

    /// <summary>
    /// Creates a new vault for the tenant if one doesn't already exist.
    /// Generates a fresh DEK and seals it with the platform root key.
    /// Idempotent — returns the existing vault if already initialized.
    /// </summary>
    public async Task<SecretVault> InitializeVaultAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
    /// <returns>(true, null) on success; (false, reason) on validation failure.</returns>
    public async Task<(bool Ok, string? Error)> SetAppCertificateAsync(
        Guid tenantId, Guid appId, string name, CertificateBundle bundle,
        Guid? environmentId = null, string? updatedBy = null, CancellationToken ct = default)
    {
        (bool ok, string? error, _) = await UpsertAppCertificateAsync(
            tenantId, appId, name, bundle, environmentId, updatedBy, ct);
        return (ok, error);
    }

    /// <summary>
    /// Imports a TLS certificate Secret observed in a cluster as a Certificate-type app
    /// secret and records its live sync target with Kubernetes sync DISABLED — the
    /// "cluster-owned"/observed state the opaque import path also uses. The reverse
    /// refresher then tracks the certificate read-only until ownership is taken.
    /// </summary>
    public async Task<(bool Ok, string? Error)> ImportObservedAppCertificateAsync(
        Guid tenantId, Guid appId, string name, CertificateBundle bundle,
        Guid clusterId, string secretName, string ns, Guid? environmentId = null,
        string? updatedBy = null, CancellationToken ct = default)
    {
        (bool ok, string? error, VaultSecret? stored) = await UpsertAppCertificateAsync(
            tenantId, appId, name, bundle, environmentId, updatedBy, ct);
        if (!ok || stored is null)
        {
            return (ok, error);
        }

        await ConfigureKubernetesSyncAsync(
            tenantId, stored.Id, syncEnabled: false, secretName, ns, ct, clusterId);
        return (true, null);
    }

    // Core upsert for a certificate app secret; returns the stored row so callers that
    // need the id (e.g. the importer, to record a sync target) don't have to re-query.
    private async Task<(bool Ok, string? Error, VaultSecret? Secret)> UpsertAppCertificateAsync(
        Guid tenantId, Guid appId, string name, CertificateBundle bundle,
        Guid? environmentId, string? updatedBy, CancellationToken ct)
    {
        (bool valid, string? validationError) = CertificateParser.Validate(bundle);
        if (!valid)
        {
            return (false, validationError, null);
        }

        using SecretsDbContext db = dbFactory.CreateDbContext();

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
            return (true, null, existing);
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
        return (true, null, secret);
    }

    // ── Tenant-scoped "library" certificates ───────────────────────────────────
    // A serving/TLS certificate that belongs to the whole tenant rather than one app.
    // Stored once as an app-less Certificate secret (all scope FKs null) and delivered to
    // apps and/or all namespaces via CertificateDistribution targets. This is the only
    // supported "neither app nor component" secret scope, reserved for certificates.

    /// <summary>
    /// Creates or updates a tenant-scoped certificate (no app). Keyed on (tenant, name).
    /// Validated, serialized, and stored encrypted with <see cref="VaultSecretType.Certificate"/>.
    /// </summary>
    public async Task<(bool Ok, string? Error, Guid? Id)> SetTenantCertificateAsync(
        Guid tenantId, string name, CertificateBundle bundle,
        string? updatedBy = null, CancellationToken ct = default)
    {
        (bool valid, string? validationError) = CertificateParser.Validate(bundle);
        if (!valid)
        {
            return (false, validationError, null);
        }

        // Ensure the tenant has a vault (idempotent) before sealing the certificate.
        await InitializeVaultAsync(tenantId, ct);

        using SecretsDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.AppId == null && s.SecretType == VaultSecretType.Certificate
                && s.Name == name, ct);

        string json = JsonSerializer.Serialize(bundle, CertificateJsonOptions);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, json);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = updatedBy;
            await db.SaveChangesAsync(ct);
            return (true, null, existing.Id);
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
            UpdatedBy = updatedBy,
            // No scope FKs set → tenant-library certificate.
        };
        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return (true, null, secret.Id);
    }

    /// <summary>Lists the tenant's app-less "library" certificates with parsed validity.</summary>
    public async Task<List<TenantCertificateInfo>> GetTenantCertificatesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();
        List<VaultSecret> secrets = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .Where(s => s.Vault.TenantId == tenantId
                && s.SecretType == VaultSecretType.Certificate
                && s.AppId == null)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        List<TenantCertificateInfo> result = [];
        foreach (VaultSecret s in secrets)
        {
            CertificateBundle? bundle = await GetCertificateBundleByIdAsync(tenantId, s.Id, ct);
            CertificateInfo? info = bundle is null ? null : CertificateParser.TryParse(bundle.Certificate);
            result.Add(new TenantCertificateInfo(
                s.Id, s.Name,
                bundle?.HasPrivateKey ?? false,
                bundle?.HasChain ?? false,
                bundle?.HasCaCertificate ?? false,
                info));
        }
        return result;
    }

    /// <summary>Deletes a tenant-scoped certificate. No-op if the id is not an app-less certificate.</summary>
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task DeleteTenantCertificateAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        // It already narrowed by shape — app-less, and a certificate — which reads like scoping
        // and is not: every tenant has app-less certificates.
        VaultSecret? secret = await db.VaultSecrets
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId
                && s.AppId == null && s.SecretType == VaultSecretType.Certificate, ct);
        if (secret is null) return;
        db.VaultSecrets.Remove(secret);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Idempotent backfill for secrets imported before TLS detection existed: opaque app
    /// secrets that are really the split keys of one TLS Secret (names <c>tls.crt</c>
    /// [+ <c>tls.key</c> / <c>ca.crt</c>] sharing the same Kubernetes target) are merged
    /// into a single readable Certificate secret named after the Kubernetes Secret, keeping
    /// the observed (sync-off) target. Only converts a group whose <c>tls.crt</c> actually
    /// parses as a certificate — everything else is left untouched. Returns the count merged.
    /// </summary>
    public async Task<int> ConvertImportedTlsSecretsToCertificatesAsync(CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> candidates = await db.Set<VaultSecret>()
            .Include(s => s.Vault)
            .Where(s => s.AppId != null
                && s.SecretType == VaultSecretType.Opaque
                && s.KubernetesClusterId != null
                && s.KubernetesSecretName != null
                && (s.Name == "tls.crt" || s.Name == "tls.key" || s.Name == "ca.crt"))
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return 0;
        }

        int converted = 0;

        IEnumerable<IGrouping<(Guid?, Guid?, Guid?, string?, string?), VaultSecret>> groups =
            candidates.GroupBy(s => (s.AppId, s.EnvironmentId, s.KubernetesClusterId,
                s.KubernetesSecretName, s.KubernetesNamespace));

        foreach (IGrouping<(Guid?, Guid?, Guid?, string?, string?), VaultSecret> group in groups)
        {
            List<VaultSecret> members = group.ToList();
            VaultSecret? crtSecret = members.FirstOrDefault(s => s.Name == "tls.crt");
            if (crtSecret is null)
            {
                continue; // no certificate body → not a TLS group
            }

            string certName = crtSecret.KubernetesSecretName!;
            // Guard the (pathological) case where the K8s Secret name collides with a data
            // key we're about to delete — skip rather than risk deleting the new cert.
            if (members.Any(m => string.Equals(m.Name, certName, StringComparison.Ordinal)))
            {
                continue;
            }

            Guid tenantId = crtSecret.Vault.TenantId;
            byte[] dataKey;
            try
            {
                dataKey = await UnsealVaultAsync(tenantId, ct);
            }
            catch
            {
                continue;
            }

            string Decrypt(VaultSecret? s) =>
                s is null ? "" : encryption.Decrypt(dataKey, s.EncryptedValue, s.Nonce);

            string crt = Decrypt(crtSecret);
            if (CertificateParser.TryParse(crt) is null)
            {
                continue; // the value isn't actually a certificate — leave the opaque keys alone
            }
            string key = Decrypt(members.FirstOrDefault(s => s.Name == "tls.key"));
            string ca = Decrypt(members.FirstOrDefault(s => s.Name == "ca.crt"));

            string combined = string.Join("\n",
                new[] { key, crt, ca }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
            (bool ok, _, CertificateBundle? bundle) = CertificateImporter.Import(
                Encoding.UTF8.GetBytes(combined), "convert.pem", null);
            if (!ok || bundle is null)
            {
                continue;
            }

            // Create the Certificate secret first (own DbContext), then delete the opaque
            // keys — if the delete fails, a retry re-upserts the same cert (idempotent).
            (bool stored, _) = await ImportObservedAppCertificateAsync(
                tenantId, crtSecret.AppId!.Value, certName, bundle,
                crtSecret.KubernetesClusterId!.Value, certName,
                crtSecret.KubernetesNamespace ?? "default", crtSecret.EnvironmentId,
                updatedBy: "system:tls-convert", ct);
            if (!stored)
            {
                continue;
            }

            db.Set<VaultSecret>().RemoveRange(members);
            await db.SaveChangesAsync(ct);
            converted++;
        }

        return converted;
    }

    /// <summary>
    /// Decrypts a certificate secret and returns its parsed bundle, or null when the
    /// secret does not exist or is not a certificate.
    /// </summary>
    /// <param name="tenantId">
    /// Whose secret. An input, and it scopes BOTH the row and the key.
    ///
    /// <para>⚠️ This read used to take a secret id alone and then unseal the vault with
    /// <c>secret.Vault.TenantId</c> — the tenant of the row the caller had just named — so the
    /// derived tenant handed over the correct data key and naming any secret id in the
    /// installation returned its plaintext. <b>The predicate is the fix</b>; unsealing with the
    /// caller's tenant is a clarity change, because once the row is scoped the two expressions
    /// are the same value (verified: putting the derived key back alone changes no behaviour).
    /// Nothing in the product reaches this with an attacker-chosen id today — every caller passes
    /// an id from a list the server rendered for one tenant — so this is hardening rather than a
    /// breach. The contract was the problem: "name a secret and I will decrypt it" is what a REST
    /// route, an MCP tool or an in-cluster agent would have inherited.</para>
    /// </param>
    public async Task<CertificateBundle?> GetCertificateBundleByIdAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        if (secret is null || secret.SecretType != VaultSecretType.Certificate)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        string json = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
        return DeserializeBundle(json);
    }

    /// <summary>
    /// Decrypts a certificate secret and returns the parsed metadata of its leaf
    /// certificate (subject, validity, etc.), or null when it is not a parseable
    /// certificate secret. Used to show expiry status in lists.
    /// </summary>
    /// <param name="tenantId">Whose secret — passed straight through to the bundle read.</param>
    public async Task<CertificateInfo?> GetCertificateInfoByIdAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        CertificateBundle? bundle = await GetCertificateBundleByIdAsync(tenantId, secretId, ct);
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

        using SecretsDbContext db = dbFactory.CreateDbContext();

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
    /// <param name="tenantId">
    /// Whose secret. An input, and it scopes BOTH the row and the key.
    ///
    /// <para>⚠️ This read used to take a secret id alone and then unseal the vault with
    /// <c>secret.Vault.TenantId</c> — the tenant of the row the caller had just named — so the
    /// derived tenant handed over the correct data key and naming any secret id in the
    /// installation returned its plaintext. <b>The predicate is the fix</b>; unsealing with the
    /// caller's tenant is a clarity change, because once the row is scoped the two expressions
    /// are the same value (verified: putting the derived key back alone changes no behaviour).
    /// Nothing in the product reaches this with an attacker-chosen id today — every caller passes
    /// an id from a list the server rendered for one tenant — so this is hardening rather than a
    /// breach. The contract was the problem: "name a secret and I will decrypt it" is what a REST
    /// route, an MCP tool or an in-cluster agent would have inherited.</para>
    /// </param>
    public async Task<OAuthClientBundle?> GetOAuthClientBundleByIdAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        if (secret is null || secret.SecretType != VaultSecretType.OAuthClient)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        string json = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
        return DeserializeOAuthBundle(json);
    }

    /// <summary>
    /// Decrypts an OAuth client secret and returns its non-secret metadata (provider,
    /// client id, issuer, scopes, expiry) for list display. Excludes the client secret.
    /// </summary>
    /// <param name="tenantId">Whose secret — passed straight through to the bundle read.</param>
    public async Task<OAuthClientInfo?> GetOAuthClientInfoByIdAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        OAuthClientBundle? bundle = await GetOAuthClientBundleByIdAsync(tenantId, secretId, ct);
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

        using SecretsDbContext db = dbFactory.CreateDbContext();

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
    /// <param name="tenantId">
    /// Whose secret. An input, and it scopes BOTH the row and the key.
    ///
    /// <para>⚠️ This read used to take a secret id alone and then unseal the vault with
    /// <c>secret.Vault.TenantId</c> — the tenant of the row the caller had just named — so the
    /// derived tenant handed over the correct data key and naming any secret id in the
    /// installation returned its plaintext. <b>The predicate is the fix</b>; unsealing with the
    /// caller's tenant is a clarity change, because once the row is scoped the two expressions
    /// are the same value (verified: putting the derived key back alone changes no behaviour).
    /// Nothing in the product reaches this with an attacker-chosen id today — every caller passes
    /// an id from a list the server rendered for one tenant — so this is hardening rather than a
    /// breach. The contract was the problem: "name a secret and I will decrypt it" is what a REST
    /// route, an MCP tool or an in-cluster agent would have inherited.</para>
    /// </param>
    public async Task<KubeconfigBundle?> GetKubeconfigBundleByIdAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        if (secret is null || secret.SecretType != VaultSecretType.Kubeconfig)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        string json = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
        return KubeconfigBundle.Deserialize(json);
    }

    /// <summary>
    /// Decrypts a cluster kubeconfig secret and returns its non-secret metadata (context,
    /// API server, expiry) for list/badge display. Excludes the kubeconfig YAML.
    /// </summary>
    /// <param name="tenantId">Whose secret — passed straight through to the bundle read.</param>
    public async Task<KubeconfigInfo?> GetKubeconfigInfoByIdAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        KubeconfigBundle? bundle = await GetKubeconfigBundleByIdAsync(tenantId, secretId, ct);
        return bundle?.ToInfo();
    }

    /// <summary>
    /// Lists every cluster kubeconfig secret in the tenant's vault (metadata only, with the
    /// owning cluster loaded), ordered by cluster name. Used by the vault admin UI to browse
    /// kubeconfigs alongside the other secret scopes.
    /// </summary>
    public async Task<List<VaultSecret>> GetClusterKubeconfigSecretsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Include(s => s.OwnerCluster)
            .Where(s => s.Vault.TenantId == tenantId && s.SecretType == VaultSecretType.Kubeconfig)
            .OrderBy(s => s.OwnerCluster!.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates or updates a generic opaque secret scoped to a cluster (by
    /// <see cref="VaultSecret.OwnerClusterId"/>). Used to persist the OpenStack
    /// application credential minted during provisioning, so the in-cluster
    /// cloud-controller-manager / Cinder CSI cloud-config can be rebuilt later.
    /// Upserts by (cluster, name).
    /// </summary>
    public async Task<VaultSecret> SetClusterSecretAsync(
        Guid tenantId, Guid clusterId, string name, string value, CancellationToken ct = default)
    {
        await InitializeVaultAsync(tenantId, ct);

        using SecretsDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.OwnerClusterId == clusterId && s.Name == name && s.SecretType == VaultSecretType.Opaque, ct);

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
            SecretType = VaultSecretType.Opaque,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            OwnerClusterId = clusterId
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>
    /// Decrypts and returns a generic opaque cluster-scoped secret by name, or null.
    /// </summary>
    public async Task<string?> GetClusterSecretValueAsync(
        Guid tenantId, Guid clusterId, string name, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.OwnerClusterId == clusterId && s.Name == name && s.SecretType == VaultSecretType.Opaque, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Stores the support mailbox's IMAP password in the tenant's vault, replacing
    /// whatever was there.
    /// </summary>
    public Task<VaultSecret> SetSupportMailboxPasswordAsync(
        Guid tenantId, Guid mailboxId, string password, CancellationToken ct = default) =>
        SetSupportMailboxSecretAsync(tenantId, mailboxId, SupportMailboxPasswordName, password, ct);

    private async Task<VaultSecret> SetSupportMailboxSecretAsync(
        Guid tenantId, Guid mailboxId, string name, string password, CancellationToken ct)
    {
        await InitializeVaultAsync(tenantId, ct);

        using SecretsDbContext db = dbFactory.CreateDbContext();

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.SupportMailboxId == mailboxId
                && s.Name == name, ct);

        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, password);

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
            SecretType = VaultSecretType.Opaque,
            EncryptedValue = ciphertext,
            Nonce = nonce,
            SupportMailboxId = mailboxId,
        };

        db.Set<VaultSecret>().Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    /// <summary>The support mailbox's IMAP password, or null when none has been set.</summary>
    /// <summary>
    /// Stores the OIDC service account's secret for this mailbox. Same store as its password, a
    /// different name: a mailbox has one or the other depending on what its server can validate,
    /// and keeping both named makes which one is in use readable rather than inferred.
    /// </summary>
    public Task<VaultSecret> SetSupportMailboxOAuthSecretAsync(
        Guid tenantId, Guid mailboxId, string secret, CancellationToken ct = default) =>
        SetSupportMailboxSecretAsync(tenantId, mailboxId, SupportMailboxOAuthSecretName, secret, ct);

    public Task<string?> GetSupportMailboxOAuthSecretAsync(
        Guid tenantId, Guid mailboxId, CancellationToken ct = default) =>
        GetSupportMailboxSecretAsync(tenantId, mailboxId, SupportMailboxOAuthSecretName, ct);

    public Task<string?> GetSupportMailboxPasswordAsync(
        Guid tenantId, Guid mailboxId, CancellationToken ct = default) =>
        GetSupportMailboxSecretAsync(tenantId, mailboxId, SupportMailboxPasswordName, ct);

    private async Task<string?> GetSupportMailboxSecretAsync(
        Guid tenantId, Guid mailboxId, string name, CancellationToken ct)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId
                && s.SupportMailboxId == mailboxId
                && s.Name == name, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>Whether a password has been stored, without decrypting it.</summary>
    public async Task<bool> HasSupportMailboxPasswordAsync(
        Guid tenantId, Guid mailboxId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>().AnyAsync(s => s.Vault.TenantId == tenantId
            && s.SupportMailboxId == mailboxId
            && s.Name == SupportMailboxPasswordName, ct);
    }

    /// <summary>
    /// Removes a hand-entered password, for when a mailbox stops being one somebody typed.
    ///
    /// <para>Switching a mailbox to a managed mail server leaves the old password behind otherwise: a
    /// live credential for an account nothing polls any more, still making the settings screen report
    /// that a password is stored.</para>
    /// </summary>
    public async Task DeleteSupportMailboxPasswordAsync(
        Guid tenantId, Guid mailboxId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> stored = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId
                && s.SupportMailboxId == mailboxId
                && s.Name == SupportMailboxPasswordName)
            .ToListAsync(ct);

        if (stored.Count == 0)
        {
            return;
        }

        db.Set<VaultSecret>().RemoveRange(stored);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The one secret name a support mailbox owns.</summary>
    public const string SupportMailboxPasswordName = "PASSWORD";

    /// <summary>
    /// The secret of the service account client this mailbox authenticates as, where the mail server's
    /// directory is OIDC and a password would not be checked by anything.
    /// </summary>
    public const string SupportMailboxOAuthSecretName = "OAUTH_CLIENT_SECRET";

    // ───────── Issue trackers (helpdesk phase 6): one API token per GitHub/GitLab host ─────────

    /// <summary>The one secret name an issue tracker owns.</summary>
    public const string IssueTrackerTokenName = "TOKEN";

    /// <summary>Stores an issue tracker's API token, replacing any earlier one (which is versioned).</summary>
    public async Task SetIssueTrackerTokenAsync(Guid tenantId, Guid trackerId, string token, CancellationToken ct = default)
    {
        await InitializeVaultAsync(tenantId, ct);

        using SecretsDbContext db = dbFactory.CreateDbContext();
        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, token);

        VaultSecret? existing = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.IssueTrackerId == trackerId && s.Name == IssueTrackerTokenName, ct);

        if (existing is not null)
        {
            await ArchiveVersionAsync(db, existing, ct);
            existing.EncryptedValue = ciphertext;
            existing.Nonce = nonce;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            SecretVault vault = (await GetVaultAsync(tenantId, ct))!;
            db.Set<VaultSecret>().Add(new VaultSecret
            {
                Id = Guid.NewGuid(),
                VaultId = vault.Id,
                Name = IssueTrackerTokenName,
                SecretType = VaultSecretType.Opaque,
                EncryptedValue = ciphertext,
                Nonce = nonce,
                IssueTrackerId = trackerId,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>An issue tracker's API token, or null when none is stored (a public tracker).</summary>
    public async Task<string?> GetIssueTrackerTokenAsync(Guid tenantId, Guid trackerId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Vault.TenantId == tenantId && s.IssueTrackerId == trackerId && s.Name == IssueTrackerTokenName, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>Whether a token is stored, without decrypting it.</summary>
    public async Task<bool> HasIssueTrackerTokenAsync(Guid tenantId, Guid trackerId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();
        return await db.Set<VaultSecret>()
            .AnyAsync(s => s.Vault.TenantId == tenantId && s.IssueTrackerId == trackerId && s.Name == IssueTrackerTokenName, ct);
    }

    /// <summary>Removes an issue tracker's token: the tracker is then read without one.</summary>
    public async Task DeleteIssueTrackerTokenAsync(Guid tenantId, Guid trackerId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();
        List<VaultSecret> stored = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.IssueTrackerId == trackerId && s.Name == IssueTrackerTokenName)
            .ToListAsync(ct);
        db.Set<VaultSecret>().RemoveRange(stored);
        await db.SaveChangesAsync(ct);
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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.CnpgDatabaseId == cnpgDatabaseId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<List<VaultSecret>> GetMongoDatabaseSecretsAsync(
        Guid tenantId, Guid mongoDatabaseId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.MongoDatabaseId == mongoDatabaseId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Removes every vault secret belonging to a CNPG database, for one tenant.
    ///
    /// <para><b>Why this is here rather than in <c>CnpgService</c>.</b> It was in
    /// <c>CnpgService</c>, deleting straight out of <c>db.VaultSecrets</c> with
    /// <c>Where(s =&gt; s.CnpgDatabaseId == databaseId)</c> — <b>no tenant predicate</b>, in a
    /// method that was handed a <c>tenantId</c> and never used it. A database id from another
    /// tenant therefore deleted that tenant's secrets. The predicate here is the one
    /// <see cref="GetCnpgDatabaseSecretsAsync"/> has always used, which is the point: the read and
    /// the delete of the same rows should not be able to disagree about who owns them.</para>
    /// </summary>
    public async Task DeleteCnpgDatabaseSecretsAsync(
        Guid tenantId, Guid cnpgDatabaseId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> secrets = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.CnpgDatabaseId == cnpgDatabaseId)
            .ToListAsync(ct);

        if (secrets.Count == 0) return;

        db.Set<VaultSecret>().RemoveRange(secrets);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes every vault secret belonging to a MongoDB database, for one tenant. Same history as
    /// <see cref="DeleteCnpgDatabaseSecretsAsync"/>, except that Mongo's version had no tenant in
    /// scope at all.
    /// </summary>
    public async Task DeleteMongoDatabaseSecretsAsync(
        Guid tenantId, Guid mongoDatabaseId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> secrets = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.MongoDatabaseId == mongoDatabaseId)
            .ToListAsync(ct);

        if (secrets.Count == 0) return;

        db.Set<VaultSecret>().RemoveRange(secrets);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes every vault secret scoped to a managed MongoDB cluster — the admin password among
    /// them, which is why deleting somebody else's would be worse than the database case.
    /// </summary>
    public async Task DeleteMongoClusterSecretsAsync(
        Guid tenantId, Guid mongoClusterId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        List<VaultSecret> secrets = await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.MongoClusterId == mongoClusterId)
            .ToListAsync(ct);

        if (secrets.Count == 0) return;

        db.Set<VaultSecret>().RemoveRange(secrets);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the decrypted password for a CNPG database. Used when another
    /// service (e.g. Keycloak) needs to read the DB password to propagate it.
    /// </summary>
    public async Task<string?> GetCnpgDatabasePasswordAsync(
        Guid tenantId, Guid cnpgDatabaseId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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

        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
    /// <param name="tenantId">
    /// Whose secret. An input, and it scopes BOTH the row and the key.
    ///
    /// <para>⚠️ This read used to take a secret id alone and then unseal the vault with
    /// <c>secret.Vault.TenantId</c> — the tenant of the row the caller had just named — so the
    /// derived tenant handed over the correct data key and naming any secret id in the
    /// installation returned its plaintext. <b>The predicate is the fix</b>; unsealing with the
    /// caller's tenant is a clarity change, because once the row is scoped the two expressions
    /// are the same value (verified: putting the derived key back alone changes no behaviour).
    /// Nothing in the product reaches this with an attacker-chosen id today — every caller passes
    /// an id from a list the server rendered for one tenant — so this is hardening rather than a
    /// breach. The contract was the problem: "name a secret and I will decrypt it" is what a REST
    /// route, an MCP tool or an in-cluster agent would have inherited.</para>
    /// </param>
    public async Task<string?> GetSecretValueByIdAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        if (secret is null)
        {
            return null;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);
    }

    /// <summary>
    /// Updates the encrypted value of an existing secret by its ID.
    /// Archives the previous value as a version before overwriting.
    /// </summary>
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task<bool> UpdateSecretValueAsync(
        Guid tenantId, Guid secretId, string newValue, string? updatedBy = null,
        CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        if (secret is null)
        {
            return false;
        }

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task<(bool Ok, string? Reason)> ChangeAppSecretScopeAsync(
        Guid tenantId, Guid secretId, Guid? environmentId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task<List<VaultSecretVersion>> GetSecretVersionsAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecretVersion>()
            .Where(v => v.SecretId == secretId && v.Secret.Vault.TenantId == tenantId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Decrypts and returns the value of a specific historical version.
    /// </summary>
    /// <param name="tenantId">
    /// Whose secret. An input, and it scopes BOTH the row and the key.
    ///
    /// <para>⚠️ This read used to take a secret id alone and then unseal the vault with
    /// <c>secret.Vault.TenantId</c> — the tenant of the row the caller had just named — so the
    /// derived tenant handed over the correct data key and naming any secret id in the
    /// installation returned its plaintext. <b>The predicate is the fix</b>; unsealing with the
    /// caller's tenant is a clarity change, because once the row is scoped the two expressions
    /// are the same value (verified: putting the derived key back alone changes no behaviour).
    /// Nothing in the product reaches this with an attacker-chosen id today — every caller passes
    /// an id from a list the server rendered for one tenant — so this is hardening rather than a
    /// breach. The contract was the problem: "name a secret and I will decrypt it" is what a REST
    /// route, an MCP tool or an in-cluster agent would have inherited.</para>
    /// </param>
    public async Task<string?> GetSecretVersionValueAsync(
        Guid tenantId, Guid secretId, Guid versionId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecretVersion? version = await db.Set<VaultSecretVersion>()
            .Include(v => v.Secret).ThenInclude(s => s.Vault)
            .FirstOrDefaultAsync(
                v => v.Id == versionId && v.SecretId == secretId
                  && v.Secret.Vault.TenantId == tenantId, ct);

        if (version is null) return null;

        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        return encryption.Decrypt(dataKey, version.EncryptedValue, version.Nonce);
    }

    /// <summary>
    /// Restores a secret's value to a specific historical version.
    /// Archives the current value before overwriting.
    /// </summary>
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task<bool> RollbackToVersionAsync(
        Guid tenantId, Guid secretId, Guid versionId, string? updatedBy = null,
        CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .Include(s => s.Vault)
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        VaultSecretVersion? version = await db.Set<VaultSecretVersion>()
            .FirstOrDefaultAsync(
                v => v.Id == versionId && v.SecretId == secretId
                  && v.Secret.Vault.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task<(bool CanDelete, string? Reason)> CanDeleteSecretAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        // FindAsync takes a key and cannot carry a predicate, which is part of why these stayed
        // tenant-less: there was nowhere to put the tenant without making it a query.
        VaultSecret? secret = await db.VaultSecrets
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

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
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task<bool> DeleteSecretAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        if (secret is null)
        {
            return false;
        }

        db.VaultSecrets.Remove(secret);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Configures Kubernetes sync for a secret — enabling or disabling sync
    /// and setting the target Secret name, namespace, and cluster.
    /// Pass <paramref name="clusterId"/> for app-scoped secrets where the cluster
    /// cannot be derived from the secret's owner relationship.
    /// </summary>
    /// <param name="tenantId">
    /// Whose secret. #251 scoped the reads that decrypt; this is the other half of the same
    /// surface — the calls that overwrite, roll back, re-scope, re-sync or destroy. Same
    /// reasoning: no caller reaches these with an attacker-chosen id today, and the contract
    /// ("name a secret and I will delete it") is what a REST route or an agent would inherit.
    /// </param>
    public async Task ConfigureKubernetesSyncAsync(
        Guid tenantId, Guid secretId, bool syncEnabled, string? secretName, string? ns,
        CancellationToken ct = default, Guid? clusterId = null)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        VaultSecret? secret = await db.VaultSecrets
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

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
    /// Reconciles a "cluster-owned" secret from its live Kubernetes Secret (the reverse,
    /// k8s→vault direction) and records <see cref="VaultSecret.LastRefreshedFromClusterAt"/>.
    /// Handles every app secret shape: Opaque (single value), Certificate
    /// (kubernetes.io/tls — tls.crt/tls.key/ca.crt) and OAuthClient (named keys). Used by
    /// the background refresher for imported/observed secrets, by the manual "Refresh from
    /// cluster" action, and before taking ownership so EntKube adopts the current live
    /// value (e.g. whatever ArgoCD/Flux last set) rather than a stale snapshot.
    /// </summary>
    public async Task<ClusterRefreshResult> RefreshAppSecretFromClusterAsync(
        Guid tenantId, Guid secretId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        // Scoped by the ASKING tenant, not by the tenant on the row. It used to find the secret by
        // id alone and then read `secret.Vault.TenantId` to resolve the cluster — a check, but one
        // whose input came from the row being checked, so every caller was trusted to have picked a
        // secret it was entitled to. Both pages that call this have a route TenantId; the sweep
        // loops tenants.
        VaultSecret? secret = await db.Set<VaultSecret>()
            .FirstOrDefaultAsync(s => s.Id == secretId && s.Vault.TenantId == tenantId, ct);

        if (secret is null
            || secret.KubernetesClusterId is null
            || string.IsNullOrEmpty(secret.KubernetesSecretName))
        {
            return ClusterRefreshResult.NoTarget;
        }

        EntKube.Web.Services.Clusters.IClusterClient? clusterAccess =
            await clusterClients.ForAsync(tenantId, secret.KubernetesClusterId.Value, ct);

        if (clusterAccess is null)
        {
            return ClusterRefreshResult.NoTarget;
        }

        string ns = secret.KubernetesNamespace ?? "default";
        Dictionary<string, string>? liveData = await ReadLiveSecretDataAsync(
            secret.KubernetesSecretName!, ns, clusterAccess, ct);

        if (liveData is null)
        {
            // The Secret no longer exists in the cluster (or couldn't be read).
            return ClusterRefreshResult.NotFound;
        }

        // ⚠️ This read `secret.Vault.TenantId`, and the commit that scoped the query by the asking
        // tenant ("Two more sweeps act as each tenant", 1fdf96b) dropped the `.Include(s => s.Vault)`
        // that made the navigation non-null — the tenant moved into the WHERE clause, which joins
        // but does not populate. So every call that got this far threw NullReferenceException: the
        // refresher logs one warning per secret and the UI button shows "Failed to refresh from
        // cluster". The asking tenant is the right value anyway and is now unavoidable, because the
        // query above refuses to return a secret whose vault belongs to anyone else.
        byte[] dataKey = await UnsealVaultAsync(tenantId, ct);
        string currentValue = encryption.Decrypt(dataKey, secret.EncryptedValue, secret.Nonce);

        (bool changed, string? newValue) = secret.SecretType switch
        {
            VaultSecretType.Certificate => ReconcileCertificateFromCluster(currentValue, liveData),
            VaultSecretType.OAuthClient => ReconcileOAuthClientFromCluster(currentValue, liveData),
            _ => ReconcileOpaqueFromCluster(currentValue, liveData, secret.Name),
        };

        // Skip the value write (and version-history churn) when nothing changed — the
        // background refresher polls these on an interval — but always record that we
        // checked, so the UI can show how fresh the vault copy is.
        //
        // ⭐ When something DID change, archive-and-write happens once per observed change,
        // claimed on the vault copy this call compared against.
        //
        // <para><b>What this used to do.</b> It assigned the new ciphertext onto the tracked row
        // and called <see cref="ArchiveVersionAsync"/>, both unconditionally, inside the single
        // SaveChangesAsync below. In one process that is correct: the only poller is
        // ObservedSecretRefreshService's own 5-minute timer, which runs one pass at a time, and
        // the no-change skip above already stopped a slow poll from churning version history.
        // With a second instance the same guard no longer holds — both observe the SAME live
        // change against the SAME vault copy, and both append a version row. <b>One change
        // recorded as two.</b> The value itself converges (the two ciphertexts decrypt to the
        // same plaintext and differ only by nonce), so nothing is lost or corrupted; what is
        // wrong is the record of what changed and when, which for a vault is not cosmetic.</para>
        //
        // ⚠️ <b>What the claim is keyed on, and why it is not the secret id.</b> The key is
        // (secret id, the nonce we read) — the nonce is freshly random on every value write, so
        // it names the exact vault copy this call diffed the live Secret against:
        //   • two instances that read the same copy hold the same nonce, so exactly one UPDATE
        //     matches a row and the other matches none;
        //   • a second, genuinely different change arrives in a later pass against the WINNER's
        //     nonce, so it is claimed and archived on its own merits. A claim keyed on the id
        //     alone — or on "this secret was refreshed in the last N minutes" — would be too
        //     broad and would silently swallow that real second change, which is the same audit
        //     hole in the other direction.
        // It also makes an operator's own edit through the UI win: that write moves the nonce
        // too, so a refresh that compared against the pre-edit copy loses instead of overwriting
        // a change it never saw.
        //
        // A claim is NOT a lock (Cost/CostLedgerWriter.cs:112-124 is the one other claim in the
        // codebase and the pattern followed here): the loser does nothing and does not error. It
        // still records LastRefreshedFromClusterAt, because "this instance checked" is true of
        // both, and it reports Unchanged — by the time it finished, the vault copy already held
        // the live value.
        if (changed && newValue is not null)
        {
            (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, newValue);
            byte[] observedNonce = secret.Nonce;
            DateTime writtenAt = DateTime.UtcNow;

            // ⚠️ The version row and the value must land together or not at all. A crash between
            // them would record the change as ZERO versions — the same hole mirrored — so the
            // claim, the archive insert and the value write share one transaction. The claim is
            // the first statement in it, so the row lock is held only for the insert.
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await db.Database.BeginTransactionAsync(ct);

            int claimed = await db.Set<VaultSecret>()
                .Where(s => s.Id == secret.Id && s.Nonce == observedNonce)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.EncryptedValue, ciphertext)
                    .SetProperty(x => x.Nonce, nonce)
                    .SetProperty(x => x.UpdatedAt, writtenAt), ct);

            if (claimed > 0)
            {
                // ⚠️ `secret` deliberately still carries the SUPERSEDED ciphertext and nonce:
                // ExecuteUpdateAsync does not go through the change tracker, so the archive row
                // gets the value that was just replaced — which is what a version row is.
                // Assigning the new value onto the entity first, as this method used to, would
                // archive the new value as though it were the old one.
                await ArchiveVersionAsync(db, secret, ct);
                secret.LastRefreshedFromClusterAt = writtenAt;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ClusterRefreshResult.Updated;
            }

            // Lost the claim: another instance observed this same change and recorded it. Drop
            // the (empty) transaction and fall through to the freshness stamp below.
            await tx.RollbackAsync(ct);
            changed = false;
        }

        secret.LastRefreshedFromClusterAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return changed ? ClusterRefreshResult.Updated : ClusterRefreshResult.Unchanged;
    }

    /// <summary>Outcome of a k8s→vault reconcile for one secret.</summary>
    public enum ClusterRefreshResult
    {
        /// <summary>No live target is configured (no cluster or Secret name recorded).</summary>
        NoTarget,
        /// <summary>The target Secret could not be found or read in the cluster.</summary>
        NotFound,
        /// <summary>
        /// Nothing was written by this call, and the vault copy now holds the live value.
        ///
        /// <para>Reached two ways: the live value already matched the vault copy, or this call
        /// observed a change but <em>lost the claim</em> to another instance that observed the
        /// same change and recorded it. Both are "already up to date, nothing for me to do", and
        /// deliberately share one result so one observed change is counted — and announced —
        /// once installation-wide rather than once per instance.</para>
        /// </summary>
        Unchanged,
        /// <summary>The vault copy was updated to match the live value, by this call.</summary>
        Updated,
    }

    // Opaque: the value lives under the vault secret's own name as the data key.
    private static (bool Changed, string? NewValue) ReconcileOpaqueFromCluster(
        string currentValue, Dictionary<string, string> liveData, string key)
    {
        if (!liveData.TryGetValue(key, out string? liveValue))
        {
            return (false, null);
        }
        return string.Equals(currentValue, liveValue, StringComparison.Ordinal)
            ? (false, null)
            : (true, liveValue);
    }

    // Certificate: compare the live tls.crt/tls.key/ca.crt material against the stored
    // bundle by certificate identity (thumbprint set) so cosmetic PEM differences and
    // leaf/chain/CA re-splitting don't cause churn; rebuild the bundle only on real change.
    private static (bool Changed, string? NewValue) ReconcileCertificateFromCluster(
        string currentJson, Dictionary<string, string> liveData)
    {
        string liveCrt = liveData.GetValueOrDefault("tls.crt") ?? "";
        if (string.IsNullOrWhiteSpace(liveCrt))
        {
            // Not a readable TLS secret — leave the stored bundle untouched.
            return (false, null);
        }
        string liveKey = liveData.GetValueOrDefault("tls.key") ?? "";
        string liveCa = liveData.GetValueOrDefault("ca.crt") ?? "";

        CertificateBundle current = DeserializeBundle(currentJson);
        string currentCerts = ConcatPemParts(current.Certificate, current.Chain, current.CaCertificate);
        string liveCerts = ConcatPemParts(liveCrt, liveCa);

        bool sameCerts = CertificateThumbprints(currentCerts).SetEquals(CertificateThumbprints(liveCerts));
        bool sameKey = NormalizePem(current.PrivateKey) == NormalizePem(liveKey);
        if (sameCerts && sameKey)
        {
            return (false, null);
        }

        // Rebuild from the live material, reusing the importer so the leaf/chain/CA are
        // split exactly as a fresh upload would be.
        string combined = ConcatPemParts(liveKey, liveCrt, liveCa);
        (bool ok, _, CertificateBundle? rebuilt) = CertificateImporter.Import(
            Encoding.UTF8.GetBytes(combined), "cluster.pem", null);
        if (!ok || rebuilt is null)
        {
            return (false, null);
        }
        return (true, JsonSerializer.Serialize(rebuilt, CertificateJsonOptions));
    }

    // OAuth client: update only the keys the cluster actually publishes, preserving
    // vault-only metadata (provider, region, user-pool, expiry) that k8s never carries.
    private static (bool Changed, string? NewValue) ReconcileOAuthClientFromCluster(
        string currentJson, Dictionary<string, string> liveData)
    {
        OAuthClientBundle b = DeserializeOAuthBundle(currentJson);
        bool changed = false;

        if (liveData.TryGetValue("client-secret", out string? cs) && (b.ClientSecret ?? "").Trim() != cs.Trim())
        {
            b.ClientSecret = cs.Trim();
            changed = true;
        }
        if (liveData.TryGetValue("client-id", out string? cid) && (b.ClientId ?? "").Trim() != cid.Trim())
        {
            b.ClientId = cid.Trim();
            changed = true;
        }
        if (liveData.TryGetValue("tenant-id", out string? tid) && (b.TenantId ?? "").Trim() != tid.Trim())
        {
            b.TenantId = tid.Trim();
            changed = true;
        }
        if (liveData.TryGetValue("scopes", out string? sc) && (b.Scopes ?? "").Trim() != sc.Trim())
        {
            b.Scopes = sc.Trim();
            changed = true;
        }
        if (liveData.TryGetValue("issuer", out string? iss) && (b.EffectiveIssuer ?? "").Trim() != iss.Trim())
        {
            // Store the explicit issuer; EffectiveIssuer then returns it verbatim.
            b.Issuer = iss.Trim();
            changed = true;
        }

        return changed ? (true, JsonSerializer.Serialize(b, CertificateJsonOptions)) : (false, null);
    }

    private static string ConcatPemParts(params string?[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private static string NormalizePem(string? pem) =>
        string.IsNullOrWhiteSpace(pem) ? "" : pem.Replace("\r", "").Trim();

    /// <summary>The set of certificate thumbprints found in a PEM blob (empty on parse failure).</summary>
    private static HashSet<string> CertificateThumbprints(string pem)
    {
        HashSet<string> thumbs = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pem))
        {
            return thumbs;
        }
        try
        {
            System.Security.Cryptography.X509Certificates.X509Certificate2Collection col = [];
            col.ImportFromPem(pem);
            foreach (System.Security.Cryptography.X509Certificates.X509Certificate2 c in col)
            {
                thumbs.Add(c.Thumbprint);
                c.Dispose();
            }
        }
        catch
        {
            // Unparseable material → empty set; treated as "changed" so we rebuild.
        }
        return thumbs;
    }

    /// <summary>
    /// Returns the IDs of <em>one tenant's</em> app secrets that are "observed": Kubernetes sync is
    /// disabled but a target cluster + Secret name are recorded — the state imported secrets land
    /// in. The background refresher re-reads their live values so the vault copy tracks whatever
    /// ArgoCD/Flux last set, until the operator takes ownership (enables sync).
    ///
    /// <para>The tenant is a parameter because this fed a sweep that then acted <em>as</em> whatever
    /// tenant each returned row named (docs/decomposition.md §4.0.1.2). Asking per tenant makes the
    /// tenant an input, so the candidate list can only ever be the asking tenant's own.</para>
    /// </summary>
    public async Task<List<Guid>> GetObservedAppSecretIdsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await ObservedAppSecretsFor(db, tenantId).Select(s => s.Id).ToListAsync(ct);
    }

    /// <summary>
    /// One tenant's observed app secrets.
    ///
    /// <para>Public and static so the scoping can be asserted without running the sweep: the
    /// previous version selected every observed secret in the installation, and a test that only
    /// counted what came back would pass against either. The tenant reaches a secret through its
    /// vault, which is where it lives in the schema.</para>
    /// </summary>
    public static IQueryable<VaultSecret> ObservedAppSecretsFor(SecretsDbContext db, Guid tenantId)
        => db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId
                && s.AppId != null
                && !s.SyncToKubernetes
                && s.KubernetesClusterId != null
                && s.KubernetesSecretName != null);

    /// <summary>
    /// Reads every key of a live Secret via kubectl and returns them base64-decoded,
    /// or null when the Secret cannot be read (e.g. it no longer exists). An existing
    /// Secret with no data section returns an empty map.
    /// </summary>
    private static async Task<Dictionary<string, string>?> ReadLiveSecretDataAsync(
        string secretName, string ns, EntKube.Web.Services.Clusters.IClusterClient clusterAccess,
        CancellationToken ct)
    {
        try
        {
            // One named Secret, so the seam's resource/namespace read with the name as a field
            // selector rather than a list — the parse below already expects a single object's
            // "data", and the caller reads null as "gone from the cluster".
            string json = await clusterAccess.GetJsonAsync($"secret/{secretName}", ns, ct: ct);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            System.Text.Json.Nodes.JsonNode? node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node?["data"] is not System.Text.Json.Nodes.JsonObject data)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            // Index by the literal key so dotted keys (e.g. tls.crt) round-trip.
            Dictionary<string, string> decoded = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> kv in data)
            {
                string? base64 = kv.Value?.GetValue<string>();
                if (string.IsNullOrEmpty(base64))
                {
                    continue;
                }
                try
                {
                    decoded[kv.Key] = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                }
                catch
                {
                    // Skip values that aren't valid base64.
                }
            }
            return decoded;
        }
        catch
        {
            // The credential file is the seam's business now, so there is nothing to clean up —
            // and a read that fails is still "gone or unreadable" to the caller.
            return null;
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
    ///
    /// <para>⚠️ <b>A scoped sync still has to account for the keys it cannot see.</b> A
    /// shared secret may target a cluster in any environment, so a sync scoped to test
    /// does reach a production cluster — and the Secret it writes there may also be fed
    /// by production-bound secrets this call is not allowed to read. This writer deletes
    /// the keys it owned last time and no longer holds, so a partial picture of its own
    /// keys reads as a revocation: saving a test secret deleted a production one. Those
    /// keys are therefore <em>retained</em> — named to the writer, neither written nor
    /// deleted, and left recorded as the vault's so the sync that can see them keeps the
    /// right to remove them.</para>
    /// </summary>
    public async Task<HelmExecutionResult> SyncAppSecretsToKubernetesAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default, Guid? environmentId = null)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        // Load ALL sync-enabled secrets — including those missing a cluster reference,
        // so we can report them clearly instead of silently skipping, and including the
        // ones this call's environment is not allowed to write.
        //
        // ⚠️ The environment filter is applied below rather than here, and that is the
        // whole point. A Kubernetes Secret is fed by every vault secret aimed at it,
        // whatever environment each belongs to, and this writer deletes the keys it
        // owned last time and no longer holds. Filtering in the query hands an
        // environment-scoped sync a partial picture of its OWN keys, and the keys it
        // cannot see read as keys it has dropped — so saving a test secret deleted a
        // production one. The out-of-scope rows are needed by NAME only; no value of
        // theirs is decrypted here.
        List<VaultSecret> everySecret = await db.Set<VaultSecret>()
            .Include(s => s.KubernetesCluster)
            .Where(s => s.AppId == appId && s.SyncToKubernetes)
            .ToListAsync(ct);

        // What this call may write: shared secrets, plus the ones bound to this
        // environment. An unscoped sync sees everything, so it retains nothing and
        // behaves exactly as before.
        bool InScope(VaultSecret s) =>
            environmentId == null || s.EnvironmentId == null || s.EnvironmentId == environmentId;

        List<VaultSecret> allSecrets = everySecret.Where(InScope).ToList();

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
        bool Actionable(VaultSecret s) =>
            s.KubernetesClusterId != null && !string.IsNullOrWhiteSpace(s.KubernetesSecretName)
            && (s.EnvironmentId == null || s.KubernetesCluster?.EnvironmentId == s.EnvironmentId);

        List<VaultSecret> actionable = allSecrets.Where(Actionable).ToList();

        // The same test over the rows this call may not write: what a sync with no
        // environment scope would have put into these same Secrets. Used only to decide
        // which key names to leave alone — never decrypted, never reported, and empty
        // whenever this sync has no environment scope.
        List<VaultSecret> actionableElsewhere = everySecret
            .Where(s => !InScope(s) && Actionable(s))
            .ToList();

        if (actionable.Count == 0)
        {
            return new HelmExecutionResult
            {
                Success = results.Count == 0,
                Output = results.Count > 0 ? string.Join("\n", results) : "No app secrets with both a K8s Secret name and a target cluster."
            };
        }

        // One action, one acknowledgment. This is the sync an operator triggers by saving a
        // secret, and it walks one group per (K8s secret name, namespace) per cluster — each
        // ensuring its namespace and writing its keys, each separately gated. So saving one
        // secret asked several times, which is the first thing an operator reported about this
        // gate.
        //
        // ⚠️ This method's gated writes go through SharedSecretKeys, which reaches the seam one
        // level down — invisible to a scan for seam verbs, which is why this one outlasted the
        // obvious cases.
        using IDisposable? operation = gate?.BeginOperation(
            $"sync {actionable.Count} app secret(s) to Kubernetes");

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

            // Through the seam, which also closes a hole the environment check above cannot see.
            // That check refuses an environment-bound secret whose target cluster is in a
            // different environment — but a SHARED secret has no environment, so the check is
            // skipped entirely and nothing else here compared the cluster's tenant to this one.
            // A shared app secret naming another tenant's cluster would have been written to it.
            // ForAsync returns null for "not this tenant's" and for "no kubeconfig" alike.
            EntKube.Web.Services.Clusters.IClusterClient? clusterAccess = cluster is null
                ? null
                : await clusterClients.ForAsync(tenantId, cluster.Id, ct);

            if (clusterAccess is null)
            {
                results.Add($"✗ Cluster '{clusterGroup.Key}' has no kubeconfig, or is not this tenant's — skipping {clusterGroup.Count()} secret(s).");
                continue;
            }

            {
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

                    // Every vault secret aimed at THIS Secret, including the ones this
                    // call's environment may not write. They are here to be named, not
                    // read.
                    List<VaultSecret> aimedHere = actionableElsewhere
                        .Where(s => s.SecretType == VaultSecretType.Opaque
                            && s.KubernetesClusterId == clusterGroup.Key
                            && s.KubernetesSecretName == k8sSecretName
                            && (s.KubernetesNamespace ?? "default") == ns)
                        .Concat(group)
                        .ToList();

                    // If a shared and an environment-bound secret share the same key name
                    // in this target, the environment-bound value wins (an explicit
                    // per-environment override). ⚠️ That is decided over every
                    // environment's secrets, not just this call's — otherwise a
                    // test-scoped sync writes the shared value over production's
                    // override, which is the same defect as the deletion below wearing
                    // different clothes.
                    List<VaultSecret> effective = aimedHere
                        .GroupBy(s => s.Name, StringComparer.Ordinal)
                        .Select(g => g.OrderByDescending(s => s.EnvironmentId.HasValue).First())
                        .ToList();

                    // Split into the keys that are this call's to write and the keys that
                    // are not. A retained key is neither written nor deleted, and stays
                    // recorded as the vault's, so the sync that CAN see it keeps the right
                    // to remove it when it is revoked.
                    Dictionary<string, string> values = [];
                    List<string> retained = [];

                    foreach (VaultSecret vaultSecret in effective)
                    {
                        if (InScope(vaultSecret))
                        {
                            values[vaultSecret.Name] =
                                encryption.Decrypt(dataKey, vaultSecret.EncryptedValue, vaultSecret.Nonce);
                        }
                        else
                        {
                            retained.Add(vaultSecret.Name);
                        }
                    }

                    if (values.Count == 0)
                    {
                        // Nothing failed and nothing needs doing: every key in this target
                        // is another environment's. Leaving it untouched is the answer, and
                        // ensuring its namespace would be a cluster change for no reason.
                        results.Add(retained.Count > 0
                            ? $"✓ Secret '{k8sSecretName}' in '{ns}' on '{cluster!.Name}' left untouched"
                              + $" — all {retained.Count} of its vault key(s) belong to another environment"
                            : $"  (skipped '{k8sSecretName}/{ns}' — no decryptable values)");
                        continue;
                    }

                    await clusterAccess.EnsureNamespaceAsync(ns, ct);

                    try
                    {
                        // ⚠️ This used to delete and recreate the Secret, so that a key removed
                        // from the vault could not survive in the cluster. That is the right
                        // property and the wrong mechanism, because this Secret can have other
                        // writers: an app bound to Redis or RabbitMQ may point those bindings at
                        // the same Secret on purpose, wanting one envFrom rather than several.
                        // Delete-and-recreate dropped their keys — and so would a plain apply,
                        // because apply deletes whatever the object's last-applied annotation
                        // named, and that annotation belongs to the object, not to the writer.
                        //
                        // So the keys this writer owns are named explicitly, and the ones it has
                        // since dropped are deleted by name — which needs knowing what it owned
                        // last time, hence the annotation.
                        await EntKube.Web.Services.Clusters.SharedSecretKeys.WriteAsync(
                            clusterAccess, k8sSecretName, ns,
                            EntKube.Web.Services.Clusters.SharedSecretKeys.Owners.Vault,
                            values.ToDictionary(v => v.Key, v => (string?)v.Value),
                            $"Sync {values.Count} secret key(s) into '{k8sSecretName}' in {ns}",
                            ct, retain: retained);

                        results.Add(
                            $"✓ Secret '{k8sSecretName}' synced to '{ns}' on '{cluster!.Name}'"
                            + $" ({values.Count} keys)"
                            + (retained.Count > 0
                                ? $", leaving {retained.Count} key(s) to another environment's sync"
                                : string.Empty));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        results.Add($"✗ Secret '{k8sSecretName}' failed on '{cluster!.Name}': {ex.Message}");
                    }
                }

                // Certificate secrets → one kubernetes.io/tls Secret each.
                foreach (VaultSecret certSecret in clusterGroup.Where(s => s.SecretType == VaultSecretType.Certificate))
                {
                    await SyncCertificateSecretAsync(certSecret, dataKey, cluster!, clusterAccess, results, ct);
                }

                // OAuth/OIDC client secrets → one Opaque Secret each (named keys).
                foreach (VaultSecret oauthSecret in clusterGroup.Where(s => s.SecretType == VaultSecretType.OAuthClient))
                {
                    await SyncOAuthClientSecretAsync(oauthSecret, dataKey, cluster!, clusterAccess, results, ct);
                }
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
    /// Renders a Secret as a manifest, base64-encoding each value.
    ///
    /// <para><b>Why a manifest rather than <c>--from-file</c>.</b> The temp-file form existed to
    /// keep values out of the argument list, which a process table exposes — and a manifest keeps
    /// them out of it just as well, in one 0600 file written by the seam instead of one per value
    /// written here. What it additionally buys is that the change goes through
    /// <c>IClusterClient</c>, so it is acknowledged and recorded like every other cluster change.
    /// It is NOT <c>--from-literal</c>, which would put the values in argv; that remains the thing
    /// never to do.</para>
    ///
    /// <para>The managed-by labels are part of the manifest, which removes the separate
    /// <c>kubectl label</c> call that used to follow every sync — a call that was best-effort, so
    /// a Secret could end up written but unlabelled, and the deployment importer would then
    /// re-adopt EntKube's own Secret back into the vault.</para>
    ///
    /// <para>Keys are quoted. Kubernetes allows <c>.</c> and <c>-</c> in a Secret key, and
    /// <c>tls.crt</c> unquoted is valid YAML but relies on it; quoting removes the question.</para>
    /// </summary>
    public static string BuildSecretManifest(
        string name, string ns, string type, IReadOnlyDictionary<string, string> values)
    {
        System.Text.StringBuilder manifest = new();

        manifest.AppendLine("apiVersion: v1");
        manifest.AppendLine("kind: Secret");
        manifest.AppendLine("metadata:");
        manifest.AppendLine($"  name: {name}");
        manifest.AppendLine($"  namespace: {ns}");
        manifest.AppendLine("  labels:");
        manifest.AppendLine($"    {ManagedByLabelKey}: {ManagedByLabelValue}");
        manifest.AppendLine("    entkube.io/managed: \"true\"");
        manifest.AppendLine($"type: {type}");
        manifest.AppendLine("data:");

        foreach ((string key, string value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
            manifest.AppendLine($"  \"{key}\": {encoded}");
        }

        return manifest.ToString();
    }

    /// <summary>
    /// Writes a single certificate secret to its target cluster as a
    /// <c>kubernetes.io/tls</c> Secret: <c>tls.crt</c> (leaf + chain), <c>tls.key</c>
    /// (private key), and <c>ca.crt</c> when a CA is present. A certificate with no
    /// private key cannot form a valid TLS Secret and is reported, not synced.
    /// </summary>
    private async Task SyncCertificateSecretAsync(
        VaultSecret certSecret, byte[] dataKey, KubernetesCluster cluster,
        EntKube.Web.Services.Clusters.IClusterClient clusterAccess, List<string> results,
        CancellationToken ct)
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

        await clusterAccess.EnsureNamespaceAsync(ns, ct);

        // The same four keys as before, with the same bodies — including the trailing newline on
        // the private key and the CA, which some consumers require and which the temp-file form
        // added explicitly.
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["tls.crt"] = bundle.CombinedCertificateChain,
            ["tls.key"] = bundle.PrivateKey!.Trim() + "\n",
            // fullchain.crt = leaf + intermediates + CA (everything but the private key),
            // for consumers that want the complete chain in a single file.
            ["fullchain.crt"] = bundle.FullChain,
        };

        if (bundle.HasCaCertificate)
        {
            values["ca.crt"] = bundle.CaCertificate!.Trim() + "\n";
        }

        try
        {
            await clusterAccess.ReplaceSecretAsync(
                k8sSecretName, ns,
                BuildSecretManifest(k8sSecretName, ns, "kubernetes.io/tls", values),
                $"Sync TLS certificate '{k8sSecretName}' into {ns}",
                ct);

            results.Add($"✓ Certificate '{k8sSecretName}' synced to '{ns}' on '{cluster.Name}' (kubernetes.io/tls, tls.crt + tls.key + fullchain.crt{(bundle.HasCaCertificate ? " + ca.crt" : "")})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            results.Add($"✗ Certificate '{k8sSecretName}' failed on '{cluster.Name}': {ex.Message}");
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
        EntKube.Web.Services.Clusters.IClusterClient clusterAccess, List<string> results,
        CancellationToken ct)
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

        await clusterAccess.EnsureNamespaceAsync(ns, ct);

        try
        {
            await clusterAccess.ReplaceSecretAsync(
                k8sSecretName, ns,
                BuildSecretManifest(k8sSecretName, ns, "Opaque", data),
                $"Sync OAuth client '{k8sSecretName}' into {ns}",
                ct);

            results.Add($"✓ OAuth client '{k8sSecretName}' synced to '{ns}' on '{cluster.Name}' ({data.Count} keys)");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            results.Add($"✗ OAuth client '{k8sSecretName}' failed on '{cluster.Name}': {ex.Message}");
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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
    //
    // ⚠️ These three are a BOUNDARY VIOLATION that is deliberately left in place for now, and a
    // separate authorization bug that is fixed here. ClusterComponent is CATALOG's table, reached
    // from Secrets' context through `Set<ClusterComponent>()` — public on DbContext, so it
    // compiles against a module context that exposes no property for it. That is what made the
    // adoption ratchet's "foreign writes: 0" untrue; see CatalogContractAdoptionTests.
    //
    // Moving the CRUD into Catalog is a PAGE refactor, not a service move: ClusterDetail reads
    // thirteen properties off the entity (including HelmRepoUrl, which InstalledComponent does not
    // carry), mirrors saved values back onto the instances in place, and feeds them to
    // entity-typed helpers. So the move waits, and the dangerous half does not: until now none of
    // these took a tenant, so a page could create, list or DELETE a component on any cluster in
    // the installation by id.

    /// <summary>
    /// Adds a new component (helm chart, deployment, etc.) to a cluster.
    /// </summary>
    /// <param name="tenantId">
    /// Whose cluster to add it to. Every one of the nine production callers already had a tenant
    /// in scope — three pages with a <c>TenantId</c> parameter, two scan methods and the blueprint
    /// generator with a <c>tenantId</c> argument — so the id was never the only thing available,
    /// just the only thing passed.
    /// </param>
    public async Task<ClusterComponent> CreateComponentAsync(
        Guid tenantId, Guid clusterId, string name, string componentType, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        if (!await db.KubernetesClusters.AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That cluster is not this tenant's.");
        }

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
    /// <param name="tenantId">
    /// Whose cluster's components. The predicate reaches the tenant through the component's
    /// cluster, which is the same route <c>CatalogApi.Scoped</c> takes — so a cluster id belonging
    /// to someone else now returns nothing instead of their component list.
    /// </param>
    public async Task<List<ClusterComponent>> GetComponentsAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<ClusterComponent>()
            .Where(c => c.ClusterId == clusterId && c.Cluster.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Deletes a component by ID. Cascade will also remove its secrets.
    /// </summary>
    /// <param name="tenantId">
    /// Whose component. ⚠️ This is the one that mattered: <c>FindAsync(componentId)</c> deletes by
    /// primary key alone, so before this parameter existed a component id was the entire
    /// authorization for destroying it <em>and</em> its secrets by cascade — and this is the only
    /// production delete of a <c>ClusterComponent</c> row anywhere in the product. A component the
    /// tenant does not own now answers false, exactly as a missing one does.
    /// </param>
    public async Task<bool> DeleteComponentAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

        ClusterComponent? component = await db.Set<ClusterComponent>()
            .FirstOrDefaultAsync(c => c.Id == componentId && c.Cluster.TenantId == tenantId, ct);

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.RabbitMQClusterId == rabbitMQClusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task DeleteRabbitMQClusterSecretAsync(
        Guid tenantId, Guid rabbitMQClusterId, string name, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

        return await db.Set<VaultSecret>()
            .Where(s => s.Vault.TenantId == tenantId && s.RedisClusterId == clusterId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    // ── Kafka cluster secrets ────────────────────────────────────────────────────

    public async Task<VaultSecret> SetKafkaClusterSecretAsync(
        Guid tenantId, Guid clusterId, string name, string value, CancellationToken ct = default)
    {
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
        using SecretsDbContext db = dbFactory.CreateDbContext();

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
    ///
    /// <para>⚠️ <b>The version number is allocated by a read-then-write, and that is only
    /// safe because the database says so.</b> <c>MAX + 1</c> is read here and the row is
    /// inserted by the caller's SaveChangesAsync; between those two moments another
    /// instance can read the same MAX. There is no application-side interlock, and with
    /// ~30 call sites through this helper there is no realistic way to add one that a new
    /// caller cannot forget.</para>
    ///
    /// <para>⭐ What makes the number unique is the unique index on
    /// (SecretId, VersionNumber) — SecretsModel.cs, where the reasoning is written out.
    /// Before it existed, two writers that read MAX=3 both inserted a row numbered 4 and
    /// the database took both: <b>one secret, two version 4s</b>, with no error anywhere
    /// and no way afterwards to say which was the fourth value. Now the loser's
    /// SaveChangesAsync throws DbUpdateException and its whole write — version row and new
    /// secret value together — rolls back, leaving the secret exactly as the winner left
    /// it. A failed save the caller can retry, instead of a history nobody can read.</para>
    ///
    /// <para>⚠️ Do not "fix" that exception by catching it here. The insert belongs to the
    /// caller's SaveChangesAsync, which also carries the new ciphertext; swallowing the
    /// conflict would commit a value change with no version row, which is the same audit
    /// hole in the other direction.</para>
    /// </summary>
    private static async Task ArchiveVersionAsync(
        SecretsDbContext db, VaultSecret secret, CancellationToken ct)
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
        using SecretsDbContext db = dbFactory.CreateDbContext();

        SecretVault vault = await db.Set<SecretVault>()
            .FirstAsync(v => v.TenantId == tenantId, ct);

        return encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
    }
}
