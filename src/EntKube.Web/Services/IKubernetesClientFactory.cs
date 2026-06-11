namespace EntKube.Web.Services;

/// <summary>
/// Abstraction over Kubernetes API operations that CnpgService needs.
/// Allows testing orchestration logic without real K8s clusters.
///
/// Implementations use the k8s .NET client or kubectl under the hood,
/// applying manifests (CNPG Cluster CRDs, Backup CRDs, Secrets) and
/// executing SQL via kubectl exec against the primary pod.
/// </summary>
public interface IKubernetesClientFactory
{
    /// <summary>
    /// Applies a YAML manifest to a Kubernetes cluster using the given kubeconfig.
    /// Equivalent to "kubectl apply -f -" with the manifest piped in.
    /// </summary>
    Task ApplyManifestAsync(string manifest, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Deletes a specific Kubernetes resource by kind/name/namespace.
    /// Equivalent to "kubectl delete {kind} {name} -n {namespace}".
    /// </summary>
    Task DeleteManifestAsync(string kind, string name, string ns, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Applies a JSON merge patch to an existing resource. Only the specified fields are changed;
    /// all other fields are preserved. Safer than ApplyManifestAsync for partial updates.
    /// </summary>
    Task PatchJsonAsync(string resource, string name, string ns, string jsonPatch, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Ensures a namespace exists, creating it if it doesn't.
    /// Equivalent to "kubectl create namespace {ns} --dry-run=client -o yaml | kubectl apply -f -".
    /// </summary>
    Task EnsureNamespaceAsync(string ns, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Gets the JSON output of a kubectl command. Used for querying cluster/pod status.
    /// </summary>
    Task<string> GetJsonAsync(string resource, string ns, string kubeconfig, string labelSelector = "", CancellationToken ct = default);

    /// <summary>
    /// Executes a SQL statement against the CNPG primary pod.
    /// Uses kubectl exec to connect to the primary and run psql.
    /// </summary>
    Task ExecuteSqlAsync(string clusterName, string ns, string sql, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Executes a MongoDB script against the primary pod via kubectl exec + mongosh.
    /// The primary pod is the first StatefulSet member: {clusterName}-0.
    /// When username and password are provided, mongosh connects with SCRAM credentials.
    /// </summary>
    Task ExecuteMongoAsync(string clusterName, string ns, string script, string kubeconfig,
        string? username = null, string? password = null, CancellationToken ct = default);

    /// <summary>Same as ExecuteMongoAsync but returns stdout from mongosh.</summary>
    Task<string> ExecuteMongoWithOutputAsync(string clusterName, string ns, string script, string kubeconfig,
        string? username = null, string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Reads a single key from a Kubernetes Secret and returns the decoded value.
    /// Returns null if the secret or key does not exist.
    /// </summary>
    Task<string?> GetSecretValueAsync(string secretName, string key, string ns, string kubeconfig,
        CancellationToken ct = default);

    /// <summary>
    /// Executes SQL on an arbitrary Postgres pod via kubectl exec + psql.
    /// The admin password is passed via the PGPASSWORD environment variable so it
    /// never appears in the process argument list.
    /// </summary>
    Task ExecuteSqlOnPodAsync(string podName, string ns, string sql, string kubeconfig,
        string username = "postgres", string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Same as ExecuteSqlOnPodAsync but returns the psql stdout output.
    /// Used for queries that need to read results (e.g. listing databases).
    /// </summary>
    Task<string> ExecuteSqlOnPodWithOutputAsync(string podName, string ns, string sql, string kubeconfig,
        string username = "postgres", string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Runs an arbitrary command on a pod (no stdin) and returns stdout.
    /// Used for pg_dump, which writes the dump to stdout.
    /// Environment variables (e.g. PGPASSWORD) are set via ArgumentList so
    /// special characters in values are never misinterpreted.
    /// </summary>
    Task<string> RunCommandOnPodAsync(string podName, string ns, IReadOnlyList<string> command,
        string kubeconfig, IReadOnlyDictionary<string, string>? envVars = null,
        CancellationToken ct = default);

    /// <summary>
    /// Executes SQL directly connected to a specific database on a CNPG primary pod.
    /// Unlike ExecuteSqlAsync (which connects to the default 'postgres' database),
    /// this opens psql with -d {database} so the SQL runs in the correct database
    /// context from the start — avoiding \c reconnection which can silently fail.
    /// </summary>
    Task ExecuteSqlInCnpgDatabaseAsync(string clusterName, string ns, string database,
        string sql, string kubeconfig, CancellationToken ct = default);

    Task<string> ExecuteSqlInCnpgDatabaseWithOutputAsync(string clusterName, string ns, string database,
        string sql, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Runs a command on a pod with content piped via stdin, returning stdout.
    /// Used for commands that consume stdin (e.g. rabbitmqctl import_definitions -).
    /// </summary>
    Task<string> RunCommandOnPodWithStdinAsync(string podName, string ns, IReadOnlyList<string> command,
        string stdin, string kubeconfig, CancellationToken ct = default);
}
