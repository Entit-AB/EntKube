namespace EntKube.Web.Data;

/// <summary>
/// Restricts which storage buckets (StorageLinks) a customer's app may link to in a specific environment.
/// When any entries exist, only those storage links may be bound via StorageBinding. Empty = no restriction.
/// </summary>
public class AppAllowedStorage
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid StorageLinkId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public StorageLink StorageLink { get; set; } = null!;
}
