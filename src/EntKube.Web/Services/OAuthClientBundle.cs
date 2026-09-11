namespace EntKube.Web.Services;

/// <summary>
/// The identity provider an OAuth/OIDC client secret belongs to. Drives the
/// adaptive add/edit form and the icon/label shown in lists.
/// </summary>
public enum OAuthProvider
{
    GenericOidc = 0,
    Entra = 1,
    Google = 2,
    AwsCognito = 3,
}

/// <summary>
/// An OAuth/OIDC client (app registration) credential — e.g. a Microsoft Entra,
/// Google, or AWS Cognito app registration. Stored encrypted as a JSON document
/// inside <see cref="EntKube.Web.Data.VaultSecret.EncryptedValue"/> when the
/// secret's type is <see cref="EntKube.Web.Data.VaultSecretType.OAuthClient"/>.
///
/// Unlike a certificate, the expiry of a client secret cannot be derived from the
/// value — providers report it at creation time, so <see cref="ExpiresAt"/> is
/// entered manually and drives the same expiry warnings as certificates.
/// </summary>
public sealed class OAuthClientBundle
{
    public OAuthProvider Provider { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The client secret value (the sensitive part).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The directory/tenant id — used by Entra.</summary>
    public string? TenantId { get; set; }

    /// <summary>The issuer / authority URL (OIDC issuer). May be derived for Cognito.</summary>
    public string? Issuer { get; set; }

    /// <summary>AWS region — used by Cognito to derive the issuer.</summary>
    public string? Region { get; set; }

    /// <summary>Cognito user pool id — used to derive the issuer.</summary>
    public string? UserPoolId { get; set; }

    /// <summary>Space-separated OAuth scopes (e.g. "openid profile email").</summary>
    public string? Scopes { get; set; }

    /// <summary>When the client secret expires, as reported by the provider. Manually entered.</summary>
    public DateTime? ExpiresAt { get; set; }

    public bool HasClientSecret => !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// The issuer URL to publish, deriving the Cognito issuer from region + user
    /// pool when an explicit issuer was not supplied.
    /// </summary>
    public string? EffectiveIssuer
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Issuer)) return Issuer.Trim();
            if (Provider == OAuthProvider.Entra && !string.IsNullOrWhiteSpace(TenantId))
            {
                // Workforce Entra's v2.0 issuer. The trailing /v2.0 is what makes the discovery
                // document and the iss claim line up — without it, token validation rejects
                // everything. (External ID / CIAM uses a {tenant}.ciamlogin.com issuer; set Issuer
                // explicitly for that.)
                return $"https://login.microsoftonline.com/{TenantId.Trim()}/v2.0";
            }
            if (Provider == OAuthProvider.Google)
            {
                return "https://accounts.google.com";
            }
            if (Provider == OAuthProvider.AwsCognito
                && !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(UserPoolId))
            {
                return $"https://cognito-idp.{Region.Trim()}.amazonaws.com/{UserPoolId.Trim()}";
            }
            return null;
        }
    }

    /// <summary>Projects the non-secret metadata for list/badge display.</summary>
    public OAuthClientInfo ToInfo() => new()
    {
        Provider = Provider,
        ClientId = ClientId,
        Issuer = EffectiveIssuer,
        TenantId = TenantId,
        Scopes = Scopes,
        ExpiresAt = ExpiresAt,
    };
}

