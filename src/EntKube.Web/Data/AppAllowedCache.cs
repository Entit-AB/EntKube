namespace EntKube.Web.Data;

/// <summary>
/// Restricts which Redis clusters a customer's app may link to in a specific environment.
/// When any entries exist, only those clusters may be bound via CacheBinding. Empty = no restriction.
/// </summary>
public class AppAllowedCache
{
    public Guid Id { get; set; }
    public Guid AppId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid RedisClusterId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public RedisCluster RedisCluster { get; set; } = null!;
}
