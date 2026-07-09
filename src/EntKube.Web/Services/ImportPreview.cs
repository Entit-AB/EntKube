using System.Text.Json.Nodes;
using EntKube.Web.Data;
using YamlDotNet.Serialization;

namespace EntKube.Web.Services;

/// <summary>
/// How a discovered resource is grouped in the import preview. Drives the UI
/// grouping and the manifest apply order.
/// </summary>
public enum ImportCategory
{
    Workload,
    Storage,
    Config,
    Networking,
    Secret,
    CustomResource,
    Unsupported
}

/// <summary>
/// What to do with a detected Postgres connection during import.
/// </summary>
public enum PostgresImportAction
{
    Skip,
    ImportDatabase,
    RegisterAndImport
}

/// <summary>
/// A single Kubernetes resource discovered in a scanned namespace, already
/// sanitized into a re-appliable manifest. Secrets are NOT represented here —
/// they go through <see cref="DetectedSecret"/> so their values land in the vault.
/// </summary>
public class DiscoveredResource
{
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public ImportCategory Category { get; set; }

    /// <summary>True if EntKube knows how to import this kind.</summary>
    public bool Supported { get; set; } = true;

    /// <summary>Whether the admin has this resource selected for import.</summary>
    public bool Selected { get; set; } = true;

    /// <summary>Short human-friendly note (e.g. "3 replicas", "bound", reason for skip).</summary>
    public string? Detail { get; set; }

    /// <summary>The cleaned YAML stored as the deployment manifest. Empty for unsupported kinds.</summary>
    public string SanitizedYaml { get; set; } = "";

    /// <summary>Apply order (PV → PVC → ConfigMap → workload → Service → routes → CR).</summary>
    public int SortOrder { get; set; }
}

/// <summary>One key inside a detected secret. The value is never surfaced to the UI.</summary>
public class DetectedSecretKey
{
    public required string Key { get; set; }
    public bool HasValue { get; set; }
    public int Length { get; set; }
}

/// <summary>
/// A Kubernetes Secret (or the target of an ExternalSecret) found during the
/// scan. Its decoded values are imported into the vault as app secrets with
/// sync-back enabled, so EntKube — not raw manifests / ESO — owns them going forward.
/// </summary>
public class DetectedSecret
{
    /// <summary>The target Kubernetes Secret name; vault keys sync back under this name.</summary>
    public required string SecretName { get; set; }
    public required string Namespace { get; set; }

    /// <summary>"Secret" for a plain Secret, "ExternalSecret" when discovered via an ES CR.</summary>
    public string Source { get; set; } = "Secret";

    public List<DetectedSecretKey> Keys { get; set; } = [];

    /// <summary>Decoded key→value pairs. Held in memory for import only; never shown.</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    public bool ImportToVault { get; set; } = true;
    public string? Warning { get; set; }

    /// <summary>
    /// True when this Secret is a TLS certificate — it carries a non-empty
    /// <c>tls.crt</c> and every key is part of the standard TLS set
    /// (<c>tls.crt</c>/<c>tls.key</c>/<c>ca.crt</c>). Such a Secret is imported as a
    /// single <c>VaultSecretType.Certificate</c> (parseable, with expiry) rather than
    /// as separate opaque keys. Extra, non-TLS keys fall back to the opaque path so no
    /// value is silently dropped.
    /// </summary>
    public bool IsCertificate =>
        Values.TryGetValue("tls.crt", out string? crt)
        && !string.IsNullOrWhiteSpace(crt)
        && Values.Keys.All(k => k is "tls.crt" or "tls.key" or "ca.crt");
}

/// <summary>
/// A Postgres connection detected inside a secret/configmap value. The admin
/// decides per-connection whether to adopt the database (and register the
/// instance if EntKube doesn't already know it).
/// </summary>
public class DetectedPostgres
{
    /// <summary>Where it was found, e.g. "Secret billing-db / DATABASE_URL".</summary>
    public required string Source { get; set; }

    public string? Host { get; set; }
    public int Port { get; set; } = 5432;
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Set when the host matches an already-registered Postgres instance.</summary>
    public Guid? MatchedInstanceId { get; set; }
    public string? MatchedInstanceName { get; set; }

    public PostgresImportAction Action { get; set; } = PostgresImportAction.Skip;

    // Fields the admin supplies when registering a new instance (superuser access).
    public string? AdminPodName { get; set; }
    public string? AdminUsername { get; set; } = "postgres";
    public string? AdminPassword { get; set; }
    public string? ServiceName { get; set; }
    public string? RegisterNamespace { get; set; }

    /// <summary>The K8s Secret name to create for the DatabaseBinding (defaults to "{db}-db").</summary>
    public string? BindingSecretName { get; set; }
}

