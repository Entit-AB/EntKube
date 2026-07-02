namespace EntKube.Web.Data;

/// <summary>
/// Docker / OCI registry authentication credential stored in the tenant vault.
/// The password is encrypted with the tenant's DEK (AES-256-GCM). When
/// KubernetesClusterId + KubernetesSecretName + KubernetesNamespace are set the
/// credential can be pushed to a cluster as a kubernetes.io/dockerconfigjson
/// typed Secret so workloads can pull private images.
/// </summary>
public class DockerRegistryCredential
{
    public Guid Id { get; set; }

    /// <summary>
    /// The tenant vault that owns this credential. Used to look up the DEK
    /// for encryption/decryption.
    /// </summary>
    public Guid VaultId { get; set; }

    /// <summary>Human-friendly display name, e.g. "Production ACR" or "Docker Hub pull".</summary>
    public required string Name { get; set; }

    /// <summary>Registry type — drives the server URL default and UI display label.</summary>
    public DockerRegistryType RegistryType { get; set; } = DockerRegistryType.Generic;

    /// <summary>
    /// Registry server URL, e.g. "https://index.docker.io/v1/", "myacr.azurecr.io",
    /// "registry.example.com". Stored in plain text.
    /// </summary>
    public required string Server { get; set; }

    /// <summary>Registry username or service principal ID. Stored in plain text.</summary>
    public required string Username { get; set; }

    /// <summary>
    /// Password, personal access token, or service principal secret.
    /// Encrypted with the tenant DEK using AES-256-GCM. Combined ciphertext + tag.
    /// </summary>
    public required byte[] EncryptedPassword { get; set; }

    /// <summary>GCM nonce used when encrypting EncryptedPassword. Unique per write.</summary>
    public required byte[] PasswordNonce { get; set; }

    /// <summary>Optional contact email. Required by some registries for the auth entry.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// If set, this credential is scoped to a specific app. Null means tenant-wide.
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// For app-scoped credentials only: the environment this credential is bound to.
    /// When null, the credential is "shared" — visible and usable across all of the app's
    /// environments. When set, it is only visible within that environment (a prod pull secret
    /// is never shown in, nor synced to, the test environment). Mirrors
    /// <see cref="VaultSecret.EnvironmentId"/>.
    /// </summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>Target Kubernetes cluster for the dockerconfigjson secret sync.</summary>
    public Guid? KubernetesClusterId { get; set; }

    /// <summary>Name of the Kubernetes Secret to create/update on sync, e.g. "registry-creds".</summary>
    public string? KubernetesSecretName { get; set; }

    /// <summary>Kubernetes namespace that receives the synced Secret.</summary>
    public string? KubernetesNamespace { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public SecretVault Vault { get; set; } = null!;
    public App? App { get; set; }
    public Environment? Environment { get; set; }
    public KubernetesCluster? KubernetesCluster { get; set; }
}
