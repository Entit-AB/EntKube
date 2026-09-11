using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Keeps a provisioned cluster's two credentials current: the kubeconfig EntKube authenticates
/// with, and the OpenStack application credential the cluster authenticates to its cloud with.
///
/// <para><b>Why this needs doing at all.</b> Both expire, neither announces it, and both fail
/// completely rather than partially. Cluster API issues the admin kubeconfig with a one-year client
/// certificate and rotates its own copy as the expiry approaches — but EntKube took a copy at
/// provisioning time and would otherwise hold that one forever, locking itself out of a cluster
/// that is perfectly healthy. The application credential is the same shape of problem seen from the
/// other side: CAPO, the cloud-controller-manager and the CSI driver all authenticate with it, so
/// when it goes, node lifecycle, load balancers and volume provisioning stop together.</para>
/// </summary>
public class ClusterCredentialRotationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vaultService,
    OpenStackKeystoneClient keystone,
    IKubernetesClientFactory k8s,
    ILogger<ClusterCredentialRotationService> logger)
{
    /// <summary>Where Cluster API keeps the admin kubeconfig it issues and renews.</summary>
    private static string KubeconfigSecretName(string clusterName) => $"{clusterName}-kubeconfig";

    /// <summary>The in-cluster secret the cloud-controller-manager and Cinder CSI read.</summary>
    private const string CloudConfigSecretName = "cloud-config";

    private const string CloudConfigNamespace = "kube-system";

    /// <summary>
    /// Takes a fresh copy of the kubeconfig Cluster API holds, if it has moved on from the one in
    /// the vault.
    ///
    /// <para>CAPI rotates its own kubeconfig secret as the certificate nears expiry, so the fix is
    /// to read it again rather than to reissue anything. Compared by certificate expiry rather than
    /// by bytes: the file is regenerated on rotation, and comparing text would replace a working
    /// credential on every pass for no reason.</para>
    /// </summary>
    public async Task<ClusterOperationResult> RefreshKubeconfigAsync(
        Guid tenantId, ProvisionedCluster spec, CancellationToken ct = default)
    {
        if (spec.KubernetesClusterId is not Guid clusterId)
        {
            return ClusterOperationResult.Failed("This cluster has not been registered yet.");
        }

        string? current = await LoadStoredKubeconfigAsync(clusterId, ct);
        if (string.IsNullOrWhiteSpace(current))
        {
            return ClusterOperationResult.Failed("No kubeconfig is stored for this cluster.");
        }

        string? fetched;
        try
        {
            fetched = await ReadKubeconfigSecretAsync(spec.Name, current, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ClusterOperationResult.Failed(
                $"The cluster's kubeconfig secret could not be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(fetched))
        {
            return ClusterOperationResult.Failed(
                $"Cluster API has no '{KubeconfigSecretName(spec.Name)}' secret, which means this cluster's "
                + "CAPI state is not where it is expected to be.");
        }

        CertificateStatus currentStatus = ClusterCertificateExpiry.FromKubeconfig(current);
        CertificateStatus fetchedStatus = ClusterCertificateExpiry.FromKubeconfig(fetched);

        // Only take it if it actually buys time. An equal or earlier expiry means CAPI has not
        // rotated yet, and replacing a working credential with the same one is churn.
        bool worthTaking = fetchedStatus.NotAfter is DateTime fresh
            && (currentStatus.NotAfter is not DateTime held || fresh > held);

        if (!worthTaking)
        {
            return ClusterOperationResult.Ok(
                currentStatus.DaysRemaining is int days
                    ? $"The stored kubeconfig is still the current one ({days} day(s) left)."
                    : "The stored kubeconfig is still the current one.");
        }

        (bool ok, string? error, _) = await vaultService.SetClusterKubeconfigAsync(
            tenantId, clusterId,
            new KubeconfigBundle
            {
                ConfigYaml = fetched,
                ApiServerUrl = ExtractServer(fetched),
                ExpiresAt = fetchedStatus.NotAfter
            },
            updatedBy: "credential-rotation", ct);

        if (!ok)
        {
            return ClusterOperationResult.Failed(error ?? "The refreshed kubeconfig could not be stored.");
        }

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            KubernetesCluster? cluster = await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
            if (cluster is not null)
            {
                cluster.Kubeconfig = fetched;
                await db.SaveChangesAsync(ct);
            }
        }

        logger.LogInformation(
            "Refreshed the kubeconfig for cluster {Cluster}; now valid until {Expiry:yyyy-MM-dd}",
            spec.Name, fetchedStatus.NotAfter);

        return ClusterOperationResult.Ok(
            $"Took Cluster API's rotated kubeconfig, valid until {fetchedStatus.NotAfter:yyyy-MM-dd}.");
    }

    /// <summary>
    /// Issues a new OpenStack application credential and moves everything that authenticates with
    /// the old one onto it: CAPO's identity secret, and the <c>cloud-config</c> the
    /// cloud-controller-manager and Cinder CSI read.
    ///
    /// <para>The old credential is deliberately left in place. Revoking it in the same breath turns
    /// any component that has not re-read the secret yet into a hard failure, and the components
    /// here re-read at their own pace — so the old one is reported for removal once the cluster has
    /// been seen working on the new one.</para>
    /// </summary>
    public async Task<ClusterOperationResult> RotateCloudCredentialAsync(
        Guid tenantId, ProvisionedCluster spec, CancellationToken ct = default)
    {
        if (spec.KubernetesClusterId is not Guid clusterId)
        {
            return ClusterOperationResult.Failed("This cluster has not been registered yet.");
        }

        string? kubeconfig = await LoadStoredKubeconfigAsync(clusterId, ct);
        if (string.IsNullOrWhiteSpace(kubeconfig))
        {
            return ClusterOperationResult.Failed("No kubeconfig is stored for this cluster.");
        }

        OpenStackConnection connection;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            connection = await db.Set<OpenStackConnection>()
                .FirstOrDefaultAsync(c => c.Id == spec.OpenStackConnectionId && c.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("The cloud connection behind this cluster no longer exists.");
        }

        try
        {
            KeystoneSession session = await keystone.AuthenticateAsync(tenantId, spec.OpenStackConnectionId, ct);

            string credentialName = $"entkube-{spec.Name}-{DateTime.UtcNow:yyyyMMddHHmm}";
            ApplicationCredential credential = await keystone.CreateApplicationCredentialAsync(
                session, connection.AuthUrl, credentialName, ct);

            string cloudsYaml = CapiTemplateInputs.BuildCloudsYaml(connection, credential);
            string cloudConf = CapiTemplateInputs.BuildCloudConf(
                connection, credential, ProvisionedClusterService.ToConfig(spec));

            // CAPO's identity secret first: it is what keeps the cluster able to manage its own
            // machines, and it is the one whose failure is hardest to notice.
            await ApplySecretAsync(
                CapiTemplateInputs.CloudSecretName, CapiManifestBuilder.Namespace,
                new Dictionary<string, string> { ["clouds.yaml"] = cloudsYaml }, kubeconfig, ct);

            await ApplySecretAsync(
                CloudConfigSecretName, CloudConfigNamespace,
                new Dictionary<string, string> { ["cloud.conf"] = cloudConf, ["clouds.yaml"] = cloudsYaml },
                kubeconfig, ct);

            await vaultService.SetClusterSecretAsync(tenantId, clusterId, "openstack-clouds-yaml", cloudsYaml, ct);
            await vaultService.SetClusterSecretAsync(tenantId, clusterId, "openstack-app-credential-id", credential.Id, ct);
            await vaultService.SetClusterSecretAsync(tenantId, clusterId, "openstack-app-credential-secret", credential.Secret, ct);

            logger.LogInformation(
                "Rotated the cloud credential for cluster {Cluster} to {CredentialName}", spec.Name, credentialName);

            return ClusterOperationResult.Ok(
                $"New application credential '{credentialName}' is in place. The cloud-controller-manager "
                + "and CSI driver pick it up as their pods restart — the previous credential is still valid "
                + "and should be revoked in OpenStack once the cluster has been seen working on this one.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Rotating the cloud credential failed for cluster {Cluster}", spec.Name);
            return ClusterOperationResult.Failed($"The credential could not be rotated: {ex.Message}");
        }
    }

    /// <summary>
    /// The cluster's kubeconfig, materialized from the vault. Public because callers keep needing
    /// it and there is exactly one correct way to get it — projecting the property does not work,
    /// and every place that tried learned that the hard way.
    /// </summary>
    public async Task<string?> GetKubeconfigAsync(Guid clusterId, CancellationToken ct = default)
        => await LoadStoredKubeconfigAsync(clusterId, ct);

    /// <summary>
    /// What the certificates in the stored kubeconfig say. Cheap enough to ask on a page load, and
    /// the answer is the one thing that predicts a cluster locking EntKube out.
    /// </summary>
    public async Task<CertificateStatus> CheckKubeconfigExpiryAsync(Guid clusterId, CancellationToken ct = default)
        => ClusterCertificateExpiry.FromKubeconfig(await LoadStoredKubeconfigAsync(clusterId, ct));

    // ──────── Internals ────────

    private async Task<string?> ReadKubeconfigSecretAsync(string clusterName, string kubeconfig, CancellationToken ct)
    {
        string json = await k8s.GetJsonAsync("secrets", CapiManifestBuilder.Namespace, kubeconfig, ct: ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string wanted = KubeconfigSecretName(clusterName);

        foreach (JsonElement secret in items.EnumerateArray())
        {
            if (!secret.TryGetProperty("metadata", out JsonElement metadata)
                || !metadata.TryGetProperty("name", out JsonElement name)
                || name.GetString() != wanted)
            {
                continue;
            }

            // CAPI stores the whole kubeconfig under "value", base64 as every secret is.
            if (secret.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("value", out JsonElement value)
                && value.GetString() is string encoded)
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
        }

        return null;
    }

    private async Task ApplySecretAsync(
        string name, string ns, IReadOnlyDictionary<string, string> entries, string kubeconfig, CancellationToken ct)
    {
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {name}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");

        // Written as base64 rather than stringData: these values are multi-line INI and YAML, and
        // getting them through a YAML block scalar intact is more ways to be wrong than this is.
        foreach ((string key, string value) in entries)
        {
            sb.AppendLine($"  {key}: {Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}");
        }

        await k8s.ApplyManifestAsync(sb.ToString(), kubeconfig, ct);
    }

    private async Task<string?> LoadStoredKubeconfigAsync(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Materialized rather than projected — see the interceptor note in ClusterReconciler.
        KubernetesCluster? cluster = await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        return cluster?.Kubeconfig;
    }

    private static string? ExtractServer(string kubeconfig)
    {
        foreach (string line in kubeconfig.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["server:".Length..].Trim();
            }
        }
        return null;
    }
}
