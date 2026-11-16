using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using EntKube.Agents.Protocol;

namespace EntKube.Web.Services.Agents;

/// <summary>
/// What an operator can see about a live agent link.
/// </summary>
/// <param name="ProtocolVersion">
/// What the agent declared on the handshake. 1 is a tunnel-only agent that predates the task
/// vocabulary: it works, and it will never be dispatched a lease.
/// </param>
/// <param name="DeclaredCapabilities">What the agent said it can do, empty until it has said.</param>
/// <param name="AgentVersion">The agent binary's own version, once it has declared one.</param>
/// <remarks>
/// Deliberately not persisted on <c>EgressAgent</c>. The allowlist is stored because an operator
/// wants it while the agent is <em>disconnected</em>; a version and a capability set describe the
/// link that is open right now, and a stored copy of them would be a claim about a process that
/// may since have been replaced.
/// </remarks>
public sealed record AgentLinkStatus(
    DateTime ConnectedAt,
    string RemoteAddress,
    int ActiveStreams,
    int ActiveLeases,
    int ProtocolVersion,
    IReadOnlyList<string> DeclaredCapabilities,
    string? AgentVersion);

/// <summary>
/// Tracks which egress agents currently have a link open, and opens streams or
/// dispatches leases over them on request.
///
/// Singleton: a link lives for as long as the agent stays connected, far longer
/// than any request or circuit that uses it.
///
/// An agent may reconnect (restart, network blip) and may legitimately be running
/// more than once for redundancy, so connections are held per agent as a list and
/// the healthiest one is chosen per stream.
///
/// <para>⚠️ <b>This registry is process-local and has no backplane.</b> An agent's link lives in
/// whichever EntKube instance it happened to dial, so another instance asking about that agent is
/// told it is not connected — which is true of that process and false of the installation. In the
/// two-instance deployment this repository documents, <c>/agent/connect</c> is round-robined
/// across the web instances and the worker is deliberately outside that pool, so background work
/// that needs agent egress can reach no agent at all. Nothing here fixes that; the messages below
/// at least stop it from reading as "your agent is down". The repair is item 10 of
/// docs/decomposition.md — the agent link moves behind a gateway — or a dispatch hop that this
/// vocabulary does not yet have an idempotency key for.</para>
/// </summary>
public sealed class AgentRegistry(
    IDbContextFactory<FleetDbContext> dbFactory,
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

        using FleetDbContext db = dbFactory.CreateDbContext();

        return await db.EgressAgents
            .FirstOrDefaultAsync(a => a.TokenHash == hash && a.IsEnabled, ct);
    }

    /// <summary>
    /// Registers a newly connected agent link and runs its receive loop until the
    /// agent disconnects. Returns when the link is finished.
    /// </summary>
    /// <param name="protocolVersion">
    /// What the agent declared on the handshake, already defaulted to
    /// <see cref="AgentProtocol.TunnelOnlyVersion"/> when it declared nothing.
    /// </param>
    public async Task RunConnectionAsync(
        EgressAgent agent, WebSocket socket, string remoteAddress, string? reportedAllowlist,
        int protocolVersion, CancellationToken ct)
    {
        AgentConnection connection = new(agent.Id, remoteAddress, socket, logger, protocolVersion);

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
            "Egress agent {Name} ({AgentId}) connected from {Address} speaking protocol {Protocol}",
            agent.Name, agent.Id, remoteAddress, protocolVersion);

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

    /// <summary>
    /// True when at least one link is open for this agent <b>in this process</b>.
    /// A false here does not mean the agent is down — see the note on this class.
    /// </summary>
    public bool IsConnected(Guid agentId)
        => connections.TryGetValue(agentId, out List<AgentConnection>? list) && list.Any(c => c.IsAlive);

    /// <summary>Live link details for the UI, or null when the agent is not connected to this instance.</summary>
    public AgentLinkStatus? GetStatus(Guid agentId)
    {
        AgentConnection? connection = Pick(agentId);

        if (connection is null) return null;

        CapabilityPayload? declared = connection.DeclaredCapabilities;

        return new AgentLinkStatus(
            connection.ConnectedAt,
            connection.RemoteAddress,
            connection.ActiveStreams,
            connection.ActiveLeases,
            connection.ProtocolVersion,
            declared?.Capabilities ?? [],
            declared?.AgentVersion);
    }

    /// <summary>
    /// Opens a TCP stream to <paramref name="host"/>:<paramref name="port"/>
    /// through the given agent.
    /// </summary>
    /// <exception cref="IOException">No link is open, or the agent refused the host.</exception>
    public async Task<Stream> OpenStreamAsync(
        Guid agentId, string host, int port, CancellationToken ct = default)
    {
        AgentConnection connection = Pick(agentId) ?? throw new IOException(NotConnectedHere(agentId));

        return await connection.OpenStreamAsync(host, port, ct);
    }

    /// <summary>
    /// Dispatches one unit of work to an agent and waits for its verdict.
    ///
    /// <para><b>The registry is where the capability is authorized.</b> A name EntKube has not
    /// registered is refused here, whatever the agent advertises, because an agent is a binary on
    /// a customer's box and cannot be allowed to argue a capability into existence by naming it.
    /// The registry also cannot express a mutating capability at all — see
    /// <see cref="AgentCapabilityEffect"/> — so this path has no way to ask an agent to change
    /// anything, which is docs/decomposition.md §5.4.1's "read and propose, never apply" enforced
    /// as an absence rather than as a check.</para>
    /// </summary>
    /// <exception cref="IOException">No link is open in this process.</exception>
    /// <exception cref="InvalidOperationException">
    /// The capability is not registered, or the agent is too old to be given a lease.
    /// </exception>
    public async Task<ResultPayload> DispatchAsync(
        Guid agentId,
        string capability,
        TimeSpan deadline,
        JsonElement? input = null,
        Func<ProgressPayload, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (AgentCapabilityRegistry.Find(capability) is null)
        {
            throw new InvalidOperationException(
                $"'{capability}' is not a capability EntKube knows how to ask for. "
                + $"Registered: {(AgentCapabilityRegistry.All.Count == 0
                    ? "none yet"
                    : string.Join(", ", AgentCapabilityRegistry.All.Select(c => c.Name)))}.");
        }

        AgentConnection connection = Pick(agentId) ?? throw new IOException(NotConnectedHere(agentId));

        return await connection.RunLeaseAsync(capability, deadline, input, onProgress, ct);
    }

    /// <summary>
    /// What to tell an operator when this process holds no link for an agent.
    ///
    /// <para>It used to say "Start the agent in the network that is allowed to reach this
    /// endpoint, then retry", which is advice to start a process that is very often already
    /// running: an agent connects to exactly one instance, so in a multi-instance deployment
    /// every other instance sees precisely this state for a perfectly healthy agent.</para>
    /// </summary>
    private static string NotConnectedHere(Guid agentId)
        => $"No egress agent link for {agentId} is open on this EntKube instance. "
            + "Either the agent is not running, or it is connected to a different instance — "
            + "an agent holds one link, and only the instance holding it can use it.";

    /// <summary>
    /// Chooses a live link for an agent. When several are connected — a redundant
    /// pair, or a reconnect that overlapped the old link — the least loaded wins,
    /// which also naturally drains a link that is on its way out.
    /// </summary>
    private AgentConnection? Pick(Guid agentId)
        => connections.TryGetValue(agentId, out List<AgentConnection>? list)
            ? list.Where(c => c.IsAlive).MinBy(c => c.ActiveStreams + c.ActiveLeases)
            : null;

    private async Task RecordSeenAsync(Guid agentId, string remoteAddress, string? allowlist, CancellationToken ct)
    {
        try
        {
            using FleetDbContext db = dbFactory.CreateDbContext();

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
