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
    IKubernetesClientFactory inner, Guid clusterId, string clusterName, string kubeconfig)
    : IClusterClient
{
    public Guid ClusterId => clusterId;

    public string ClusterName => clusterName;

    public Task<string> ApplyManifestAsync(string manifest, CancellationToken ct = default)
        => inner.ApplyManifestAsync(manifest, kubeconfig, ct);

    public Task DeleteManifestAsync(string kind, string name, string ns, CancellationToken ct = default)
        => inner.DeleteManifestAsync(kind, name, ns, kubeconfig, ct);

    public Task PatchJsonAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default)
        => inner.PatchJsonAsync(resource, name, ns, jsonPatch, kubeconfig, ct);

    public Task PatchStrategicAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default)
        => inner.PatchStrategicAsync(resource, name, ns, jsonPatch, kubeconfig, ct);

    public Task EnsureNamespaceAsync(string ns, CancellationToken ct = default)
        => inner.EnsureNamespaceAsync(ns, kubeconfig, ct);

    public Task<string> DeleteManifestSetAsync(string manifest, CancellationToken ct = default)
        => inner.DeleteManifestSetAsync(manifest, kubeconfig, ct);

    public Task<HelmExecutionResult> RunHelmAsync(HelmInvocation invocation, CancellationToken ct = default)
        => inner.RunHelmAsync(invocation, kubeconfig, ct);

    public Task<bool> ResourceExistsAsync(string resource, string name, string ns = "", CancellationToken ct = default)
        => inner.ResourceExistsAsync(resource, name, ns, kubeconfig, ct);

    public Task<string> GetJsonAsync(string resource, string ns, string labelSelector = "", CancellationToken ct = default)
        => inner.GetJsonAsync(resource, ns, kubeconfig, labelSelector, ct);

    public Task<string> GetJsonAllNamespacesAsync(string resource, string labelSelector = "", CancellationToken ct = default)
        => inner.GetJsonAllNamespacesAsync(resource, kubeconfig, labelSelector, ct);

    public Task<string> GetPodLogsAsync(string target, string ns, int tailLines = 200,
        CancellationToken ct = default)
        => inner.GetPodLogsAsync(target, ns, kubeconfig, tailLines, ct);

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
    IKubernetesClientFactory inner) : IClusterClientFactory
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
            : new ClusterClient(inner, cluster.Id, cluster.Name, cluster.Kubeconfig);
    }
}
