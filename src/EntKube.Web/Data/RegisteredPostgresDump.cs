namespace EntKube.Web.Data;

/// <summary>
/// A pg_dump backup of a RegisteredPostgresDatabase stored as plain SQL in an S3 bucket.
/// Created manually from the Databases tab. Can be restored to a new CNPG database.
/// </summary>
public class RegisteredPostgresDump
{
    public Guid Id { get; set; }
    public Guid RegisteredPostgresDatabaseId { get; set; }
    public Guid StorageLinkId { get; set; }

    /// <summary>
    /// S3 object key, e.g. pg-dumps/my-instance/keycloak/2026-05-22T09-15-00Z.sql
    /// </summary>
    public required string S3Key { get; set; }

    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public RegisteredPostgresDatabase RegisteredPostgresDatabase { get; set; } = null!;
    public StorageLink StorageLink { get; set; } = null!;
}
