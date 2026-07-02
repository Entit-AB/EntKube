using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// A Kubernetes kubeconfig used by EntKube to authenticate against a registered
/// cluster. Stored encrypted as a JSON document inside
/// <see cref="EntKube.Web.Data.VaultSecret.EncryptedValue"/> when the secret's type
/// is <see cref="EntKube.Web.Data.VaultSecretType.Kubeconfig"/>.
///
/// Like an OAuth client secret, the expiry cannot be reliably derived from the value
/// (a kubeconfig may embed a short-lived client certificate, a token, or an exec
/// plugin), so <see cref="ExpiresAt"/> is entered manually and drives the same expiry
/// warnings as certificates and OAuth clients.
/// </summary>
public sealed class KubeconfigBundle
{
    /// <summary>The raw kubeconfig YAML content (the sensitive part).</summary>
    public string? ConfigYaml { get; set; }

    /// <summary>The kubeconfig context selected when registering the cluster.</summary>
    public string? ContextName { get; set; }

    /// <summary>The API server URL this kubeconfig connects to (informational).</summary>
    public string? ApiServerUrl { get; set; }

    /// <summary>
    /// When the kubeconfig's credential expires, as known by the operator. Manually
    /// entered. Null means "no expiry tracked" (surfaced as such by the expiry scanner).
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public bool HasConfig => !string.IsNullOrWhiteSpace(ConfigYaml);

    /// <summary>Projects the non-secret metadata for list/badge display.</summary>
    public KubeconfigInfo ToInfo() => new()
    {
        ContextName = ContextName,
        ApiServerUrl = ApiServerUrl,
        ExpiresAt = ExpiresAt,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static KubeconfigBundle Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<KubeconfigBundle>(json, JsonOptions) ?? new KubeconfigBundle();
        }
        catch
        {
            return new KubeconfigBundle();
        }
    }
}

/// <summary>
/// Non-secret metadata about a cluster kubeconfig, safe to surface in lists
/// (excludes the kubeconfig YAML itself).
/// </summary>
public sealed class KubeconfigInfo
{
    public string? ContextName { get; init; }
    public string? ApiServerUrl { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>Validation helpers for cluster kubeconfigs.</summary>
public static class KubeconfigHelper
{
    /// <summary>
    /// Lightweight structural validation — enough to reject an obviously wrong paste
    /// without pulling in a full YAML parser. A valid kubeconfig must contain the
    /// top-level <c>clusters</c>, <c>contexts</c>, and <c>users</c> keys.
    /// </summary>
    public static (bool Ok, string? Error) Validate(KubeconfigBundle b)
    {
        if (string.IsNullOrWhiteSpace(b.ConfigYaml))
        {
            return (false, "A kubeconfig is required.");
        }

        string yaml = b.ConfigYaml;
        bool looksLikeKubeconfig =
            yaml.Contains("clusters", StringComparison.OrdinalIgnoreCase)
            && yaml.Contains("contexts", StringComparison.OrdinalIgnoreCase)
            && yaml.Contains("users", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeKubeconfig)
        {
            return (false, "This does not look like a kubeconfig (missing clusters/contexts/users).");
        }

        return (true, null);
    }
}
