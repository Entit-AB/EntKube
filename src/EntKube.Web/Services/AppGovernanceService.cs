using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Manages app-level governance: namespace assignment, ResourceQuota,
/// NetworkPolicy, and RBAC (ServiceAccount/Role/RoleBinding).
///
/// All governance is now scoped per-environment. Each environment an app is
/// deployed to can have its own quota, network policies, and RBAC. When
/// applying to clusters, only the clusters that host deployments for that
/// specific environment are targeted.
/// </summary>
public class AppGovernanceService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<AppGovernanceService> logger)
{
    // ── Namespace ─────────────────────────────────────────────────────────────

    public async Task<List<AppEnvironment>> GetAppEnvironmentsAsync(Guid appId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppEnvironments
            .Where(ae => ae.AppId == appId)
            .ToListAsync(ct);
    }

    public async Task<string?> GetNamespaceAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == environmentId, ct);
        return ae?.Namespace;
    }

    public async Task SaveNamespaceAsync(
        Guid appId, Guid environmentId, string? ns, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == environmentId, ct);
        if (ae is null) return;
        ae.Namespace = string.IsNullOrWhiteSpace(ns) ? null : ns.Trim().ToLowerInvariant();
        await db.SaveChangesAsync(ct);
    }

    // ── Quota ─────────────────────────────────────────────────────────────────

    public async Task<AppQuota?> GetQuotaAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppQuotas
            .FirstOrDefaultAsync(q => q.AppId == appId && q.EnvironmentId == environmentId, ct);
    }

    public async Task<AppQuota> SaveQuotaAsync(
        Guid appId, Guid environmentId,
        string? cpuRequest, string? cpuLimit,
        string? memRequest, string? memLimit,
        int? maxPods, int? maxPvcs,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppQuota? existing = await db.AppQuotas
            .FirstOrDefaultAsync(q => q.AppId == appId && q.EnvironmentId == environmentId, ct);

        if (existing is null)
        {
            existing = new AppQuota { Id = Guid.NewGuid(), AppId = appId, EnvironmentId = environmentId };
            db.AppQuotas.Add(existing);
        }

        existing.CpuRequest    = Blank(cpuRequest);
        existing.CpuLimit      = Blank(cpuLimit);
        existing.MemoryRequest = Blank(memRequest);
        existing.MemoryLimit   = Blank(memLimit);
        existing.MaxPods       = maxPods;
        existing.MaxPvcs       = maxPvcs;
        existing.UpdatedAt     = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteQuotaAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppQuota? q = await db.AppQuotas
            .FirstOrDefaultAsync(x => x.AppId == appId && x.EnvironmentId == environmentId, ct);
        if (q is null) return false;
        db.AppQuotas.Remove(q);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Network policies ──────────────────────────────────────────────────────

    public async Task<List<AppNetworkPolicy>> GetNetworkPoliciesAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppNetworkPolicies
            .Where(p => p.AppId == appId && p.EnvironmentId == environmentId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<AppNetworkPolicy> AddNetworkPolicyAsync(
        Guid appId, Guid environmentId, string name, AppNetworkPolicyType type,
        string? allowFromNs = null, string? customYaml = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppNetworkPolicy policy = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            EnvironmentId = environmentId,
            Name = name.Trim().ToLowerInvariant(),
            PolicyType = type,
            AllowFromNamespace = Blank(allowFromNs),
            CustomYaml = customYaml
        };
        db.AppNetworkPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        return policy;
    }

    public async Task<bool> DeleteNetworkPolicyAsync(Guid policyId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppNetworkPolicy? p = await db.AppNetworkPolicies.FindAsync([policyId], ct);
        if (p is null) return false;
        db.AppNetworkPolicies.Remove(p);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── RBAC ──────────────────────────────────────────────────────────────────

    public async Task<AppRbacPolicy?> GetRbacPolicyAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppRbacPolicies
            .Include(p => p.Rules)
            .FirstOrDefaultAsync(p => p.AppId == appId && p.EnvironmentId == environmentId, ct);
    }

    public async Task<AppRbacPolicy> SaveRbacPolicyAsync(
        Guid appId, Guid environmentId, string serviceAccountName, bool autoMount,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppRbacPolicy? existing = await db.AppRbacPolicies
            .FirstOrDefaultAsync(p => p.AppId == appId && p.EnvironmentId == environmentId, ct);

        if (existing is null)
        {
            existing = new AppRbacPolicy
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                EnvironmentId = environmentId,
                ServiceAccountName = serviceAccountName.Trim().ToLowerInvariant()
            };
            db.AppRbacPolicies.Add(existing);
        }
        else
        {
            existing.ServiceAccountName = serviceAccountName.Trim().ToLowerInvariant();
        }

        existing.AutoMountToken = autoMount;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<AppRbacRule> AddRbacRuleAsync(
        Guid rbacPolicyId, string apiGroups, string resources, string verbs,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppRbacRule rule = new()
        {
            Id = Guid.NewGuid(),
            AppRbacPolicyId = rbacPolicyId,
            ApiGroups = apiGroups.Trim(),
            Resources = resources.Trim(),
            Verbs = verbs.Trim()
        };
        db.AppRbacRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<bool> DeleteRbacRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppRbacRule? r = await db.AppRbacRules.FindAsync([ruleId], ct);
        if (r is null) return false;
        db.AppRbacRules.Remove(r);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteRbacPolicyAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppRbacPolicy? p = await db.AppRbacPolicies
            .FirstOrDefaultAsync(x => x.AppId == appId && x.EnvironmentId == environmentId, ct);
        if (p is null) return false;
        db.AppRbacPolicies.Remove(p);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Load all governance for an app+environment ────────────────────────────

    public async Task<AppGovernanceData> LoadAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        return new AppGovernanceData
        {
            EnvironmentId    = environmentId,
            Namespace        = await GetNamespaceAsync(appId, environmentId, ct),
            Quota            = await GetQuotaAsync(appId, environmentId, ct),
            NetworkPolicies  = await GetNetworkPoliciesAsync(appId, environmentId, ct),
            RbacPolicy       = await GetRbacPolicyAsync(appId, environmentId, ct),
            AllowedDatabases = await GetAllowedDatabasesAsync(appId, environmentId, ct),
            AllowedCaches    = await GetAllowedCachesAsync(appId, environmentId, ct),
            AllowedStorages  = await GetAllowedStoragesAsync(appId, environmentId, ct)
        };
    }

    // ── Copy governance from one environment to another ───────────────────────

    /// <summary>
    /// Copies quota, network policies, and RBAC from <paramref name="sourceEnvId"/>
    /// to <paramref name="targetEnvId"/>, overwriting any existing settings.
    /// </summary>
    public async Task CopyFromEnvironmentAsync(
        Guid appId, Guid sourceEnvId, Guid targetEnvId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // ── Namespace ──
        AppEnvironment? srcAe = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == sourceEnvId, ct);
        AppEnvironment? dstAe = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == targetEnvId, ct);
        if (srcAe is not null && dstAe is not null)
            dstAe.Namespace = srcAe.Namespace;

        // ── Quota ──
        AppQuota? srcQuota = await db.AppQuotas
            .FirstOrDefaultAsync(q => q.AppId == appId && q.EnvironmentId == sourceEnvId, ct);

        AppQuota? dstQuota = await db.AppQuotas
            .FirstOrDefaultAsync(q => q.AppId == appId && q.EnvironmentId == targetEnvId, ct);

        if (srcQuota is not null)
        {
            if (dstQuota is null)
            {
                dstQuota = new AppQuota { Id = Guid.NewGuid(), AppId = appId, EnvironmentId = targetEnvId };
                db.AppQuotas.Add(dstQuota);
            }
            dstQuota.CpuRequest    = srcQuota.CpuRequest;
            dstQuota.CpuLimit      = srcQuota.CpuLimit;
            dstQuota.MemoryRequest = srcQuota.MemoryRequest;
            dstQuota.MemoryLimit   = srcQuota.MemoryLimit;
            dstQuota.MaxPods       = srcQuota.MaxPods;
            dstQuota.MaxPvcs       = srcQuota.MaxPvcs;
            dstQuota.UpdatedAt     = DateTime.UtcNow;
        }

        // ── Network policies ── (replace target's policies with source's)
        List<AppNetworkPolicy> srcPolicies = await db.AppNetworkPolicies
            .Where(p => p.AppId == appId && p.EnvironmentId == sourceEnvId)
            .ToListAsync(ct);

        List<AppNetworkPolicy> dstPolicies = await db.AppNetworkPolicies
            .Where(p => p.AppId == appId && p.EnvironmentId == targetEnvId)
            .ToListAsync(ct);

        db.AppNetworkPolicies.RemoveRange(dstPolicies);

        foreach (AppNetworkPolicy src in srcPolicies)
        {
            db.AppNetworkPolicies.Add(new AppNetworkPolicy
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                EnvironmentId = targetEnvId,
                Name = src.Name,
                PolicyType = src.PolicyType,
                AllowFromNamespace = src.AllowFromNamespace,
                CustomYaml = src.CustomYaml
            });
        }

        // ── RBAC ──
        AppRbacPolicy? srcRbac = await db.AppRbacPolicies
            .Include(p => p.Rules)
            .FirstOrDefaultAsync(p => p.AppId == appId && p.EnvironmentId == sourceEnvId, ct);

        AppRbacPolicy? dstRbac = await db.AppRbacPolicies
            .Include(p => p.Rules)
            .FirstOrDefaultAsync(p => p.AppId == appId && p.EnvironmentId == targetEnvId, ct);

        if (srcRbac is not null)
        {
            if (dstRbac is null)
            {
                dstRbac = new AppRbacPolicy
                {
                    Id = Guid.NewGuid(),
                    AppId = appId,
                    EnvironmentId = targetEnvId,
                    ServiceAccountName = srcRbac.ServiceAccountName
                };
                db.AppRbacPolicies.Add(dstRbac);
            }
            else
            {
                dstRbac.ServiceAccountName = srcRbac.ServiceAccountName;
                db.AppRbacRules.RemoveRange(dstRbac.Rules);
            }

            dstRbac.AutoMountToken = srcRbac.AutoMountToken;

            foreach (AppRbacRule rule in srcRbac.Rules)
            {
                db.AppRbacRules.Add(new AppRbacRule
                {
                    Id = Guid.NewGuid(),
                    AppRbacPolicyId = dstRbac.Id,
                    ApiGroups = rule.ApiGroups,
                    Resources = rule.Resources,
                    Verbs = rule.Verbs
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ── K8s apply ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies all governance resources (Namespace, ResourceQuota, NetworkPolicies,
    /// ServiceAccount/Role/RoleBinding) to a specific cluster. Uses the deployment's
    /// namespace — not a global app namespace — so each environment targets the
    /// correct namespace on the correct cluster.
    /// </summary>
    public async Task<(bool Success, string Output)> ApplyToClusterAsync(
        Guid appId, Guid environmentId, KubernetesCluster cluster,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Resolve namespace from the deployment targeting this cluster in this environment.
        AppDeployment? deployment = await db.AppDeployments
            .Where(d => d.AppId == appId
                     && d.EnvironmentId == environmentId
                     && d.ClusterId == cluster.Id)
            .FirstOrDefaultAsync(ct);

        string? govNs = await GetNamespaceAsync(appId, environmentId, ct);
        string? ns = govNs ?? deployment?.Namespace;
        if (string.IsNullOrWhiteSpace(ns))
            return (false, "No deployment found for this app in this environment on this cluster. Create a deployment first.");

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (false, "Cluster has no kubeconfig configured.");

        AppGovernanceData data = await LoadAsync(appId, environmentId, ct);
        string yaml = BuildManifest(data, ns);

        string kubeconfigPath = Path.Combine(Path.GetTempPath(), $"entkube-gov-{Guid.NewGuid():N}.kubeconfig");
        string manifestPath   = Path.Combine(Path.GetTempPath(), $"entkube-gov-{Guid.NewGuid():N}.yaml");

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
            string result = output.ToString().TrimEnd();

            if (ok)
                logger.LogInformation("Governance applied to {Cluster}/{Namespace}", cluster.Name, ns);
            else
                logger.LogWarning("Governance apply failed for {Cluster}: {Output}", cluster.Name, result);

            return (ok, result);
        }
        finally
        {
            if (File.Exists(kubeconfigPath)) File.Delete(kubeconfigPath);
            if (File.Exists(manifestPath))   File.Delete(manifestPath);
        }
    }

    // ── Manifest builder ──────────────────────────────────────────────────────

    public static string BuildManifest(AppGovernanceData data, string ns)
    {
        var docs = new List<string>();

        // 1. Namespace
        docs.Add(Y(
            "apiVersion: v1",
            "kind: Namespace",
            "metadata:",
            $"  name: {ns}"));

        // 2. ResourceQuota
        if (data.Quota is { } q)
        {
            var hard = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(q.CpuRequest))    hard.AppendLine($"    requests.cpu: \"{q.CpuRequest}\"");
            if (!string.IsNullOrWhiteSpace(q.CpuLimit))      hard.AppendLine($"    limits.cpu: \"{q.CpuLimit}\"");
            if (!string.IsNullOrWhiteSpace(q.MemoryRequest)) hard.AppendLine($"    requests.memory: \"{q.MemoryRequest}\"");
            if (!string.IsNullOrWhiteSpace(q.MemoryLimit))   hard.AppendLine($"    limits.memory: \"{q.MemoryLimit}\"");
            if (q.MaxPods.HasValue)  hard.AppendLine($"    pods: \"{q.MaxPods}\"");
            if (q.MaxPvcs.HasValue)  hard.AppendLine($"    persistentvolumeclaims: \"{q.MaxPvcs}\"");

            if (hard.Length > 0)
            {
                docs.Add(Y(
                    "apiVersion: v1",
                    "kind: ResourceQuota",
                    "metadata:",
                    $"  name: app-quota",
                    $"  namespace: {ns}",
                    "spec:",
                    "  hard:",
                    hard.ToString().TrimEnd()));
            }
        }

        // 3. NetworkPolicies
        foreach (AppNetworkPolicy np in data.NetworkPolicies)
        {
            string header = Y(
                "apiVersion: networking.k8s.io/v1",
                "kind: NetworkPolicy",
                "metadata:",
                $"  name: {np.Name}",
                $"  namespace: {ns}",
                "spec:",
                "  podSelector: {}");

            string policyYaml = np.PolicyType switch
            {
                AppNetworkPolicyType.DenyAll =>
                    header + "\n  policyTypes:\n    - Ingress\n    - Egress",

                AppNetworkPolicyType.AllowFromIngress =>
                    header + Y(
                        "  ingress:",
                        "    - from:",
                        "        - namespaceSelector:",
                        "            matchExpressions:",
                        "              - key: kubernetes.io/metadata.name",
                        "                operator: In",
                        "                values:",
                        "                  - ingress-nginx",
                        "                  - traefik"),

                AppNetworkPolicyType.AllowFromSameNamespace =>
                    header + Y(
                        "  ingress:",
                        "    - from:",
                        "        - podSelector: {}"),

                AppNetworkPolicyType.AllowFromNamespace
                    when !string.IsNullOrWhiteSpace(np.AllowFromNamespace) =>
                    header + Y(
                        "  ingress:",
                        "    - from:",
                        "        - namespaceSelector:",
                        "            matchLabels:",
                        $"              kubernetes.io/metadata.name: {np.AllowFromNamespace}"),

                AppNetworkPolicyType.Custom when !string.IsNullOrWhiteSpace(np.CustomYaml) =>
                    np.CustomYaml,

                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(policyYaml))
                docs.Add(policyYaml.Trim());
        }

        // 4. ServiceAccount + Role + RoleBinding
        if (data.RbacPolicy is { } rbac && rbac.Rules.Count > 0)
        {
            string sa = rbac.ServiceAccountName;

            docs.Add(Y(
                "apiVersion: v1",
                "kind: ServiceAccount",
                "metadata:",
                $"  name: {sa}",
                $"  namespace: {ns}",
                $"automountServiceAccountToken: {rbac.AutoMountToken.ToString().ToLowerInvariant()}"));

            var rulesYaml = new StringBuilder();
            foreach (AppRbacRule rule in rbac.Rules)
            {
                string apiGroupsYaml = string.Join(", ",
                    (string.IsNullOrWhiteSpace(rule.ApiGroups)
                        ? [""]
                        : rule.ApiGroups.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Select(g => $"\"{g.Trim()}\""));

                string resourcesYaml = string.Join(", ",
                    rule.Resources.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(r => $"\"{r.Trim()}\""));

                string verbsYaml = string.Join(", ",
                    rule.Verbs.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => $"\"{v.Trim()}\""));

                rulesYaml.AppendLine($"  - apiGroups: [{apiGroupsYaml}]");
                rulesYaml.AppendLine($"    resources: [{resourcesYaml}]");
                rulesYaml.AppendLine($"    verbs: [{verbsYaml}]");
            }

            docs.Add(Y(
                "apiVersion: rbac.authorization.k8s.io/v1",
                "kind: Role",
                "metadata:",
                $"  name: {sa}-role",
                $"  namespace: {ns}",
                "rules:",
                rulesYaml.ToString().TrimEnd()));

            docs.Add(Y(
                "apiVersion: rbac.authorization.k8s.io/v1",
                "kind: RoleBinding",
                "metadata:",
                $"  name: {sa}-binding",
                $"  namespace: {ns}",
                "subjects:",
                "  - kind: ServiceAccount",
                $"    name: {sa}",
                $"    namespace: {ns}",
                "roleRef:",
                "  kind: Role",
                $"  name: {sa}-role",
                "  apiGroup: rbac.authorization.k8s.io"));
        }

        return string.Join("\n---\n", docs);
    }

    // ── Allowed Databases ─────────────────────────────────────────────────────

    public async Task<List<AppAllowedDatabase>> GetAllowedDatabasesAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppAllowedDatabases
            .Include(a => a.CnpgDatabase).ThenInclude(d => d!.CnpgCluster)
            .Include(a => a.MongoDatabase).ThenInclude(d => d!.MongoCluster)
            .Include(a => a.RegisteredPostgresDatabase)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<AppAllowedDatabase> AddAllowedDatabaseAsync(
        Guid appId, Guid environmentId,
        Guid? cnpgDatabaseId, Guid? mongoDatabaseId, Guid? registeredPostgresDatabaseId,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedDatabase entry = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            EnvironmentId = environmentId,
            CnpgDatabaseId = cnpgDatabaseId,
            MongoDatabaseId = mongoDatabaseId,
            RegisteredPostgresDatabaseId = registeredPostgresDatabaseId
        };
        db.AppAllowedDatabases.Add(entry);
        await db.SaveChangesAsync(ct);

        return await db.AppAllowedDatabases
            .Include(a => a.CnpgDatabase).ThenInclude(d => d!.CnpgCluster)
            .Include(a => a.MongoDatabase).ThenInclude(d => d!.MongoCluster)
            .Include(a => a.RegisteredPostgresDatabase)
            .FirstAsync(a => a.Id == entry.Id, ct);
    }

    public async Task<bool> DeleteAllowedDatabaseAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedDatabase? entry = await db.AppAllowedDatabases.FindAsync([id], ct);
        if (entry is null) return false;
        db.AppAllowedDatabases.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Allowed Caches ────────────────────────────────────────────────────────

    public async Task<List<AppAllowedCache>> GetAllowedCachesAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppAllowedCaches
            .Include(a => a.RedisCluster)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<AppAllowedCache> AddAllowedCacheAsync(
        Guid appId, Guid environmentId, Guid redisClusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedCache entry = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            EnvironmentId = environmentId,
            RedisClusterId = redisClusterId
        };
        db.AppAllowedCaches.Add(entry);
        await db.SaveChangesAsync(ct);

        return await db.AppAllowedCaches
            .Include(a => a.RedisCluster)
            .FirstAsync(a => a.Id == entry.Id, ct);
    }

    public async Task<bool> DeleteAllowedCacheAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedCache? entry = await db.AppAllowedCaches.FindAsync([id], ct);
        if (entry is null) return false;
        db.AppAllowedCaches.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Allowed Storages ──────────────────────────────────────────────────────

    public async Task<List<AppAllowedStorage>> GetAllowedStoragesAsync(
        Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppAllowedStorages
            .Include(a => a.StorageLink)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<AppAllowedStorage> AddAllowedStorageAsync(
        Guid appId, Guid environmentId, Guid storageLinkId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedStorage entry = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            EnvironmentId = environmentId,
            StorageLinkId = storageLinkId
        };
        db.AppAllowedStorages.Add(entry);
        await db.SaveChangesAsync(ct);

        return await db.AppAllowedStorages
            .Include(a => a.StorageLink)
            .FirstAsync(a => a.Id == entry.Id, ct);
    }

    public async Task<bool> DeleteAllowedStorageAsync(Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedStorage? entry = await db.AppAllowedStorages.FindAsync([id], ct);
        if (entry is null) return false;
        db.AppAllowedStorages.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Available resource pickers (for governance dropdowns) ─────────────────

    public async Task<List<AvailableDatabaseOption>> GetAvailableDatabasesAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<AvailableDatabaseOption> results = [];

        List<CnpgDatabase> cnpg = await db.CnpgDatabases
            .Include(d => d.CnpgCluster).ThenInclude(c => c.KubernetesCluster)
            .Where(d => d.CnpgCluster.TenantId == tenantId
                     && d.CnpgCluster.KubernetesCluster.EnvironmentId == environmentId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        results.AddRange(cnpg.Select(d => new AvailableDatabaseOption(
            d.Id, $"{d.Name} ({d.CnpgCluster.Name})", "PostgreSQL", CnpgId: d.Id)));

        List<MongoDatabase> mongo = await db.MongoDatabases
            .Include(d => d.MongoCluster)
            .Where(d => d.MongoCluster.TenantId == tenantId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        results.AddRange(mongo.Select(d => new AvailableDatabaseOption(
            d.Id, $"{d.Name} ({d.MongoCluster.Name})", "MongoDB", MongoId: d.Id)));

        List<RegisteredPostgresDatabase> regPg = await db.RegisteredPostgresDatabases
            .Include(d => d.RegisteredPostgresInstance).ThenInclude(i => i.KubernetesCluster)
            .Where(d => d.RegisteredPostgresInstance.TenantId == tenantId
                     && d.RegisteredPostgresInstance.KubernetesCluster.EnvironmentId == environmentId)
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        results.AddRange(regPg.Select(d => new AvailableDatabaseOption(
            d.Id, $"{d.Name} ({d.RegisteredPostgresInstance.Name})", "Postgres (registered)", RegPostgresId: d.Id)));

        return results;
    }

    public async Task<List<RedisCluster>> GetAvailableCachesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.RedisClusters
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    public async Task<List<StorageLink>> GetAvailableStoragesAsync(
        Guid tenantId, Guid environmentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.StorageLinks
            .Where(s => s.TenantId == tenantId && s.EnvironmentId == environmentId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    private static string Y(params string[] lines) => string.Join("\n", lines);

    private static string? Blank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Snapshot of all governance data for one app+environment.</summary>
public class AppGovernanceData
{
    public Guid EnvironmentId { get; init; }
    public string? Namespace { get; init; }
    public AppQuota? Quota { get; init; }
    public List<AppNetworkPolicy> NetworkPolicies { get; init; } = [];
    public AppRbacPolicy? RbacPolicy { get; init; }
    public List<AppAllowedDatabase> AllowedDatabases { get; init; } = [];
    public List<AppAllowedCache> AllowedCaches { get; init; } = [];
    public List<AppAllowedStorage> AllowedStorages { get; init; } = [];
}

public record AvailableDatabaseOption(
    Guid Id,
    string DisplayName,
    string TypeLabel,
    Guid? CnpgId = null,
    Guid? MongoId = null,
    Guid? RegPostgresId = null);
