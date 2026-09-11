using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Resolves the two values the EntKube Telemetry Collector cannot know on its own: the externally
/// reachable OTLP ingest URL and the per-cluster HMAC ingest token that binds (tenant, cluster).
///
/// The catalog ships both as literal <c>REPLACE_WITH_*</c> placeholders. A collector installed with the
/// placeholders still starts, still reports Ready, and still passes its health check — while every export
/// fails against a hostname that does not resolve. Nothing about the cluster looks wrong, and the Logs tab
/// is simply empty forever. So the placeholders are filled in for the operator instead of being left as a
/// manual copy-paste step: pre-filled when the component is registered (both from the Components tab and
/// from a blueprint bootstrap), and healed at install time for components registered before this existed.
/// </summary>
public static class TelemetryIngestDefaults
{
    /// <summary>Catalog key of the collector entry these defaults belong to.</summary>
    public const string CollectorKey = "otel-collector";

    /// <summary>Form-field keys on that entry (see ComponentCatalog).</summary>
    public const string EndpointFieldKey = "ingest-endpoint";
    public const string TokenFieldKey = "ingest-token";

    /// <summary>The full endpoint placeholder as it appears in the entry's default values.</summary>
    public const string EndpointPlaceholder = "https://REPLACE_WITH_ENTKUBE_URL/ingest/otlp";

    /// <summary>The bare host marker, in case the endpoint line was hand-edited around it.</summary>
    public const string HostMarker = "REPLACE_WITH_ENTKUBE_URL";

    /// <summary>The bearer-token placeholder in <c>config.extensions.bearertokenauth.token</c>.</summary>
    public const string TokenPlaceholder = "REPLACE_WITH_INGEST_TOKEN";

    /// <summary>Shown when there is no in-cluster destination — the one case an operator must fix by hand.</summary>
    public const string MissingUrlMessage =
        "The EntKube Telemetry Collector has no ingest URL: this cluster has no EntKube Telemetry Indexer, "
        + "and the collector no longer falls back to the management plane — a cluster's telemetry stays in "
        + "the cluster and is read from its own object storage. Install the EntKube Telemetry Indexer on "
        + "this cluster first (it is listed as a dependency), or set the component's \"Telemetry Ingest URL\" "
        + "field to an OTLP endpoint you run yourself.";

    /// <summary>True when this catalog entry is the telemetry collector.</summary>
    public static bool IsCollector(CatalogEntry? entry) => entry?.Key == CollectorKey;

    /// <summary>Vault secret name the token field stores under — read from the entry so it tracks the
    /// catalog rather than duplicating the name here.</summary>
    public static string TokenSecretName(CatalogEntry entry) =>
        entry.FormFields.FirstOrDefault(f => f.Key == TokenFieldKey)?.SecretName ?? TokenFieldKey;

    /// <summary>
    /// The management plane's own ingest base URL (<c>{PublicIngestUrl}/ingest/otlp</c>), or null when
    /// Telemetry:PublicIngestUrl is unset.
    ///
    /// <para>RECOGNITION ONLY. Nothing is configured to point here any more: a collector ships to the
    /// indexer in its own cluster, and this address exists so an older collector still pointed at the
    /// management plane can be identified — to repoint it, and to know that the data it sent is in the
    /// management plane's store rather than the cluster's. Do not reintroduce it as a destination.</para>
    /// </summary>
    public static string? IngestUrl(IConfiguration config)
    {
        string? baseUrl = config["Telemetry:PublicIngestUrl"];
        return string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/ingest/otlp";
    }

    /// <summary>
    /// Fills blank (or still-placeholder) ingest form values for the collector entry, leaving anything the
    /// operator actually typed alone. No-op for every other catalog entry. The URL is only filled when it
    /// can be derived — a blank field is still better than a placeholder, and the install-time check
    /// explains what to configure.
    /// </summary>
    /// <param name="inClusterIngestUrl">
    /// The cluster's own telemetry indexer, when one is installed. Preferred over the management plane's
    /// public URL: keeping a cluster's logs inside it is the entire point of that component, and it also
    /// removes the WAN hop, the egress cost and the need for the collector to reach EntKube at all.
    /// Resolved by the caller, which has database access. See docs/telemetry-in-cluster.md.
    /// </param>
    public static void ApplyTo(
        CatalogEntry entry, IDictionary<string, string> formValues,
        Guid clusterId, Guid tenantId, IngestTokenService tokens, IConfiguration config,
        string? inClusterIngestUrl = null)
    {
        if (!IsCollector(entry)) return;

        // Only ever the cluster's own indexer. There is deliberately no fallback to the management
        // plane's public ingest URL: filling one in is what made shipping telemetry out of the cluster
        // the default, silently, at registration time. With no indexer the field is left blank and the
        // install refuses with MissingUrlMessage, which names the component to install.
        formValues.TryGetValue(EndpointFieldKey, out string? endpoint);
        if (IsBlankOrPlaceholder(endpoint) && inClusterIngestUrl is { Length: > 0 } url)
            formValues[EndpointFieldKey] = url;

        formValues.TryGetValue(TokenFieldKey, out string? existingToken);
        if (IsBlankOrPlaceholder(existingToken))
            formValues[TokenFieldKey] = tokens.Mint(clusterId, tenantId);
    }

