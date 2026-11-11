using EntKube.Web.Services.ClusterChanges;
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
    /// <returns>
    /// kubectl's output. Returned rather than discarded because callers that logged it kept their
    /// own copy of this method in order to see it — see TrustBundleService before this change.
    /// </returns>
    Task<string> ApplyManifestAsync(string manifest, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Deletes a specific Kubernetes resource by kind/name/namespace.
    /// Equivalent to "kubectl delete {kind} {name} -n {namespace}".
    /// </summary>
    Task DeleteManifestAsync(string kind, string name, string ns, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// The cluster's API server address and CA, read out of a kubeconfig — with nothing that
    /// authenticates to it. See <c>IClusterClient.GetConnectionAsync</c> for why that distinction
    /// is the point.
    /// </summary>
    EntKube.Web.Services.Clusters.ClusterConnection ReadConnection(string kubeconfig);

    /// <summary>
    /// Sends one HTTP request to a Service in the cluster, through the API server's proxy
    /// endpoint, and returns what came back.
    ///
    /// <para>This is the verb that lets a caller talk to something running <em>in</em> a cluster
    /// without holding the cluster's credential. Three services needed it and none could be
    /// converted without it — the telemetry node client, the JIT proxy and the egress tunnel all
    /// wanted a connection rather than a command.</para>
    /// </summary>
    Task<ServiceProxyResponse> ProxyServiceAsync(
        string ns, string service, int port, HttpMethod method, string path, string kubeconfig,
        object? body = null, IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default);

    /// <summary>
    /// A short-lived token bound to a ServiceAccount, with the expiry the API server actually
    /// granted. See the implementation for why the requested window is not trusted.
    /// </summary>
    Task<ServiceAccountToken> RequestServiceAccountTokenAsync(
        string serviceAccount, string ns, TimeSpan duration, IReadOnlyList<string> audiences,
        string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Applies a JSON merge patch to an existing resource. Only the specified fields are changed;
    /// all other fields are preserved. Safer than ApplyManifestAsync for partial updates.
    /// </summary>
    Task PatchJsonAsync(string resource, string name, string ns, string jsonPatch, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Applies a strategic merge patch to an existing resource.
    /// Unlike PatchJsonAsync (which replaces arrays), strategic merge uses per-type merge keys
    /// so array items (volumes, containers, volumeMounts) are added rather than replaced.
    /// </summary>
    Task PatchStrategicAsync(string resource, string name, string ns, string jsonPatch, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Ensures a namespace exists, creating it if it doesn't.
    /// Equivalent to "kubectl create namespace {ns} --dry-run=client -o yaml | kubectl apply -f -".
    /// </summary>
    Task EnsureNamespaceAsync(string ns, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Gets the JSON output of a kubectl command. Used for querying cluster/pod status.
    /// </summary>
    /// <summary>
    /// Asks a cluster how it differs from a manifest, by server-side dry run. A read: nothing is
    /// changed and no acknowledgment is raised.
    /// </summary>
    Task<EntKube.Web.Services.Clusters.ManifestDiff> DiffManifestAsync(
        string manifest, string ns, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// The cluster's server version as reported by <c>kubectl version</c>, or null when it cannot
    /// be read. Used to tell a deprecated API that is already gone from one that is only coming.
    /// </summary>
    Task<string?> GetServerVersionAsync(string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Applies a manifest set, defaulting any document that names no namespace to this one.
    ///
    /// <para>Separate from <see cref="ApplyManifestAsync"/> rather than an optional parameter on
    /// it: three call sites need the default and 153 do not, and adding a parameter there would
    /// rewrite 65 Moq setups to serve those three. The two share one implementation.</para>
    /// </summary>
    Task<string> ApplyManifestInNamespaceAsync(
        string manifest, string ns, string kubeconfig, string? summary = null,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces one Secret's contents wholesale: removes what is there and applies this manifest,
    /// as a single acknowledged change.
    ///
    /// <para><b>Why not just apply.</b> These Secrets are not created by <c>apply</c>, so they
    /// carry no last-applied annotation — which is the only thing that tells kubectl which keys it
    /// previously owned. A plain apply therefore <em>merges</em>, and a key removed from the vault
    /// stays in the cluster. For a credential that has been revoked, that is the one outcome that
    /// must not happen, and it is why the code being replaced said "delete and recreate for clean
    /// state".</para>
    ///
    /// <para><b>Why one operation and not two.</b> Raising an acknowledgment for the delete and
    /// another for the apply would ask the operator twice about one decision — and the first
    /// dialog would describe destroying a Secret that is about to be rewritten, which reads as
    /// far more alarming than what is happening. The operator is asked once, about the apply, so
    /// the diff they see is the real before-and-after. The delete is how that result is reached,
    /// not a separate choice.</para>
    /// </summary>
    /// <summary>
    /// Adds, updates and removes <b>individual keys</b> of a Secret, leaving every other key in
    /// it untouched. Creates the Secret if it does not exist yet.
    ///
    /// <para><b>Why this exists.</b> A Secret in an app's namespace can have more than one
    /// writer — the app's own vault secrets and the credentials of each service it is bound to
    /// (Redis, RabbitMQ, Mongo, object storage) — and pointing several of them at one Secret is
    /// intended: an app wants one <c>envFrom</c>, not five.</para>
    ///
    /// <para><b>Neither of the two obvious ways to write it is additive.</b>
    /// <see cref="ReplaceSecretAsync"/> deletes and recreates, so it drops every key it does not
    /// itself carry. And a plain <c>apply</c> is <em>not</em> additive either, which is the part
    /// that is easy to get wrong: apply's three-way merge deletes the keys recorded in the
    /// object's <c>last-applied-configuration</c> annotation but missing from the new config —
    /// and that annotation belongs to the <b>object</b>, not to the writer. So two writers
    /// applying in turn each delete the other's keys, and the one that ran last appears to have
    /// won.</para>
    ///
    /// <para>A merge patch has no such memory: it names the keys it means. Removal therefore has
    /// to be explicit — a null value deletes that key — which is why
    /// <paramref name="keys"/> is a map to a <em>nullable</em> string.</para>
    /// </summary>
    /// <param name="keys">
    /// The keys this writer owns. A non-null value is set (it is base64-encoded here, so callers
    /// pass the plain text); a <b>null</b> value deletes that key. Keys absent from the map are
    /// left exactly as they are.
    /// </param>
    /// <param name="annotations">
    /// Annotations to set alongside, or null for none. Used to record which keys a writer owns,
    /// so the next sync can delete the ones it has since dropped without guessing.
    /// </param>
    Task<string> MergeSecretKeysAsync(
        string name, string ns, IReadOnlyDictionary<string, string?> keys,
        IReadOnlyDictionary<string, string>? annotations, string kubeconfig,
        string? summary = null, CancellationToken ct = default);

    Task<string> ReplaceSecretAsync(
        string name, string ns, string manifest, string kubeconfig, string? summary = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes every resource in a manifest set, by <c>kubectl delete -f --ignore-not-found</c>.
    ///
    /// <para>Distinct from <see cref="DeleteManifestAsync"/>, which deletes one named resource.
    /// A caller holding a multi-document manifest cannot express itself with that one without
    /// parsing the YAML to find each kind and name — which is why those callers kept writing the
    /// credential themselves.</para>
    /// </summary>
    Task<string> DeleteManifestSetAsync(
        string manifest, string kubeconfig, string ns = "", string? summary = null,
        CancellationToken ct = default);

    /// <summary>
    /// Raises the acknowledgment for a change this factory is not the one carrying out.
    ///
    /// <para>Every other method here mutates and acknowledges in one act. A caller that mutates
    /// through the k8s SDK — scaling a Deployment, patching a CronJob — cannot, so it has to ask
    /// for the acknowledgment itself. Before this, that meant holding a kubeconfig purely to
    /// satisfy <c>PlannedClusterChange</c>'s required credential, which is why a dozen methods
    /// that otherwise needed nothing from a cluster row still read one.</para>
    /// </summary>
    Task AcknowledgeAsync(
        EntKube.Web.Services.ClusterChanges.PlannedChange change, string kubeconfig,
        string clusterLabel, CancellationToken ct = default);

    /// <summary>
    /// Creates a Job from an existing CronJob's template — "run it now"
    /// (<c>kubectl create job --from=cronjob/…</c>).
    ///
    /// <para>Not an apply: EntKube does not have the manifest. The template lives on the cluster,
    /// and kubectl copies it, which is the whole point — reconstructing it here would be a guess
    /// at what the CronJob actually says, and a wrong guess runs the wrong thing.</para>
    ///
    /// <para>The caller names the Job, because it also has to record which name it used.</para>
    /// </summary>
    Task<HelmExecutionResult> CreateJobFromCronJobAsync(
        string jobName, string cronJobName, string ns, string kubeconfig,
        string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// Drains a node: cordons it, then evicts every evictable pod (<c>kubectl drain</c>).
    ///
    /// <para>Not expressible as a patch or a delete. A drain respects PodDisruptionBudgets, waits
    /// for evictions to complete, and has its own opinions about DaemonSet pods and pods with
    /// local storage — all of which live in kubectl rather than in the API. The alternative to
    /// this verb is a caller holding a kubeconfig, which is what <c>NodeManagementService</c>
    /// did.</para>
    ///
    /// <para>The result is returned rather than thrown: a drain that evicted most of a node and
    /// then hit a budget is the outcome an operator needs to read.</para>
    /// </summary>
    /// <summary>
    /// Sets a Deployment's replica count, as one acknowledged change.
    ///
    /// <para>Its own verb rather than a patch, because <c>ChangeVerb.Scale</c> is what the
    /// acknowledgment dialog can describe in the operator's terms — "take Deployment/x to 0
    /// replicas" — where a strategic patch would show them a JSON fragment. The gate has known
    /// this verb since it was written; the seam simply never had the operation, which is why the
    /// one service that needed it kept its own <c>kubectl</c> (§5.4).</para>
    /// </summary>
    Task<HelmExecutionResult> ScaleDeploymentAsync(
        string name, string ns, int replicas, string kubeconfig,
        string? summary = null, CancellationToken ct = default);

    Task<HelmExecutionResult> DrainNodeAsync(
        string nodeName, string kubeconfig, int gracePeriodSeconds = 30,
        bool ignoreDaemonSets = true, bool deleteEmptyDirData = false,
        string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// PUTs to an action subresource of a namespaced object —
    /// <c>/apis/{groupVersion}/namespaces/{ns}/{plural}/{name}/{subresource}</c> — as one acknowledged
    /// change (<see cref="ClusterChanges.ChangeVerb.Invoke"/>).
    ///
    /// <para>Its own verb because neither kubectl nor the typed SDK can reach these. KubeVirt's pause
    /// and unpause exist only as subresources, and the only other way to send the request is the SDK's
    /// raw <c>HttpClient</c>, which the gate cannot see. Every segment is validated as a DNS name, so
    /// a caller cannot steer the request to another path.</para>
    /// </summary>
    Task InvokeSubresourceAsync(
        string groupVersion, string plural, string name, string ns, string subresource,
        string kubeconfig, string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// Opens a websocket to a streaming subresource of a namespaced object — KubeVirt's
    /// <c>virtualmachineinstances/{name}/vnc</c> or <c>/console</c> — authenticated with the cluster's
    /// own credential. The caller owns the returned socket and must dispose it.
    ///
    /// <para><b>Not gated.</b> A console changes no cluster state; it is a session with a guest that
    /// already runs, the same standing as <c>ProxyServiceAsync</c>. It is also opened from an HTTP
    /// request rather than a circuit, where no acknowledgment dialog exists to ask. What it is instead
    /// is <b>audited</b>, by the caller, with the person's name.</para>
    ///
    /// <para>Built here, from the kubeconfig, through the client library's own websocket connect —
    /// which applies every credential a kubeconfig can carry (client certificate, bearer token, exec
    /// plugin) — so that no caller ever holds the credential. Paths are validated exactly as
    /// <see cref="InvokeSubresourceAsync"/> validates them.</para>
    /// </summary>
    Task<System.Net.WebSockets.WebSocket> ConnectSubresourceAsync(
        string groupVersion, string plural, string name, string ns, string subresource,
        string webSocketSubProtocol, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Rolls every Deployment in a namespace back to the revision Kubernetes last recorded for
    /// it (<c>kubectl rollout undo</c>), as one acknowledged change.
    ///
    /// <para>The namespace is the unit, not a single workload: a release is a set of Deployments,
    /// and undoing one of three would leave a combination that never ran.</para>
    ///
    /// <para>The per-workload outcome is returned rather than thrown. A rollback that partly
    /// succeeded is the single most important thing for a caller to be able to record, and an
    /// exception would collapse it into "it failed".</para>
    /// </summary>
    Task<EntKube.Web.Services.Clusters.WorkloadUndoResult> UndoDeploymentsAsync(
        string ns, string kubeconfig, string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// Runs one helm invocation against a cluster, supplying the credential and raising the
    /// acknowledgment. See <see cref="EntKube.Web.Services.Clusters.HelmInvocation"/> for why the
    /// caller passes arguments rather than a modelled chart API.
    /// </summary>
    Task<HelmExecutionResult> RunHelmAsync(
        EntKube.Web.Services.Clusters.HelmInvocation invocation, string kubeconfig,
        CancellationToken ct = default);

    /// <summary>
    /// Whether one named resource exists, by <c>kubectl get … --ignore-not-found</c>. Pass an empty
    /// <paramref name="ns"/> for cluster-scoped resources such as CRDs.
    /// </summary>
    Task<bool> ResourceExistsAsync(string resource, string name, string ns, string kubeconfig, CancellationToken ct = default);

    Task<string> GetJsonAsync(string resource, string ns, string kubeconfig, string labelSelector = "", CancellationToken ct = default);

    /// <summary>
    /// One named resource as YAML (<c>kubectl get … -o yaml</c>).
    ///
    /// <para>Distinct from <see cref="GetJsonAsync"/>, which lists a kind and returns JSON. A
    /// caller showing a resource to a human wants the YAML kubectl would print — re-serialising
    /// JSON would reorder and requote it — and it wants one object by name, which that method
    /// cannot express.</para>
    /// </summary>
    Task<string> GetResourceYamlAsync(
        string resource, string name, string ns, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// One named resource as JSON — the sibling of <see cref="GetResourceYamlAsync"/>, for callers
    /// that parse rather than display. <c>GetJsonAsync</c> lists a kind; this fetches one object,
    /// which is what a manifest refresh needs (it re-reads each stored manifest by kind and name).
    /// </summary>
    Task<HelmExecutionResult> GetResourceJsonAsync(
        string resource, string name, string ns, string kubeconfig, CancellationToken ct = default);

    /// <summary>
    /// Reads the tail of a workload's logs (kubectl logs {target}). The target can be a pod name or
    /// any selector kubectl accepts — "job/foo" and "deployment/bar" both work, which is what makes
    /// this usable for a one-shot Job whose pod name is generated.
    /// </summary>
    /// <param name="container">
    /// One container of a multi-container pod. Without this a caller quoting the tail of a
    /// specific container's log had to run its own kubectl — which is what
    /// <c>ComponentLifecycleService</c>'s stalled-install diagnostics did, and the reason it kept
    /// a kubeconfig on disk through the whole helm run.
    /// </param>
    Task<string> GetPodLogsAsync(string target, string ns, string kubeconfig, int tailLines = 200,
        CancellationToken ct = default, string? container = null);

    /// <summary>
    /// Server-side applies a manifest the cluster fetches itself, by URL.
    ///
    /// <para>Distinct from <see cref="ApplyManifestAsync"/>, which pipes manifest text: an
    /// operator install (CRD bundles especially) is published as a URL, and downloading it here to
    /// pipe it would make EntKube the thing that has to reach the internet rather than the
    /// cluster. The acknowledgment therefore carries the URL as its summary — there is no manifest
    /// text to preview, which is the honest description of what is about to happen.</para>
    ///
    /// <para><c>--server-side --force-conflicts</c> on purpose: a client-side apply writes its
    /// whole manifest into an annotation and large CRD bundles exceed the 256 KB ceiling, and the
    /// first server-side apply over a client-side-managed resource is otherwise rejected as a
    /// field-manager conflict — which is the state every existing install is in.</para>
    ///
    /// <para><paramref name="patches"/>, when given, are applied by kustomize to what kubectl
    /// fetched (<c>apply -k</c> over a kustomization naming the URL), so the cluster still does the
    /// fetching and never sees the unpatched objects. The acknowledgment names each patch.</para>
    /// </summary>
    Task<HelmExecutionResult> ApplyManifestFromUrlAsync(
        string manifestUrl, string kubeconfig, string? summary = null, CancellationToken ct = default,
        IReadOnlyList<ManifestPatch>? patches = null);

    /// <summary>
    /// Lists a resource type across all namespaces (kubectl get {resource} -A -o json).
    /// </summary>
    Task<string> GetJsonAllNamespacesAsync(string resource, string kubeconfig, string labelSelector = "", CancellationToken ct = default);

    /// <summary>
    /// Executes a SQL statement against the CNPG primary pod.
    /// Uses kubectl exec to connect to the primary and run psql.
    /// </summary>
    Task ExecuteSqlAsync(string clusterName, string ns, string sql, string kubeconfig, ChangeIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Executes a MongoDB script against the primary pod via kubectl exec + mongosh.
    /// The primary pod is the first StatefulSet member: {clusterName}-0.
    /// When username and password are provided, mongosh connects with SCRAM credentials.
    /// </summary>
    Task ExecuteMongoAsync(string clusterName, string ns, string script, string kubeconfig, ChangeIntent intent,
        string? username = null, string? password = null, CancellationToken ct = default);

    /// <summary>Same as ExecuteMongoAsync but returns stdout from mongosh.</summary>
    Task<string> ExecuteMongoWithOutputAsync(string clusterName, string ns, string script, string kubeconfig, ChangeIntent intent,
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
    Task ExecuteSqlOnPodAsync(string podName, string ns, string sql, string kubeconfig, ChangeIntent intent,
        string username = "postgres", string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Same as ExecuteSqlOnPodAsync but returns the psql stdout output.
    /// Used for queries that need to read results (e.g. listing databases).
    /// </summary>
    Task<string> ExecuteSqlOnPodWithOutputAsync(string podName, string ns, string sql, string kubeconfig, ChangeIntent intent,
        string username = "postgres", string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Runs an arbitrary command on a pod (no stdin) and returns stdout.
    /// Used for pg_dump, which writes the dump to stdout.
    /// Environment variables (e.g. PGPASSWORD) are set via ArgumentList so
    /// special characters in values are never misinterpreted.
    /// </summary>
    Task<string> RunCommandOnPodAsync(string podName, string ns, IReadOnlyList<string> command,
        string kubeconfig, ChangeIntent intent, IReadOnlyDictionary<string, string>? envVars = null,
        CancellationToken ct = default, int timeoutSeconds = 0, bool verbose = false);

    /// <summary>
    /// Executes SQL directly connected to a specific database on a CNPG primary pod.
    /// Unlike ExecuteSqlAsync (which connects to the default 'postgres' database),
    /// this opens psql with -d {database} so the SQL runs in the correct database
    /// context from the start — avoiding \c reconnection which can silently fail.
    /// </summary>
    Task ExecuteSqlInCnpgDatabaseAsync(string clusterName, string ns, string database,
        string sql, string kubeconfig, ChangeIntent intent, CancellationToken ct = default);

    Task<string> ExecuteSqlInCnpgDatabaseWithOutputAsync(string clusterName, string ns, string database,
        string sql, string kubeconfig, ChangeIntent intent, CancellationToken ct = default);

    /// <summary>
    /// Runs a command on a pod with content piped via stdin, returning stdout.
    /// Used for commands that consume stdin (e.g. rabbitmqctl import_definitions -).
    /// </summary>
    /// <param name="container">
    /// Container to exec into. Null lets kubectl pick the first one — which works, but prints a
    /// "Defaulted container" notice on stderr that then leads every error message.
    /// </param>
    Task<string> RunCommandOnPodWithStdinAsync(string podName, string ns, IReadOnlyList<string> command,
        string stdin, string kubeconfig, ChangeIntent intent, CancellationToken ct = default, string? container = null);
}

/// <summary>
/// A bound ServiceAccount token. <paramref name="ExpiresAt"/> is what the API server returned,
/// which can be sooner than was asked for.
/// </summary>
public sealed record ServiceAccountToken(string Token, DateTime? ExpiresAt);

/// <summary>
/// What a proxied request to an in-cluster Service came back with.
///
/// <para><paramref name="Status"/> is carried rather than thrown on, because a request that
/// travels through the API server's proxy can be refused at either end and the two need different
/// fixes — the API server means the cluster credential was not accepted, the Service means its own
/// credential was not. A caller that only saw an exception could not tell them apart.</para>
/// </summary>
public sealed record ServiceProxyResponse(int Status, string? ReasonPhrase, string Body)
{
    public bool Success => Status is >= 200 and < 300;
}
