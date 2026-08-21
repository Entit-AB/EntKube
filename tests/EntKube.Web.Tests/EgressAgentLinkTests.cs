using System.Net;
using System.Net.Sockets;
using System.Text;
using EntKube.Agent;
using EntKube.Agents.Protocol;
using EntKube.Web.Data;
using EntKube.Web.Services.Agents;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// End-to-end tests for the egress agent link, running both halves for real: a
/// live HTTP server hosting the agent endpoint, a real <see cref="AgentClient"/>
/// connecting out to it over a WebSocket, and real TCP sockets on the far side.
///
/// This is the feature's whole reason for existing — reaching a host that only a
/// different network is allowed to talk to — so the parts worth proving are that
/// bytes survive the round trip intact, that several streams can share one link
/// without crossing over, and above all that the agent refuses destinations
/// outside its own allowlist no matter what the server asks for.
/// </summary>
public sealed class EgressAgentLinkTests : IAsyncLifetime
{
    private const string Token = "test-enrolment-token";

    private SqliteConnection connection = null!;
    private ApplicationDbContext db = null!;
    private TestDbContextFactory dbFactory = null!;
    private AgentRegistry registry = null!;
    private WebApplication app = null!;
    private CancellationTokenSource agentLifetime = null!;
    private Task agentTask = null!;
    private Guid agentId;

    /// <summary>An echo server standing in for the endpoint only the agent's network may reach.</summary>
    private TcpListener echoServer = null!;
    private int echoPort;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        agentId = Guid.NewGuid();
        db.EgressAgents.Add(new EgressAgent
        {
            Id = agentId,
            TenantId = tenant.Id,
            Name = "Test agent",
            TokenHash = AgentRegistry.HashToken(Token)
        });
        db.SaveChanges();

        StartEchoServer();

        registry = new AgentRegistry(dbFactory, NullLogger<AgentRegistry>.Instance);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(dbFactory);

        app = builder.Build();
        app.UseWebSockets();
        app.MapAgentEndpoint();
        await app.StartAsync();

        string serverUrl = app.Urls.First();

        AgentOptions options = new()
        {
            ServerUrl = serverUrl,
            Token = Token,
            AllowedHosts = ["127.0.0.1"],
            AllowedPorts = [echoPort],
            ReconnectSeconds = 1
        };

        agentLifetime = new CancellationTokenSource();
        AgentClient client = new(options, _ => { });
        agentTask = client.RunAsync(agentLifetime.Token);

