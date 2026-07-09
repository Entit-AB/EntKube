using System.Collections.Concurrent;
using StackExchange.Redis;

namespace EntKube.Web.Services;

/// <summary>
/// Cross-instance presence backend backed by Redis. Opt-in via
/// <c>Presence:Provider = "Redis"</c>; the default remains
/// <see cref="InMemoryPresenceTracker"/>.
///
/// Each user maps to a Redis sorted set of connection ids scored by the last
/// time they were seen. A connection is "live" while its score is newer than
/// <c>Presence:StaleAfterSeconds</c>; <see cref="PresenceHeartbeatService"/>
/// re-stamps this instance's live connections every
/// <c>Presence:RefreshSeconds</c> so they never expire while the circuit is up.
/// This makes presence self-healing: if an instance crashes without firing a
/// disconnect, its connections simply age out instead of lingering forever.
/// </summary>
public sealed class RedisPresenceTracker : IPresenceTracker
{
    private const string KeyPrefix = "entkube:presence";
    private static string UsersKey => $"{KeyPrefix}:users";
    private static string UserKey(string userId) => $"{KeyPrefix}:user:{userId}";

    private readonly IConnectionMultiplexer _redis;
    private readonly int _staleAfterSeconds;

    // This instance's live connections, so the heartbeat can refresh their scores.
    private readonly ConcurrentDictionary<string, string> _local = new(); // connectionId -> userId

    public RedisPresenceTracker(IConnectionMultiplexer redis, IConfiguration config)
    {
        _redis = redis;
        _staleAfterSeconds = config.GetValue<int?>("Presence:StaleAfterSeconds") ?? 30;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public async Task ConnectAsync(string userId, string connectionId)
    {
        _local[connectionId] = userId;
        IDatabase db = _redis.GetDatabase();
        await db.SortedSetAddAsync(UserKey(userId), connectionId, Now);
        await db.SetAddAsync(UsersKey, userId);
    }

    public async Task DisconnectAsync(string userId, string connectionId)
    {
        _local.TryRemove(connectionId, out _);
        IDatabase db = _redis.GetDatabase();
        await db.SortedSetRemoveAsync(UserKey(userId), connectionId);
        if (await db.SortedSetLengthAsync(UserKey(userId)) == 0)
            await db.SetRemoveAsync(UsersKey, userId);
    }

    public async Task<bool> IsOnlineAsync(string userId)
    {
        IDatabase db = _redis.GetDatabase();
        await PruneAsync(db, userId);
        long count = await db.SortedSetLengthAsync(UserKey(userId));
        if (count == 0)
        {
            await db.SetRemoveAsync(UsersKey, userId);
            return false;
        }
        return true;
    }

    public async Task<IReadOnlySet<string>> GetOnlineUsersAsync()
    {
        IDatabase db = _redis.GetDatabase();
        RedisValue[] candidates = await db.SetMembersAsync(UsersKey);
        HashSet<string> online = new();
        foreach (RedisValue candidate in candidates)
        {
            string? userId = candidate;
            if (userId is not null && await IsOnlineAsync(userId))
                online.Add(userId);
        }
        return online;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_local.IsEmpty) return;
        IDatabase db = _redis.GetDatabase();
        long now = Now;
        foreach (KeyValuePair<string, string> conn in _local)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // conn.Value = userId, conn.Key = connectionId
            await db.SortedSetAddAsync(UserKey(conn.Value), conn.Key, now);
            await db.SetAddAsync(UsersKey, conn.Value);
        }
    }

    private async Task PruneAsync(IDatabase db, string userId)
    {
        long cutoff = Now - _staleAfterSeconds;
        await db.SortedSetRemoveRangeByScoreAsync(UserKey(userId), double.NegativeInfinity, cutoff);
    }
}
