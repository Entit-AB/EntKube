using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
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
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultEncryptionService encryption)
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

        using ApplicationDbContext db = dbFactory.CreateDbContext();
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
        using ApplicationDbContext db = dbFactory.CreateDbContext();

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
    public async Task ChangeScopeAsync(
        Guid credentialId,
        Guid? environmentId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>().FindAsync([credentialId], ct);
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
    public async Task<string?> GetPasswordAsync(
        Guid credentialId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .Include(c => c.Vault)
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct);

        if (cred is null) return null;

        byte[] dataKey = await UnsealAsync(cred.Vault.TenantId, ct);
        return encryption.Decrypt(dataKey, cred.EncryptedPassword, cred.PasswordNonce);
    }

    /// <summary>
    /// Updates the mutable fields of a credential. Pass null to leave a field unchanged.
    /// </summary>
    public async Task UpdateAsync(
        Guid credentialId,
        string? newUsername,
        string? newPassword,
        string? newEmail,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .Include(c => c.Vault)
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct);

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
    public async Task<bool> DeleteAsync(Guid credentialId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>().FindAsync([credentialId], ct);
        if (cred is null) return false;
        db.Set<DockerRegistryCredential>().Remove(cred);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ─── Kubernetes Sync ───────────────────────────────────────────────────────

    /// <summary>
    /// Saves the Kubernetes sync target (cluster, namespace, secret name) for a credential.
    /// </summary>
    public async Task ConfigureSyncAsync(
        Guid credentialId,
        Guid clusterId,
        string secretName,
        string @namespace,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>().FindAsync([credentialId], ct);
        if (cred is null) return;

        cred.KubernetesClusterId = clusterId;
        cred.KubernetesSecretName = secretName.Trim();
        cred.KubernetesNamespace = @namespace.Trim();
        cred.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Generates a kubernetes.io/dockerconfigjson Secret manifest from the credential
    /// and applies it to the configured cluster with kubectl. Idempotent — safe to
    /// call multiple times; kubectl apply will update in place.
    ///
    /// A Namespace manifest is prepended so the namespace is created if absent.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> SyncToKubernetesAsync(
        Guid credentialId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        DockerRegistryCredential? cred = await db.Set<DockerRegistryCredential>()
            .Include(c => c.Vault)
            .Include(c => c.KubernetesCluster)
            .FirstOrDefaultAsync(c => c.Id == credentialId, ct);

        if (cred is null)
            return KubernetesOperationResult<string>.Failure("Credential not found.");

        if (cred.KubernetesCluster is null || string.IsNullOrEmpty(cred.KubernetesCluster.Kubeconfig))
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

        // Build the combined manifest: namespace + secret.
        string manifest = $"""
            apiVersion: v1
            kind: Namespace
            metadata:
              name: {cred.KubernetesNamespace}
            ---
            apiVersion: v1
            kind: Secret
            metadata:
              name: {cred.KubernetesSecretName}
              namespace: {cred.KubernetesNamespace}
            type: kubernetes.io/dockerconfigjson
            data:
              .dockerconfigjson: {dockerConfigBase64}
            """;

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-docker-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, cred.KubernetesCluster.Kubeconfig, ct);
            await File.WriteAllTextAsync(tempManifest, manifest, ct);

            HelmExecutionResult result = await RunCliAsync(
                "kubectl", $"apply -f {tempManifest} --kubeconfig {tempKubeconfig}", ct);

            return result.Success
                ? KubernetesOperationResult<string>.Success(result.Output)
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
            if (File.Exists(tempManifest)) File.Delete(tempManifest);
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
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.Set<SecretVault>().FirstAsync(v => v.TenantId == tenantId, ct);
    }

    private async Task<byte[]> UnsealAsync(Guid tenantId, CancellationToken ct)
    {
        SecretVault vault = await GetVaultAsync(tenantId, ct);
        return encryption.UnsealDataKey(vault.EncryptedDataKey, vault.Nonce);
    }

    private static async Task<HelmExecutionResult> RunCliAsync(
        string program, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        string combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout}\n{stderr}";

        return new HelmExecutionResult { Success = process.ExitCode == 0, Output = combined };
    }
}
