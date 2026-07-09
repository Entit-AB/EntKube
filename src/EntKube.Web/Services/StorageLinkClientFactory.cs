using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Builds an S3 client for a <see cref="StorageLink"/> — the single place that turns a storage link
/// (AWS S3, Cleura S3, MinIO) plus its vault-held credentials into an <see cref="AmazonS3Client"/>.
/// Shared by <see cref="StorageBrowserService"/> (file browsing) and the telemetry segment engine's
/// object-storage blob store, so every S3 access in the app goes through one provider-aware factory.
///
/// MinIO links whose endpoint is a cluster-internal service URL are routed through the Kubernetes API
/// server proxy; AWS S3 uses a regional endpoint; Cleura / custom-endpoint S3 uses ServiceURL with
/// path-style addressing. Credentials come from the vault (ACCESS_KEY / SECRET_KEY per storage link).
/// </summary>
public sealed class StorageLinkClientFactory(VaultService vaultService, IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// Creates an S3 client for <paramref name="link"/>. Returns the client plus an optional
    /// <see cref="IDisposable"/> to clean up (non-null only for MinIO K8s-proxy clients).
    /// </summary>
    public async Task<(AmazonS3Client Client, IDisposable? Cleanup)> CreateClientAsync(
        Guid tenantId, StorageLink link, CancellationToken ct = default)
    {
        string? accessKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, link.Id, "ACCESS_KEY", ct);
        string? secretKey = await vaultService.GetStorageLinkSecretValueAsync(tenantId, link.Id, "SECRET_KEY", ct);

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException(
                "Storage credentials not found in vault. Ensure ACCESS_KEY and SECRET_KEY are configured.");

        // MinIO / CubeFS: cluster-hosted S3 gateways — proxy via the K8s API server so
        // cluster-internal service URLs are reachable.
        if (link.Provider is StorageProvider.MinIO or StorageProvider.CubeFS && link.Endpoint is not null)
        {
            string? kubeconfig = await ResolveKubeconfigAsync(link, ct);
            if (!string.IsNullOrWhiteSpace(kubeconfig))
            {
                ProxiedS3Client? proxied = await KubernetesS3Proxy.OpenAsync(kubeconfig, link.Endpoint, accessKey, secretKey, ct: ct);
                if (proxied is not null)
                    return (proxied.S3, proxied);
            }
        }

        BasicAWSCredentials credentials = new(accessKey, secretKey);

        // Standard AWS S3 — regional endpoint, no custom ServiceURL.
        if (link.Provider == StorageProvider.AwsS3 && string.IsNullOrWhiteSpace(link.Endpoint))
        {
            AmazonS3Config config = new()
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(link.Region ?? "us-east-1"),
                Timeout = TimeSpan.FromSeconds(30),
                MaxErrorRetry = 1,
            };
            return (new AmazonS3Client(credentials, config), null);
        }

        // Cleura S3 / AWS with a custom endpoint — force path-style addressing.
        string endpoint = link.Endpoint
            ?? throw new InvalidOperationException("Endpoint URL is required for this storage provider.");

        AmazonS3Config s3Config = new()
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = link.Region ?? "us-east-1",
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            Timeout = TimeSpan.FromSeconds(30),
            MaxErrorRetry = 1,
        };
        return (new AmazonS3Client(credentials, s3Config), null);
    }

    /// <summary>
    /// Resolves the kubeconfig for the cluster that hosts a MinIO storage link. Uses ComponentId when set;
    /// falls back to a namespace-matched query for older links registered before ComponentId tracking.
    /// </summary>
    private async Task<string?> ResolveKubeconfigAsync(StorageLink link, CancellationToken ct)
    {
        if (KubernetesS3Proxy.ParseInternalServiceUrl(link.Endpoint ?? "") is null)
            return null;

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (link.ComponentId is not null)
        {
            // Project the cluster entity (not the unmapped Kubeconfig column) so the materialization
            // interceptor resolves the kubeconfig from the vault.
            KubernetesCluster? cluster = await db.ClusterComponents
                .Where(c => c.Id == link.ComponentId.Value)
                .Select(c => c.Cluster)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(cluster?.Kubeconfig))
                return cluster.Kubeconfig;
        }

        // Fallback: a MinIO component on any cluster for this tenant, preferring the endpoint's namespace.
        var parsed = KubernetesS3Proxy.ParseInternalServiceUrl(link.Endpoint ?? "");
        IQueryable<ClusterComponent> query = db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.Cluster.TenantId == link.TenantId
                && (c.Name == "minio-operator" || c.Name == "minio" || c.Name == "cubefs")
                && c.Status == ComponentStatus.Installed
                && c.Cluster.KubeconfigSecretId != null);
        if (parsed is not null)
            query = query.OrderByDescending(c => c.Namespace == parsed.Value.Namespace);

        ClusterComponent? component = await query.FirstOrDefaultAsync(ct);
        return component?.Cluster?.Kubeconfig;
    }
}
