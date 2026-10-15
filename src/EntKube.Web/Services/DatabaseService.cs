using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Result of checking whether database operators are available on a tenant's clusters.
/// </summary>
public class DatabaseOperatorStatus
{
    public bool CnpgAvailable { get; set; }
    public bool MongoDbAvailable { get; set; }
    public string? CnpgClusterName { get; set; }
    public string? MongoDbClusterName { get; set; }
}

/// <summary>
/// Queries Kubernetes clusters for CNPG and MongoDB custom resources.
/// Discovers database clusters and their databases by reading CRDs directly
/// from the K8s API — no need for the database operators to expose REST APIs.
///
/// Requires the respective operators to be installed:
/// - CloudNativePG: Cluster CRD (postgresql.cnpg.io/v1)
/// - MongoDB Community Operator: MongoDBCommunity CRD (mongodbcommunity.mongodb.com/v1)
/// </summary>
public class DatabaseService(
    IDbContextFactory<DataServicesDbContext> dbFactory,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    EntKube.Contracts.Catalog.ICatalogApi catalog)
{
    // ──────── Operator Availability ────────

    /// <summary>
    /// Checks which database operators are installed across the tenant's clusters.
    /// A tenant can use the Databases page only if at least one operator is installed.
    /// </summary>
    public async Task<DatabaseOperatorStatus> GetOperatorStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        // Find all clusters belonging to this tenant that have the relevant components installed.

        // Asked of Catalog rather than of its table. The predicate is unchanged and
        // applied here: these alias lists are not symmetrical across Name,
        // ReleaseName and HelmChartName, so a contract method matching every key
        // against every column would answer about installations these never accepted.
        IReadOnlyList<EntKube.Contracts.Catalog.InstalledComponent> installedComponents =
            (await catalog.GetComponentsForTenantAsync(tenantId, ct))
            .Where(c => c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed
                && (c.Name == "cloudnative-pg" || c.ReleaseName == "cnpg"
                    || c.Name == "mongodb-community-operator" || c.Name == "mongodb-operator"
                    || c.ReleaseName == "mongodb-community-operator" || c.ReleaseName == "mongodb-operator"
                    || c.HelmChartName == "community-operator"))
            .ToList();

        EntKube.Contracts.Catalog.InstalledComponent? cnpg = installedComponents.FirstOrDefault(c =>
            c.Name == "cloudnative-pg" || c.ReleaseName == "cnpg");
        EntKube.Contracts.Catalog.InstalledComponent? mongo = installedComponents.FirstOrDefault(c =>
            c.Name is "mongodb-community-operator" or "mongodb-operator"
            || c.ReleaseName is "mongodb-community-operator" or "mongodb-operator"
            || c.HelmChartName == "community-operator");

        return new DatabaseOperatorStatus
        {
            CnpgAvailable = cnpg is not null,
            MongoDbAvailable = mongo is not null,
            CnpgClusterName = cnpg?.ClusterName,
            MongoDbClusterName = mongo?.ClusterName
        };
    }

    /// <summary>
    /// Returns all Kubernetes clusters that have the CNPG operator installed.
    /// Used by the UI to populate the target cluster dropdown when creating
    /// a new managed CNPG cluster.
    /// </summary>
    public async Task<List<KubernetesCluster>> GetCnpgEnabledClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        // Which clusters run the operator is Catalog's answer, not a subquery into its table.
        // The alias pair is asymmetric — the catalog name and the release name — so that half
        // stays in memory, the same way every other converted predicate here does.
        HashSet<Guid> clusterIds =
        [
            .. (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                .Where(c => (c.Name == "cloudnative-pg" || c.ReleaseName == "cnpg")
                    && c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed)
                .Select(c => c.ClusterId),
        ];

        if (clusterIds.Count == 0) return [];

        using DataServicesDbContext db = dbFactory.CreateDbContext();

        return await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId && clusterIds.Contains(k.Id))
            .OrderBy(k => k.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns all Kubernetes clusters that have the Percona MongoDB operator installed.
    /// Used by the UI to populate the target cluster dropdown when creating
    /// a new managed MongoDB cluster.
    /// </summary>
    public async Task<List<KubernetesCluster>> GetMongoEnabledClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        HashSet<Guid> clusterIds =
        [
            .. (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                .Where(c => (c.Name == "mongodb-community-operator" || c.Name == "mongodb-operator"
                        || c.ReleaseName == "mongodb-community-operator" || c.ReleaseName == "mongodb-operator"
                        || c.HelmChartName == "community-operator")
                    && c.Status == EntKube.Contracts.Catalog.ComponentStatus.Installed)
                .Select(c => c.ClusterId),
        ];

        if (clusterIds.Count == 0) return [];

        using DataServicesDbContext db = dbFactory.CreateDbContext();

        return await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId && clusterIds.Contains(k.Id))
            .OrderBy(k => k.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns all Kubernetes clusters for a tenant, regardless of installed operators.
    /// Used to populate the cluster dropdown when registering external Postgres instances.
    /// </summary>
    public async Task<List<KubernetesCluster>> GetAllClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using DataServicesDbContext db = dbFactory.CreateDbContext();

        return await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.Name)
            .ToListAsync(ct);
    }


    // ──────── Internal ────────

}
