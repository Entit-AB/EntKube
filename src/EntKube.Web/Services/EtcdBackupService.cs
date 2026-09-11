using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>Whether a cluster is taking etcd snapshots, and where to.</summary>
public sealed record EtcdBackupStatus(bool Installed, string? Bucket, string? Schedule, string? Detail);

/// <summary>
/// Installs and removes the etcd snapshot job on a provisioned cluster.
///
/// <para><b>Why this is not part of the production baseline.</b> Every other baseline step can pick
/// a sensible default; this one cannot. The destination has to be storage the cluster does not
/// serve, and the obvious default — the CubeFS the baseline just installed on the cluster's own
/// nodes — is precisely the circular arrangement that makes the backups worthless: losing the
/// cluster loses the snapshots taken to recover it. So the target is chosen deliberately, from a
/// storage link, and a cluster without one is told it has no etcd backup rather than quietly given
/// a useless one.</para>
/// </summary>
public class EtcdBackupService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    StorageService storage,
    IKubernetesClientFactory k8s,
    ILogger<EtcdBackupService> logger)
{
    /// <summary>
    /// Installs the snapshot CronJob against a storage link, refusing one the cluster serves itself.
    /// </summary>
    public async Task<ClusterOperationResult> EnableAsync(
        Guid tenantId, ProvisionedCluster spec, Guid storageLinkId, string schedule, string kubeconfig,
        CancellationToken ct = default)
    {
        StorageLink? link;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            link = await db.StorageLinks.FirstOrDefaultAsync(
                s => s.Id == storageLinkId && s.TenantId == tenantId, ct);
        }

        if (link is null)
        {
            return ClusterOperationResult.Failed("That storage link no longer exists.");
        }

        // The one refusal that matters here. A snapshot stored on the cluster it was taken from is
        // not a backup — it is a copy that dies with the original.
        if (await IsServedByClusterAsync(link, spec, ct))
        {
            return ClusterOperationResult.Failed(
                $"'{link.Name}' is served by this cluster, so its snapshots would be lost with the "
                + "cluster they exist to restore. Pick storage that lives somewhere else.");
        }

        if (string.IsNullOrWhiteSpace(link.BucketName))
        {
            return ClusterOperationResult.Failed($"'{link.Name}' has no bucket, so there is nowhere to write.");
        }

        (string accessKey, string secretKey) = await storage.GetStoredCredentialsInternalAsync(tenantId, link.Id, ct);
        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            return ClusterOperationResult.Failed(
                $"'{link.Name}' has no stored credentials, so the job would fail every night without saying why.");
        }

        EtcdBackupTarget target = new()
        {
            Endpoint = link.Endpoint ?? "",
            Bucket = link.BucketName,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = link.Region ?? "us-east-1"
        };

        await k8s.ApplyManifestAsync(EtcdBackupManifest.Build(spec.Name, target, schedule), kubeconfig, ct);

        logger.LogInformation(
            "Enabled etcd snapshots for cluster {Cluster} to {Bucket} on schedule {Schedule}",
            spec.Name, target.Bucket, schedule);

        return ClusterOperationResult.Ok(
            $"etcd snapshots enabled to '{link.Name}' ({schedule}). The first one runs at the next "
            + "scheduled time; each is verified before it is uploaded.");
    }

    public async Task<ClusterOperationResult> DisableAsync(string kubeconfig, CancellationToken ct = default)
    {
        await k8s.DeleteManifestAsync("cronjob", EtcdBackupManifest.JobName, EtcdBackupManifest.Namespace, kubeconfig, ct);
        await k8s.DeleteManifestAsync("secret", EtcdBackupManifest.SecretName, EtcdBackupManifest.Namespace, kubeconfig, ct);

        return ClusterOperationResult.Ok(
            "etcd snapshots disabled. Snapshots already taken are left where they are.");
    }

    /// <summary>
    /// Whether the job is installed, read from the cluster rather than from anything EntKube stored
    /// — the question being asked is "are snapshots actually being taken", and a local record of
    /// having once installed it does not answer that.
    /// </summary>
    public async Task<EtcdBackupStatus> GetStatusAsync(string kubeconfig, CancellationToken ct = default)
    {
        try
        {
            string json = await k8s.GetJsonAsync("cronjobs.batch", EtcdBackupManifest.Namespace, kubeconfig, ct: ct);
            return EtcdBackupReader.Read(json, EtcdBackupManifest.JobName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the etcd snapshot job");
            return new EtcdBackupStatus(false, null, null, "The cluster could not be asked.");
        }
    }

    /// <summary>
    /// True when the storage link is backed by a component running on this very cluster — the CubeFS
    /// or MinIO its own foundation installed. Resolved through the component, which is the only
    /// thing that says where the storage physically is.
    /// </summary>
    private async Task<bool> IsServedByClusterAsync(StorageLink link, ProvisionedCluster spec, CancellationToken ct)
    {
        if (link.ComponentId is not Guid componentId || spec.KubernetesClusterId is not Guid clusterId)
        {
            return false;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterComponents
            .AnyAsync(c => c.Id == componentId && c.ClusterId == clusterId, ct);
    }
}

/// <summary>Reading the snapshot job's state out of what the cluster returns. Pure, so it is testable.</summary>
public static class EtcdBackupReader
{
    public static EtcdBackupStatus Read(string cronJobsJson, string jobName)
    {
        if (string.IsNullOrWhiteSpace(cronJobsJson))
        {
            return new EtcdBackupStatus(false, null, null, "No etcd snapshot job is installed.");
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(cronJobsJson);
            if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items)
                || items.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return new EtcdBackupStatus(false, null, null, "No etcd snapshot job is installed.");
            }

            foreach (System.Text.Json.JsonElement job in items.EnumerateArray())
            {
                if (!job.TryGetProperty("metadata", out System.Text.Json.JsonElement metadata)
                    || !metadata.TryGetProperty("name", out System.Text.Json.JsonElement name)
                    || name.GetString() != jobName)
                {
                    continue;
                }

                string? schedule = job.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                    && spec.TryGetProperty("schedule", out System.Text.Json.JsonElement s)
                        ? s.GetString()
                        : null;

                string? lastRun = job.TryGetProperty("status", out System.Text.Json.JsonElement status)
                    && status.TryGetProperty("lastSuccessfulTime", out System.Text.Json.JsonElement last)
                        ? last.GetString()
                        : null;

                // A job that exists but has never succeeded is worth distinguishing from one that
                // has: the first is an arrangement, the second is a backup.
                string detail = lastRun is null
                    ? "Installed, but no snapshot has succeeded yet."
                    : $"Last successful snapshot {lastRun}.";

                return new EtcdBackupStatus(true, null, schedule, detail);
            }

            return new EtcdBackupStatus(false, null, null, "No etcd snapshot job is installed.");
        }
        catch (System.Text.Json.JsonException)
        {
            return new EtcdBackupStatus(false, null, null, "The cluster's reply could not be read.");
        }
    }
}
