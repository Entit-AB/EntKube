namespace EntKube.Web.Data;

public enum KeycloakBackupStatus
{
    Creating,
    Ready,
    Failed
}

/// <summary>
/// A backup of a Keycloak realm — the full realm JSON exported via the Keycloak
/// Admin REST API and stored in an S3 bucket. Used for disaster recovery and
/// migrating realms between environments.
/// </summary>
public class KeycloakBackup
{
    public Guid Id { get; set; }

    public Guid KeycloakRealmId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? StorageLinkId { get; set; }

    /// <summary>
    /// S3 object key (e.g. "keycloak-backups/my-realm/2026-05-19T14-32-00.json").
    /// </summary>
    public required string ObjectKey { get; set; }

    /// <summary>
    /// Snapshot of the realm name at backup time, for display after the realm
    /// may have been renamed or deleted.
    /// </summary>
    public required string RealmName { get; set; }

    public long SizeBytes { get; set; }

    public KeycloakBackupStatus Status { get; set; } = KeycloakBackupStatus.Creating;

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    // Navigation
    public KeycloakRealm Realm { get; set; } = null!;
    public StorageLink? StorageLink { get; set; }
}
