namespace EntKube.Web.Data;

/// <summary>
/// Connects a Keycloak OIDC client to an app deployment so the platform can sync
/// the client secret into a Kubernetes Secret in the app's namespace.
///
/// When a binding exists and SyncEnabled is true, syncing writes a K8s Secret named
/// KubernetesSecretName containing OIDC_ISSUER_URL, OIDC_CLIENT_ID, and
/// OIDC_CLIENT_SECRET into the AppDeployment's namespace. The app can then mount
/// the secret as env vars without being coupled to a specific Keycloak instance.
/// </summary>
public class IdentityBinding
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Keycloak realm that issues tokens for this binding.
    /// </summary>
    public Guid KeycloakRealmId { get; set; }

    /// <summary>
    /// The deployment that consumes the OIDC credentials.
    /// The secret is written into this deployment's namespace on its cluster.
    /// </summary>
    public Guid AppDeploymentId { get; set; }

    /// <summary>
    /// Internal Keycloak UUID for the client (used in Admin REST API calls).
    /// Different from ClientId — this is the UUID Keycloak assigns, not the human-readable name.
    /// </summary>
    public required string ClientUuid { get; set; }

    /// <summary>
    /// Human-readable client ID (e.g., "billing-api"). Written into the K8s Secret
    /// as OIDC_CLIENT_ID so the app can reference it without hardcoding.
    /// </summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// The Kubernetes Secret name to create in the app's namespace.
    /// </summary>
    public required string KubernetesSecretName { get; set; }

    /// <summary>
    /// When true, the platform syncs the OIDC credentials into the app namespace
    /// whenever the binding is triggered (e.g., via "Sync All"). Disable to pause
    /// propagation without removing the binding record.
    /// </summary>
    public bool SyncEnabled { get; set; } = true;

    /// <summary>
    /// Last time the secret was successfully synced to the target cluster.
    /// Null if never synced.
    /// </summary>
    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KeycloakRealm KeycloakRealm { get; set; } = null!;
    public AppDeployment AppDeployment { get; set; } = null!;
}
