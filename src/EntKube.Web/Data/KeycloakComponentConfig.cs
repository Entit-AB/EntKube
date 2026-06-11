namespace EntKube.Web.Data;

/// <summary>
/// Keycloak-specific configuration for an instance tracked by EntKube.
/// Covers both EntKube-managed installs (ClusterComponentId set) and externally
/// deployed instances (ClusterComponentId null, DisplayName used instead).
///
/// For managed instances: DB credentials and the admin password are stored as
/// component vault secrets (keyed by ClusterComponentId) and synced to K8s.
/// For external instances: only the admin password is stored in vault, keyed by
/// this config's own Id.
/// </summary>
public class KeycloakComponentConfig
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The installed Keycloak ClusterComponent this config belongs to.
    /// Null for externally deployed Keycloaks not managed by EntKube.
    /// </summary>
    public Guid? ClusterComponentId { get; set; }

    /// <summary>
    /// Human-readable name for externally deployed Keycloaks (ClusterComponentId is null).
    /// Ignored when ClusterComponentId is set — the component name is used instead.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The managed CNPG database backing this Keycloak instance.
    /// Null when using a registered Postgres database or no database is linked yet.
    /// Mutually exclusive with RegisteredPostgresDatabaseId.
    /// </summary>
    public Guid? CnpgDatabaseId { get; set; }

    /// <summary>
    /// A registered (non-CNPG) PostgreSQL database backing this Keycloak instance.
    /// Mutually exclusive with CnpgDatabaseId.
    /// </summary>
    public Guid? RegisteredPostgresDatabaseId { get; set; }

    /// <summary>
    /// Keycloak admin username (default "admin").
    /// </summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// URL used by the platform to call the Keycloak Admin REST API.
    /// Typically the external hostname (e.g. "https://auth.example.com").
    /// Can be auto-suggested from the component's ExternalRoutes.
    /// </summary>
    public string? AdminUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ClusterComponent? ClusterComponent { get; set; }
    public CnpgDatabase? CnpgDatabase { get; set; }
    public RegisteredPostgresDatabase? RegisteredPostgresDatabase { get; set; }
    public ICollection<KeycloakRealm> Realms { get; set; } = [];
}
