namespace EntKube.Web.Services;

/// <summary>
/// Tracks which users currently have an active Blazor Server circuit, giving a
/// live "online now" signal for the admin users page.
///
/// A connection is identified by a stable per-circuit id so connect/disconnect
/// (including transient reconnects) balance out correctly. Two implementations
/// exist:
/// <list type="bullet">
///   <item><see cref="InMemoryPresenceTracker"/> — process-local, the default.</item>
///   <item><c>RedisPresenceTracker</c> — shared across instances via a Redis
///   backplane, opt-in through configuration.</item>
/// </list>
/// </summary>
public interface IPresenceTracker
{
    /// <summary>Records that a circuit for the user came up.</summary>
    Task ConnectAsync(string userId, string connectionId);

    /// <summary>Records that a circuit for the user went away.</summary>
    Task DisconnectAsync(string userId, string connectionId);

    /// <summary>True if the user has at least one active circuit right now.</summary>
    Task<bool> IsOnlineAsync(string userId);

    /// <summary>The set of user IDs with at least one active circuit.</summary>
    Task<IReadOnlySet<string>> GetOnlineUsersAsync();

    /// <summary>
    /// Refreshes the liveness of this instance's connections. Backends that
    /// expire presence via TTL (Redis) use this to keep live circuits from
    /// being reaped; the in-memory backend does nothing.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}