    /// <summary>
    /// Replaces any <c>REPLACE_WITH_*</c> placeholder left in a collector values document with the real
    /// ingest URL and token — the repair path for collectors registered before pre-filling existed, and the
    /// backstop for a vault that captured a placeholder as its "token". Values the operator set explicitly
    /// are left untouched. <c>MintedToken</c> is non-null only when the token placeholder was replaced, so
    /// the caller can repair the vault copy too.
    /// </summary>
    public static (string? Yaml, string? MintedToken) FillPlaceholders(
        string? valuesYaml, Guid clusterId, Guid tenantId, IngestTokenService tokens, IConfiguration config,
        string? inClusterIngestUrl = null)
    {
        if (string.IsNullOrEmpty(valuesYaml)) return (valuesYaml, null);

        bool needsUrl = valuesYaml.Contains(HostMarker, StringComparison.Ordinal);
        bool needsToken = valuesYaml.Contains(TokenPlaceholder, StringComparison.Ordinal);
        if (!needsUrl && !needsToken) return (valuesYaml, null);

        if (needsUrl)
        {
            // Fail loudly rather than install a collector that cannot reach anything — and fail rather
            // than substitute the management plane, which is what this used to do. The placeholder is
            // resolved to one address only: the indexer running in this cluster.
            string url = inClusterIngestUrl is { Length: > 0 } inCluster
                ? inCluster
                : throw new InvalidOperationException(MissingUrlMessage);
            valuesYaml = valuesYaml.Replace(EndpointPlaceholder, url, StringComparison.Ordinal);
            // A hand-edited endpoint line may keep only the bare host marker; its scheme is whatever the
            // document already carries, so substitute just the authority.
            if (valuesYaml.Contains(HostMarker, StringComparison.Ordinal))
                valuesYaml = valuesYaml.Replace(HostMarker, new Uri(url).Authority, StringComparison.Ordinal);
        }

        string? minted = null;
        if (needsToken)
        {
            minted = tokens.Mint(clusterId, tenantId);
            valuesYaml = valuesYaml.Replace(TokenPlaceholder, minted, StringComparison.Ordinal);
        }

        return (valuesYaml, minted);
    }

    /// <summary>YAML path of the collector's EntKube exporter endpoint.</summary>
    public const string EndpointYamlPath = "config.exporters.otlphttp/entkube.endpoint";

    /// <summary>
    /// Repoints an already-installed collector at the cluster's own telemetry indexer.
    ///
    /// This cannot be done when the collector is registered, because the indexer <i>depends on</i> the
    /// collector and is therefore always installed second — at registration time there is nothing to point
    /// at yet. So it happens on each install instead, and a collector re-applied after the indexer arrives
    /// starts shipping into the cluster.
    ///
    /// <para>Only a value EntKube itself generated is overwritten: the management plane's public ingest URL,
    /// or a blank/placeholder. Anything else is an operator's deliberate choice of destination and is left
    /// exactly as it is — repointing that would silently redirect their telemetry.</para>
    /// </summary>
    public static (string? Yaml, bool Repointed) RepointToInCluster(
        string? valuesYaml, string? inClusterIngestUrl, IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(inClusterIngestUrl) || string.IsNullOrEmpty(valuesYaml))
            return (valuesYaml, false);

        string? current = YamlFormMerger.ExtractValue(valuesYaml, EndpointYamlPath);
        if (string.Equals(current, inClusterIngestUrl, StringComparison.OrdinalIgnoreCase))
            return (valuesYaml, false);   // already there

        if (!IsManagementPlaneDestination(current, config)) return (valuesYaml, false);

        return (YamlFormMerger.MergeFormValues(valuesYaml,
            new Dictionary<string, string> { [EndpointYamlPath] = inClusterIngestUrl }), true);
    }

    /// <summary>
    /// Whether a collector endpoint is one EntKube itself produced to reach the management plane — the
    /// public ingest URL, or a blank/placeholder that has never been anywhere.
    ///
    /// <para>Two callers need exactly this question, and they must not answer it differently.
    /// <see cref="RepointToInCluster"/> asks it to decide whether the endpoint is EntKube's to rewrite;
    /// the read path asks it to decide whether the management plane's own store is still receiving this
    /// cluster's telemetry, and therefore whether that store or the cluster's node holds the data. An
    /// address the operator chose is neither ours to rewrite nor ours to assume about — and either way
    /// it is not the management plane, so the data is not here.</para>
    /// </summary>
    public static bool IsManagementPlaneDestination(string? endpoint, IConfiguration config) =>
        IsBlankOrPlaceholder(endpoint)
        || (IngestUrl(config) is string publicUrl
            && string.Equals(endpoint, publicUrl, StringComparison.OrdinalIgnoreCase));

    private static bool IsBlankOrPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains(HostMarker, StringComparison.Ordinal)
        || value.Contains(TokenPlaceholder, StringComparison.Ordinal);
}
