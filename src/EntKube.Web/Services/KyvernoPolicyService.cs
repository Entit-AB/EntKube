using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Services;

/// <summary>
/// A live, cluster-scoped Kyverno <c>ClusterPolicy</c> observed on a cluster but NOT
/// owned by EntKube. Surfaced read-only for visibility — EntKube only authors namespaced
/// <c>Policy</c> objects, so a ClusterPolicy is assumed to be managed outside EntKube.
/// </summary>
public class KyvernoClusterPolicyInfo
{
    public required string Name { get; set; }
    public required string ClusterName { get; set; }
    public KyvernoValidationFailureAction Mode { get; set; }
    public int RuleCount { get; set; }
}

/// <summary>Summary of a reverse-discovery run that adopts live Kyverno policies into EntKube.</summary>
public class KyvernoDiscoveryResult
{
    /// <summary>Number of policies newly adopted into the DB.</summary>
    public int Detected { get; set; }

    /// <summary>Display names of the adopted policies (custom ones flagged).</summary>
    public List<string> PolicyNames { get; set; } = [];

    /// <summary>Per-target failures (cluster/namespace unreachable, CRD missing, etc.).</summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// Manages Kyverno admission policies at tenant+environment scope.
/// Provides CRUD operations, generates Policy CRD manifests, and applies
/// them to every app namespace the tenant owns in a given environment.
/// </summary>
public class KyvernoPolicyService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate,
    ILogger<KyvernoPolicyService> logger)
{
    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<List<KyvernoPolicy>> GetPoliciesAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.KyvernoPolicies
            .Where(p => p.TenantId == tenantId && p.EnvironmentId == environmentId)
            .OrderBy(p => p.PolicyType)
            .ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Enables a built-in policy type for a tenant+environment.
    /// If a policy of this type already exists, it is updated in place.
    /// </summary>
    public async Task<KyvernoPolicy> EnablePolicyAsync(
        Guid tenantId, Guid environmentId,
        KyvernoPolicyType type,
        KyvernoValidationFailureAction mode,
        string? configuration = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KyvernoPolicy? existing = await db.KyvernoPolicies
            .FirstOrDefaultAsync(p => p.TenantId == tenantId
                                   && p.EnvironmentId == environmentId
                                   && p.PolicyType == type
                                   && p.PolicyType != KyvernoPolicyType.Custom, ct);

        if (existing is not null)
        {
            existing.ValidationFailureAction = mode;
            existing.Configuration = configuration;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new KyvernoPolicy
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EnvironmentId = environmentId,
                PolicyType = type,
                ValidationFailureAction = mode,
                Configuration = configuration
            };
            db.KyvernoPolicies.Add(existing);
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>Updates the validation mode and/or configuration of an existing policy by ID.</summary>
    public async Task<KyvernoPolicy> UpdatePolicyAsync(
        Guid id,
        KyvernoValidationFailureAction mode,
        string? configuration,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KyvernoPolicy policy = await db.KyvernoPolicies.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Kyverno policy {id} not found.");
        policy.ValidationFailureAction = mode;
        policy.Configuration = configuration;
        policy.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return policy;
    }

    /// <summary>Adds a custom raw-YAML Kyverno policy.</summary>
    public async Task<KyvernoPolicy> AddCustomPolicyAsync(
        Guid tenantId, Guid environmentId,
        string name,
        KyvernoValidationFailureAction mode,
        string customYaml,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KyvernoPolicy policy = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EnvironmentId = environmentId,
            PolicyType = KyvernoPolicyType.Custom,
            ValidationFailureAction = mode,
            Name = name.Trim().ToLowerInvariant(),
            CustomYaml = customYaml
        };
        db.KyvernoPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        return policy;
    }

    /// <summary>Disables (deletes) a policy by ID.</summary>
    public async Task<bool> DeletePolicyAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KyvernoPolicy? policy = await db.KyvernoPolicies.FindAsync([id], ct);
        if (policy is null) return false;
        db.KyvernoPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Copies all Kyverno policies from one environment to another, replacing the target.</summary>
    public async Task CopyFromEnvironmentAsync(
        Guid tenantId, Guid sourceEnvId, Guid targetEnvId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KyvernoPolicy> source = await db.KyvernoPolicies
            .Where(p => p.TenantId == tenantId && p.EnvironmentId == sourceEnvId)
            .ToListAsync(ct);

        List<KyvernoPolicy> target = await db.KyvernoPolicies
            .Where(p => p.TenantId == tenantId && p.EnvironmentId == targetEnvId)
            .ToListAsync(ct);

        db.KyvernoPolicies.RemoveRange(target);

        foreach (KyvernoPolicy src in source)
        {
            db.KyvernoPolicies.Add(new KyvernoPolicy
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EnvironmentId = targetEnvId,
                PolicyType = src.PolicyType,
                ValidationFailureAction = src.ValidationFailureAction,
                Name = src.Name,
                Configuration = src.Configuration,
                CustomYaml = src.CustomYaml
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Cluster availability check ────────────────────────────────────────────

    /// <summary>
    /// Returns true if at least one cluster registered for this tenant+environment
    /// has Kyverno installed (ComponentStatus.Installed).
    /// </summary>
    public async Task<bool> IsKyvernoAvailableAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<Guid> clusterIds = await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId && c.EnvironmentId == environmentId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (clusterIds.Count == 0) return false;

        return await db.ClusterComponents
            .AnyAsync(c => clusterIds.Contains(c.ClusterId)
                        && c.Status == ComponentStatus.Installed
                        && (c.Name == "kyverno"
                            || c.HelmChartName == "kyverno"
                            || c.ReleaseName == "kyverno"), ct);
    }

    // ── Cluster-wide policies (observed, not owned) ────────────────────────────

    /// <summary>
    /// Lists live cluster-scoped <c>ClusterPolicy</c> objects on every cluster registered
    /// for this tenant+environment. These are surfaced read-only: EntKube only authors
    /// namespaced <c>Policy</c> objects, so a ClusterPolicy is treated as externally managed
    /// (ArgoCD/Flux/manual). Best-effort — clusters where the CRD is absent or that are
    /// unreachable are silently skipped.
    /// </summary>
    public async Task<List<KyvernoClusterPolicyInfo>> GetClusterPoliciesAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Where(c => c.TenantId == tenantId && c.EnvironmentId == environmentId && c.KubeconfigSecretId != null)
            .ToListAsync(ct);

        List<KyvernoClusterPolicyInfo> result = [];

        foreach (KubernetesCluster cluster in clusters)
        {
            string json;
            try
            {
                json = await k8s.GetJsonAllNamespacesAsync(
                    "clusterpolicies.kyverno.io", cluster.Kubeconfig!, ct: ct);
            }
            catch
            {
                // ClusterPolicy CRD not installed or cluster unreachable — skip.
                continue;
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch { continue; }

            using JsonDocument _ = doc;

            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement item in items.EnumerateArray())
            {
                string? name = item.TryGetProperty("metadata", out JsonElement meta)
                            && meta.TryGetProperty("name", out JsonElement nameEl)
                    ? nameEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(name)) continue;

                int ruleCount = item.TryGetProperty("spec", out JsonElement spec)
                             && spec.TryGetProperty("rules", out JsonElement rules)
                             && rules.ValueKind == JsonValueKind.Array
                    ? rules.GetArrayLength() : 0;

                result.Add(new KyvernoClusterPolicyInfo
                {
                    Name = name,
                    ClusterName = cluster.Name,
                    Mode = ReadFailureAction(item),
                    RuleCount = ruleCount
                });
            }
        }

        return result.OrderBy(p => p.ClusterName).ThenBy(p => p.Name).ToList();
    }

    // ── Apply to environment ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves every (cluster, namespace) pair for apps in the tenant+environment,
    /// then applies the Kyverno Policy manifests to each namespace via kubectl.
    /// Returns one output string per cluster, keyed by cluster name.
    /// </summary>
    public async Task<List<(string Target, bool Success, string Output)>> ApplyToEnvironmentAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KyvernoPolicy> policies = await GetPoliciesAsync(tenantId, environmentId, ct);
        if (policies.Count == 0)
            return [("(no policies)", false, "No policies configured — nothing to apply.")];

        List<(KubernetesCluster Cluster, string Namespace)> targets =
            await ResolveTargetsAsync(db, tenantId, environmentId, ct);

        if (targets.Count == 0)
            return [("(no deployments)", false, "No deployments found for this tenant in this environment.")];

        var results = new List<(string Target, bool Success, string Output)>();
        foreach (var (cluster, ns) in targets)
        {
            (bool ok, string output) = await ApplyToNamespaceAsync(policies, cluster, ns, ct);
            results.Add(($"{cluster.Name}/{ns}", ok, output));
        }
        return results;
    }

    /// <summary>
    /// Resolves the unique (cluster, namespace) pairs that make up this tenant's
    /// app footprint in an environment — the same targets policies are applied to.
    /// Uses the governance-locked AppEnvironment namespace when present.
    /// </summary>
    private static async Task<List<(KubernetesCluster Cluster, string Namespace)>> ResolveTargetsAsync(
        ApplicationDbContext db, Guid tenantId, Guid environmentId, CancellationToken ct)
    {
        // Collect all app IDs for this tenant (App → Customer → Tenant).
        List<Guid> appIds = await db.Apps
            .Where(a => a.Customer.TenantId == tenantId)
            .Select(a => a.Id)
            .ToListAsync(ct);

        // Locked namespaces from AppEnvironment (governance namespace lock).
        Dictionary<Guid, string?> lockedNs = (await db.AppEnvironments
            .Where(ae => appIds.Contains(ae.AppId) && ae.EnvironmentId == environmentId)
            .Select(ae => new { ae.AppId, ae.Namespace })
            .ToListAsync(ct))
            .ToDictionary(x => x.AppId, x => x.Namespace);

        // All deployments for these apps in this environment, with cluster kubeconfig.
        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.Cluster)
            .Where(d => appIds.Contains(d.AppId) && d.EnvironmentId == environmentId)
            .ToListAsync(ct);

        // Build unique (cluster, namespace) pairs using the locked ns when available.
        return deployments
            .Select(d =>
            {
                string? ns = lockedNs.TryGetValue(d.AppId, out string? locked) && !string.IsNullOrWhiteSpace(locked)
                    ? locked : d.Namespace;
                return (Cluster: d.Cluster!, Namespace: ns);
            })
            .Where(t => t.Cluster is not null && !string.IsNullOrWhiteSpace(t.Namespace))
            .DistinctBy(t => (t.Cluster.Id, t.Namespace))
            .Select(t => (t.Cluster, t.Namespace!))
            .ToList();
    }

    // ── Reverse discovery (adopt live policies) ────────────────────────────────

    /// <summary>
    /// Reverse of <see cref="ApplyToEnvironmentAsync"/>: scans the tenant's app
    /// namespaces for live Kyverno <c>Policy</c> resources and adopts them into the
    /// DB so an imported/pre-existing cluster reflects its true policy state instead
    /// of showing everything disabled.
    ///
    /// Recognised EntKube-shaped policies (matched by resource name) become their
    /// built-in <see cref="KyvernoPolicyType"/> with the live validationFailureAction;
    /// parametrised ones (registries/labels) recover their list from the rule message.
    /// Anything unrecognised is adopted as a Custom policy carrying the sanitised YAML.
    /// Built-ins already present (singleton per type) and custom names already present
    /// are skipped, so it is safe to re-run.
    /// </summary>
    public async Task<KyvernoDiscoveryResult> DiscoverPoliciesAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        KyvernoDiscoveryResult result = new();
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<(KubernetesCluster Cluster, string Namespace)> targets =
            await ResolveTargetsAsync(db, tenantId, environmentId, ct);

        if (targets.Count == 0)
        {
            result.Errors.Add("No app namespaces found for this tenant in this environment.");
            return result;
        }

        List<KyvernoPolicy> existing = await db.KyvernoPolicies
            .Where(p => p.TenantId == tenantId && p.EnvironmentId == environmentId)
            .ToListAsync(ct);

        HashSet<KyvernoPolicyType> seenBuiltIns = existing
            .Where(p => p.PolicyType != KyvernoPolicyType.Custom)
            .Select(p => p.PolicyType)
            .ToHashSet();

        HashSet<string> seenCustomNames = existing
            .Where(p => p.PolicyType == KyvernoPolicyType.Custom && p.Name is not null)
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<KyvernoPolicy> toAdd = [];

        foreach ((KubernetesCluster cluster, string ns) in targets)
        {
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig)) continue;

            string json;
            try
            {
                json = await k8s.GetJsonAsync("policies.kyverno.io", ns, cluster.Kubeconfig!, ct: ct);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{cluster.Name}/{ns}: {ex.Message}");
                continue;
            }

            ParseDiscoveredPolicies(json, tenantId, environmentId,
                seenBuiltIns, seenCustomNames, toAdd, result);
        }

        if (toAdd.Count > 0)
        {
            db.KyvernoPolicies.AddRange(toAdd);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Adopted {Count} live Kyverno policies for tenant {Tenant} / env {Env}",
                toAdd.Count, tenantId, environmentId);
        }

        return result;
    }

    /// <summary>Maps a live Kyverno Policy resource name back to its built-in type (reverse of BuildPolicyYaml).</summary>
    private static readonly Dictionary<string, KyvernoPolicyType> NameToType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disallow-privileged-containers"]  = KyvernoPolicyType.DisallowPrivilegedContainers,
        ["disallow-root-user"]              = KyvernoPolicyType.DisallowRootUser,
        ["require-readonly-rootfs"]         = KyvernoPolicyType.RequireReadOnlyRootFilesystem,
        ["disallow-privilege-escalation"]   = KyvernoPolicyType.DisallowPrivilegeEscalation,
        ["disallow-host-network"]           = KyvernoPolicyType.DisallowHostNetwork,
        ["disallow-host-pid"]               = KyvernoPolicyType.DisallowHostPID,
        ["disallow-host-path"]              = KyvernoPolicyType.DisallowHostPath,
        ["restrict-image-registries"]       = KyvernoPolicyType.RestrictImageRegistries,
        ["require-resource-limits"]         = KyvernoPolicyType.RequireResourceLimits,
        ["require-resource-requests"]       = KyvernoPolicyType.RequireResourceRequests,
        ["require-seccomp-profile"]         = KyvernoPolicyType.RequireSeccompProfile,
        ["require-pod-labels"]              = KyvernoPolicyType.RequirePodLabels,
    };

    private static void ParseDiscoveredPolicies(
        string json, Guid tenantId, Guid environmentId,
        HashSet<KyvernoPolicyType> seenBuiltIns, HashSet<string> seenCustomNames,
        List<KyvernoPolicy> toAdd, KyvernoDiscoveryResult result)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }

        using JsonDocument _ = doc;

        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in items.EnumerateArray())
        {
            string? name = item.TryGetProperty("metadata", out JsonElement meta)
                        && meta.TryGetProperty("name", out JsonElement nameEl)
                ? nameEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(name)) continue;

            KyvernoValidationFailureAction mode = ReadFailureAction(item);

            if (NameToType.TryGetValue(name, out KyvernoPolicyType type))
            {
                // Built-in singleton per type — skip if already known/seen.
                if (!seenBuiltIns.Add(type)) continue;

                KyvernoPolicy policy = new()
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EnvironmentId = environmentId,
                    PolicyType = type,
                    ValidationFailureAction = mode
                };

                // Recover the parametrised lists from the rule message where possible.
                if (type == KyvernoPolicyType.RestrictImageRegistries)
                    policy.Configuration = ExtractConfigFromMessage(item, "approved registry:");
                else if (type == KyvernoPolicyType.RequirePodLabels)
                    policy.Configuration = ExtractConfigFromMessage(item, "required labels:");

                toAdd.Add(policy);
                result.Detected++;
                result.PolicyNames.Add(name);
            }
            else
            {
                // Unrecognised — adopt as a Custom policy preserving the live YAML.
                if (!seenCustomNames.Add(name)) continue;

                string yaml;
                try { yaml = ImportManifestSanitizer.ToYaml(JsonNode.Parse(item.GetRawText())!); }
                catch { continue; }

                toAdd.Add(new KyvernoPolicy
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EnvironmentId = environmentId,
                    PolicyType = KyvernoPolicyType.Custom,
                    ValidationFailureAction = mode,
                    Name = name,
                    CustomYaml = yaml
                });
                result.Detected++;
                result.PolicyNames.Add($"{name} (custom)");
            }
        }
    }

    /// <summary>
    /// Reads the effective validation failure action from a live Policy: the
    /// spec-level <c>validationFailureAction</c> (what BuildPolicyYaml emits) with a
    /// fallback to the newer per-rule <c>validate.failureAction</c>. Defaults to Audit.
    /// </summary>
    private static KyvernoValidationFailureAction ReadFailureAction(JsonElement policy)
    {
        if (!policy.TryGetProperty("spec", out JsonElement spec))
            return KyvernoValidationFailureAction.Audit;

        if (spec.TryGetProperty("validationFailureAction", out JsonElement vfa)
            && string.Equals(vfa.GetString(), "Enforce", StringComparison.OrdinalIgnoreCase))
            return KyvernoValidationFailureAction.Enforce;

        if (spec.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement rule in rules.EnumerateArray())
            {
                if (rule.TryGetProperty("validate", out JsonElement validate)
                    && validate.TryGetProperty("failureAction", out JsonElement fa)
                    && string.Equals(fa.GetString(), "Enforce", StringComparison.OrdinalIgnoreCase))
                    return KyvernoValidationFailureAction.Enforce;
            }
        }

        return KyvernoValidationFailureAction.Audit;
    }

    /// <summary>
    /// Recovers a comma-separated config list from a parametrised policy's rule message
    /// (e.g. "…approved registry: ghcr.io/org, docker.io"). Returns a JSON array string,
    /// or null when the marker isn't present. Mirrors the messages BuildPolicyYaml emits.
    /// </summary>
    private static string? ExtractConfigFromMessage(JsonElement policy, string marker)
    {
        if (!policy.TryGetProperty("spec", out JsonElement spec)
            || !spec.TryGetProperty("rules", out JsonElement rules)
            || rules.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement rule in rules.EnumerateArray())
        {
            if (!rule.TryGetProperty("validate", out JsonElement validate)
                || !validate.TryGetProperty("message", out JsonElement msgEl))
                continue;

            string? message = msgEl.GetString();
            int idx = message?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (message is null || idx < 0) continue;

            string tail = message[(idx + marker.Length)..].TrimEnd('.', ' ');
            List<string> items = tail
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (items.Count > 0)
                return SerializeConfigList(items);
        }

        return null;
    }

    /// <summary>Applies the Kyverno Policy YAML for all policies to a single namespace via kubectl.</summary>
    public async Task<(bool Success, string Output)> ApplyToNamespaceAsync(
        List<KyvernoPolicy> policies, KubernetesCluster cluster, string ns, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (false, "Cluster has no kubeconfig configured.");

        string yaml = BuildManifest(policies, ns);
        if (string.IsNullOrWhiteSpace(yaml))
            return (false, "No policies generated (check configuration).");

        await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
        {
            Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
            Kubeconfig = cluster.Kubeconfig,
            ClusterLabel = cluster.Name,
            Namespace = ns,
            Summary = $"Apply Kyverno policies to {ns}",
            Manifest = yaml,
        }, ct);

        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-kyverno-{Guid.NewGuid():N}.kubeconfig");
        string manifestPath   = Path.Combine(Path.GetTempPath(), $"entkube-kyverno-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, cluster.Kubeconfig, ct);
            await File.WriteAllTextAsync(manifestPath, yaml, ct);

            System.Diagnostics.ProcessStartInfo psi = new("kubectl",
                $"apply -f {manifestPath} --kubeconfig {kubeconfigPath}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using System.Diagnostics.Process proc = new() { StartInfo = psi };
            StringBuilder output = new();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            bool ok = proc.ExitCode == 0;
            if (ok)
                logger.LogInformation("Kyverno policies applied to {Cluster}/{Namespace}", cluster.Name, ns);
            else
                logger.LogWarning("Kyverno apply failed for {Cluster}/{Namespace}: {Output}", cluster.Name, ns, output);

            return (ok, output.ToString().TrimEnd());
        }
        finally
        {
            if (File.Exists(kubeconfigPath)) File.Delete(kubeconfigPath);
            if (File.Exists(manifestPath))   File.Delete(manifestPath);
        }
    }

    // ── Configuration helpers ─────────────────────────────────────────────────

    public static List<string> GetConfigList(KyvernoPolicy? policy)
    {
        if (policy?.Configuration is null) return [];
        try { return JsonSerializer.Deserialize<List<string>>(policy.Configuration) ?? []; }
        catch { return []; }
    }

    public static string SerializeConfigList(IEnumerable<string> items) =>
        JsonSerializer.Serialize(items.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList());

    // ── Manifest builder ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates Kyverno Policy CRD YAML for all enabled policies for the given namespace,
    /// separated by "---". Returns an empty string when there are no policies.
    /// </summary>
    public static string BuildManifest(List<KyvernoPolicy> policies, string ns)
    {
        if (policies.Count == 0) return string.Empty;

        List<string> docs = [];
        foreach (KyvernoPolicy policy in policies)
        {
            string? yaml = BuildPolicyYaml(policy, ns);
            if (!string.IsNullOrWhiteSpace(yaml))
                docs.Add(yaml.Trim());
        }

        return string.Join("\n---\n", docs);
    }

    // Namespaced Kyverno Policy objects only ever match resources in their own namespace, so a
    // namespace-based `exclude` is both redundant and rejected by the admission webhook
    // ("Filtering namespaces not allowed in namespaced policies"). We deploy these per app namespace,
    // and those namespaces are always app namespaces — never kube internals or component namespaces —
    // so no exclude is needed. The placeholder is kept empty so callers need no changes if a future
    // ClusterPolicy variant ever needs to reinstate exclusions.
    private const string ExcludeSystemNamespaces = "";

    private static string? BuildPolicyYaml(KyvernoPolicy policy, string ns)
    {
        string mode = policy.ValidationFailureAction == KyvernoValidationFailureAction.Enforce
            ? "Enforce" : "Audit";
        string excl = ExcludeSystemNamespaces;

        return policy.PolicyType switch
        {
            KyvernoPolicyType.DisallowPrivilegedContainers => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: disallow-privileged-containers
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-privileged
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "Privileged containers are not allowed."
                        pattern:
                          spec:
                            =(initContainers):
                              - =(securityContext):
                                  =(privileged): "false"
                            containers:
                              - =(securityContext):
                                  =(privileged): "false"
                """,

            KyvernoPolicyType.DisallowRootUser => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: disallow-root-user
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-runasnonroot
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "Containers must not run as root. Set runAsNonRoot: true or runAsUser > 0."
                        anyPattern:
                          - spec:
                              securityContext:
                                runAsNonRoot: true
                          - spec:
                              securityContext:
                                runAsUser: ">0"
                """,

            KyvernoPolicyType.RequireReadOnlyRootFilesystem => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: require-readonly-rootfs
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-readonly-rootfs
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "Containers must use a read-only root filesystem."
                        pattern:
                          spec:
                            containers:
                              - securityContext:
                                  readOnlyRootFilesystem: true
                """,

            KyvernoPolicyType.DisallowPrivilegeEscalation => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: disallow-privilege-escalation
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-no-escalation
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "Privilege escalation is not allowed."
                        pattern:
                          spec:
                            =(initContainers):
                              - securityContext:
                                  allowPrivilegeEscalation: false
                            containers:
                              - securityContext:
                                  allowPrivilegeEscalation: false
                """,

            KyvernoPolicyType.DisallowHostNetwork => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: disallow-host-network
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-host-network
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "Host networking is not allowed."
                        pattern:
                          spec:
                            =(hostNetwork): false
                """,

            KyvernoPolicyType.DisallowHostPID => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: disallow-host-pid
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-host-pid
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "Host process ID namespace sharing is not allowed."
                        pattern:
                          spec:
                            =(hostPID): false
                """,

            KyvernoPolicyType.DisallowHostPath => BuildHostPathPolicy(ns, mode, excl),

            KyvernoPolicyType.RestrictImageRegistries => BuildImageRegistriesPolicy(policy, ns, mode, excl),

            KyvernoPolicyType.RequireResourceLimits => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: require-resource-limits
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-resource-limits
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "All containers must specify CPU and memory limits."
                        pattern:
                          spec:
                            containers:
                              - resources:
                                  limits:
                                    memory: "?*"
                                    cpu: "?*"
                """,

            KyvernoPolicyType.RequireResourceRequests => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: require-resource-requests
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-resource-requests
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "All containers must specify CPU and memory requests."
                        pattern:
                          spec:
                            containers:
                              - resources:
                                  requests:
                                    memory: "?*"
                                    cpu: "?*"
                """,

            KyvernoPolicyType.RequireSeccompProfile => $"""
                apiVersion: kyverno.io/v1
                kind: Policy
                metadata:
                  name: require-seccomp-profile
                  namespace: {ns}
                spec:
                  validationFailureAction: {mode}
                  background: true
                  rules:
                    - name: check-seccomp-profile
                      match:
                        any:
                          - resources:
                              kinds:
                                - Pod
                {excl}
                      validate:
                        message: "Pods must have a seccomp profile set to RuntimeDefault or Localhost."
                        anyPattern:
                          - spec:
                              securityContext:
                                seccompProfile:
                                  type: "RuntimeDefault | Localhost"
                          - spec:
                              initContainers:
                                - =(securityContext):
                                    seccompProfile:
                                      type: "RuntimeDefault | Localhost"
                              containers:
                                - securityContext:
                                    seccompProfile:
                                      type: "RuntimeDefault | Localhost"
                """,

            KyvernoPolicyType.RequirePodLabels => BuildRequirePodLabelsPolicy(policy, ns, mode, excl),

            KyvernoPolicyType.Custom when !string.IsNullOrWhiteSpace(policy.CustomYaml) =>
                policy.CustomYaml,

            _ => null
        };
    }

    private static string BuildHostPathPolicy(string ns, string mode, string excl)
    {
        // The Kyverno JMESPath expression contains {{ }} which would conflict with C# raw string
        // interpolation, so we compose it via a local variable.
        const string jmesPath = "{{ request.object.spec.volumes[].hostPath | length(@) }}";
        return $"""
            apiVersion: kyverno.io/v1
            kind: Policy
            metadata:
              name: disallow-host-path
              namespace: {ns}
            spec:
              validationFailureAction: {mode}
              background: true
              rules:
                - name: check-host-path
                  match:
                    any:
                      - resources:
                          kinds:
                            - Pod
            {excl}
                  validate:
                    message: "HostPath volumes are not allowed."
                    deny:
                      conditions:
                        any:
                          - key: "{jmesPath}"
                            operator: GreaterThan
                            value: "0"
            """;
    }

    private static string? BuildImageRegistriesPolicy(KyvernoPolicy policy, string ns, string mode, string excl)
    {
        List<string> registries = GetConfigList(policy);
        if (registries.Count == 0) return null;

        string pattern = string.Join(" | ", registries.Select(r =>
            r.TrimEnd('/') + (r.Contains('*') ? "" : "/*")));

        return $"""
            apiVersion: kyverno.io/v1
            kind: Policy
            metadata:
              name: restrict-image-registries
              namespace: {ns}
            spec:
              validationFailureAction: {mode}
              background: false
              rules:
                - name: validate-registries
                  match:
                    any:
                      - resources:
                          kinds:
                            - Pod
            {excl}
                  validate:
                    message: "Images must come from an approved registry: {string.Join(", ", registries)}"
                    pattern:
                      spec:
                        =(initContainers):
                          - image: "{pattern}"
                        containers:
                          - image: "{pattern}"
            """;
    }

    private static string? BuildRequirePodLabelsPolicy(KyvernoPolicy policy, string ns, string mode, string excl)
    {
        List<string> labels = GetConfigList(policy);
        if (labels.Count == 0) return null;

        var labelsYaml = new StringBuilder();
        foreach (string label in labels)
            labelsYaml.AppendLine($"              {label}: \"?*\"");

        return $"""
            apiVersion: kyverno.io/v1
            kind: Policy
            metadata:
              name: require-pod-labels
              namespace: {ns}
            spec:
              validationFailureAction: {mode}
              background: true
              rules:
                - name: check-required-labels
                  match:
                    any:
                      - resources:
                          kinds:
                            - Pod
            {excl}
                  validate:
                    message: "Pods must have all required labels: {string.Join(", ", labels)}"
                    pattern:
                      metadata:
                        labels:
            {labelsYaml.ToString().TrimEnd()}
            """;
    }
}
