namespace EntKube.Web.Services.Upgrades;

/// <summary>One manifest object using an API version that upstream removes (or has removed).</summary>
public sealed record DeprecatedApiUsage
{
    /// <summary>The apiVersion as written in the manifest, e.g. "policy/v1beta1".</summary>
    public required string ApiVersion { get; init; }

    public required string Kind { get; init; }

    /// <summary>metadata.name when it could be read, for pointing the operator at the object.</summary>
    public string? Name { get; init; }

    /// <summary>Kubernetes minor version that removes this API, e.g. "1.25".</summary>
    public required string RemovedInMinor { get; init; }

    /// <summary>The apiVersion to migrate to.</summary>
    public required string ReplacedBy { get; init; }

    /// <summary>True when the target cluster is already at or past the removing version.</summary>
    public bool AlreadyRemoved { get; init; }

    /// <summary>1-based index of the YAML document within the manifest set, for locating it.</summary>
    public int DocumentIndex { get; init; }
}

/// <summary>
/// Finds Kubernetes APIs that a manifest still uses but upstream has removed or will
/// remove, so a control-plane upgrade doesn't silently break a workload.
///
/// This scans the DESIRED manifests EntKube stores, not live cluster objects. That is
/// the more useful direction: the API server rewrites objects it serves to a current
/// version, so a live read often shows the new apiVersion even while the stored
/// manifest still carries the removed one — and it is the stored manifest that breaks
/// on the next apply after the upgrade.
///
/// The table is curated rather than exhaustive: it covers the removals that actually
/// bite in practice. An unlisted apiVersion is reported as fine, so this can produce
/// false negatives but never a false alarm — the right direction for a check that
/// gates an upgrade.
/// </summary>
public static class DeprecatedApiScanner
{
    private sealed record Removal(string RemovedInMinor, string ReplacedBy);

    /// <summary>
    /// Keyed by "apiVersion/Kind" where the removal is kind-specific, or by bare
    /// "apiVersion" where the whole group version went away.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Removal> Removals =
        new Dictionary<string, Removal>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Removed in 1.16 ──
            ["extensions/v1beta1/Deployment"] = new("1.16", "apps/v1"),
            ["extensions/v1beta1/DaemonSet"] = new("1.16", "apps/v1"),
            ["extensions/v1beta1/ReplicaSet"] = new("1.16", "apps/v1"),
            ["apps/v1beta1"] = new("1.16", "apps/v1"),
            ["apps/v1beta2"] = new("1.16", "apps/v1"),

            // ── Removed in 1.22 ──
            ["extensions/v1beta1/Ingress"] = new("1.22", "networking.k8s.io/v1"),
            ["networking.k8s.io/v1beta1/Ingress"] = new("1.22", "networking.k8s.io/v1"),
            ["networking.k8s.io/v1beta1/IngressClass"] = new("1.22", "networking.k8s.io/v1"),
            ["apiextensions.k8s.io/v1beta1"] = new("1.22", "apiextensions.k8s.io/v1"),
            ["admissionregistration.k8s.io/v1beta1"] = new("1.22", "admissionregistration.k8s.io/v1"),
            ["rbac.authorization.k8s.io/v1beta1"] = new("1.22", "rbac.authorization.k8s.io/v1"),
            ["certificates.k8s.io/v1beta1"] = new("1.22", "certificates.k8s.io/v1"),
            ["coordination.k8s.io/v1beta1"] = new("1.22", "coordination.k8s.io/v1"),
            ["scheduling.k8s.io/v1beta1"] = new("1.22", "scheduling.k8s.io/v1"),
            ["storage.k8s.io/v1beta1/CSIDriver"] = new("1.22", "storage.k8s.io/v1"),
            ["storage.k8s.io/v1beta1/CSINode"] = new("1.22", "storage.k8s.io/v1"),
            ["storage.k8s.io/v1beta1/StorageClass"] = new("1.22", "storage.k8s.io/v1"),
            ["storage.k8s.io/v1beta1/VolumeAttachment"] = new("1.22", "storage.k8s.io/v1"),

            // ── Removed in 1.25 ──
            ["batch/v1beta1/CronJob"] = new("1.25", "batch/v1"),
            ["discovery.k8s.io/v1beta1/EndpointSlice"] = new("1.25", "discovery.k8s.io/v1"),
            ["events.k8s.io/v1beta1/Event"] = new("1.25", "events.k8s.io/v1"),
            ["autoscaling/v2beta1"] = new("1.25", "autoscaling/v2"),
            ["policy/v1beta1/PodDisruptionBudget"] = new("1.25", "policy/v1"),
            ["policy/v1beta1/PodSecurityPolicy"] = new("1.25", "(removed — use Pod Security Admission or Kyverno)"),
            ["node.k8s.io/v1beta1/RuntimeClass"] = new("1.25", "node.k8s.io/v1"),

