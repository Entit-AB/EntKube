using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using k8s;
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
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    EntKube.Contracts.Catalog.ICatalogApi catalog,
    ILogger<AppGovernanceService> logger)
{
    // ── Namespace ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether an app and an environment are both this tenant's.
    ///
    /// <para>Checked at each public entry point that takes them from a caller. Everything below
    /// is keyed by the pair, so once it is proven the rest of the method is scoped by
    /// construction — the same boundary-check shape <c>TimeService</c> uses, and cheaper than
    /// threading a tenant into every query in a 31-method service.</para>
    ///
    /// <para>⚠️ Both ends, because this is a pairing: an app of ours with an environment of
    /// theirs is as wrong as the reverse, and governance written against that pair would be
    /// applied to a namespace in the wrong tenant.</para>
    /// </summary>
    /// <summary>
    /// Whether every named database belongs to <paramref name="tenantId"/>. None of the three
    /// carries a <c>TenantId</c> of its own: a CNPG or Mongo database is owned by its cluster,
    /// a registered one by its instance. A null id grants nothing, so it passes.
    /// </summary>
    private async Task<bool> DatabasesBelongToTenantAsync(
        Guid tenantId,
        Guid? cnpgDatabaseId, Guid? mongoDatabaseId, Guid? registeredPostgresDatabaseId,
        CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        if (cnpgDatabaseId is Guid cnpgId
            && !await db.CnpgDatabases.AnyAsync(
                d => d.Id == cnpgId && d.CnpgCluster.TenantId == tenantId, ct))
        {
            return false;
        }

        if (mongoDatabaseId is Guid mongoId
            && !await db.MongoDatabases.AnyAsync(
                d => d.Id == mongoId && d.MongoCluster.TenantId == tenantId, ct))
        {
            return false;
        }

        if (registeredPostgresDatabaseId is Guid registeredId
            && !await db.RegisteredPostgresDatabases.AnyAsync(
                d => d.Id == registeredId
                  && d.RegisteredPostgresInstance.TenantId == tenantId, ct))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> PairBelongsToTenantAsync(
        Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Apps.AnyAsync(a => a.Id == appId && a.Customer.TenantId == tenantId, ct)
            && await db.Environments.AnyAsync(e => e.Id == environmentId && e.TenantId == tenantId, ct);
    }

    /// <summary>Whether an app is this tenant's, for the entry points that take no environment.</summary>
    private async Task<bool> AppBelongsToTenantAsync(Guid tenantId, Guid appId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.Apps.AnyAsync(a => a.Id == appId && a.Customer.TenantId == tenantId, ct);
    }


    public async Task<List<AppEnvironment>> GetAppEnvironmentsAsync(
        Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        if (!await AppBelongsToTenantAsync(tenantId, appId, ct))
        {
            return [];
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppEnvironments
            .Where(ae => ae.AppId == appId)
            .ToListAsync(ct);
    }

    public async Task<string?> GetNamespaceAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return null;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == environmentId, ct);
        return ae?.Namespace;
    }

    public async Task SaveNamespaceAsync(Guid tenantId, Guid appId, Guid environmentId, string? ns, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e => e.AppId == appId && e.EnvironmentId == environmentId, ct);
        if (ae is null) return;
        ae.Namespace = string.IsNullOrWhiteSpace(ns) ? null : ns.Trim().ToLowerInvariant();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Checks whether <paramref name="ns"/> already exists on every cluster this app+environment
    /// is governed on, so a namespace lock can be validated (and the namespace created) before any
    /// governance is pushed.
    ///
    /// Cluster set: the clusters hosting a deployment for this app+environment — the same set
    /// <see cref="ApplyToClusterAsync"/> targets. Before the first deployment exists there are none,
    /// so it falls back to every cluster assigned to the environment, which is where a deployment
    /// would land anyway.
    /// </summary>
    public async Task<NamespaceCheckResult> CheckNamespaceAsync(
        Guid tenantId, Guid appId, Guid environmentId, string? ns, CancellationToken ct = default)
    {
        string? name = Blank(ns)?.ToLowerInvariant();
        if (name is null) return new NamespaceCheckResult { Namespace = "" };

        List<KubernetesCluster> clusters;
        bool fromDeployments;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            List<Guid> clusterIds = await db.AppDeployments
                .Where(d => d.AppId == appId && d.EnvironmentId == environmentId)
                .Select(d => d.ClusterId)
                .Distinct()
                .ToListAsync(ct);

            fromDeployments = clusterIds.Count > 0;

            clusters = fromDeployments
                ? await db.KubernetesClusters.Where(c => clusterIds.Contains(c.Id)).ToListAsync(ct)
                : await db.KubernetesClusters.Where(c => c.EnvironmentId == environmentId).ToListAsync(ct);
        }

        var statuses = new List<NamespaceClusterStatus>();
        var known = new SortedSet<string>(StringComparer.Ordinal);

        foreach (KubernetesCluster cluster in clusters.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            EntKube.Web.Services.Clusters.IClusterClient? access =
                await clusterAccess.ForAsync(tenantId, cluster.Id, ct);

            if (access is null)
            {
                statuses.Add(new NamespaceClusterStatus
                {
                    ClusterId   = cluster.Id,
                    ClusterName = cluster.Name,
                    Error       = "Cluster is not this tenant's, or has no kubeconfig configured.",
                });
                continue;
            }

            (List<string>? names, string? error) = await ListNamespacesAsync(access, ct);
            if (names is null)
            {
                statuses.Add(new NamespaceClusterStatus
                {
                    ClusterId   = cluster.Id,
                    ClusterName = cluster.Name,
                    Error       = error,
                });
                continue;
            }

            foreach (string n in names) known.Add(n);

            statuses.Add(new NamespaceClusterStatus
            {
                ClusterId   = cluster.Id,
                ClusterName = cluster.Name,
                Exists      = names.Contains(name, StringComparer.Ordinal),
            });
        }

        return new NamespaceCheckResult
        {
            Namespace       = name,
            FromDeployments = fromDeployments,
            Clusters        = statuses,
            KnownNamespaces = [.. known],
        };
    }

    /// <summary>
    /// Creates the namespace on one cluster. Goes through the change gate first, so an interactive
    /// operator acknowledges the dry-run before the namespace is written.
    /// </summary>
    public async Task<(bool Success, string Output)> CreateNamespaceOnClusterAsync(
        Guid tenantId, Guid clusterId, string ns, CancellationToken ct = default)
    {
        string? name = Blank(ns)?.ToLowerInvariant();
        if (name is null) return (false, "Namespace name is empty.");
        if (ValidateNamespaceName(name) is { } invalid) return (false, invalid);

        KubernetesCluster? cluster;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            cluster = await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        }

        if (cluster is null) return (false, "Cluster not found.");

        EntKube.Web.Services.Clusters.IClusterClient? access =
            await clusterAccess.ForAsync(tenantId, clusterId, ct);

        if (access is null)
            return (false, "Cluster is not this tenant's, or has no kubeconfig configured.");

        // One seam call instead of a hand-built acknowledgment followed by this service's own
        // kubectl: EnsureNamespaceAsync is the same operation, and the factory raises the
        // acknowledgment on the way through with a dry-run preview rather than a summary string.
        try
        {
            await access.EnsureNamespaceAsync(name, ct);
            logger.LogInformation("Namespace {Namespace} created on {Cluster}", name, cluster.Name);
            return (true, $"namespace/{name} applied");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Namespace {Namespace} create failed on {Cluster}", name, cluster.Name);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Returns null when the name is a valid Kubernetes namespace (DNS-1123 label),
    /// otherwise a human-readable reason.
    /// </summary>
    public static string? ValidateNamespaceName(string ns)
    {
        if (ns.Length > 63)
            return "Namespace names may be at most 63 characters.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(ns, "^[a-z0-9]([-a-z0-9]*[a-z0-9])?$"))
            return "Namespace names may contain only lowercase letters, digits and '-', and must start and end with a letter or digit.";
        return null;
    }

    /// <summary>
    /// The cluster's namespaces, through the seam. It used to build its own <c>Kubernetes</c>
    /// client from the kubeconfig — a second route to the API server that nothing else in the
    /// product could see or pool.
    /// </summary>
    private static async Task<(List<string>? Names, string? Error)> ListNamespacesAsync(
        EntKube.Web.Services.Clusters.IClusterClient access, CancellationToken ct)
    {
        try
        {
            // Empty namespace means cluster-scoped, which is the seam's convention.
            string json = await access.GetJsonAsync("namespaces", "", ct: ct);

            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out JsonElement items))
            {
                return ([], null);
            }

            return (
                [
                    .. items.EnumerateArray()
                        .Select(i => i.TryGetProperty("metadata", out JsonElement m)
                                     && m.TryGetProperty("name", out JsonElement n)
                            ? n.GetString()
                            : null)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                ], null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Could not list namespaces: {ex.Message}");
        }
    }

    // ── Quota ─────────────────────────────────────────────────────────────────

    public async Task<AppQuota?> GetQuotaAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return null;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppQuotas
            .FirstOrDefaultAsync(q => q.AppId == appId && q.EnvironmentId == environmentId, ct);
    }

    public async Task<AppQuota> SaveQuotaAsync(Guid tenantId, Guid appId, Guid environmentId,
        string? cpuRequest, string? cpuLimit,
        string? memRequest, string? memLimit,
        int? maxPods, int? maxPvcs,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The app or the environment does not belong to this tenant.");
        }

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

    public async Task<bool> DeleteQuotaAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return false;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppQuota? q = await db.AppQuotas
            .FirstOrDefaultAsync(x => x.AppId == appId && x.EnvironmentId == environmentId, ct);
        if (q is null) return false;
        db.AppQuotas.Remove(q);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Network policies ──────────────────────────────────────────────────────

    public async Task<List<AppNetworkPolicy>> GetNetworkPoliciesAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return [];
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppNetworkPolicies
            .Where(p => p.AppId == appId && p.EnvironmentId == environmentId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<AppNetworkPolicy> AddNetworkPolicyAsync(Guid tenantId, Guid appId, Guid environmentId, string name, AppNetworkPolicyType type,
        string? allowFromNs = null, string? customYaml = null,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The app or the environment does not belong to this tenant.");
        }

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

    /// <param name="tenantId">
    /// Whose row. ⚠️ It used <c>FindAsync</c>, which takes a key and cannot carry a predicate —
    /// part of why these deletes never gained a tenant. Every one of these entities has an
    /// <c>AppId</c>, so the scope travels through the app.
    /// </param>
    public async Task<bool> DeleteNetworkPolicyAsync(
        Guid tenantId, Guid policyId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppNetworkPolicy? p = await db.AppNetworkPolicies
            .FirstOrDefaultAsync(
                x => x.Id == policyId && x.App.Customer.TenantId == tenantId, ct);
        if (p is null) return false;
        db.AppNetworkPolicies.Remove(p);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── RBAC ──────────────────────────────────────────────────────────────────

    public async Task<AppRbacPolicy?> GetRbacPolicyAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return null;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppRbacPolicies
            .Include(p => p.Rules)
            .FirstOrDefaultAsync(p => p.AppId == appId && p.EnvironmentId == environmentId, ct);
    }

    public async Task<AppRbacPolicy> SaveRbacPolicyAsync(Guid tenantId, Guid appId, Guid environmentId, string serviceAccountName, bool autoMount,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The app or the environment does not belong to this tenant.");
        }

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

    /// <summary>
    /// Adds one rule to an app's Role. Throws <see cref="InvalidOperationException"/> when the rule
    /// is on the RBAC deny-list — see <see cref="AppRbacRuleValidator"/> for what that covers and why.
    /// </summary>
    /// <param name="tenantId">
    /// Whose policy. The rule hangs off an <c>AppRbacPolicy</c>, so the policy is what has to
    /// be this tenant's — a rule added to someone else's policy grants verbs in their namespace.
    /// </param>
    public async Task<AppRbacRule> AddRbacRuleAsync(
        Guid tenantId, Guid rbacPolicyId, string apiGroups, string resources, string verbs,
        CancellationToken ct = default)
    {
        using ApplicationDbContext guard = dbFactory.CreateDbContext();

        if (!await guard.AppRbacPolicies
                .AnyAsync(x => x.Id == rbacPolicyId && x.App.Customer.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException(
                "RBAC policy not found, or it does not belong to this tenant.");
        }

        AppRbacRuleValidator.ThrowIfBlocked(apiGroups, resources, verbs);

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

    /// <param name="tenantId">
    /// Whose row. ⚠️ It used <c>FindAsync</c>, which takes a key and cannot carry a predicate —
    /// part of why these deletes never gained a tenant. Every one of these entities has an
    /// <c>AppId</c>, so the scope travels through the app.
    /// </param>
    public async Task<bool> DeleteRbacRuleAsync(
        Guid tenantId, Guid ruleId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        // AppRbacRule has no App of its own — it hangs off a policy, which has one.
        AppRbacRule? r = await db.AppRbacRules
            .FirstOrDefaultAsync(
                x => x.Id == ruleId && x.Policy.App.Customer.TenantId == tenantId, ct);
        if (r is null) return false;
        db.AppRbacRules.Remove(r);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteRbacPolicyAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return false;
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppRbacPolicy? p = await db.AppRbacPolicies
            .FirstOrDefaultAsync(x => x.AppId == appId && x.EnvironmentId == environmentId, ct);
        if (p is null) return false;
        db.AppRbacPolicies.Remove(p);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Load all governance for an app+environment ────────────────────────────

    public async Task<AppGovernanceData> LoadAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The app or the environment does not belong to this tenant.");
        }

        return new AppGovernanceData
        {
            EnvironmentId    = environmentId,
            Namespace        = await GetNamespaceAsync(tenantId, appId, environmentId, ct),
            Quota            = await GetQuotaAsync(tenantId, appId, environmentId, ct),
            NetworkPolicies  = await GetNetworkPoliciesAsync(tenantId, appId, environmentId, ct),
            RbacPolicy       = await GetRbacPolicyAsync(tenantId, appId, environmentId, ct),
            AllowedDatabases = await GetAllowedDatabasesAsync(tenantId, appId, environmentId, ct),
            AllowedCaches    = await GetAllowedCachesAsync(tenantId, appId, environmentId, ct),
            AllowedStorages  = await GetAllowedStoragesAsync(tenantId, appId, environmentId, ct)
        };
    }

    // ── Copy governance from one environment to another ───────────────────────

    /// <summary>
    /// Copies quota, network policies, and RBAC from <paramref name="sourceEnvId"/>
    /// to <paramref name="targetEnvId"/>, overwriting any existing settings.
    /// </summary>
    /// <param name="tenantId">
    /// Whose governance. This one names three things, so it is guarded twice: the app with the
    /// source environment, and the app with the target. Copying is a read of one environment and
    /// a write to another, and either side crossing the boundary is a breach — reading a
    /// stranger's quota and network policies, or overwriting theirs with ours.
    /// </param>
    public async Task CopyFromEnvironmentAsync(
        Guid tenantId, Guid appId, Guid sourceEnvId, Guid targetEnvId,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, sourceEnvId, ct)
            || !await PairBelongsToTenantAsync(tenantId, appId, targetEnvId, ct))
        {
            throw new InvalidOperationException(
                "The app or one of the environments does not belong to this tenant.");
        }

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
    /// <param name="cluster">
    /// Identifying fields only. It used to be the whole entity, which carried a materialised
    /// kubeconfig — the credential came in through the parameter, so the caller had to hold one.
    /// </param>
    public async Task<(bool Success, string Output)> ApplyToClusterAsync(
        Guid tenantId, Guid appId, Guid environmentId, KubernetesCluster cluster,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Resolve namespace from the deployment targeting this cluster in this environment.
        AppDeployment? deployment = await db.AppDeployments
            .Where(d => d.AppId == appId
                     && d.EnvironmentId == environmentId
                     && d.ClusterId == cluster.Id)
            .FirstOrDefaultAsync(ct);

        string? govNs = await GetNamespaceAsync(tenantId, appId, environmentId, ct);
        string? ns = govNs ?? deployment?.Namespace;
        if (string.IsNullOrWhiteSpace(ns))
            return (false, "No deployment found for this app in this environment on this cluster. Create a deployment first.");

        EntKube.Web.Services.Clusters.IClusterClient? access =
            await clusterAccess.ForAsync(tenantId, cluster.Id, ct);

        if (access is null)
            return (false, "Cluster is not this tenant's, or has no kubeconfig configured.");

        AppGovernanceData data = await LoadAsync(tenantId, appId, environmentId, ct);

        // Where this cluster's Prometheus runs, asked of Catalog as the tenant — the same answer
        // the metrics views and the SeaweedFS policy get — so the namespace's policies let it in.
        string? prometheusNamespace = EntKube.Web.Services.ScrapeMonitors.PrometheusScrapeAllowance.NamespaceToAdmit(
            await catalog.FindPrometheusAsync(tenantId, cluster.Id, ct));

        string yaml = BuildManifest(data, ns, prometheusNamespace);

        // The seam applies and acknowledges in one call. What this replaces was an acknowledgment
        // built here from a summary string, followed by this service running its own kubectl with
        // the kubeconfig on disk — so the operator was asked, but about a sentence rather than a
        // diff, and the apply itself was invisible to everything that watches the factory.
        try
        {
            string result = await access.ApplyManifestInNamespaceAsync(
                yaml, ns, $"Apply governance (quota/limits/NetworkPolicy/RBAC) to {ns}", ct);

            logger.LogInformation("Governance applied to {Cluster}/{Namespace}", cluster.Name, ns);
            return (true, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Governance apply failed for {Cluster}", cluster.Name);
            return (false, ex.Message);
        }
    }


    // ── Manifest builder ──────────────────────────────────────────────────────

    /// <summary>The NetworkPolicy that lets the cluster's Prometheus through governance's own policies.</summary>
    public const string PrometheusAllowanceName = "entkube-allow-prometheus";

    /// <param name="prometheusNamespace">
    /// Where the cluster's Prometheus runs, as Catalog records it, or null when there is none or
    /// nobody recorded where. Only a known namespace is let through the namespace's policies.
    /// </param>
    public static string BuildManifest(AppGovernanceData data, string ns, string? prometheusNamespace)
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
        //
        // Every built-in type selects every pod of the namespace and restricts its ingress, so the
        // moment one of them is applied the namespace is closed to whatever it does not name —
        // the cluster's Prometheus included. We remember whether one was written, to let
        // Prometheus back in below.
        bool namespaceClosedToIngress = false;

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

            // Each body follows the header on lines of its own. The allow types used to be glued
            // straight onto it, which put "podSelector: {}" and "ingress:" on one line — YAML that
            // kubectl refused, taking the whole governance apply down with it.
            string policyYaml = np.PolicyType switch
            {
                AppNetworkPolicyType.DenyAll =>
                    Y(header,
                        "  policyTypes:",
                        "    - Ingress",
                        "    - Egress"),

                // The namespace list must name whatever actually terminates external traffic on
                // the target cluster. Omitting the one in use does not fail loudly: the packets
                // are dropped, so callers see a TIMEOUT rather than a refusal, and the pod stays
                // Ready the whole time — it reads as a hung backend, not a blocked one. istio-system
                // is included because an Istio ingress gateway is the common case on EntKube-managed
                // clusters; add more via a Custom policy when a cluster fronts traffic differently.
                AppNetworkPolicyType.AllowFromIngress =>
                    Y(header,
                        "  ingress:",
                        "    - from:",
                        "        - namespaceSelector:",
                        "            matchExpressions:",
                        "              - key: kubernetes.io/metadata.name",
                        "                operator: In",
                        "                values:",
                        "                  - ingress-nginx",
                        "                  - traefik",
                        "                  - istio-system"),

                AppNetworkPolicyType.AllowFromSameNamespace =>
                    Y(header,
                        "  ingress:",
                        "    - from:",
                        "        - podSelector: {}"),

                AppNetworkPolicyType.AllowFromNamespace
                    when !string.IsNullOrWhiteSpace(np.AllowFromNamespace) =>
                    Y(header,
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

            // A custom policy is not counted: its own selector and types decide what it closes,
            // and an allowance over every pod beside one that closes only some of them would
            // close the rest.
            if (np.PolicyType != AppNetworkPolicyType.Custom && !string.IsNullOrWhiteSpace(policyYaml))
            {
                namespaceClosedToIngress = true;
            }
        }

        // The cluster's Prometheus is let back in wherever a built-in policy closed the namespace,
        // so the app's metrics keep arriving. Only where we know where Prometheus runs: a guessed
        // namespace would let in somebody else's pods. And never on its own — an ingress allowance
        // over every pod is itself a policy that closes the namespace to everything else.
        if (namespaceClosedToIngress && !string.IsNullOrWhiteSpace(prometheusNamespace))
        {
            docs.Add(EntKube.Web.Services.ScrapeMonitors.PrometheusScrapeAllowance.BuildNetworkPolicy(
                PrometheusAllowanceName, ns, prometheusNamespace));
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

    public async Task<List<AppAllowedDatabase>> GetAllowedDatabasesAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return [];
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppAllowedDatabases
            .Include(a => a.CnpgDatabase).ThenInclude(d => d!.CnpgCluster)
            .Include(a => a.MongoDatabase).ThenInclude(d => d!.MongoCluster)
            .Include(a => a.RegisteredPostgresDatabase)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose grant. A grant names two things — the app+environment it is written against and the
    /// database it opens — so both have to be this tenant's: the pair guard alone would let one
    /// tenant hand its own app a stranger's database.
    /// </param>
    public async Task<AppAllowedDatabase> AddAllowedDatabaseAsync(
        Guid tenantId, Guid appId, Guid environmentId,
        Guid? cnpgDatabaseId, Guid? mongoDatabaseId, Guid? registeredPostgresDatabaseId,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The app or the environment does not belong to this tenant.");
        }

        if (!await DatabasesBelongToTenantAsync(
                tenantId, cnpgDatabaseId, mongoDatabaseId, registeredPostgresDatabaseId, ct))
        {
            throw new InvalidOperationException(
                "The database does not belong to this tenant.");
        }

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

    /// <param name="tenantId">
    /// Whose row. ⚠️ It used <c>FindAsync</c>, which takes a key and cannot carry a predicate —
    /// part of why these deletes never gained a tenant. Every one of these entities has an
    /// <c>AppId</c>, so the scope travels through the app.
    /// </param>
    public async Task<bool> DeleteAllowedDatabaseAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedDatabase? entry = await db.AppAllowedDatabases
            .FirstOrDefaultAsync(
                x => x.Id == id && x.App.Customer.TenantId == tenantId, ct);
        if (entry is null) return false;
        db.AppAllowedDatabases.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Allowed Caches ────────────────────────────────────────────────────────

    public async Task<List<AppAllowedCache>> GetAllowedCachesAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return [];
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppAllowedCaches
            .Include(a => a.RedisCluster)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose grant — the app+environment and the Redis cluster both have to be this tenant's.
    /// </param>
    public async Task<AppAllowedCache> AddAllowedCacheAsync(
        Guid tenantId, Guid appId, Guid environmentId, Guid redisClusterId,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The app or the environment does not belong to this tenant.");
        }

        using ApplicationDbContext guard = dbFactory.CreateDbContext();
        if (!await guard.RedisClusters.AnyAsync(
                c => c.Id == redisClusterId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException(
                "The cache does not belong to this tenant.");
        }

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

    /// <param name="tenantId">
    /// Whose row. ⚠️ It used <c>FindAsync</c>, which takes a key and cannot carry a predicate —
    /// part of why these deletes never gained a tenant. Every one of these entities has an
    /// <c>AppId</c>, so the scope travels through the app.
    /// </param>
    public async Task<bool> DeleteAllowedCacheAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedCache? entry = await db.AppAllowedCaches
            .FirstOrDefaultAsync(
                x => x.Id == id && x.App.Customer.TenantId == tenantId, ct);
        if (entry is null) return false;
        db.AppAllowedCaches.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Allowed Storages ──────────────────────────────────────────────────────

    public async Task<List<AppAllowedStorage>> GetAllowedStoragesAsync(Guid tenantId, Guid appId, Guid environmentId, CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            return [];
        }

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AppAllowedStorages
            .Include(a => a.StorageLink)
            .Where(a => a.AppId == appId && a.EnvironmentId == environmentId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose grant — the app+environment and the storage link both have to be this tenant's.
    /// </param>
    public async Task<AppAllowedStorage> AddAllowedStorageAsync(
        Guid tenantId, Guid appId, Guid environmentId, Guid storageLinkId,
        CancellationToken ct = default)
    {
        if (!await PairBelongsToTenantAsync(tenantId, appId, environmentId, ct))
        {
            throw new InvalidOperationException(
                "The app or the environment does not belong to this tenant.");
        }

        using ApplicationDbContext guard = dbFactory.CreateDbContext();
        if (!await guard.StorageLinks.AnyAsync(
                l => l.Id == storageLinkId && l.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException(
                "The storage link does not belong to this tenant.");
        }

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

    /// <param name="tenantId">
    /// Whose row. ⚠️ It used <c>FindAsync</c>, which takes a key and cannot carry a predicate —
    /// part of why these deletes never gained a tenant. Every one of these entities has an
    /// <c>AppId</c>, so the scope travels through the app.
    /// </param>
    public async Task<bool> DeleteAllowedStorageAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppAllowedStorage? entry = await db.AppAllowedStorages
            .FirstOrDefaultAsync(
                x => x.Id == id && x.App.Customer.TenantId == tenantId, ct);
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

/// <summary>Per-cluster existence of a governance namespace.</summary>
public class NamespaceClusterStatus
{
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }

    /// <summary>True when the namespace already exists on this cluster.</summary>
    public bool Exists { get; init; }

    /// <summary>Non-null when the cluster could not be reached — existence is then unknown.</summary>
    public string? Error { get; init; }
}

/// <summary>Result of checking a namespace across the clusters an app+environment is governed on.</summary>
public class NamespaceCheckResult
{
    public required string Namespace { get; init; }

    /// <summary>
    /// True when the checked clusters came from this environment's deployments; false when the app
    /// has no deployment yet and every cluster in the environment was checked instead.
    /// </summary>
    public bool FromDeployments { get; init; }

    public List<NamespaceClusterStatus> Clusters { get; init; } = [];

    /// <summary>All namespace names seen across the checked clusters — used to suggest existing ones.</summary>
    public List<string> KnownNamespaces { get; init; } = [];

    /// <summary>Clusters that were reachable and are missing the namespace.</summary>
    public List<NamespaceClusterStatus> Missing =>
        Clusters.Where(c => c.Error is null && !c.Exists).ToList();
}

public record AvailableDatabaseOption(
    Guid Id,
    string DisplayName,
    string TypeLabel,
    Guid? CnpgId = null,
    Guid? MongoId = null,
    Guid? RegPostgresId = null);
