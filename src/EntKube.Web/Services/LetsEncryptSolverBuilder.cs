using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Builds the <c>spec.acme.solvers</c> list for the Let's Encrypt ClusterIssuer
/// catalog component from its form-field values. Both an HTTP-01 (Gateway API)
/// solver and a DNS-01 solver can be produced, and DNS-01 supports multiple
/// providers (Azure DNS, Cloudflare, AWS Route53, Google Cloud DNS) — not just
/// Azure. The issuer's DefaultValues therefore ships with NO solvers; this
/// helper appends whatever the operator selected.
///
/// Centralised here so every merge site (interactive add, edit, and blueprint
/// bootstrap registration) produces identical output. Provider credential
/// secrets are stored in the vault and synced to K8s Secrets by the normal
/// StoreAsSecret machinery; the <c>...SecretRef.name</c> matches each field's
/// KubernetesSecretName and <c>...SecretRef.key</c> matches its SecretName
/// (the K8s Secret data key equals the vault secret name — see
/// ComponentLifecycleService.SyncComponentSecretsAsync).
/// </summary>
public static class LetsEncryptSolverBuilder
{
    public const string CatalogKey = "letsencrypt-issuer";

    // Fixed K8s Secret name / key pairs each provider's credential syncs to. These
    // must match the corresponding StoreAsSecret form fields in ComponentCatalog.
    private const string AzureSecretName = "azuredns-config";
    private const string AzureSecretKey = "azuredns-client-secret";
    private const string CloudflareSecretName = "cloudflare-api-token-secret";
    private const string CloudflareSecretKey = "cloudflare-api-token";
    private const string Route53SecretName = "route53-credentials";
    private const string Route53SecretKey = "route53-secret-access-key";
    private const string GoogleSecretName = "clouddns-sa";
    private const string GoogleSecretKey = "clouddns-service-account";

    /// <summary>
    /// Returns <paramref name="yaml"/> with the selected ACME solvers merged in.
    /// Falls back to an HTTP-01 solver when neither challenge type is enabled so a
    /// usable issuer is always produced.
    /// </summary>
    public static string Apply(
        string yaml,
        IReadOnlyDictionary<string, string> formValues,
        IEnumerable<ClusterComponent> existingComponents)
    {
        bool http01 = GetBool(formValues, "enable-http01", defaultValue: true);
        bool dns01 = GetBool(formValues, "enable-dns01", defaultValue: false);
        if (!http01 && !dns01)
        {
            http01 = true;
        }

        Dictionary<string, string> paths = new();
        int idx = 0;

        if (http01)
        {
            (string gwName, string gwNamespace) = ExternalRouteService.ResolveGateway(existingComponents);
            string p = $"spec.acme.solvers.{idx}.http01.gatewayHTTPRoute.parentRefs.0.";
            paths[p + "name"] = gwName;
            paths[p + "namespace"] = gwNamespace;
            paths[p + "kind"] = "Gateway";
            idx++;
        }

        if (dns01)
        {
            string provider = Get(formValues, "dns-provider", "azure");
            string b = $"spec.acme.solvers.{idx}.dns01.";

            switch (provider)
            {
                case "cloudflare":
                    paths[b + "cloudflare.apiTokenSecretRef.name"] = CloudflareSecretName;
                    paths[b + "cloudflare.apiTokenSecretRef.key"] = CloudflareSecretKey;
                    break;

                case "route53":
                    SetIfPresent(paths, formValues, b + "route53.region", "route53-region");
                    SetIfPresent(paths, formValues, b + "route53.accessKeyID", "route53-access-key-id");
                    SetIfPresent(paths, formValues, b + "route53.hostedZoneID", "route53-hosted-zone-id");
                    paths[b + "route53.secretAccessKeySecretRef.name"] = Route53SecretName;
                    paths[b + "route53.secretAccessKeySecretRef.key"] = Route53SecretKey;
                    break;

                case "google":
                    SetIfPresent(paths, formValues, b + "cloudDNS.project", "clouddns-project");
                    paths[b + "cloudDNS.serviceAccountSecretRef.name"] = GoogleSecretName;
                    paths[b + "cloudDNS.serviceAccountSecretRef.key"] = GoogleSecretKey;
                    break;

                default: // azure
                    SetIfPresent(paths, formValues, b + "azureDNS.hostedZoneName", "dns-zone");
                    SetIfPresent(paths, formValues, b + "azureDNS.resourceGroupName", "dns-resource-group");
                    SetIfPresent(paths, formValues, b + "azureDNS.subscriptionID", "dns-subscription-id");
                    SetIfPresent(paths, formValues, b + "azureDNS.tenantID", "dns-tenant-id");
                    SetIfPresent(paths, formValues, b + "azureDNS.clientID", "dns-client-id");
                    paths[b + "azureDNS.environment"] = "AzurePublicCloud";
                    paths[b + "azureDNS.clientSecretSecretRef.name"] = AzureSecretName;
                    paths[b + "azureDNS.clientSecretSecretRef.key"] = AzureSecretKey;
                    break;
            }
        }

        return paths.Count == 0 ? yaml : YamlFormMerger.MergeFormValues(yaml, paths);
    }

    /// <summary>
    /// Detects which challenge types / DNS provider an already-installed issuer uses
    /// from its stored Helm values, so the edit form reflects reality instead of
    /// resetting to defaults. Writes the derived toggle/provider values into
    /// <paramref name="fieldValues"/>.
    /// </summary>
    public static void PopulateEditValues(string? helmValues, IDictionary<string, string> fieldValues)
    {
        string yaml = helmValues ?? "";
        fieldValues["enable-http01"] = yaml.Contains("http01") ? "true" : "false";
        fieldValues["enable-dns01"] = yaml.Contains("dns01") ? "true" : "false";

        if (yaml.Contains("cloudflare")) fieldValues["dns-provider"] = "cloudflare";
        else if (yaml.Contains("route53")) fieldValues["dns-provider"] = "route53";
        else if (yaml.Contains("cloudDNS")) fieldValues["dns-provider"] = "google";
        else if (yaml.Contains("azureDNS")) fieldValues["dns-provider"] = "azure";
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> f, string key, bool defaultValue)
        => f.TryGetValue(key, out string? v) ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : defaultValue;

    private static string Get(IReadOnlyDictionary<string, string> f, string key, string fallback)
        => f.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    private static void SetIfPresent(
        Dictionary<string, string> paths, IReadOnlyDictionary<string, string> f, string path, string key)
    {
        if (f.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v))
        {
            paths[path] = v;
        }
    }
}
