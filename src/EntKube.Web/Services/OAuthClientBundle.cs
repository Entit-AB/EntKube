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

/// <summary>Validation and display helpers for OAuth client credentials.</summary>
public static class OAuthClientHelper
{
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
