using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Clusters;

/// <summary>
/// An <see cref="IClusterClient"/> over the existing <see cref="IKubernetesClientFactory"/>.
///
/// <para>Every method is the same call it always was, with the credential supplied from here
/// instead of by the caller. Nothing about how EntKube talks to a cluster changes; what
/// changes is who has to be holding the kubeconfig to ask.</para>
/// </summary>
public sealed class ClusterClient(
    IKubernetesClientFactory inner, Guid clusterId, string clusterName, string kubeconfig,
    EntKube.Web.Services.KubernetesProxyClientPool? pool = null)
    : IClusterClient
{
    public Guid ClusterId => clusterId;

    public string ClusterName => clusterName;

    public Task<string> ApplyManifestAsync(string manifest, CancellationToken ct = default)
        => inner.ApplyManifestAsync(manifest, kubeconfig, ct);

    public Task DeleteManifestAsync(string kind, string name, string ns, CancellationToken ct = default)
        => inner.DeleteManifestAsync(kind, name, ns, kubeconfig, ct);

    public Task<ServiceProxyResponse> ProxyServiceAsync(
        string ns, string service, int port, HttpMethod method, string path,
        object? body = null, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => inner.ProxyServiceAsync(ns, service, port, method, path, kubeconfig, body, headers, ct);

    public Task<ServiceAccountToken> RequestServiceAccountTokenAsync(
        string serviceAccount, string ns, TimeSpan duration, IReadOnlyList<string> audiences,
        CancellationToken ct = default)
        => inner.RequestServiceAccountTokenAsync(serviceAccount, ns, duration, audiences, kubeconfig, ct);

    public Task PatchJsonAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default)
        => inner.PatchJsonAsync(resource, name, ns, jsonPatch, kubeconfig, ct);

    public Task PatchStrategicAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default)
        => inner.PatchStrategicAsync(resource, name, ns, jsonPatch, kubeconfig, ct);

    public Task EnsureNamespaceAsync(string ns, CancellationToken ct = default)
        => inner.EnsureNamespaceAsync(ns, kubeconfig, ct);

    public Task<ManifestDiff> DiffManifestAsync(string manifest, string ns, CancellationToken ct = default)
        => inner.DiffManifestAsync(manifest, ns, kubeconfig, ct);

    public Task<string?> GetServerVersionAsync(CancellationToken ct = default)
        => inner.GetServerVersionAsync(kubeconfig, ct);

    public Task<string> ApplyManifestInNamespaceAsync(
        string manifest, string ns, string? summary = null, CancellationToken ct = default)
        => inner.ApplyManifestInNamespaceAsync(manifest, ns, kubeconfig, summary, ct);

    public Task<string> ReplaceSecretAsync(
        string name, string ns, string manifest, string? summary = null,
        CancellationToken ct = default)
        => inner.ReplaceSecretAsync(name, ns, manifest, kubeconfig, summary, ct);

    public Task<string> DeleteManifestSetAsync(
        string manifest, string ns = "", string? summary = null, CancellationToken ct = default)
        => inner.DeleteManifestSetAsync(manifest, kubeconfig, ns, summary, ct);

    public Task<HelmExecutionResult> RunHelmAsync(HelmInvocation invocation, CancellationToken ct = default)
        => inner.RunHelmAsync(invocation, kubeconfig, ct);

    public Task<HelmExecutionResult> ApplyManifestFromUrlAsync(
        string manifestUrl, string? summary = null, CancellationToken ct = default)
        => inner.ApplyManifestFromUrlAsync(manifestUrl, kubeconfig, summary, ct);

    public Task<HelmExecutionResult> GetResourceJsonAsync(
        string resource, string name, string ns, CancellationToken ct = default)
        => inner.GetResourceJsonAsync(resource, name, ns, kubeconfig, ct);

    public Task<string> GetResourceYamlAsync(
        string resource, string name, string ns, CancellationToken ct = default)
        => inner.GetResourceYamlAsync(resource, name, ns, kubeconfig, ct);

    public Task AcknowledgeAsync(
        EntKube.Web.Services.ClusterChanges.PlannedChange change, CancellationToken ct = default)
        => inner.AcknowledgeAsync(change, kubeconfig, clusterName, ct);

    public Task<HelmExecutionResult> CreateJobFromCronJobAsync(
        string jobName, string cronJobName, string ns, string? summary = null,
        CancellationToken ct = default)
        => inner.CreateJobFromCronJobAsync(jobName, cronJobName, ns, kubeconfig, summary, ct);

    public Task<HelmExecutionResult> ScaleDeploymentAsync(
        string name, string ns, int replicas, string? summary = null, CancellationToken ct = default)
        => inner.ScaleDeploymentAsync(name, ns, replicas, kubeconfig, summary, ct);

    public Task<HelmExecutionResult> DrainNodeAsync(
        string nodeName, int gracePeriodSeconds = 30, bool ignoreDaemonSets = true,
        bool deleteEmptyDirData = false, string? summary = null, CancellationToken ct = default)
        => inner.DrainNodeAsync(nodeName, kubeconfig, gracePeriodSeconds, ignoreDaemonSets,
            deleteEmptyDirData, summary, ct);

    public Task<WorkloadUndoResult> UndoDeploymentsAsync(
        string ns, string? summary = null, CancellationToken ct = default)
        => inner.UndoDeploymentsAsync(ns, kubeconfig, summary, ct);

    public Task<bool> ResourceExistsAsync(string resource, string name, string ns = "", CancellationToken ct = default)
        => inner.ResourceExistsAsync(resource, name, ns, kubeconfig, ct);

    public Task<string> GetJsonAsync(string resource, string ns, string labelSelector = "", CancellationToken ct = default)
        => inner.GetJsonAsync(resource, ns, kubeconfig, labelSelector, ct);

    public Task<string> GetJsonAllNamespacesAsync(string resource, string labelSelector = "", CancellationToken ct = default)
        => inner.GetJsonAllNamespacesAsync(resource, kubeconfig, labelSelector, ct);

    public Task<string> GetPodLogsAsync(string target, string ns, int tailLines = 200,
        string? container = null, CancellationToken ct = default)
        => inner.GetPodLogsAsync(target, ns, kubeconfig, tailLines, ct, container);

    public Task<string?> GetSecretValueAsync(string secretName, string key, string ns, CancellationToken ct = default)
        => inner.GetSecretValueAsync(secretName, key, ns, kubeconfig, ct);

    public Task ExecuteSqlAsync(string clusterName_, string ns, string sql, CancellationToken ct = default)
        => inner.ExecuteSqlAsync(clusterName_, ns, sql, kubeconfig, ct);

    public Task ExecuteSqlOnPodAsync(string podName, string ns, string sql,
        string username = "postgres", string? password = null, CancellationToken ct = default)
        => inner.ExecuteSqlOnPodAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public Task<string> ExecuteSqlOnPodWithOutputAsync(string podName, string ns, string sql,
        string username = "postgres", string? password = null, CancellationToken ct = default)
        => inner.ExecuteSqlOnPodWithOutputAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public Task ExecuteSqlInCnpgDatabaseAsync(string clusterName_, string ns, string database,
        string sql, CancellationToken ct = default)
        => inner.ExecuteSqlInCnpgDatabaseAsync(clusterName_, ns, database, sql, kubeconfig, ct);

    public Task<string> ExecuteSqlInCnpgDatabaseWithOutputAsync(string clusterName_, string ns, string database,
        string sql, CancellationToken ct = default)
        => inner.ExecuteSqlInCnpgDatabaseWithOutputAsync(clusterName_, ns, database, sql, kubeconfig, ct);

    public Task ExecuteMongoAsync(string clusterName_, string ns, string script,
        string? username = null, string? password = null, CancellationToken ct = default)
        => inner.ExecuteMongoAsync(clusterName_, ns, script, kubeconfig, username, password, ct);

    public Task<string> ExecuteMongoWithOutputAsync(string clusterName_, string ns, string script,
        string? username = null, string? password = null, CancellationToken ct = default)
        => inner.ExecuteMongoWithOutputAsync(clusterName_, ns, script, kubeconfig, username, password, ct);

    public Task<string> RunCommandOnPodAsync(string podName, string ns, IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string>? envVars = null, CancellationToken ct = default,
        int timeoutSeconds = 0, bool verbose = false)
        => inner.RunCommandOnPodAsync(podName, ns, command, kubeconfig, envVars, ct, timeoutSeconds, verbose);

    public Task<string> RunCommandOnPodWithStdinAsync(string podName, string ns, IReadOnlyList<string> command,
        string stdin, CancellationToken ct = default, string? container = null)
        => inner.RunCommandOnPodWithStdinAsync(podName, ns, command, stdin, kubeconfig, ct, container);

    /// <summary>
    /// The same four lines that were copied into ten services, in one place. The kubeconfig is
    /// read from here rather than passed in, which is the whole point.
    /// </summary>
    /// <summary>
    /// The pooled client, from the pool that already existed for exactly this.
    ///
    /// <para><b>Falls back to an unpooled client when no pool was supplied.</b> The application
    /// always supplies one — <c>KubernetesProxyClientPool</c> is a registered singleton — so the
    /// fallback exists for the forty-seven test fixtures that construct this factory directly and
    /// do not care about pooling. Stated rather than silent, because "pooled" that quietly is not
    /// would hide exactly the per-tile latency this op was added to protect.</para>
    /// </summary>
    public k8s.Kubernetes GetPooledSdkClient()
        => pool is null ? CreateSdkClient() : pool.Get(kubeconfig);

    public k8s.Kubernetes CreateSdkClient()
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(kubeconfig));

        return new k8s.Kubernetes(
            k8s.KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));
    }
}

/// <summary>
/// Resolves a cluster and binds a client to it.
///
/// <para>The cluster is loaded through Fleet's own context, whose factory carries the
/// kubeconfig materialisation interceptor — which is what decrypts the credential out of the
/// vault. That decryption now happens in one place instead of wherever a caller happened to
/// load a cluster.</para>
/// </summary>
public sealed class ClusterClientFactory(
    IDbContextFactory<FleetDbContext> dbFactory,
    IKubernetesClientFactory inner,
    EntKube.Web.Services.KubernetesProxyClientPool? pool = null) : IClusterClientFactory
{
    public async Task<IClusterClient?> ForAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using FleetDbContext db = await dbFactory.CreateDbContextAsync(ct);

        KubernetesCluster? cluster = await db.KubernetesClusters
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);

        // Null rather than throwing: "not this tenant's" and "has no kubeconfig" are both
        // ordinary states a caller has to handle. They used to be a `.Kubeconfig!` that threw
        // further in, with a message naming neither.
        return cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig)
            ? null
            : new ClusterClient(inner, cluster.Id, cluster.Name, cluster.Kubeconfig, pool);
    }
}