            // ── Removed in 1.26 ──
            ["autoscaling/v2beta2"] = new("1.26", "autoscaling/v2"),
            ["flowcontrol.apiserver.k8s.io/v1beta1"] = new("1.26", "flowcontrol.apiserver.k8s.io/v1"),

            // ── Removed in 1.27 ──
            ["storage.k8s.io/v1beta1/CSIStorageCapacity"] = new("1.27", "storage.k8s.io/v1"),

            // ── Removed in 1.29 ──
            ["flowcontrol.apiserver.k8s.io/v1beta2"] = new("1.29", "flowcontrol.apiserver.k8s.io/v1"),

            // ── Removed in 1.32 ──
            ["flowcontrol.apiserver.k8s.io/v1beta3"] = new("1.32", "flowcontrol.apiserver.k8s.io/v1"),
        };

    /// <summary>
    /// Scans a multi-document YAML manifest for removed APIs.
    /// </summary>
    /// <param name="manifestYaml">One or more YAML documents separated by "---".</param>
    /// <param name="targetMinor">
    /// The Kubernetes minor to judge against ("1.31"), normally the cluster's current
    /// version. Usages removed at or below it are flagged <see cref="DeprecatedApiUsage.AlreadyRemoved"/>;
    /// later removals are reported as upcoming. Null reports every known removal as upcoming.
    /// </param>
    public static IReadOnlyList<DeprecatedApiUsage> Scan(string? manifestYaml, string? targetMinor)
    {
        if (string.IsNullOrWhiteSpace(manifestYaml))
        {
            return [];
        }

        SemVer? target = SemVer.Parse(targetMinor);
        List<DeprecatedApiUsage> usages = [];

        string[] documents = SplitDocuments(manifestYaml);
        for (int i = 0; i < documents.Length; i++)
        {
            string? apiVersion = ReadScalar(documents[i], "apiVersion");
            string? kind = ReadScalar(documents[i], "kind");

            if (apiVersion is null || kind is null)
            {
                continue;
            }

            // Kind-specific entries win over whole-group-version ones, so a group that lost
            // only some kinds doesn't flag the ones that survived.
            if (!Removals.TryGetValue($"{apiVersion}/{kind}", out Removal? removal)
                && !Removals.TryGetValue(apiVersion, out removal))
            {
                continue;
            }

            SemVer? removedIn = SemVer.Parse(removal.RemovedInMinor);
            bool alreadyRemoved = target is not null
                && removedIn is not null
                && target >= removedIn;

            usages.Add(new DeprecatedApiUsage
            {
                ApiVersion = apiVersion,
                Kind = kind,
                Name = ReadScalar(documents[i], "name"),
                RemovedInMinor = removal.RemovedInMinor,
                ReplacedBy = removal.ReplacedBy,
                AlreadyRemoved = alreadyRemoved,
                DocumentIndex = i + 1,
            });
        }

        return usages;
    }

    /// <summary>
    /// Splits a multi-document YAML string on document markers. Deliberately simple —
    /// only a "---" alone on its own line separates documents, so a "---" inside a block
    /// scalar or a string value is not mistaken for one.
    /// </summary>
    private static string[] SplitDocuments(string yaml) =>
        yaml.Replace("\r\n", "\n")
            .Split("\n---", StringSplitOptions.None)
            .Select(d => d.TrimStart('-').TrimStart('\n'))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToArray();

    /// <summary>
    /// Reads a top-level scalar (apiVersion, kind) or the first "name:" from a document.
    ///
    /// A deliberately shallow reader rather than a full YAML parse: manifests routinely
    /// contain Helm template syntax and CRD bodies that a strict parser rejects outright,
    /// and refusing to scan a file because one unrelated field won't parse would mean
    /// missing the removed API sitting two lines above it.
    /// </summary>
    private static string? ReadScalar(string document, string key)
    {
        bool topLevelOnly = key is "apiVersion" or "kind";

        foreach (string rawLine in document.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            // apiVersion/kind must sit at column zero; a nested one belongs to an embedded
            // object (a CRD's template, a List item) and is not this document's own.
            if (topLevelOnly && (line[0] == ' ' || line[0] == '\t'))
            {
                continue;
            }

            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }

            string value = trimmed[(key.Length + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }
}
