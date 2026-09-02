using System.Security.Cryptography;
using EntKube.Web.Data;
using EntKube.Web.Services.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Configures the in-cluster telemetry components (<c>entkube-telemetry-indexer</c> /
/// <c>entkube-telemetry-query</c>) from the platform's own state, so an operator picks a bucket from a
/// dropdown rather than copying credentials into a values file.
///
/// Mirrors <see cref="TempoService"/>: non-sensitive S3 settings go straight into the component's Helm
/// values, credentials become vault secrets injected at install time through the entry's hidden fields.
///
/// It also settles the two tokens. The <b>ingest</b> token is the cluster's existing
/// <see cref="IngestTokenService"/> token — the same value the OpenTelemetry Collector is already
/// configured with — so pointing the collector at the in-cluster indexer instead of the management plane
/// is purely an endpoint change and nothing has to be re-copied. The <b>query</b> token is derived from
/// (cluster, tenant) by <see cref="IngestTokenService.MintQuery"/>, so every component on a cluster — and
/// the management plane reading from them — computes the same value without it being copied anywhere.
/// </summary>
public class EntKubeTelemetryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IngestTokenService ingestTokens,
    IConfiguration configuration)
{
    /// <summary>Catalog keys of the two components this service configures.</summary>
    public const string IndexerKey = "entkube-telemetry-indexer";
    public const string QuerierKey = "entkube-telemetry-query";

    /// <summary>The chart both components install. A ClusterComponent records no catalog key, so this is
    /// how the two are recognised as siblings on the same cluster.</summary>
    public const string ChartName = "entkube-telemetry";

    /// <summary>Form-field key on the query entry carrying the indexer it federates with.</summary>
    public const string IndexerUrlFieldKey = "indexer-url";

    /// <summary>YAML path that field writes to (chart: <c>querier.indexerUrl</c>).</summary>
    public const string IndexerUrlYamlPath = "querier.indexerUrl";

    /// <summary>
    /// The indexer address for a release installed under the catalog's own default release name and
    /// namespace — and, not coincidentally, the literal the query component used to hard-code.
    ///
    /// <para>Treated as EntKube-generated rather than operator-chosen, so a querier carrying it is
    /// repointed at whatever indexer this cluster actually has. For the default naming that is a no-op;
    /// for an indexer released under any other name it is the correction that makes the querier work.</para>
    ///
    /// <para>A const because <c>[InlineData]</c> needs one. A test pins it to
    /// <see cref="IndexerServiceUrl"/> of the indexer entry's defaults, so it cannot drift from the
    /// chart.</para>
    /// </summary>
    public const string DefaultIndexerUrl = "http://entkube-telemetry-indexer.monitoring:8080";

    /// <summary>The port both roles serve on — the chart's <c>service.port</c>.</summary>
    private const int NodePort = 8080;

    /// <summary>Vault secret names matching the hidden fields on both catalog entries.</summary>
    public const string IngestTokenSecret = "telemetry-ingest-token";
    public const string QueryTokenSecret = "telemetry-query-token";
    public const string S3AccessKeySecret = "telemetry-s3-access-key";
    public const string S3SecretKeySecret = "telemetry-s3-secret-key";

    /// <summary>
    /// Fills in everything the operator should not have to: the cluster and tenant identity the node
    /// refuses to start without, and both bearer tokens. Called on registration for either component.
    /// </summary>
    public async Task ConfigureIdentityAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        // The node validates the presented bearer against this literal value, so it must be the very token
        // the collector sends. Mint is deterministic per (cluster, tenant), so this is that same token.
        string ingestToken = ingestTokens.Mint(component.ClusterId, tenantId);

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", new Dictionary<string, string>
        {
            ["node.tenantId"] = tenantId.ToString(),
            ["node.clusterId"] = component.ClusterId.ToString(),
        });
        await db.SaveChangesAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);
        await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, IngestTokenSecret, ingestToken, ct);

        // Derived from (cluster, tenant), so every telemetry component on this cluster computes the same
        // token without anything being copied between them — and so the management plane can compute it
        // too rather than looking up whichever component row it happened to find first.
        string queryToken = ingestTokens.MintQuery(component.ClusterId, tenantId);
        await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, QueryTokenSecret, queryToken, ct);
    }

    /// <summary>True when this catalog entry is one of the two in-cluster telemetry components.</summary>
    public static bool IsTelemetryNode(CatalogEntry? entry) =>
        entry?.Key is IndexerKey or QuerierKey;

    /// <summary>
    /// Fills any identity or token the component's values are missing, at install time.
    ///
    /// The repair path for components registered before <see cref="ConfigureIdentityAsync"/> ran on their
    /// registration route — the same reason <c>TelemetryIngestDefaults.FillPlaceholders</c> exists. Here
    /// the failure is at least loud: the chart refuses to render without an identity rather than deploying
    /// a node that does not know whose data it holds. Loud is better than silent, but one Apply fixing it
    /// is better than re-registering the component.
    ///
    /// Anything already set is left alone, so an operator who supplied a value keeps it.
    /// </summary>
    public async Task<string?> FillMissingIdentityAsync(
        ClusterComponent component, string? valuesYaml, CancellationToken ct = default)
    {
        Guid tenantId = component.Cluster.TenantId;
        Dictionary<string, string> missing = [];

        if (IsBlank(valuesYaml, "node.tenantId")) missing["node.tenantId"] = tenantId.ToString();
        if (IsBlank(valuesYaml, "node.clusterId")) missing["node.clusterId"] = component.ClusterId.ToString();

        // Deterministic per (cluster, tenant) — the very token the collector already presents, so the
        // node accepts its exports without anything being re-copied.
        if (IsBlank(valuesYaml, "node.ingestToken"))
            missing["node.ingestToken"] = ingestTokens.Mint(component.ClusterId, tenantId);

        // Unlike the rest, this is corrected rather than merely filled. It is a hidden, EntKube-issued
        // value that every component on the cluster must agree on, and the management plane derives the
        // same one to read with — so a stale token stored by an earlier install is exactly what produces
        // "401 Unauthorized" from a node that is otherwise healthy.
        string expected = ingestTokens.MintQuery(component.ClusterId, tenantId);
        if (!string.Equals(YamlFormMerger.ExtractValue(valuesYaml ?? "", "node.queryToken"), expected, StringComparison.Ordinal))
        {
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetComponentSecretAsync(tenantId, component.Id, QueryTokenSecret, expected, ct);
            missing["node.queryToken"] = expected;
        }

        return missing.Count == 0
            ? valuesYaml
            : YamlFormMerger.MergeFormValues(valuesYaml ?? "", missing);
    }

    private static bool IsBlank(string? yaml, string dotPath) =>
        string.IsNullOrWhiteSpace(yaml) || string.IsNullOrWhiteSpace(YamlFormMerger.ExtractValue(yaml, dotPath));

    /// <summary>
    /// Points the component at a storage link for its sealed segments. Without one the node seals to its
    /// own volume, which means sealed history dies with the volume and a querier cannot read it at all.
    /// </summary>
    public async Task WriteStorageHelmValuesAsync(
        Guid tenantId, Guid clusterComponentId, Guid storageLinkId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        ClusterComponent component = await db.ClusterComponents
            .Include(c => c.Cluster)
            .FirstOrDefaultAsync(c => c.Id == clusterComponentId && c.Cluster.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Component not found.");

        StorageLink link = await db.StorageLinks
            .FirstOrDefaultAsync(s => s.Id == storageLinkId && s.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException("Storage link not found.");

        string region = link.Region ?? "us-east-1";
        (string endpointHost, bool insecure) = S3EndpointUtil.Normalize(link.Endpoint, region);

        component.HelmValues = YamlFormMerger.MergeFormValues(component.HelmValues ?? "", new Dictionary<string, string>
        {
            ["objectStorage.bucket"] = link.BucketName ?? "",
            // The engine's S3 client takes a full URL, unlike Tempo's host-only form.
            ["objectStorage.endpoint"] = string.IsNullOrEmpty(endpointHost)
                ? ""
                : (insecure ? "http://" : "https://") + endpointHost,
            ["objectStorage.region"] = region,
            // Required by MinIO and Ceph RGW, which do not support virtual-host bucket addressing.
            ["objectStorage.forcePathStyle"] = "true",
        });
        await db.SaveChangesAsync(ct);

        await vaultService.InitializeVaultAsync(tenantId, ct);
        // Straight from the vault rather than through StorageService: that service carries a long
        // dependency chain (OpenStack, egress relays, k8s clients) for the sake of two secret reads, and
        // pulling it in here would drag all of it into anything that installs a component.
        string accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, storageLinkId, "ACCESS_KEY", ct) ?? "";
        string secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, storageLinkId, "SECRET_KEY", ct) ?? "";

        if (!string.IsNullOrEmpty(accessKey))
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, S3AccessKeySecret, accessKey, ct);
        if (!string.IsNullOrEmpty(secretKey))
            await vaultService.SetComponentSecretAsync(tenantId, clusterComponentId, S3SecretKeySecret, secretKey, ct);
    }

    /// <summary>
    /// The chart's own fullname for a release: the release name, prefixed with the chart name only when it
    /// does not already contain it. This mirrors <c>entkube-telemetry.fullname</c> in the chart's
    /// _helpers.tpl, including its 63-character truncation, and the two are pinned together by a test that
    /// renders the chart — a name derived here that the chart does not render is a hostname that resolves
    /// nowhere, and DNS is the only place the mismatch ever shows up.
    ///
    /// <para>Assumes no <c>nameOverride</c> or <c>fullnameOverride</c>, neither of which the catalog
    /// sets. An operator who sets one by hand also has to set the querier's indexer URL by hand.</para>
    /// </summary>
    public static string Fullname(string release) =>
        Truncate63(release.Contains(ChartName, StringComparison.Ordinal) ? release : $"{release}-{ChartName}");

    /// <summary>
    /// In-cluster base URL of the indexer Service a release named <paramref name="release"/> in
    /// <paramref name="ns"/> renders — <c>{fullname}-indexer</c>, matching the chart's
    /// <c>entkube-telemetry.indexerName</c>.
    /// </summary>
    public static string IndexerServiceUrl(string release, string? ns) =>
        $"http://{Truncate63($"{Fullname(release)}-indexer")}"
        + $".{(string.IsNullOrWhiteSpace(ns) ? "monitoring" : ns)}:{NodePort}";

    /// <summary>Helm's <c>trunc 63 | trimSuffix "-"</c>, which every name in the chart passes through.</summary>
    private static string Truncate63(string value) =>
        (value.Length > 63 ? value[..63] : value).TrimEnd('-');

    /// <summary>
    /// The in-cluster base URL of this cluster's telemetry indexer, or null when none is installed on it.
    ///
    /// The Service name follows the chart's own naming rather than being looked up live, because this is
    /// called while REGISTERING a component — potentially before the indexer's Service exists, and
    /// certainly before anything has been applied.
    /// </summary>
    public async Task<string?> GetInClusterIndexerUrlAsync(Guid clusterId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Matched on the catalog key, NOT on the chart name: the query component installs the SAME chart
        // with indexer.enabled=false, so a chart-name match can return the querier's row and derive an
        // indexer address inside a release that renders no indexer at all.
        var indexer = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId && c.Name == IndexerKey)
            .Select(c => new { c.ReleaseName, c.Name, c.Namespace })
            .FirstOrDefaultAsync(ct);
        if (indexer is null) return null;

        return IndexerServiceUrl(indexer.ReleaseName ?? indexer.Name, indexer.Namespace);
    }

    /// <summary>
    /// The in-cluster OTLP endpoint for this cluster, or null when no telemetry indexer is installed on it.
    /// Used to point the OpenTelemetry Collector at the indexer beside it instead of across the WAN.
    /// </summary>
    public async Task<string?> GetInClusterIngestUrlAsync(Guid clusterId, CancellationToken ct = default)
    {
        return await GetInClusterIndexerUrlAsync(clusterId, ct) is string url
            ? url + "/ingest/otlp"
            : null;
    }

    /// <summary>
    /// Pre-fills the query component's "Indexer URL" field from the indexer actually installed on this
    /// cluster. The catalog cannot carry a correct literal here: the address contains the indexer's
    /// RELEASE name, which the operator chooses, so the only honest default is a derived one.
    /// No-op for every other catalog entry, and for a value the operator typed themselves.
    /// </summary>
    public static void ApplyIndexerUrlDefault(
        CatalogEntry entry, IDictionary<string, string> formValues, string? indexerUrl)
    {
        if (entry.Key != QuerierKey || string.IsNullOrWhiteSpace(indexerUrl)) return;

        formValues.TryGetValue(IndexerUrlFieldKey, out string? current);
        if (IsOursToCorrect(current)) formValues[IndexerUrlFieldKey] = indexerUrl;
    }

    /// <summary>
    /// Corrects a query component whose stored values point at an indexer that does not exist, at install
    /// time — the repair path for anything registered while the catalog shipped a hard-coded default.
    ///
    /// <para>Only a value EntKube itself produced is touched: blank, or the literal legacy default that
    /// never resolved anywhere. Any other address is an operator's deliberate choice of which indexer to
    /// federate with, and silently repointing it would be worse than the bug.</para>
    ///
    /// <para>Returns the values document and, when it changed, the URL now written — so the caller can say
    /// so in the install log rather than leaving a silent edit.</para>
    /// </summary>
    public async Task<(string? Yaml, string? Corrected)> FixQuerierIndexerUrlAsync(
        ClusterComponent component, string? valuesYaml, CancellationToken ct = default)
    {
        if (component.Name != QuerierKey) return (valuesYaml, null);

        if (await GetInClusterIndexerUrlAsync(component.ClusterId, ct) is not string derived)
            return (valuesYaml, null);

        string? current = YamlFormMerger.ExtractValue(valuesYaml ?? "", IndexerUrlYamlPath);
        if (string.Equals(current, derived, StringComparison.OrdinalIgnoreCase)) return (valuesYaml, null);
        if (!IsOursToCorrect(current)) return (valuesYaml, null);

        return (YamlFormMerger.MergeFormValues(valuesYaml ?? "",
            new Dictionary<string, string> { [IndexerUrlYamlPath] = derived }), derived);
    }

    private static bool IsOursToCorrect(string? url) =>
        string.IsNullOrWhiteSpace(url)
        || string.Equals(url, DefaultIndexerUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this cluster's OpenTelemetry Collector is still exporting to the management plane — which
    /// is the same question as "does the management plane's own store still hold this cluster's data".
    ///
    /// <para>Installing the indexer moves READS to it at once, because the read path finds it simply by
    /// existing. It does not move WRITES: the collector only changes destination when it is itself
    /// re-applied. In the gap between the two the node is empty while the data continues arriving here,
    /// and an empty node answers <i>successfully</i> — so every log and trace view goes blank with
    /// nothing anywhere reporting an error. That is the failure this exists to prevent: reads follow
    /// the data rather than the presence of a component.</para>
    ///
    /// <para>Returns null when the cluster has no installed collector at all. Then there is nothing to
    /// conclude — something else may be exporting straight to the node — and the caller keeps its
    /// default.</para>
    /// </summary>
    public async Task<bool?> ManagementPlaneStillReceivesAsync(Guid clusterId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Projected into an anonymous type, not straight to the string: HelmValues is nullable, so a
        // bare Select cannot tell "no collector on this cluster" from "a collector with no values".
        var collector = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId
                        && c.Name == TelemetryIngestDefaults.CollectorKey
                        && c.Status == ComponentStatus.Installed)
            .Select(c => new { c.HelmValues })
            .FirstOrDefaultAsync(ct);

        if (collector is null) return null;

        string? endpoint = YamlFormMerger.ExtractValue(
            collector.HelmValues ?? "", TelemetryIngestDefaults.EndpointYamlPath);

        return TelemetryIngestDefaults.IsManagementPlaneDestination(endpoint, configuration);
    }
}
