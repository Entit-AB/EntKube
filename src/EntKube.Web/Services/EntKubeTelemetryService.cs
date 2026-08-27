using System.Security.Cryptography;
using EntKube.Web.Data;
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
/// is purely an endpoint change and nothing has to be re-copied. The <b>query</b> token is minted here,
/// randomly, because it grants read access to raw log bodies and should not be derivable from a cluster id.
/// </summary>
public class EntKubeTelemetryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    IngestTokenService ingestTokens)
{
    /// <summary>Catalog keys of the two components this service configures.</summary>
    public const string IndexerKey = "entkube-telemetry-indexer";
    public const string QuerierKey = "entkube-telemetry-query";

    /// <summary>The chart both components install. A ClusterComponent records no catalog key, so this is
    /// how the two are recognised as siblings on the same cluster.</summary>
    public const string ChartName = "entkube-telemetry";

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

        // Both components must present the SAME query token: the management plane uses it to read, and the
        // querier uses it to reach the indexer. Reuse the cluster's existing one if a sibling component
        // already minted it, so installing the second component does not lock the first out.
        string queryToken = await ResolveOrMintQueryTokenAsync(tenantId, component.ClusterId, clusterComponentId, ct);
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

        if (IsBlank(valuesYaml, "node.queryToken"))
        {
            // Must match whatever a sibling telemetry component on this cluster already holds: the querier
            // authenticates to the indexer with it, so a freshly minted one here would lock them apart.
            string token = await ResolveOrMintQueryTokenAsync(tenantId, component.ClusterId, component.Id, ct);
            await vaultService.InitializeVaultAsync(tenantId, ct);
            await vaultService.SetComponentSecretAsync(tenantId, component.Id, QueryTokenSecret, token, ct);
            missing["node.queryToken"] = token;
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
    /// The in-cluster OTLP endpoint for this cluster, or null when no telemetry indexer is installed on it.
    /// Used to point the OpenTelemetry Collector at the indexer beside it instead of across the WAN.
    ///
    /// The Service name follows the chart's own naming (release + chart + role) rather than being looked up
    /// live, because this is called while REGISTERING the collector — potentially before the indexer's
    /// Service exists, and certainly before anything has been applied.
    /// </summary>
    public async Task<string?> GetInClusterIngestUrlAsync(Guid clusterId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var indexer = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId && c.HelmChartName == ChartName)
            .Select(c => new { c.ReleaseName, c.Name, c.Namespace })
            .FirstOrDefaultAsync(ct);
        if (indexer is null) return null;

        string release = indexer.ReleaseName ?? indexer.Name;
        string ns = string.IsNullOrWhiteSpace(indexer.Namespace) ? "monitoring" : indexer.Namespace;
        return $"http://{release}-{ChartName}-indexer.{ns}:8080/ingest/otlp";
    }

    /// <summary>
    /// The cluster's telemetry query token: whatever a sibling telemetry component on the same cluster
    /// already holds, or a fresh random one. Both components on a cluster must agree on it — the querier
    /// authenticates to the indexer with it — so minting independently would leave them unable to talk.
    /// </summary>
    private async Task<string> ResolveOrMintQueryTokenAsync(
        Guid tenantId, Guid clusterId, Guid thisComponentId, CancellationToken ct)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<Guid> siblings = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId && c.Id != thisComponentId && c.HelmChartName == ChartName)
            .Select(c => c.Id)
            .ToListAsync(ct);

        foreach (Guid sibling in siblings)
        {
            string? existing = await vaultService.GetComponentSecretValueAsync(tenantId, sibling, QueryTokenSecret, ct);
            if (!string.IsNullOrEmpty(existing)) return existing;
        }

        // 32 bytes of CSPRNG output, base64url: this token reads raw log bodies, so it must not be
        // guessable from anything an attacker can see (a cluster id, a tenant slug, an install time).
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