/// <summary>
/// Non-secret metadata about an OAuth client, safe to surface in lists (excludes
/// the client secret).
/// </summary>
public sealed class OAuthClientInfo
{
    public OAuthProvider Provider { get; init; }
    public string? ClientId { get; init; }
    public string? Issuer { get; init; }
    public string? TenantId { get; init; }
    public string? Scopes { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>
/// The settings a component needs to accept logins from an identity provider, resolved from a
/// stored app registration. The point of this type is that the provider-specific correctness lives
/// in one verified place rather than being re-derived (and re-broken) per component.
/// </summary>
/// <param name="IssuerUrl">OIDC issuer — where the discovery document is fetched and the iss claim is matched.</param>
/// <param name="ClientId">The application (client) id.</param>
/// <param name="RequireAudience">
/// The audience a token must carry. For Entra this is the client id and Entra rejects a token whose
/// aud does not match, so leaving it blank silently fails every login.
/// </param>
/// <param name="ClaimUsername">The claim carrying the login name.</param>
/// <param name="Scopes">Space-separated scopes to request.</param>
/// <param name="ClaimGroups">The claim carrying group memberships, or null when groups are not mapped.</param>
public sealed record ResolvedOidc(
    string IssuerUrl, string? ClientId, string? RequireAudience,
    string ClaimUsername, string Scopes, string? ClaimGroups);

/// <summary>Validation and display helpers for OAuth client credentials.</summary>
public static class OAuthClientHelper
{
    /// <summary>
    /// The correct OIDC settings for a stored app registration, per provider. Every value here that
    /// is not obvious was verified against the provider's own documentation:
    ///
    /// <list type="bullet">
    /// <item><description>Entra requires <c>aud</c> = the client id (it rejects a token whose audience
    /// does not match), and <c>preferred_username</c> only appears when the <c>profile</c> scope is
    /// requested — so both are set here rather than left to a generic default that omits them.</description></item>
    /// <item><description>Entra emits <c>groups</c> as group object IDs, not names, unless the app is
    /// configured otherwise, and drops the claim entirely past 200 groups.</description></item>
    /// <item><description>Google's username is <c>email</c>, and its issuer is the fixed
    /// <c>accounts.google.com</c>.</description></item>
    /// </list>
    ///
    /// Returns null when the bundle has no resolvable issuer (an incomplete registration).
    /// </summary>
    public static ResolvedOidc? Resolve(OAuthClientBundle bundle)
    {
        string? issuer = bundle.EffectiveIssuer;
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        string scopes = string.IsNullOrWhiteSpace(bundle.Scopes) ? DefaultScopes(bundle.Provider) : bundle.Scopes.Trim();

        return bundle.Provider switch
        {
            OAuthProvider.Entra => new ResolvedOidc(
                issuer, bundle.ClientId,
                // Entra enforces aud == client id; not requiring it accepts tokens minted for other apps.
                RequireAudience: bundle.ClientId,
                ClaimUsername: "preferred_username",
                Scopes: scopes,
                ClaimGroups: "groups"),

            OAuthProvider.Google => new ResolvedOidc(
                issuer, bundle.ClientId, RequireAudience: bundle.ClientId,
                ClaimUsername: "email", Scopes: scopes, ClaimGroups: null),

            OAuthProvider.AwsCognito => new ResolvedOidc(
                issuer, bundle.ClientId, RequireAudience: bundle.ClientId,
                ClaimUsername: "cognito:username", Scopes: scopes, ClaimGroups: "cognito:groups"),

            _ => new ResolvedOidc(
                issuer, bundle.ClientId, RequireAudience: bundle.ClientId,
                ClaimUsername: "preferred_username", Scopes: scopes, ClaimGroups: "groups"),
        };
    }

    /// <summary>The scopes a provider needs by default. Entra needs <c>profile</c> for preferred_username.</summary>
    public static string DefaultScopes(OAuthProvider provider) => provider switch
    {
        OAuthProvider.Entra => "openid email profile",
        OAuthProvider.Google => "openid email profile",
        _ => "openid email",
    };

    public static (bool Ok, string? Error) Validate(OAuthClientBundle b)
    {
        if (string.IsNullOrWhiteSpace(b.ClientSecret))
        {
            return (false, "A client secret is required.");
        }

        if (b.Provider == OAuthProvider.Entra && string.IsNullOrWhiteSpace(b.TenantId))
        {
            return (false, "Microsoft Entra app registrations require a Tenant ID.");
        }

        if (b.Provider == OAuthProvider.AwsCognito && string.IsNullOrWhiteSpace(b.EffectiveIssuer))
        {
            return (false, "AWS Cognito requires an Issuer URL, or a Region plus User Pool ID.");
        }

        return (true, null);
    }

    public static string DisplayName(OAuthProvider provider) => provider switch
    {
        OAuthProvider.Entra => "Microsoft Entra",
        OAuthProvider.Google => "Google",
        OAuthProvider.AwsCognito => "AWS Cognito",
        _ => "Generic OIDC",
    };

    public static string Icon(OAuthProvider provider) => provider switch
    {
        OAuthProvider.Entra => "bi-microsoft",
        OAuthProvider.Google => "bi-google",
        OAuthProvider.AwsCognito => "bi-amazon",
        _ => "bi-shield-lock",
    };
}
