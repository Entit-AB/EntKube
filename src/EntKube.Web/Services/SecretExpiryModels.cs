using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// A vault secret that carries an expiry date (a certificate or an OAuth/OIDC client
/// credential), projected with its computed days-until-expiry and a human-readable
/// scope label. Produced by <see cref="VaultService.GetExpiringSecretCandidatesAsync"/>
/// and consumed by the expiry-notification scanner and the management UI. Never
/// contains the secret value.
/// </summary>
public sealed class ExpiringSecretInfo
{
    public required Guid SecretId { get; init; }
    public required string Name { get; init; }
    public required VaultSecretType SecretType { get; init; }

    /// <summary>Owning app id, when the secret is app-scoped.</summary>
    public Guid? AppId { get; init; }
    public string? AppName { get; init; }

    /// <summary>Bound environment id, when the secret is environment-scoped (null = shared).</summary>
    public Guid? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }

    /// <summary>
    /// When the secret expires. Null for an OAuth client whose expiry was not entered
    /// (such secrets cannot be tracked and are surfaced as "no expiry set").
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Short descriptor — certificate subject or OAuth provider/client id.</summary>
    public string? Detail { get; init; }

    public bool HasExpiry => ExpiresAt.HasValue;

    /// <summary>Whole days until expiry (negative once expired). Null when no expiry is set.</summary>
    public int? DaysUntilExpiry =>
        ExpiresAt.HasValue ? (int)Math.Floor((ExpiresAt.Value - DateTime.UtcNow).TotalDays) : null;

    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

    /// <summary>A human-readable scope label, e.g. "Acme / Production" or "Acme (shared)".</summary>
    public string ScopeLabel
    {
        get
        {
            string app = AppName ?? "—";
            string env = EnvironmentName is null ? "shared" : EnvironmentName;
            return EnvironmentName is null ? $"{app} ({env})" : $"{app} / {env}";
        }
    }

    public string TypeLabel => SecretType switch
    {
        VaultSecretType.Certificate => "Certificate",
        VaultSecretType.OAuthClient => "OAuth client",
        _ => SecretType.ToString()
    };
}
