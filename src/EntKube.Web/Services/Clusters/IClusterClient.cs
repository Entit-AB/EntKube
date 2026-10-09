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

    /// <returns>kubectl's output, so a caller can log what happened.</returns>
    Task<string> ApplyManifestAsync(string manifest, CancellationToken ct = default);

    Task DeleteManifestAsync(string kind, string name, string ns, CancellationToken ct = default);

    Task PatchJsonAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default);

    Task PatchStrategicAsync(string resource, string name, string ns, string jsonPatch, CancellationToken ct = default);

    Task EnsureNamespaceAsync(string ns, CancellationToken ct = default);

    /// <summary>
    /// How this cluster differs from a manifest, by server-side dry run. A read; nothing changes.
    /// </summary>
    Task<ManifestDiff> DiffManifestAsync(string manifest, string ns, CancellationToken ct = default);

    /// <summary>This cluster's server version, or null when it cannot be read.</summary>
    Task<string?> GetServerVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies a manifest set, defaulting documents that name no namespace to this one.
    ///
    /// <para><paramref name="summary"/> is what the operator reads in the acknowledgment dialog.
    /// Pass it: the seam knows the manifest but not which deployment it belongs to, so without one
    /// the dialog says "Apply manifest".</para>
    /// </summary>
    Task<string> ApplyManifestInNamespaceAsync(
        string manifest, string ns, string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes every resource in a manifest set. Use when holding a multi-document manifest;
    /// <see cref="DeleteManifestAsync"/> is for one named resource.
    /// </summary>
    Task<string> DeleteManifestSetAsync(
        string manifest, string ns = "", string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// Runs one helm invocation against this cluster. The credential is supplied from here and the
    /// acknowledgment is raised on the way through.
    /// </summary>
    Task<HelmExecutionResult> RunHelmAsync(HelmInvocation invocation, CancellationToken ct = default);

    /// <summary>
    /// Whether one named resource exists on this cluster. Pass an empty <paramref name="ns"/> for
    /// cluster-scoped resources such as CRDs.
    /// </summary>
    Task<bool> ResourceExistsAsync(string resource, string name, string ns = "", CancellationToken ct = default);

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

    /// <summary>
    /// An authenticated client for the typed Kubernetes SDK, bound to this cluster. The caller
    /// disposes it, which is how the ten hand-rolled copies of this already worked.
    ///
    /// <para><b>Why this exists.</b> The methods above all go through <c>kubectl</c>, and a good
    /// deal of EntKube does not: ten services build their own <c>Kubernetes</c> client from a
    /// kubeconfig, which accounts for roughly 167 of the places still taking a credential out of
    /// a cluster row. Without this they could not use a cluster client at all, so the custody
    /// work would have stopped at about half.</para>
    ///
    /// <para><b>What it does and does not achieve.</b> It takes the credential out of caller
    /// code, puts the tenant check in one place, and replaces ten copies of the same five lines.
    /// It does <em>not</em> make the credential unreachable — a client necessarily holds one, and
    /// code determined to dig it out still can. The point is that reaching a cluster no longer
    /// *requires* handling a credential, so doing it is now a deliberate act rather than the
    /// ordinary way of working.</para>
    /// </summary>
    k8s.Kubernetes CreateSdkClient();
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
