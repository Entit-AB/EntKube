namespace EntKube.Web.Data;

/// <summary>
/// Restricts which databases a customer's app may link to in a specific environment.
/// When any entries exist for an app+environment, only those databases may be bound
/// via DatabaseBinding. An empty list means no restriction.
/// </summary>
public class AppAllowedDatabase
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public Guid EnvironmentId { get; set; }

    public Guid? CnpgDatabaseId { get; set; }
    public Guid? MongoDatabaseId { get; set; }
    public Guid? RegisteredPostgresDatabaseId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public CnpgDatabase? CnpgDatabase { get; set; }
    public MongoDatabase? MongoDatabase { get; set; }
    public RegisteredPostgresDatabase? RegisteredPostgresDatabase { get; set; }
}
