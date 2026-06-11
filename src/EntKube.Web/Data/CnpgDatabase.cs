namespace EntKube.Web.Data;

/// <summary>
/// Status of an individual database within a CNPG cluster.
/// </summary>
public enum CnpgDatabaseStatus
{
    Creating,
    Ready,
    Deleting,
    Failed
}

/// <summary>
/// A logical PostgreSQL database within a managed CNPG cluster.
/// Each database has its own owner role and a set of vault secrets
/// (host, port, database name, username, password) that can be synced
/// to Kubernetes for application consumption.
///
/// When a database is created, EntKube runs SQL against the primary
/// to CREATE DATABASE and CREATE ROLE, then stores the credentials
/// in the tenant's vault tagged for K8s sync.
/// </summary>
public class CnpgDatabase
{
    public Guid Id { get; set; }

    /// <summary>
    /// The CNPG cluster this database belongs to.
    /// </summary>
    public Guid CnpgClusterId { get; set; }

    /// <summary>
    /// The PostgreSQL database name (e.g. "myapp", "analytics").
    /// Must be valid PostgreSQL identifier — lowercase, no spaces.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The PostgreSQL role that owns this database (e.g. "myapp_owner").
    /// Created automatically when the database is provisioned.
    /// </summary>
    public required string Owner { get; set; }

    /// <summary>
    /// Current status of the database.
    /// </summary>
    public CnpgDatabaseStatus Status { get; set; } = CnpgDatabaseStatus.Creating;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public CnpgCluster CnpgCluster { get; set; } = null!;
    public ICollection<DatabaseBinding> DatabaseBindings { get; set; } = [];
}
