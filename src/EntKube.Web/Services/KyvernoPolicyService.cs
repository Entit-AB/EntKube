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

        List<(KubernetesCluster Cluster, string Namespace)> targets =
            await ResolveTargetsAsync(db, tenantId, environmentId, ct);

        var results = new List<(string Target, bool Success, string Output)>();

        // The cluster-scoped policy is reconciled even when nothing is enabled, because "nothing
        // enabled" is exactly when a previously applied ClusterPolicy has to be removed.
        foreach (KubernetesCluster cluster in targets.Select(t => t.Cluster).DistinctBy(c => c.Id))
        {
            (bool cpOk, string cpOutput) = await ReconcileClusterRbacPolicyAsync(cluster, ct);
            if (!string.IsNullOrWhiteSpace(cpOutput))
                results.Add(($"{cluster.Name} (cluster RBAC)", cpOk, cpOutput));
        }

        if (policies.Count == 0)
        {
            results.Add(("(no policies)", false, "No namespaced policies configured — nothing to apply."));
            return results;
        }

        if (targets.Count == 0)
        {
            results.Add(("(no deployments)", false, "No deployments found for this tenant in this environment."));
            return results;
        }

        foreach (var (cluster, ns) in targets)
        {
            (bool ok, string output) = await ApplyToNamespaceAsync(policies, cluster, ns, ct);
            results.Add(($"{cluster.Name}/{ns}", ok, output));
        }
        return results;
    }

    /// <summary>
    /// Every customer app namespace on a cluster, across every tenant and environment.
    ///
    /// Deliberately not scoped to the tenant that triggered the apply. A ClusterPolicy is a single
    /// cluster-wide object, so building it from one tenant's namespaces would silently drop every
    /// other tenant's from the deny-list the moment that tenant applied — the protection would
    /// disappear for whoever did not apply most recently. Cluster-wide state has to be computed
    /// from cluster-wide data.
    /// </summary>
    public static async Task<List<string>> ResolveClusterAppNamespacesAsync(
        ApplicationDbContext db, Guid clusterId, CancellationToken ct)
    {
        var deployments = await db.AppDeployments
            .Where(d => d.ClusterId == clusterId)
            .Select(d => new { d.AppId, d.EnvironmentId, d.Namespace })
            .ToListAsync(ct);

        if (deployments.Count == 0) return [];

        List<Guid> appIds = deployments.Select(d => d.AppId).Distinct().ToList();

        Dictionary<(Guid, Guid), string?> locked = (await db.AppEnvironments
            .Where(ae => appIds.Contains(ae.AppId))
            .Select(ae => new { ae.AppId, ae.EnvironmentId, ae.Namespace })
            .ToListAsync(ct))
            .ToDictionary(x => (x.AppId, x.EnvironmentId), x => x.Namespace);

        return deployments
            .Select(d => locked.TryGetValue((d.AppId, d.EnvironmentId), out string? ns)
                         && !string.IsNullOrWhiteSpace(ns)
                ? ns!
                : d.Namespace)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The mode the cluster-scoped RBAC policy should run in, or null when no tenant or environment
    /// with a footprint on this cluster has it enabled.
    ///
    /// Strongest setting wins. With one object shared by every tenant on the cluster, last-writer
    /// -wins would let one tenant's apply quietly drop another's policy from Enforce to Audit, and
    /// the two would flip it back and forth on every deploy.
    /// </summary>
    public static async Task<KyvernoValidationFailureAction?> ResolveClusterRbacModeAsync(
        ApplicationDbContext db, Guid clusterId, CancellationToken ct)
    {
        var scopes = await db.AppDeployments
            .Where(d => d.ClusterId == clusterId)
            .Select(d => new { d.App.Customer.TenantId, d.EnvironmentId })
            .Distinct()
            .ToListAsync(ct);

        if (scopes.Count == 0) return null;

        List<Guid> tenantIds = scopes.Select(x => x.TenantId).Distinct().ToList();
        List<Guid> envIds = scopes.Select(x => x.EnvironmentId).Distinct().ToList();

        List<KyvernoPolicy> candidates = await db.KyvernoPolicies
            .Where(p => p.PolicyType == KyvernoPolicyType.RestrictClusterRbac
                        && tenantIds.Contains(p.TenantId)
                        && envIds.Contains(p.EnvironmentId))
            .ToListAsync(ct);

        // The query above is a cross-product of the two id lists, so narrow it back to the
        // (tenant, environment) pairs that actually have a footprint here.
        HashSet<(Guid, Guid)> live = scopes.Select(x => (x.TenantId, x.EnvironmentId)).ToHashSet();

        List<KyvernoPolicy> enabled = candidates
            .Where(p => live.Contains((p.TenantId, p.EnvironmentId)))
            .ToList();

        if (enabled.Count == 0) return null;

        return enabled.Any(p => p.ValidationFailureAction == KyvernoValidationFailureAction.Enforce)
            ? KyvernoValidationFailureAction.Enforce
            : KyvernoValidationFailureAction.Audit;
    }

    /// <summary>
    /// Brings the cluster's <c>restrict-cluster-rbac</c> ClusterPolicy in line with the database —
    /// applying it with the current app-namespace list, or removing it when nobody has it enabled.
    ///
    /// Must run after every deploy, not only when the policy is toggled. The namespace list is data,
    /// not configuration: a newly created app namespace is not in the policy that was written before
    /// it existed, and until the policy is rewritten that namespace is the one place the deny-list
    /// does not cover.
    /// </summary>
    public async Task<(bool Success, string Output)> ReconcileClusterRbacPolicyAsync(
        KubernetesCluster cluster, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (false, "Cluster has no kubeconfig configured.");

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KyvernoValidationFailureAction? mode = await ResolveClusterRbacModeAsync(db, cluster.Id, ct);
        List<string> appNamespaces = mode is null
            ? []
            : await ResolveClusterAppNamespacesAsync(db, cluster.Id, ct);

        string? yaml = mode is null
            ? null
            : BuildClusterRbacPolicy(appNamespaces,
                mode == KyvernoValidationFailureAction.Enforce ? "Enforce" : "Audit");

        if (yaml is null)
        {
            // Disabled, or nothing left to protect. Either way the object must go — leaving it
            // would keep enforcing a namespace list that no longer reflects the cluster.
            return await RunKubectlAsync(cluster,
                $"delete clusterpolicy {ClusterRbacPolicyName} --ignore-not-found", ct);
        }

        await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
        {
            Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
            Kubeconfig = cluster.Kubeconfig,
            ClusterLabel = cluster.Name,
            Summary = $"Apply cluster RBAC policy covering {appNamespaces.Count} app namespace(s)",
            Manifest = yaml,
        }, ct);

        string manifestPath = Path.Combine(Path.GetTempPath(), $"entkube-kyverno-cp-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(manifestPath, yaml, ct);
            return await RunKubectlAsync(cluster, $"apply -f {manifestPath}", ct);
        }
        finally
        {
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
    }

    /// <summary>Runs one kubectl command against a cluster with its kubeconfig in a temp file.</summary>
    private async Task<(bool Success, string Output)> RunKubectlAsync(
        KubernetesCluster cluster, string args, CancellationToken ct)
    {
        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-kyverno-{Guid.NewGuid():N}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, cluster.Kubeconfig, ct);

            System.Diagnostics.ProcessStartInfo psi = new("kubectl", $"{args} --kubeconfig {kubeconfigPath}")
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
            if (!ok)
                logger.LogWarning("kubectl {Args} failed on {Cluster}: {Output}", args, cluster.Name, output);

            return (ok, output.ToString().TrimEnd());
        }
        finally
        {
            if (File.Exists(kubeconfigPath)) File.Delete(kubeconfigPath);
        }
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
        ["verify-image-signatures"]         = KyvernoPolicyType.VerifyImageSignatures,
        ["require-resource-limits"]         = KyvernoPolicyType.RequireResourceLimits,
        ["require-resource-requests"]       = KyvernoPolicyType.RequireResourceRequests,
        ["require-seccomp-profile"]         = KyvernoPolicyType.RequireSeccompProfile,
        ["require-pod-labels"]              = KyvernoPolicyType.RequirePodLabels,
        ["restrict-rbac"]                   = KyvernoPolicyType.RestrictRbac,
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

            KyvernoPolicyType.VerifyImageSignatures => BuildVerifyImagesPolicy(policy, ns, mode),

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

            KyvernoPolicyType.RestrictRbac => BuildRestrictRbacPolicy(ns, mode, excl),

            KyvernoPolicyType.Custom when !string.IsNullOrWhiteSpace(policy.CustomYaml) =>
                policy.CustomYaml,

            _ => null
        };
    }

    private static string BuildHostPathPolicy(string ns, string mode, string excl)
    {
        // The Kyverno JMESPath expression contains {{ }} which would conflict with C# raw string
        // interpolation, so we compose it via a local variable.
        //
        // The `|| `[]`` is not decoration. A Pod with no volumes at all — cert-manager's ACME
        // solver, a plain sidecar-less worker — projects `spec.volumes[].hostPath` to null, and
        // length(null) is not a type error JMESPath tolerates: the rule fails to evaluate, and a
        // rule that fails to evaluate under the default failurePolicy of Fail DENIES the pod.
        // The policy that was meant to reject hostPath mounts instead rejects the pods that have
        // no volumes whatsoever, which is every pod it should have waved through. Defaulting the
        // projection to an empty list makes the nil case length 0, and 0 is not GreaterThan 0.
        const string jmesPath = "{{ request.object.spec.volumes[].hostPath || `[]` | length(@) }}";
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

    /// <summary>Name of the single cluster-scoped RBAC policy. One per cluster, not per namespace.</summary>
    public const string ClusterRbacPolicyName = "restrict-cluster-rbac";

    /// <summary>
    /// Groups that are never an acceptable subject of a ClusterRoleBinding, whatever namespaces
    /// exist. <c>system:serviceaccounts</c> is every ServiceAccount in the cluster; the other two
    /// are every user who can authenticate, and every user who cannot.
    /// </summary>
    private static readonly string[] AlwaysDeniedGroups =
        ["system:serviceaccounts", "system:authenticated", "system:anonymous"];

    /// <summary>
    /// Builds the cluster-scoped half of the RBAC deny-list: a Kyverno <c>ClusterPolicy</c> that
    /// refuses to let a ClusterRoleBinding grant cluster-wide permissions to anything living in a
    /// customer app namespace.
    ///
    /// The obvious design — deny ClusterRole and ClusterRoleBinding except from trusted callers —
    /// cannot work here. EntKube installs catalog components and customer Helm charts with the same
    /// cluster credential, so admission sees one subject for both and has nothing to tell them
    /// apart. Excluding "component namespaces" fails for the same reason: a ClusterRoleBinding has
    /// no namespace of its own to exclude.
    ///
    /// What distinguishes the two is not who created the binding but who it grants to. A component's
    /// ClusterRoleBinding names a ServiceAccount in the component's own namespace; the escalation
    /// this exists to stop names a ServiceAccount in an app namespace. So the policy carries the
    /// list of app namespaces — which EntKube already computes to decide where to apply the
    /// namespaced policies — and denies on the subject. Components need no exclusion list at all,
    /// because they were never matched.
    ///
    /// Creating a ClusterRole is left alone deliberately. A ClusterRole that is bound to nothing
    /// grants nothing, and denying the kind outright would break every operator chart in the
    /// catalog for no gain.
    ///
    /// Returns null when <paramref name="appNamespaces"/> is empty — with no app namespaces the
    /// subject list is empty, and an AnyIn against an empty list matches nothing, so the policy
    /// would be an object that does nothing but look like protection.
    /// </summary>
    public static string? BuildClusterRbacPolicy(IReadOnlyCollection<string> appNamespaces, string mode)
    {
        if (appNamespaces.Count == 0) return null;

        string[] ordered = appNamespaces
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0) return null;

        // A binding may name a namespace's ServiceAccounts collectively rather than one by name,
        // which grants the same thing to everything the app runs.
        string[] deniedGroups = AlwaysDeniedGroups
            .Concat(ordered.Select(n => $"system:serviceaccounts:{n}"))
            .ToArray();

        string saNamespaces = string.Join("\n", ordered.Select(n => $"                  - \"{n}\""));
        string groupNames   = string.Join("\n", deniedGroups.Select(g => $"                  - \"{g}\""));

        // Composed rather than inlined: these contain {{ }}, which collides with C# interpolation.
        // The `|| `[]`` defaults matter for the same reason they do in the namespaced policies — a
        // binding with no ServiceAccount subject projects to null, and a rule that fails to
        // evaluate denies the resource under the default failurePolicy of Fail.
        const string saNamespacePath =
            "{{ request.object.subjects[?kind=='ServiceAccount'].namespace || `[]` }}";
        const string groupNamePath =
            "{{ request.object.subjects[?kind=='Group'].name || `[]` }}";

        return $"""
            apiVersion: kyverno.io/v1
            kind: ClusterPolicy
            metadata:
              name: {ClusterRbacPolicyName}
            spec:
              validationFailureAction: {mode}
              background: true
              rules:
                - name: deny-cluster-rbac-to-app-namespaces
                  match:
                    any:
                      - resources:
                          kinds:
                            - ClusterRoleBinding
                  exclude:
                    any:
                      - resources:
                          names:
                            - "system:*"
                  validate:
                    message: "A ClusterRoleBinding may not grant cluster-wide permissions to a customer app namespace."
                    deny:
                      conditions:
                        any:
                          - key: "{saNamespacePath}"
                            operator: AnyIn
                            value:
            {saNamespaces}
                          - key: "{groupNamePath}"
                            operator: AnyIn
                            value:
            {groupNames}
            """;
    }

    private static string BuildRestrictRbacPolicy(string ns, string mode, string excl)
    {
        // Admission-time half of the RBAC deny-list. AppRbacRuleValidator enforces the same set
        // when a rule is written through the governance UI; this catches every other way a Role
        // can reach the namespace — a Helm chart's templates, a hand-applied manifest, an operator
        // that creates RBAC for its own CRs. The two lists must stay identical: a difference
        // between them shows up as a Role that saves cleanly and is then refused at apply, with
        // nothing to say which side is right.
        //
        // The `|| `[]`` defaults are load-bearing for the same reason they are in the hostPath
        // policy above. A Role whose rules omit apiGroups projects to null, AnyIn against null is
        // an evaluation failure, and a rule that fails to evaluate under the default failurePolicy
        // of Fail denies the resource — so the omission would block ordinary Roles rather than
        // dangerous ones.
        const string apiGroupsPath = "{{ request.object.rules[].apiGroups[] || `[]` }}";
        const string resourcesPath = "{{ request.object.rules[].resources[] || `[]` }}";
        const string verbsPath     = "{{ request.object.rules[].verbs[] || `[]` }}";
        const string roleRefKind   = "{{ request.object.roleRef.kind }}";
        const string roleRefName   = "{{ request.object.roleRef.name }}";

        return $"""
            apiVersion: kyverno.io/v1
            kind: Policy
            metadata:
              name: restrict-rbac
              namespace: {ns}
            spec:
              validationFailureAction: {mode}
              background: true
              rules:
                - name: restrict-role-rules
                  match:
                    any:
                      - resources:
                          kinds:
                            - Role
            {excl}
                  validate:
                    message: "A Role may not use wildcards, manage RBAC, or grant exec, attach or port-forward."
                    deny:
                      conditions:
                        any:
                          - key: "{apiGroupsPath}"
                            operator: AnyIn
                            value: ["*"]
                          - key: "{resourcesPath}"
                            operator: AnyIn
                            value:
                              - "*"
                              - "roles"
                              - "rolebindings"
                              - "clusterroles"
                              - "clusterrolebindings"
                              - "pods/exec"
                              - "pods/attach"
                              - "pods/portforward"
                          - key: "{verbsPath}"
                            operator: AnyIn
                            value: ["*", "escalate", "bind", "impersonate"]
                - name: restrict-rolebinding-target
                  match:
                    any:
                      - resources:
                          kinds:
                            - RoleBinding
            {excl}
                  validate:
                    message: "A RoleBinding may not bind to a privileged built-in ClusterRole."
                    deny:
                      conditions:
                        all:
                          - key: "{roleRefKind}"
                            operator: Equals
                            value: "ClusterRole"
                          - key: "{roleRefName}"
                            operator: AnyIn
                            value: ["cluster-admin", "admin", "edit"]
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

    /// <summary>
    /// Configuration for <see cref="KyvernoPolicyType.VerifyImageSignatures"/>. Stored as
    /// JSON in <see cref="KyvernoPolicy.Configuration"/> because, unlike the other built-ins,
    /// this policy needs structured settings rather than a flat list of strings.
    /// </summary>
    public sealed record VerifyImagesConfig
    {
        /// <summary>Image glob patterns the rule applies to, e.g. "registry.example.com/*".</summary>
        public List<string> ImagePatterns { get; init; } = [];

        /// <summary>Cosign public key (PEM). Set this OR the keyless issuer/subject pair.</summary>
        public string? PublicKey { get; init; }

        /// <summary>Keyless (OIDC) issuer, e.g. "https://token.actions.githubusercontent.com".</summary>
        public string? Issuer { get; init; }

        /// <summary>Keyless identity, e.g. "https://github.com/acme/app/.github/workflows/build.yml@refs/heads/main".</summary>
        public string? Subject { get; init; }

        /// <summary>
        /// When true an unsigned image is rejected. When false Kyverno only verifies
        /// signatures that exist, which is a materially weaker guarantee.
        /// </summary>
        public bool Required { get; init; } = true;

        /// <summary>True when this config has enough to produce a usable policy.</summary>
        public bool IsUsable =>
            ImagePatterns.Count > 0
            && (!string.IsNullOrWhiteSpace(PublicKey)
                || (!string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Subject)));
    }

    public static VerifyImagesConfig GetVerifyImagesConfig(KyvernoPolicy? policy)
    {
        if (policy?.Configuration is null) return new VerifyImagesConfig();
        try { return JsonSerializer.Deserialize<VerifyImagesConfig>(policy.Configuration) ?? new(); }
        catch { return new VerifyImagesConfig(); }
    }

    public static string SerializeVerifyImagesConfig(VerifyImagesConfig config) =>
        JsonSerializer.Serialize(config);

    /// <summary>
    /// Builds a Kyverno <c>verifyImages</c> rule — signature verification, not a validate
    /// pattern, so it uses a different rule shape from every other built-in here.
    ///
    /// Written with explicit indentation rather than an interpolated raw string literal:
    /// the attestor block nests seven levels deep and embeds a PEM block scalar, and
    /// getting that alignment wrong produces YAML that parses as a different shape (or
    /// not at all) while still looking plausible in the source.
    ///
    /// Returns null when the config is incomplete rather than emitting a policy that would
    /// verify nothing: a signature policy that silently passes everything is worse than no
    /// policy, because it reads as protection on the governance page.
    /// </summary>
    private static string? BuildVerifyImagesPolicy(KyvernoPolicy policy, string ns, string mode)
    {
        VerifyImagesConfig config = GetVerifyImagesConfig(policy);
        if (!config.IsUsable) return null;

        StringBuilder yaml = new();
        yaml.Append("apiVersion: kyverno.io/v1\n");
        yaml.Append("kind: Policy\n");
        yaml.Append("metadata:\n");
        yaml.Append("  name: verify-image-signatures\n");
        yaml.Append($"  namespace: {ns}\n");
        yaml.Append("spec:\n");
        yaml.Append($"  validationFailureAction: {mode}\n");
        // Signature verification must run at admission, not as a background re-scan: the
        // point is to stop an unsigned image being admitted in the first place.
        yaml.Append("  background: false\n");
        yaml.Append("  rules:\n");
        yaml.Append("    - name: verify-signature\n");
        yaml.Append("      match:\n");
        yaml.Append("        any:\n");
        yaml.Append("          - resources:\n");
        yaml.Append("              kinds:\n");
        yaml.Append("                - Pod\n");
        yaml.Append("      verifyImages:\n");
        yaml.Append("        - imageReferences:\n");

        foreach (string pattern in config.ImagePatterns.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            yaml.Append($"            - \"{pattern}\"\n");
        }

        yaml.Append($"          required: {(config.Required ? "true" : "false")}\n");
        // mutateDigest rewrites the tag to the verified digest, so what is admitted is
        // exactly what was verified — a mutable tag cannot be swapped afterwards.
        yaml.Append("          mutateDigest: true\n");
        yaml.Append("          verifyDigest: true\n");
        yaml.Append("          attestors:\n");
        yaml.Append("            - count: 1\n");
        yaml.Append("              entries:\n");

        if (!string.IsNullOrWhiteSpace(config.PublicKey))
        {
            yaml.Append("                - keys:\n");
            yaml.Append("                    publicKeys: |-\n");
            foreach (string line in config.PublicKey.Replace("\r\n", "\n").Split('\n'))
            {
                yaml.Append("                      ").Append(line).Append('\n');
            }
        }
        else
        {
            yaml.Append("                - keyless:\n");
            yaml.Append($"                    issuer: \"{config.Issuer}\"\n");
            yaml.Append($"                    subject: \"{config.Subject}\"\n");
            yaml.Append("                    rekor:\n");
            yaml.Append("                      url: https://rekor.sigstore.dev\n");
        }

        return yaml.ToString();
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
