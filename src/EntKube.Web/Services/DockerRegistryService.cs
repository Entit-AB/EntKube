using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages Docker / OCI registry credentials stored in the tenant vault.
/// Credentials are scoped per-tenant and optionally per-app. The password is
/// encrypted with the tenant DEK (AES-256-GCM) — the same envelope encryption
/// used by VaultSecret.
///
/// The SyncToKubernetesAsync method generates a kubernetes.io/dockerconfigjson
/// manifest and applies it via kubectl, so workloads can reference the Secret
/// in imagePullSecrets without ever seeing the raw password in plain text.
/// </summary>
public class DockerRegistryService(
    IDbContextFactory<DataServicesDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    VaultEncryptionService encryption,
    // Optional, and last, so the existing positional constructions keep compiling. Safe to be
    // absent: BeginOperation only ever suppresses the second and later questions of one action,
    // never a refusal — so a null gate means more dialogs, never fewer.
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate? gate = null)
{
    // ─── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new docker registry credential in the tenant vault.
    /// The vault must already be initialized.
    /// </summary>
    public async Task<DockerRegistryCredential> CreateAsync(
        Guid tenantId,
        Guid? appId,
        string name,
        DockerRegistryType registryType,
        string server,
        string username,
        string password,
        string? email,
        Guid? environmentId = null,
        CancellationToken ct = default)
    {
        byte[] dataKey = await UnsealAsync(tenantId, ct);
        SecretVault vault = await GetVaultAsync(tenantId, ct);
        (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, password);

        DockerRegistryCredential cred = new()
        {
            Id = Guid.NewGuid(),
            VaultId = vault.Id,
            AppId = appId,
            // An environment binding only makes sense for an app-scoped credential.
            EnvironmentId = appId.HasValue ? environmentId : null,
            Name = name,
            RegistryType = registryType,
            Server = server,
            Username = username,
            EncryptedPassword = ciphertext,
            PasswordNonce = nonce,
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim()
        };

        using DataServicesDbContext db = dbFactory.CreateDbContext();
        db.Set<DockerRegistryCredential>().Add(cred);
        await db.SaveChangesAsync(ct);
        return cred;
    }

    /// <summary>
    /// Returns all credentials for a tenant, optionally filtered to a single app.
    /// Credentials with AppId = null are tenant-wide and are always included.
    ///
    /// When <paramref name="environmentId"/> is supplied, only credentials visible in that
    /// environment are returned: environment-bound credentials for that environment plus
    /// "shared" ones (EnvironmentId = null). This mirrors
    /// <see cref="VaultService.GetAppSecretsForEnvironmentAsync"/> so a prod pull secret never
    /// leaks into the test environment view.
    /// </summary>
    public async Task<List<DockerRegistryCredential>> GetAsync(
        Guid tenantId,
        Guid? appId = null,
        Guid? environmentId = null,
        CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        IQueryable<DockerRegistryCredential> query = db.Set<DockerRegistryCredential>()
            .Include(c => c.KubernetesCluster)
            .Include(c => c.Environment)
            .Where(c => c.Vault.TenantId == tenantId);

        if (appId.HasValue)
            query = query.Where(c => c.AppId == appId || c.AppId == null);

        if (environmentId.HasValue)
            query = query.Where(c => c.EnvironmentId == null || c.EnvironmentId == environmentId);

        return await query.OrderBy(c => c.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Changes the environment scope of an app-scoped credential: pass null to make it "shared"
    /// across the app's environments, or an environment id to bind it to that environment.
    /// No-op for tenant-wide credentials (which have no app to own the environment).
    /// </summary>
    /// <param name="tenantId">Whose credential.</param>
    public async Task ChangeScopeAsync(
        Guid tenantId,
        Guid credentialId,
        Guid? environmentId,
        CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        // DockerRegistryCredential has no TenantId; it lives in a vault, and the vault has one.
        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.Vault.TenantId == tenantId, ct);
        if (cred is null || cred.AppId is null)
            return;

        cred.EnvironmentId = environmentId;
        cred.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Decrypts and returns the password for a credential.
    /// Returns null if the credential is not found.
    /// </summary>
    /// <param name="tenantId">
    /// Whose credential. ⚠️ This <b>returns the registry password in cleartext</b> and took a
    /// bare id — the sharpest shape in this unit, because there is no cluster, no manifest and
    /// no seam between the id and the secret. The credential has no TenantId of its own; it
    /// lives in a vault, and the vault has one.
    /// </param>
    public async Task<string?> GetPasswordAsync(
        Guid tenantId,
        Guid credentialId,
        CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .Include(c => c.Vault)
            // DockerRegistryCredential has no TenantId of its own; it lives in a vault, and the
            // vault has one. The cluster seam further down already refused a foreign cluster, so
            // this is the row-level half of the same check.
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.Vault.TenantId == tenantId, ct);

        if (cred is null) return null;

        byte[] dataKey = await UnsealAsync(cred.Vault.TenantId, ct);
        return encryption.Decrypt(dataKey, cred.EncryptedPassword, cred.PasswordNonce);
    }

    /// <summary>
    /// Updates the mutable fields of a credential. Pass null to leave a field unchanged.
    /// </summary>
    /// <param name="tenantId">
    /// Whose credential. Rewriting the username and password of a stranger's registry
    /// credential breaks every pull that uses it, and replaces it with one the editor knows.
    /// </param>
    public async Task UpdateAsync(
        Guid tenantId,
        Guid credentialId,
        string? newUsername,
        string? newPassword,
        string? newEmail,
        CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .Include(c => c.Vault)
            // DockerRegistryCredential has no TenantId of its own; it lives in a vault, and the
            // vault has one. The cluster seam further down already refused a foreign cluster, so
            // this is the row-level half of the same check.
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.Vault.TenantId == tenantId, ct);

        if (cred is null) return;

        if (newUsername is not null)
            cred.Username = newUsername.Trim();

        if (newPassword is not null)
        {
            byte[] dataKey = await UnsealAsync(cred.Vault.TenantId, ct);
            (byte[] ciphertext, byte[] nonce) = encryption.Encrypt(dataKey, newPassword);
            cred.EncryptedPassword = ciphertext;
            cred.PasswordNonce = nonce;
        }

        if (newEmail is not null)
            cred.Email = string.IsNullOrWhiteSpace(newEmail) ? null : newEmail.Trim();

        cred.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a credential by ID. Returns false if not found.</summary>
    /// <param name="tenantId">Whose credential. Deleting one breaks every pull that uses it.</param>
    public async Task<bool> DeleteAsync(
        Guid tenantId, Guid credentialId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        // DockerRegistryCredential has no TenantId; it lives in a vault, and the vault has one.
        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.Vault.TenantId == tenantId, ct);
        if (cred is null) return false;
        db.Set<DockerRegistryCredential>().Remove(cred);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ─── Kubernetes Sync ───────────────────────────────────────────────────────

    /// <summary>
    /// Saves the Kubernetes sync target (cluster, namespace, secret name) for a credential.
    /// </summary>
    /// <param name="tenantId">
    /// Whose credential and whose cluster. This decides the Secret name and namespace the pull
    /// credential is projected into, so it is a pair — the cluster is checked too.
    /// </param>
    public async Task ConfigureSyncAsync(
        Guid tenantId,
        Guid credentialId,
        Guid clusterId,
        string secretName,
        string @namespace,
        CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        // DockerRegistryCredential has no TenantId; it lives in a vault, and the vault has one.
        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.Vault.TenantId == tenantId, ct);
        if (cred is null) return;

        cred.KubernetesClusterId = clusterId;
        cred.KubernetesSecretName = secretName.Trim();
        cred.KubernetesNamespace = @namespace.Trim();
        cred.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes the credential into the configured cluster as a
    /// <c>kubernetes.io/dockerconfigjson</c> Secret, so workloads can reference it as an
    /// <c>imagePullSecret</c>. Idempotent.
    ///
    /// <para><b>Through the seam, so the write is acknowledged.</b> This used to build a combined
    /// Namespace+Secret manifest and run <c>kubectl apply -f</c> as its own process with
    /// <c>--kubeconfig</c>, which bypassed <c>ClusterChangeGate</c> entirely (§5.4): a registry
    /// password was written into a namespace with nobody asked and nothing recorded. It is now
    /// <c>EnsureNamespaceAsync</c> followed by <c>ReplaceSecretAsync</c> — replace rather than
    /// apply, because a Secret written this way carries no last-applied annotation and an apply
    /// would leave a removed key behind. The acknowledgment dialog shows the Secret's keys and not
    /// its values, which is what made routing secrets through the gate acceptable.</para>
    ///
    /// <para>The tenant comes from the caller rather than from the credential's vault row: it is
    /// what decides whether this cluster may be reached at all, and a value read off the row being
    /// acted on cannot decide that.</para>
    /// </summary>
    public async Task<KubernetesOperationResult<string>> SyncToKubernetesAsync(
        Guid tenantId,
        Guid credentialId,
        CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .Include(c => c.Vault)
            .Include(c => c.KubernetesCluster)
            // DockerRegistryCredential has no TenantId of its own; it lives in a vault, and the
            // vault has one. The cluster seam further down already refused a foreign cluster, so
            // this is the row-level half of the same check.
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.Vault.TenantId == tenantId, ct);

        if (cred is null)
            return KubernetesOperationResult<string>.Failure("Credential not found.");

        if (cred.KubernetesCluster is null || cred.KubernetesClusterId is not Guid clusterId)
            return KubernetesOperationResult<string>.Failure(
                "No Kubernetes cluster configured for this credential. Configure sync first.");

        // Cross-environment guard: an environment-bound credential may only be written to a cluster
        // belonging to its own environment, so a prod pull secret can't be synced into a test cluster.
        if (cred.EnvironmentId is Guid boundEnv && cred.KubernetesCluster.EnvironmentId != boundEnv)
            return KubernetesOperationResult<string>.Failure(
                "This credential is bound to a different environment than the target cluster. " +
                "Change its scope or pick a cluster in the same environment.");

        if (string.IsNullOrEmpty(cred.KubernetesSecretName) || string.IsNullOrEmpty(cred.KubernetesNamespace))
            return KubernetesOperationResult<string>.Failure(
                "K8s secret name and namespace must be set before syncing.");

        // Decrypt password.
        byte[] dataKey = await UnsealAsync(cred.Vault.TenantId, ct);
        string password = encryption.Decrypt(dataKey, cred.EncryptedPassword, cred.PasswordNonce);

        // Build the dockerconfigjson payload.
        string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cred.Username}:{password}"));
        var authEntry = new Dictionary<string, string>
        {
            ["username"] = cred.Username,
            ["password"] = password,
            ["auth"] = auth
        };
        if (!string.IsNullOrEmpty(cred.Email))
            authEntry["email"] = cred.Email;

        string dockerConfigJson = JsonSerializer.Serialize(new
        {
            auths = new Dictionary<string, Dictionary<string, string>>
            {
                [cred.Server] = authEntry
            }
        });
        string dockerConfigBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(dockerConfigJson));

        // The Secret alone: the namespace is its own operation now rather than a document
        // prepended to the manifest, because ReplaceSecretAsync replaces one named Secret.
        string manifest = $"""
            apiVersion: v1
            kind: Secret
            metadata:
              name: {cred.KubernetesSecretName}
              namespace: {cred.KubernetesNamespace}
            type: kubernetes.io/dockerconfigjson
            data:
              .dockerconfigjson: {dockerConfigBase64}
            """;

        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusterAccess.ForAsync(tenantId, clusterId, ct);

        if (cluster is null)
            return KubernetesOperationResult<string>.Failure(
                "That cluster is not this tenant's, or it has no stored kubeconfig.");

        // One action, one acknowledgment: creating the namespace and writing the pull secret
        // into it are halves of the same sync.
        using IDisposable? operation = gate?.BeginOperation("write a registry pull secret into a namespace");

        try
        {
            await cluster.EnsureNamespaceAsync(cred.KubernetesNamespace, ct);

            string output = await cluster.ReplaceSecretAsync(
                cred.KubernetesSecretName, cred.KubernetesNamespace, manifest,
                $"Write the '{cred.Server}' registry pull secret into {cred.KubernetesNamespace}",
                ct);

            return KubernetesOperationResult<string>.Success(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<string>.Failure(ex.Message);
        }
    }

    // ─── Static helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the well-known server URL for registry types that have a fixed endpoint.
    /// Returns an empty string for types where the user must supply the server.
    /// </summary>
    public static string DefaultServer(DockerRegistryType type) => type switch
    {
        DockerRegistryType.DockerHub => "https://index.docker.io/v1/",
        DockerRegistryType.Quay => "quay.io",
        DockerRegistryType.GitHubContainerRegistry => "ghcr.io",
        _ => string.Empty
    };

    /// <summary>
    /// Returns a human-readable display label for a registry type.
    /// </summary>
    public static string TypeLabel(DockerRegistryType type) => type switch
    {
        DockerRegistryType.DockerHub => "Docker Hub",
        DockerRegistryType.AzureContainerRegistry => "Azure Container Registry",
        DockerRegistryType.Harbor => "Harbor",
        DockerRegistryType.Quay => "Quay.io",
        DockerRegistryType.GitHubContainerRegistry => "GitHub Container Registry",
        _ => "Generic"
    };

    /// <summary>
    /// Returns a placeholder hint for the server field based on registry type.
    /// </summary>
    public static string ServerPlaceholder(DockerRegistryType type) => type switch
    {
        DockerRegistryType.AzureContainerRegistry => "myregistry.azurecr.io",
        DockerRegistryType.Harbor => "registry.example.com",
        _ => "registry.example.com"
    };

    // ─── Private helpers ───────────────────────────────────────────────────────

    private async Task<SecretVault> GetVaultAsync(Guid tenantId, CancellationToken ct)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();
        return await db.Set<SecretVault>().FirstAsync(v => v.TenantId == tenantId, ct);
    }

    private async Task<byte[]> UnsealAsync(Guid tenantId, CancellationToken ct)
    {
        SecretVault vault = await GetVaultAsync(tenantId, ct);
        return encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
    }

}