/// <summary>
/// A Secret that was deliberately excluded from import because another controller
/// owns/derives it (an ExternalSecret, cert-manager Certificate, or any
/// ownerReference). Importing such a secret into the vault would make EntKube
/// fight the controller that recreates it, so it is never selectable — only shown.
/// </summary>
public class SkippedSecret
{
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public required string Reason { get; set; }
}

/// <summary>
/// A <c>kubernetes.io/dockerconfigjson</c> (or legacy <c>dockercfg</c>) image-pull
/// Secret found during the scan. Instead of importing it as an opaque vault secret,
/// it becomes a <see cref="DockerRegistryCredential"/> so EntKube manages it as a
/// registry credential and re-syncs the same dockerconfigjson Secret.
/// </summary>
public class DetectedRegistryCredential
{
    /// <summary>The original Secret name; the credential syncs back under this name.</summary>
    public required string SecretName { get; set; }
    public required string Namespace { get; set; }

    /// <summary>Registry server (the key under <c>auths</c>), e.g. "ghcr.io".</summary>
    public required string Server { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Email { get; set; }

    /// <summary>Best-effort registry type inferred from the server host.</summary>
    public DockerRegistryType RegistryType { get; set; }

    public bool Import { get; set; } = true;
}

/// <summary>One routing rule parsed from an HTTPRoute (a path → backend service).</summary>
public class DetectedRouteRule
{
    public string PathPrefix { get; set; } = "/";
    public string? ServiceName { get; set; }
    public int ServicePort { get; set; } = 80;
    public string? RewritePath { get; set; }
}

/// <summary>
/// An HTTPRoute discovered in the cluster, parsed into EntKube's external-access
/// shape. Imported as an <see cref="AppRoute"/> + <see cref="AppDeploymentRoute"/>
/// (not a raw manifest) so the app shows external access and EntKube owns the
/// HTTPRoute it regenerates.
/// </summary>
public class DetectedRoute
{
    public required string Hostname { get; set; }

    /// <summary>The HTTPRoute's namespace (informational; may differ from the app namespace).</summary>
    public required string Namespace { get; set; }

    public string? GatewayName { get; set; }
    public string? GatewayNamespace { get; set; }

    public TlsMode TlsMode { get; set; } = TlsMode.ClusterIssuer;
    public string? ClusterIssuerName { get; set; } = "letsencrypt-prod";

    public List<DetectedRouteRule> Rules { get; set; } = [];
    public bool Import { get; set; } = true;
    public string? SourceHttpRoute { get; set; }
}

/// <summary>
/// An ArgoCD <c>Application</c> custom resource discovered on the cluster. Purely
/// informational in the import preview — it shows what ArgoCD manages so the operator
/// can pick the right namespaces. ArgoCD is never required; when it isn't installed
/// this list is simply empty.
/// </summary>
public class DetectedArgoApplication
{
    public required string Name { get; set; }
    public string? Project { get; set; }
    public string? RepoUrl { get; set; }
    public string? Path { get; set; }
    public string? TargetRevision { get; set; }
    public string? DestinationNamespace { get; set; }
    public string? DestinationServer { get; set; }
    public string? SyncStatus { get; set; }
    public string? HealthStatus { get; set; }
}

/// <summary>The full result of scanning one or more namespaces — the wizard's working model.</summary>
public class ImportPreview
{
    public required Guid ClusterId { get; set; }
    public List<string> Namespaces { get; set; } = [];

    /// <summary>The deployment's primary namespace (first workload's namespace, else first scanned).</summary>
    public string PrimaryNamespace { get; set; } = "default";

    public List<DiscoveredResource> Resources { get; set; } = [];
    public List<DetectedSecret> Secrets { get; set; } = [];

    /// <summary>Secrets excluded because they are managed/derived by another controller.</summary>
    public List<SkippedSecret> SkippedSecrets { get; set; } = [];

    /// <summary>Image-pull (dockerconfigjson) secrets to import as registry credentials.</summary>
    public List<DetectedRegistryCredential> RegistryCredentials { get; set; } = [];

    /// <summary>HTTPRoutes parsed into EntKube external-access routes.</summary>
    public List<DetectedRoute> Routes { get; set; } = [];

    public List<DetectedPostgres> PostgresConnections { get; set; } = [];

    /// <summary>ArgoCD Applications detected on the cluster (informational; empty when ArgoCD is absent).</summary>
    public List<DetectedArgoApplication> ArgoApplications { get; set; } = [];

