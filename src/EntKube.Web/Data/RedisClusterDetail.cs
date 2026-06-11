namespace EntKube.Web.Data;

public class RedisClusterDetail
{
    public required RedisCluster Cluster { get; set; }
    public List<RedisPodInfo> Pods { get; set; } = [];
    public int ReadyLeaders { get; set; }
    public int ReadyFollowers { get; set; }
    public string Phase { get; set; } = "Unknown";
}

public class RedisPodInfo
{
    public required string Name { get; set; }
    public required string Role { get; set; }   // "leader" or "follower"
    public required string Status { get; set; }
    public bool Ready { get; set; }
    public string? Node { get; set; }
    public DateTime? StartTime { get; set; }
    public int Restarts { get; set; }
}
