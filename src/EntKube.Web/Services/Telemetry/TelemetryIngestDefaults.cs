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

    /// <summary>Shown when the management plane has no public ingest URL configured.</summary>
    public const string MissingUrlMessage =
        "The EntKube Telemetry Collector has no ingest URL: Telemetry:PublicIngestUrl is not set, so "
        + "EntKube does not know the address your clusters can reach it at. Set it to this installation's "
        + "externally reachable base URL (the collector appends /ingest/otlp), or set the component's "
        + "\"Telemetry Ingest URL\" field to an OTLP endpoint you run yourself.";

    /// <summary>
    /// Recognises an address EntKube generated to reach a telemetry indexer inside a cluster.
    ///
    /// <para>The address EntKube generates is <c>{fullname}-indexer.{namespace}:8080/ingest/otlp</c>
    /// (see <see cref="EntKubeTelemetryService.IndexerServiceUrl"/>), so the test is the SERVICE NAME —
    /// the host's first DNS label — ending in <c>-indexer</c>, together with our own
    /// <c>/ingest/otlp</c> path. Testing the whole host would never match, because the namespace follows
    /// it. Both halves are required: an operator's own OTLP endpoint is a different path, and a host
    /// ending in <c>-indexer</c> may still be something of theirs.</para>
    ///
    /// <para>Used to decide what is ours to rewrite when moving a collector back to the management plane.
    /// An address they chose is never rewritten, whatever it points at — so the conservative answer here
    /// is <c>false</c>, and that is also what a release name long enough for helm's 63-character
    /// truncation to eat the <c>-indexer</c> suffix gets: a stale address left alone, rather than
    /// somebody's chosen destination silently redirected.</para>
    /// </summary>
    public static bool IsInClusterIndexerDestination(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string service = uri.Host.Split('.')[0];

        return uri.AbsolutePath.TrimEnd('/').EndsWith("/ingest/otlp", StringComparison.Ordinal)
            && service.EndsWith("-indexer", StringComparison.Ordinal);
    }

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
    /// <para>THE DESTINATION. A collector ships here, and the management plane indexes what arrives into
    /// its own segment store. It was recognition-only while clusters held their own telemetry; that is
    /// reversed — see docs/telemetry-central-again.md — and the read path still asks the same question of
    /// it, because an endpoint pointing here is what tells it the data is in this store.</para>
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
    public static void ApplyTo(
        CatalogEntry entry, IDictionary<string, string> formValues,
        Guid clusterId, Guid tenantId, IngestTokenService tokens, IConfiguration config)
    {
        if (!IsCollector(entry)) return;

        // The management plane, which is where telemetry is held again. Left blank when
        // Telemetry:PublicIngestUrl is unset — blank is better than a placeholder, and the install
        // refuses with MissingUrlMessage, which names the setting to fill in.
        formValues.TryGetValue(EndpointFieldKey, out string? endpoint);
        if (IsBlankOrPlaceholder(endpoint) && IngestUrl(config) is { Length: > 0 } url)
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
        string? valuesYaml, Guid clusterId, Guid tenantId, IngestTokenService tokens, IConfiguration config)
    {
        if (string.IsNullOrEmpty(valuesYaml)) return (valuesYaml, null);

        bool needsUrl = valuesYaml.Contains(HostMarker, StringComparison.Ordinal);
        bool needsToken = valuesYaml.Contains(TokenPlaceholder, StringComparison.Ordinal);
        if (!needsUrl && !needsToken) return (valuesYaml, null);

        if (needsUrl)
        {
            // Fail loudly rather than install a collector that cannot reach anything. The placeholder
            // resolves to the management plane's public ingest URL — the one address telemetry is held
            // at now — and its absence is a configuration gap MissingUrlMessage names.
            string url = IngestUrl(config) is { Length: > 0 } publicUrl
                ? publicUrl
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
    /// Moves an already-installed collector back to the management plane.
    ///
    /// <para>The migration path for every collector installed while clusters held their own telemetry: on
    /// the next apply it ships to EntKube again. New installs never take this route — the endpoint is the
    /// management plane's from registration onwards.</para>
    ///
    /// <para>Only a value EntKube itself generated is overwritten: an address it derived for an in-cluster
    /// indexer, or a blank/placeholder. Anything else is an operator's deliberate choice of destination and
    /// is left exactly as it is — repointing that would silently redirect their telemetry, which is the
    /// same discipline the previous direction held.</para>
    /// </summary>
    public static (string? Yaml, bool Repointed) RepointToManagementPlane(
        string? valuesYaml, IConfiguration config)
    {
        if (string.IsNullOrEmpty(valuesYaml) || IngestUrl(config) is not { Length: > 0 } publicUrl)
            return (valuesYaml, false);

        string? current = YamlFormMerger.ExtractValue(valuesYaml, EndpointYamlPath);
        if (string.Equals(current, publicUrl, StringComparison.OrdinalIgnoreCase))
            return (valuesYaml, false);   // already there

        if (!IsBlankOrPlaceholder(current) && !IsInClusterIndexerDestination(current))
            return (valuesYaml, false);

        return (YamlFormMerger.MergeFormValues(valuesYaml,
            new Dictionary<string, string> { [EndpointYamlPath] = publicUrl }), true);
    }

    /// <summary>
    /// Whether a collector endpoint is one EntKube itself produced to reach the management plane — the
    /// public ingest URL, or a blank/placeholder that has never been anywhere.
    ///
    /// <para>Two callers need exactly this question, and they must not answer it differently.
    /// <see cref="RepointToManagementPlane"/> asks it to decide whether the endpoint is already there;
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
