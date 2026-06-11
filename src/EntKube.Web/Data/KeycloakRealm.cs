namespace EntKube.Web.Data;

/// <summary>
/// A Keycloak realm managed by EntKube. The realm exists in Keycloak;
/// this entity tracks it so EntKube can surface it in the UI, manage backups,
/// and optionally link it to a customer app for self-service portal access.
/// </summary>
public class KeycloakRealm
{
    public Guid Id { get; set; }

    public Guid KeycloakComponentConfigId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The Keycloak realm ID (e.g. "acme-prod"). Used in all API calls.
    /// </summary>
    public required string RealmName { get; set; }

    /// <summary>
    /// Human-readable display name shown inside the Keycloak login UI.
    /// </summary>
    public required string DisplayName { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Login page theme assigned to this realm (e.g. "keycloak", "custom-brand").
    /// Null means the Keycloak default.
    /// </summary>
    public string? LoginTheme { get; set; }

    /// <summary>
    /// Account console theme for self-service password management.
    /// </summary>
    public string? AccountTheme { get; set; }

    /// <summary>
    /// When set, this realm is linked to a customer app and the customer can
    /// manage their realm (users, groups, IdPs) through the portal.
    /// </summary>
    public Guid? LinkedAppId { get; set; }

    /// <summary>
    /// Optional named theme (from EntKube's theme library for this Keycloak instance).
    /// When set, applying the theme writes its LoginTheme/AccountTheme to Keycloak
    /// and associates its CSS overrides with this realm.
    /// </summary>
    public Guid? KeycloakThemeId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KeycloakComponentConfig ComponentConfig { get; set; } = null!;
    public App? LinkedApp { get; set; }
    public KeycloakTheme? Theme { get; set; }
    public ICollection<KeycloakBackup> Backups { get; set; } = [];
}
