using System.Collections.Concurrent;

namespace EntKube.Web.Services;

/// <summary>
/// Process-local presence backend (the default). Holds a per-user set of active
/// connection ids in memory.
///
/// This is single-instance only: in a multi-instance deployment each instance
/// sees only the users connected to it. Switch to the Redis backend
/// (<c>Presence:Provider = "Redis"</c>) for cross-instance presence.
/// </summary>
public sealed class InMemoryPresenceTracker : IPresenceTracker
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _connections = new();

    public Task ConnectAsync(string userId, string connectionId)
    {
        HashSet<string> set = _connections.GetOrAdd(userId, _ => new HashSet<string>());
        lock (set)
        {
            set.Add(connectionId);
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string userId, string connectionId)
    {
        if (_connections.TryGetValue(userId, out HashSet<string>? set))
        {
            lock (set)
            {
                set.Remove(connectionId);
                if (set.Count == 0)
                    _connections.TryRemove(userId, out _);
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsOnlineAsync(string userId)
    {
        bool online = _connections.TryGetValue(userId, out HashSet<string>? set) && set.Count > 0;
        return Task.FromResult(online);
    }

    public Task<IReadOnlySet<string>> GetOnlineUsersAsync()
    {
        IReadOnlySet<string> users = _connections
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => kv.Key)
            .ToHashSet();
        return Task.FromResult(users);
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
