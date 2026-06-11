namespace EntKube.Web.Data;

/// <summary>
/// A named CSS theme bundle for a Keycloak instance. Captures both the
/// Keycloak-native theme to activate (loginTheme / accountTheme) and stores
/// custom CSS overrides in the vault keyed by this theme's Id. Realms can
/// reference a theme to inherit its settings in one operation.
/// </summary>
public class KeycloakTheme
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid KeycloakComponentConfigId { get; set; }

    /// <summary>Human-readable name shown in the theme selector (e.g. "Capio Blue").</summary>
    public required string Name { get; set; }

    /// <summary>Keycloak-native login theme provider name (e.g. "capio", "keycloak").</summary>
    public string? LoginTheme { get; set; }

    /// <summary>Keycloak-native account console theme provider name.</summary>
    public string? AccountTheme { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KeycloakComponentConfig ComponentConfig { get; set; } = null!;
    public ICollection<KeycloakRealm> Realms { get; set; } = [];
}
