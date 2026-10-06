namespace EntKube.Web.Services.Clusters;

/// <summary>
/// Everything EntKube does to one cluster, with no credential in the signatures.
///
/// <para><b>Why this exists.</b> Every method on <see cref="IKubernetesClientFactory"/> takes
/// a <c>string kubeconfig</c>, so every caller had to load the cluster row and take the
/// credential out of it — 534 places across twelve modules did. That made the credential
/// travel everywhere, and it meant no module could stop depending on Fleet's cluster table,
/// because it needed the kubeconfig out of it. See <c>docs/decomposition.md</c> §4.0.1.</para>
///
/// <para>A client is bound to one cluster and holds the credential itself. Callers name the
/// cluster once, by id, and never see what is used to reach it.</para>
/// </summary>
public interface IClusterClient
{
    /// <summary>The cluster this client talks to.</summary>
    Guid ClusterId { get; }

    /// <summary>The cluster's name, for messages that need to say which one.</summary>
    string ClusterName { get; }

    Task ApplyManifestAsync(string manifest, CancellationToken ct = default);

    Task DeleteManifestAsync(string kind, string name, string ns, CancellationToken ct = default);

    Task PatchJsonAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default);

    Task PatchStrategicAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default);

    Task EnsureNamespaceAsync(string ns, CancellationToken ct = default);

    Task<string> GetJsonAsync(string resource, string ns, string labelSelector = "", CancellationToken ct = default);

    Task<string> GetJsonAllNamespacesAsync(string resource, string labelSelector = "", CancellationToken ct = default);

    Task<string> GetPodLogsAsync(string target, string ns, int tailLines = 200,
        CancellationToken ct = default);

    Task<string?> GetSecretValueAsync(string secretName, string key, string ns, CancellationToken ct = default);

    Task ExecuteSqlAsync(string clusterName, string ns, string sql, CancellationToken ct = default);

    Task ExecuteSqlOnPodAsync(string podName, string ns, string sql,
        string username = "postgres", string? password = null, CancellationToken ct = default);

    Task<string> ExecuteSqlOnPodWithOutputAsync(string podName, string ns, string sql,
        string username = "postgres", string? password = null, CancellationToken ct = default);

    Task ExecuteSqlInCnpgDatabaseAsync(string clusterName, string ns, string database,
        string sql, CancellationToken ct = default);

    Task<string> ExecuteSqlInCnpgDatabaseWithOutputAsync(string clusterName, string ns, string database,
        string sql, CancellationToken ct = default);

    Task ExecuteMongoAsync(string clusterName, string ns, string script,
        string? username = null, string? password = null, CancellationToken ct = default);

    Task<string> ExecuteMongoWithOutputAsync(string clusterName, string ns, string script,
        string? username = null, string? password = null, CancellationToken ct = default);

    Task<string> RunCommandOnPodAsync(string podName, string ns, IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string>? envVars = null, CancellationToken ct = default,
        int timeoutSeconds = 0, bool verbose = false);

    Task<string> RunCommandOnPodWithStdinAsync(string podName, string ns, IReadOnlyList<string> command,
        string stdin, CancellationToken ct = default, string? container = null);
}

/// <summary>
/// Hands out <see cref="IClusterClient"/>s. Fleet owns this, because Fleet owns clusters and
/// therefore owns reaching them.
/// </summary>
public interface IClusterClientFactory
{
    /// <summary>
    /// A client for one of the tenant's clusters.
    /// </summary>
    /// <returns>
    /// Null when the cluster is not this tenant's, or has no kubeconfig stored — the two cases
    /// a caller has to handle, and which were previously a null-forgiving <c>.Kubeconfig!</c>
    /// that threw somewhere further in.
    /// </returns>
    Task<IClusterClient?> ForAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default);
}
