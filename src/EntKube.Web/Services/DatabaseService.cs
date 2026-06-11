using System.Text.Json;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// A discovered database cluster (CNPG or MongoDB) running on a Kubernetes cluster.
/// Contains the metadata we care about for display: name, namespace, status, size,
/// and which K8s cluster it belongs to.
/// </summary>
public class DatabaseClusterInfo
{
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public required string Type { get; set; }
    public required string Status { get; set; }
    public int Instances { get; set; }
    public string? Version { get; set; }
    public string? PrimaryPod { get; set; }
    public string? Storage { get; set; }
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// The K8s cluster this database cluster lives on.
    /// </summary>
    public required string ClusterName { get; set; }
    public Guid ClusterId { get; set; }

    /// <summary>
    /// The environment this database cluster belongs to (e.g. "production", "staging").
    /// </summary>
    public required string EnvironmentName { get; set; }

    /// <summary>
    /// Individual databases within this cluster (for CNPG: database names,
    /// for MongoDB: the replica set databases).
    /// </summary>
    public List<string> Databases { get; set; } = [];
}

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
public class DatabaseService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    // ──────── Operator Availability ────────

    /// <summary>
    /// Checks which database operators are installed across the tenant's clusters.
    /// A tenant can use the Databases page only if at least one operator is installed.
    /// </summary>
    public async Task<DatabaseOperatorStatus> GetOperatorStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Find all clusters belonging to this tenant that have the relevant components installed.

        List<ClusterComponent> installedComponents = await db.ClusterComponents
            .Include(c => c.Cluster)
            .Where(c => c.Cluster.TenantId == tenantId
                && c.Status == ComponentStatus.Installed
                && (c.Name == "cloudnative-pg" || c.ReleaseName == "cnpg"
                    || c.Name == "mongodb-community-operator" || c.Name == "mongodb-operator"
                    || c.ReleaseName == "mongodb-community-operator" || c.ReleaseName == "mongodb-operator"
                    || c.HelmChartName == "community-operator"))
            .ToListAsync(ct);

        ClusterComponent? cnpg = installedComponents.FirstOrDefault(c =>
            c.Name == "cloudnative-pg" || c.ReleaseName == "cnpg");
        ClusterComponent? mongo = installedComponents.FirstOrDefault(c =>
            c.Name is "mongodb-community-operator" or "mongodb-operator"
            || c.ReleaseName is "mongodb-community-operator" or "mongodb-operator"
            || c.HelmChartName == "community-operator");

        return new DatabaseOperatorStatus
        {
            CnpgAvailable = cnpg is not null,
            MongoDbAvailable = mongo is not null,
            CnpgClusterName = cnpg?.Cluster.Name,
            MongoDbClusterName = mongo?.Cluster.Name
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
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId
                && k.Components.Any(c =>
                    (c.Name == "cloudnative-pg" || c.ReleaseName == "cnpg")
                    && c.Status == ComponentStatus.Installed))
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
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId
                && k.Components.Any(c =>
                    (c.Name == "mongodb-community-operator" || c.Name == "mongodb-operator"
                     || c.ReleaseName == "mongodb-community-operator" || c.ReleaseName == "mongodb-operator"
                     || c.HelmChartName == "community-operator")
                    && c.Status == ComponentStatus.Installed))
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
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.Name)
            .ToListAsync(ct);
    }

    // ──────── CNPG Discovery ────────

    /// <summary>
    /// Discovers all CloudNativePG Cluster resources across the tenant's clusters.
    /// Reads the postgresql.cnpg.io/v1 Cluster CRD from every cluster that has
    /// the cloudnative-pg operator installed.
    /// </summary>
    public async Task<List<DatabaseClusterInfo>> GetCnpgClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        // Find all K8s clusters with CNPG installed.

        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId
                && k.Components.Any(c =>
                    (c.Name == "cloudnative-pg" || c.ReleaseName == "cnpg")
                    && c.Status == ComponentStatus.Installed))
            .ToListAsync(ct);

        List<DatabaseClusterInfo> results = [];

        foreach (KubernetesCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                continue;
            }

            try
            {
                List<DatabaseClusterInfo> clusterDbs = await QueryCnpgClustersAsync(cluster, ct);
                results.AddRange(clusterDbs);
            }
            catch
            {
                // If we can't reach a cluster, skip it gracefully.
            }
        }

        return results;
    }

    /// <summary>
    /// Queries a single K8s cluster for CNPG Cluster custom resources.
    /// </summary>
    private static async Task<List<DatabaseClusterInfo>> QueryCnpgClustersAsync(
        KubernetesCluster cluster, CancellationToken ct)
    {
        using Kubernetes client = CreateClient(cluster.Kubeconfig!);

        // List all CNPG Cluster CRs across all namespaces.

        object response = await client.CustomObjects.ListClusterCustomObjectAsync(
            group: "postgresql.cnpg.io",
            version: "v1",
            plural: "clusters",
            cancellationToken: ct);

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(response));

        List<DatabaseClusterInfo> results = [];

        if (json.TryGetProperty("items", out JsonElement items))
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                DatabaseClusterInfo info = ParseCnpgCluster(item, cluster);
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>
    /// Parses a single CNPG Cluster resource JSON into a DatabaseClusterInfo.
    /// Extracts name, namespace, instances, status, storage, and database names.
    /// </summary>
    private static DatabaseClusterInfo ParseCnpgCluster(JsonElement item, KubernetesCluster cluster)
    {
        JsonElement metadata = item.GetProperty("metadata");
        JsonElement spec = item.GetProperty("spec");

        string name = metadata.GetProperty("name").GetString() ?? "unknown";
        string ns = metadata.GetProperty("namespace").GetString() ?? "default";

        int instances = spec.TryGetProperty("instances", out JsonElement instEl)
            ? instEl.GetInt32() : 1;

        string? version = spec.TryGetProperty("imageName", out JsonElement imgEl)
            ? imgEl.GetString() : null;

        string? storage = spec.TryGetProperty("storage", out JsonElement storageEl)
            && storageEl.TryGetProperty("size", out JsonElement sizeEl)
            ? sizeEl.GetString() : null;

        // Extract database names from the bootstrap or managed configuration.

        List<string> databases = [];

        if (spec.TryGetProperty("bootstrap", out JsonElement bootstrap)
            && bootstrap.TryGetProperty("initdb", out JsonElement initdb)
            && initdb.TryGetProperty("database", out JsonElement dbNameEl))
        {
            string? dbName = dbNameEl.GetString();

            if (!string.IsNullOrEmpty(dbName))
            {
                databases.Add(dbName);
            }
        }

        // Status extraction.

        string status = "Unknown";

        if (item.TryGetProperty("status", out JsonElement statusEl))
        {
            if (statusEl.TryGetProperty("phase", out JsonElement phaseEl))
            {
                status = phaseEl.GetString() ?? "Unknown";
            }

            if (statusEl.TryGetProperty("currentPrimary", out JsonElement primaryEl))
            {
                // primaryPod is available
            }
        }

        string? primaryPod = item.TryGetProperty("status", out JsonElement st)
            && st.TryGetProperty("currentPrimary", out JsonElement pEl)
            ? pEl.GetString() : null;

        DateTime? createdAt = metadata.TryGetProperty("creationTimestamp", out JsonElement tsEl)
            ? tsEl.GetDateTime() : null;

        return new DatabaseClusterInfo
        {
            Name = name,
            Namespace = ns,
            Type = "PostgreSQL",
            Status = status,
            Instances = instances,
            Version = version,
            PrimaryPod = primaryPod,
            Storage = storage,
            CreatedAt = createdAt,
            ClusterName = cluster.Name,
            ClusterId = cluster.Id,
            EnvironmentName = cluster.Environment.Name,
            Databases = databases
        };
    }

    // ──────── MongoDB Discovery ────────

    /// <summary>
    /// Discovers all Percona Server for MongoDB resources across the tenant's clusters.
    /// Reads the psmdb.percona.com/v1 PerconaServerMongoDB CRD from every
    /// cluster that has the mongodb-community-operator installed.
    /// </summary>
    public async Task<List<DatabaseClusterInfo>> GetMongoDbClustersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<KubernetesCluster> clusters = await db.KubernetesClusters
            .Include(k => k.Environment)
            .Where(k => k.TenantId == tenantId
                && k.Components.Any(c =>
                    (c.Name == "mongodb-community-operator" || c.Name == "mongodb-operator"
                     || c.ReleaseName == "mongodb-community-operator" || c.ReleaseName == "mongodb-operator")
                    && c.Status == ComponentStatus.Installed))
            .ToListAsync(ct);

        List<DatabaseClusterInfo> results = [];

        foreach (KubernetesCluster cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            {
                continue;
            }

            try
            {
                List<DatabaseClusterInfo> clusterDbs = await QueryMongoDbClustersAsync(cluster, ct);
                results.AddRange(clusterDbs);
            }
            catch
            {
                // If we can't reach a cluster, skip it gracefully.
            }
        }

        return results;
    }

    /// <summary>
    /// Queries a single K8s cluster for MongoDBCommunity custom resources.
    /// </summary>
    private static async Task<List<DatabaseClusterInfo>> QueryMongoDbClustersAsync(
        KubernetesCluster cluster, CancellationToken ct)
    {
        using Kubernetes client = CreateClient(cluster.Kubeconfig!);

        object response = await client.CustomObjects.ListClusterCustomObjectAsync(
            group: "mongodbcommunity.mongodb.com",
            version: "v1",
            plural: "mongodbcommunity",
            cancellationToken: ct);

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(response));

        List<DatabaseClusterInfo> results = [];

        if (json.TryGetProperty("items", out JsonElement items))
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                DatabaseClusterInfo info = ParseMongoDbCluster(item, cluster);
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>
    /// Parses a single MongoDBCommunity resource JSON into a DatabaseClusterInfo.
    /// </summary>
    private static DatabaseClusterInfo ParseMongoDbCluster(JsonElement item, KubernetesCluster cluster)
    {
        JsonElement metadata = item.GetProperty("metadata");
        JsonElement spec = item.GetProperty("spec");

        string name = metadata.GetProperty("name").GetString() ?? "unknown";
        string ns = metadata.GetProperty("namespace").GetString() ?? "default";

        // Community Operator uses spec.members directly.

        int members = 1;

        if (spec.TryGetProperty("members", out JsonElement membersEl))
        {
            members = membersEl.GetInt32();
        }

        // Version is in spec.version (e.g. "8.0.8").

        string? version = null;

        if (spec.TryGetProperty("version", out JsonElement versionEl))
        {
            version = versionEl.GetString();
        }

        // Databases are not listed in the CRD — implicit in MongoDB.

        List<string> databases = [];

        // Status from the CR.

        string status = "Unknown";

        if (item.TryGetProperty("status", out JsonElement statusEl)
            && statusEl.TryGetProperty("phase", out JsonElement phaseEl))
        {
            status = phaseEl.GetString() ?? "Unknown";
        }

        DateTime? createdAt = metadata.TryGetProperty("creationTimestamp", out JsonElement tsEl)
            ? tsEl.GetDateTime() : null;

        return new DatabaseClusterInfo
        {
            Name = name,
            Namespace = ns,
            Type = "MongoDB",
            Status = status,
            Instances = members,
            Version = version,
            Storage = null,
            CreatedAt = createdAt,
            ClusterName = cluster.Name,
            ClusterId = cluster.Id,
            EnvironmentName = cluster.Environment.Name,
            Databases = databases
        };
    }

    // ──────── Internal ────────

    private static Kubernetes CreateClient(string kubeconfig)
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }
}