    public List<string> Warnings { get; set; } = [];
}

/// <summary>The admin-confirmed request handed to <c>DeploymentImportService.ImportAsync</c>.</summary>
public class ImportRequest
{
    public required Guid CustomerId { get; set; }
    public required string AppName { get; set; }
    public required Guid EnvironmentId { get; set; }
    public required ImportPreview Preview { get; set; }
    public string? PerformedBy { get; set; }

    /// <summary>
    /// When set, the import targets this exact existing app (reuse), regardless of name.
    /// Used when launching from within an app, where the target is unambiguous and must
    /// not be resolved by fuzzy name matching. When null the app is resolved/created by
    /// (<see cref="CustomerId"/>, <see cref="AppName"/>).
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// Explicit name for the created deployment. When null/blank the deployment is named
    /// after the app (qualified by environment on collision).
    /// </summary>
    public string? DeploymentName { get; set; }
}

/// <summary>Summary of what an import actually created, with best-effort warnings/errors.</summary>
public class ImportResult
{
    public Guid AppId { get; set; }
    public Guid DeploymentId { get; set; }
    public string AppName { get; set; } = "";
    public string DeploymentName { get; set; } = "";

    /// <summary>True when a brand-new app was created; false when an existing app was extended.</summary>
    public bool AppCreated { get; set; }

    /// <summary>True when a new deployment was created; false when an existing one was updated.</summary>
    public bool DeploymentCreated { get; set; }

    public int ManifestCount { get; set; }

    /// <summary>Manifests skipped because a resource of the same kind+name already existed on the deployment.</summary>
    public int ManifestSkipped { get; set; }

    public int SecretCount { get; set; }
    public int SecretKeyCount { get; set; }
    public int RegistryCredentialCount { get; set; }
    public int RouteCount { get; set; }
    public int RouteRuleCount { get; set; }
    public List<string> PostgresOutcomes { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Turns a live Kubernetes resource (as a JSON node) into a clean, re-appliable
/// manifest by stripping server-managed and instance-specific fields — the
/// equivalent of a "kubectl get -o yaml" cleanup. Used for both typed objects
/// (serialized via KubernetesJson first) and raw custom resources.
/// </summary>
internal static class ImportManifestSanitizer
{
    private static readonly ISerializer Yaml = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithIndentedSequences()
        .Build();

    public static string ToYaml(JsonNode node)
    {
        Sanitize(node);
        object? graph = ToGraph(node);
        return Yaml.Serialize(graph ?? new Dictionary<string, object?>());
    }

    private static void Sanitize(JsonNode node)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        obj.Remove("status");

        string? kind = obj["kind"]?.GetValue<string>();

        if (obj["metadata"] is JsonObject meta)
        {
            foreach (string field in ServerManagedMetadata)
            {
                meta.Remove(field);
            }

            if (meta["annotations"] is JsonObject annotations)
            {
                annotations.Remove("kubectl.kubernetes.io/last-applied-configuration");
                annotations.Remove("deployment.kubernetes.io/revision");

                if (annotations.Count == 0)
                {
                    meta.Remove("annotations");
                }
            }

            if (meta["labels"] is JsonObject labels)
            {
                labels.Remove("kubernetes.io/metadata.name");
            }
        }

        // Service-only volatile fields that the API server assigns.
        if (kind == "Service" && obj["spec"] is JsonObject spec)
        {
            spec.Remove("clusterIP");
            spec.Remove("clusterIPs");
        }

        // Strip the per-pod-template creation timestamp on workloads.
        if (obj["spec"] is JsonObject ws
            && ws["template"] is JsonObject tmpl
            && tmpl["metadata"] is JsonObject tmplMeta)
        {
            tmplMeta.Remove("creationTimestamp");
        }
    }

    private static readonly string[] ServerManagedMetadata =
    [
        "uid", "resourceVersion", "generation", "creationTimestamp",
        "managedFields", "ownerReferences", "selfLink"
    ];

    private static object? ToGraph(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                // Keep empty objects ({}) — they can be meaningful (e.g. emptyDir: {}).
                // Only individual null-valued keys are dropped.
                Dictionary<string, object?> dict = new();
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    object? value = ToGraph(kv.Value);
                    if (value is not null)
                    {
                        dict[kv.Key] = value;
                    }
                }
                return dict;
            case JsonArray array:
                List<object?> list = new();
                foreach (JsonNode? element in array)
                {
                    list.Add(ToGraph(element));
                }
                return list;
            case JsonValue value:
                if (value.TryGetValue(out bool b)) return b;
                if (value.TryGetValue(out long l)) return l;
                if (value.TryGetValue(out double d)) return d;
                if (value.TryGetValue(out string? s)) return s;
                return value.ToString();
            default:
                return null;
        }
    }
}
