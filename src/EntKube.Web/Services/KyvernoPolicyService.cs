using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Services;

/// <summary>
/// Manages Kyverno admission policies at tenant+environment scope.
/// Provides CRUD operations, generates Policy CRD manifests, and applies
/// them to every app namespace the tenant owns in a given environment.
/// </summary>
public class KyvernoPolicyService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
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

        if (deployments.Count == 0)
            return [("(no deployments)", false, "No deployments found for this tenant in this environment.")];

        // Build unique (cluster, namespace) pairs using the locked ns when available.
        var targets = deployments
            .Select(d =>
            {
                string? ns = lockedNs.TryGetValue(d.AppId, out string? locked) && !string.IsNullOrWhiteSpace(locked)
                    ? locked : d.Namespace;
                return (Cluster: d.Cluster!, Namespace: ns);
            })
            .Where(t => t.Cluster is not null && !string.IsNullOrWhiteSpace(t.Namespace))
            .DistinctBy(t => (t.Cluster.Id, t.Namespace))
            .ToList();

        var results = new List<(string Target, bool Success, string Output)>();
        foreach (var (cluster, ns) in targets)
        {
            (bool ok, string output) = await ApplyToNamespaceAsync(policies, cluster, ns!, ct);
            results.Add(($"{cluster.Name}/{ns}", ok, output));
        }
        return results;
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
