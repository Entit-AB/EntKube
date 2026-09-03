using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using EntKube.Agents.Protocol;

namespace EntKube.Web.Services.Agents;

/// <summary>
/// Tracks which egress agents currently have a link open, and opens streams over
/// them on request.
///
/// Singleton: a link lives for as long as the agent stays connected, far longer
/// than any request or circuit that uses it.
///
/// An agent may reconnect (restart, network blip) and may legitimately be running
/// more than once for redundancy, so connections are held per agent as a list and
/// the healthiest one is chosen per stream.
/// </summary>
public sealed class AgentRegistry(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<AgentRegistry> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, List<AgentConnection>> connections = new();
    private readonly SemaphoreSlim mutation = new(1, 1);

    /// <summary>Hashes an enrolment token the same way it is stored.</summary>
    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// Generates an enrolment token. Returned once to be shown to the operator;
    /// only its hash is persisted.
    /// </summary>
    public static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Resolves the agent a presented token belongs to, or null when the token is
    /// unknown or the agent is disabled.
    ///
    /// The lookup compares hashes in the database rather than scanning and
    /// comparing in memory, so an unknown token costs the same as a known one.
    /// </summary>
    public async Task<EgressAgent?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        string hash = HashToken(token);

        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.EgressAgents
            .FirstOrDefaultAsync(a => a.TokenHash == hash && a.IsEnabled, ct);
    }

    /// <summary>
    /// Registers a newly connected agent link and runs its receive loop until the
    /// agent disconnects. Returns when the link is finished.
    /// </summary>
    public async Task RunConnectionAsync(
        EgressAgent agent, WebSocket socket, string remoteAddress, string? reportedAllowlist, CancellationToken ct)
    {
        AgentConnection connection = new(agent.Id, remoteAddress, socket, logger);

        await mutation.WaitAsync(ct);
        try
        {
            connections.GetOrAdd(agent.Id, _ => []).Add(connection);
        }
        finally
        {
            mutation.Release();
        }

        logger.LogInformation(
            "Egress agent {Name} ({AgentId}) connected from {Address}", agent.Name, agent.Id, remoteAddress);

        await RecordSeenAsync(agent.Id, remoteAddress, reportedAllowlist, ct);

        try
        {
            await connection.ReceiveLoopAsync(ct);
        }
        finally
        {
            await mutation.WaitAsync(CancellationToken.None);
            try
            {
                if (connections.TryGetValue(agent.Id, out List<AgentConnection>? list))
                {
                    list.Remove(connection);
                    if (list.Count == 0) connections.TryRemove(agent.Id, out _);
                }
            }
            finally
            {
                mutation.Release();
            }

            await connection.DisposeAsync();

            logger.LogInformation("Egress agent {Name} ({AgentId}) disconnected", agent.Name, agent.Id);
        }
    }

    /// <summary>True when at least one link is open for this agent.</summary>
    public bool IsConnected(Guid agentId)
        => connections.TryGetValue(agentId, out List<AgentConnection>? list) && list.Any(c => c.IsAlive);

    /// <summary>Live link details for the UI, or null when the agent is not connected.</summary>
    public (DateTime ConnectedAt, string RemoteAddress, int ActiveStreams)? GetStatus(Guid agentId)
    {
        AgentConnection? connection = Pick(agentId);
        return connection is null ? null : (connection.ConnectedAt, connection.RemoteAddress, connection.ActiveStreams);
    }

    /// <summary>
    /// Opens a TCP stream to <paramref name="host"/>:<paramref name="port"/>
    /// through the given agent.
    /// </summary>
    /// <exception cref="IOException">No link is open, or the agent refused the host.</exception>
    public async Task<Stream> OpenStreamAsync(
        Guid agentId, string host, int port, CancellationToken ct = default)
    {
        AgentConnection connection = Pick(agentId)
            ?? throw new IOException(
                "The egress agent for this connection is not currently connected. "
                + "Start the agent in the network that is allowed to reach this endpoint, then retry.");

        return await connection.OpenStreamAsync(host, port, ct);
    }

    /// <summary>
    /// Chooses a live link for an agent. When several are connected — a redundant
    /// pair, or a reconnect that overlapped the old link — the least loaded wins,
    /// which also naturally drains a link that is on its way out.
    /// </summary>
    private AgentConnection? Pick(Guid agentId)
        => connections.TryGetValue(agentId, out List<AgentConnection>? list)
            ? list.Where(c => c.IsAlive).MinBy(c => c.ActiveStreams)
            : null;

    private async Task RecordSeenAsync(Guid agentId, string remoteAddress, string? allowlist, CancellationToken ct)
    {
        try
        {
            using ApplicationDbContext db = dbFactory.CreateDbContext();

            EgressAgent? agent = await db.EgressAgents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
            if (agent is null) return;

            agent.LastSeenAt = DateTime.UtcNow;
            agent.LastRemoteAddress = remoteAddress;
            if (allowlist is not null) agent.ReportedAllowlist = allowlist;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Bookkeeping only — a failure here must not drop a working link.
            logger.LogWarning(ex, "Could not record last-seen for agent {AgentId}", agentId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (List<AgentConnection> list in connections.Values)
        {
            foreach (AgentConnection connection in list.ToList())
            {
                await connection.DisposeAsync();
            }
        }

        connections.Clear();
        mutation.Dispose();
    }
}
