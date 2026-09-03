namespace EntKube.Web.Services.Sso;

/// <summary>
/// Configuration for single sign-on, bound from the <c>Oidc</c> configuration section.
///
/// SSO is opt-in and entirely config-driven: an EntKube instance with no <c>Oidc</c>
/// section registers no OIDC scheme at all, so the login page is unchanged and there is
/// no half-configured provider to misbehave.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>Master switch. False (the default) means no OIDC scheme is registered.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The provider's issuer URL, e.g. "https://login.microsoftonline.com/{tenant}/v2.0"
    /// or "https://keycloak.example.com/realms/entkube". Discovery is done from here.
    /// </summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Button label on the login page. Defaults to the scheme name.</summary>
    public string DisplayName { get; set; } = "Single sign-on";

    /// <summary>
    /// Extra scopes beyond openid/profile/email. A groups claim usually needs one —
    /// "groups" for Keycloak, or a directory-specific scope for Entra.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// Claim carrying the user's group memberships. Providers disagree: Keycloak emits
    /// "groups", Entra emits "groups" (object ids) or "roles", Okta is configurable.
    /// </summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>
    /// Require HTTPS metadata. Only set false for a local development IdP — turning it
    /// off in production allows the discovery document to be fetched over plain HTTP,
    /// which is enough to hand an attacker the whole login flow.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// When true, an SSO user whose groups map to nothing is still signed in with no
    /// tenant access. When false (the default) they are refused, which is usually what
    /// an operator wants: a directory-wide SSO app should not let the whole directory
    /// create accounts.
    /// </summary>
    public bool AllowUsersWithoutMappedGroups { get; set; }

    /// <summary>The scheme name used in Identity's external-login records.</summary>
    public const string Scheme = "oidc";

    /// <summary>True when the options carry enough to register a working scheme.</summary>
    public bool IsUsable =>
        Enabled
        && !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(ClientId);
}