        await WaitUntilConnectedAsync();
    }

    public async Task DisposeAsync()
    {
        await agentLifetime.CancelAsync();
        try { await agentTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }

        await app.StopAsync();
        await app.DisposeAsync();
        await registry.DisposeAsync();

        echoServer.Stop();
        db.Dispose();
        connection.Dispose();
        agentLifetime.Dispose();
    }

    /// <summary>Accepts connections and echoes everything back, so a round trip proves both directions.</summary>
    private void StartEchoServer()
    {
        echoServer = new TcpListener(IPAddress.Loopback, 0);
        echoServer.Start();
        echoPort = ((IPEndPoint)echoServer.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await echoServer.AcceptTcpClientAsync(); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[8192];

                        try
                        {
                            while (true)
                            {
                                int read = await stream.ReadAsync(buffer);
                                if (read == 0) return;
                                await stream.WriteAsync(buffer.AsMemory(0, read));
                            }
                        }
                        catch { /* client went away */ }
                    }
                });
            }
        });
    }

    private async Task WaitUntilConnectedAsync()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (registry.IsConnected(agentId)) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("The agent did not connect within 5 seconds.");
    }

    // ──────── The link ────────

    [Fact]
    public void Agent_connects_outbound_and_registers_itself()
    {
        // Nothing listens on the agent's side; the link exists only because the
        // agent dialled out. That is the property the whole feature rests on.
        registry.IsConnected(agentId).Should().BeTrue();

        var status = registry.GetStatus(agentId);
        status.Should().NotBeNull();
        status!.Value.RemoteAddress.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Bytes_survive_the_round_trip_through_the_agent()
    {
        await using Stream stream = await registry.OpenStreamAsync(agentId, "127.0.0.1", echoPort);

        byte[] sent = Encoding.UTF8.GetBytes("hello through the agent");
        await stream.WriteAsync(sent);

        byte[] received = await ReadExactlyAsync(stream, sent.Length);

        received.Should().BeEquivalentTo(sent);
    }

    [Fact]
    public async Task A_payload_larger_than_one_frame_is_reassembled_in_order()
    {
        // Writes above the frame limit are split by the stream layer; if the
        // reassembly were wrong this is where it would show, and a TLS record
        // split across frames would fail in exactly this way.
        await using Stream stream = await registry.OpenStreamAsync(agentId, "127.0.0.1", echoPort);

        byte[] sent = new byte[AgentProtocol.MaxPayloadSize * 3 / 2];
        Random.Shared.NextBytes(sent);

        await stream.WriteAsync(sent);
        byte[] received = await ReadExactlyAsync(stream, sent.Length);

        received.Should().BeEquivalentTo(sent);
    }

    [Fact]
    public async Task Concurrent_streams_do_not_cross_over()
    {
        // Everything shares one WebSocket, so a stream-id mix-up would deliver one
        // conversation's bytes into another — corruption rather than an error.
        await using Stream first = await registry.OpenStreamAsync(agentId, "127.0.0.1", echoPort);
        await using Stream second = await registry.OpenStreamAsync(agentId, "127.0.0.1", echoPort);

        byte[] firstPayload = Encoding.UTF8.GetBytes(new string('a', 4096));
        byte[] secondPayload = Encoding.UTF8.GetBytes(new string('b', 4096));

        await Task.WhenAll(first.WriteAsync(firstPayload).AsTask(), second.WriteAsync(secondPayload).AsTask());

        byte[] firstBack = await ReadExactlyAsync(first, firstPayload.Length);
        byte[] secondBack = await ReadExactlyAsync(second, secondPayload.Length);

        firstBack.Should().BeEquivalentTo(firstPayload);
        secondBack.Should().BeEquivalentTo(secondPayload);
    }

    // ──────── The security boundary ────────

    [Fact]
    public async Task Agent_refuses_a_host_outside_its_own_allowlist()
    {
        // This is the control that makes the agent safe to run inside a customer
        // network: EntKube asking is not enough.
        Func<Task> act = async () => await registry.OpenStreamAsync(agentId, "example.com", echoPort);

        await act.Should().ThrowAsync<IOException>().WithMessage("*allowlist*");
    }

    [Fact]
    public async Task Agent_refuses_an_allowed_host_on_a_port_it_does_not_permit()
    {
        // Host and port are both part of the rule; allowing a host must not open
        // every service on it.
        Func<Task> act = async () => await registry.OpenStreamAsync(agentId, "127.0.0.1", echoPort + 1);

        await act.Should().ThrowAsync<IOException>().WithMessage("*allowlist*");
    }

    [Fact]
    public async Task A_refused_stream_does_not_break_the_link()
    {
        // A refusal must be an ordinary answer, not something that poisons the
        // connection for everything else.
        try { await registry.OpenStreamAsync(agentId, "example.com", echoPort); } catch (IOException) { }

        await using Stream stream = await registry.OpenStreamAsync(agentId, "127.0.0.1", echoPort);
        byte[] sent = Encoding.UTF8.GetBytes("still working");
        await stream.WriteAsync(sent);

        (await ReadExactlyAsync(stream, sent.Length)).Should().BeEquivalentTo(sent);
    }

    // ──────── Authentication ────────

    [Fact]
    public async Task An_unknown_token_authenticates_to_nothing()
    {
        (await registry.AuthenticateAsync("not-the-token")).Should().BeNull();
    }

    [Fact]
    public async Task A_disabled_agent_cannot_authenticate()
    {
        EgressAgent agent = await db.EgressAgents.FirstAsync(a => a.Id == agentId);
        agent.IsEnabled = false;
        await db.SaveChangesAsync();

        try
        {
            (await registry.AuthenticateAsync(Token)).Should().BeNull();
        }
        finally
        {
            agent.IsEnabled = true;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task The_connect_record_captures_where_the_agent_dialled_from()
    {
        // An operator needs to be able to confirm the link comes from the network
        // they expect, without logging into the box.
        EgressAgent agent = await db.EgressAgents.AsNoTracking().FirstAsync(a => a.Id == agentId);

        agent.LastSeenAt.Should().NotBeNull();
        agent.LastRemoteAddress.Should().NotBeNullOrEmpty();
        agent.ReportedAllowlist.Should().Be("127.0.0.1");
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), timeout.Token);
            if (read == 0) throw new IOException($"Stream ended after {offset} of {count} bytes.");
            offset += read;
        }

        return buffer;
    }
}
