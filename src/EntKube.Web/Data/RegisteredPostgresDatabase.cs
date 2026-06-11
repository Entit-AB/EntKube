namespace EntKube.Web.Data;

/// <summary>
/// Status of a database managed within a RegisteredPostgresInstance.
/// </summary>
public enum RegisteredPostgresDatabaseStatus
{
    Creating,
    Ready,
    Deleting,
    Failed
}

/// <summary>
/// A logical PostgreSQL database within a RegisteredPostgresInstance.
/// EntKube created (or imported) this database and manages its owner role
/// and vault credentials — but does not own the Postgres server itself.
/// </summary>
public class RegisteredPostgresDatabase
{
    public Guid Id { get; set; }

    public Guid RegisteredPostgresInstanceId { get; set; }

    /// <summary>
    /// The PostgreSQL database name (e.g. "keycloak").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The PostgreSQL role that owns this database (e.g. "keycloak_owner").
    /// </summary>
    public required string Owner { get; set; }

    public RegisteredPostgresDatabaseStatus Status { get; set; } = RegisteredPostgresDatabaseStatus.Creating;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public RegisteredPostgresInstance RegisteredPostgresInstance { get; set; } = null!;
    public ICollection<DatabaseBinding> DatabaseBindings { get; set; } = [];
}
