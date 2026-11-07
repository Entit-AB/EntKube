using System.Net;
using System.Net.Sockets;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>A real socket answering one canned response, counting who reached it.</summary>
internal sealed class CannedServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly string response;
    private readonly TimeSpan delay;
    private readonly bool keepAlive;

    public int Hits;
    public string? LastRequest;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <param name="keepAlive">Answer more than one request on a connection, so a client can reuse it.</param>
    public CannedServer(int status = 200, string body = "ok", string extraHeaders = "", TimeSpan delay = default, bool keepAlive = false)
    {
        this.delay = delay;
        this.keepAlive = keepAlive;
        string connection = keepAlive ? "" : "Connection: close\r\n";
        response = $"HTTP/1.1 {status} X\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{extraHeaders}{connection}\r\n{body}";
        listener.Start();
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref Hits);

                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[4096];

                do
                {
                    int read = await stream.ReadAsync(buffer, stop.Token);
                    if (read == 0) break;
                    LastRequest = Encoding.ASCII.GetString(buffer, 0, read);

                    if (delay > TimeSpan.Zero) await Task.Delay(delay, stop.Token);

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response), stop.Token);
                }
                while (keepAlive);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}

/// <summary>
/// The request itself, over real sockets. Loopback is reachable only with
/// <c>allowPrivateTargets</c>, which is what lets the same server prove both halves.
/// </summary>
public class SyntheticProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static HttpClient Client(bool allowPrivate = true, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        new(SyntheticProbe.CreateHandler(allowPrivate, resolve)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private static SyntheticCheck Check(string url, int? status = null, string? text = null) =>
        new() { Url = url, ExpectedStatus = status, ExpectedText = text };

    private static Task<SyntheticProbeResult> RunAsync(HttpClient client, SyntheticCheck check) =>
        SyntheticProbe.RunAsync(client, check, Timeout, CancellationToken.None);

    [Fact]
    public async Task A_healthy_answer_passes_and_is_timed()
    {
        using CannedServer server = new();
        using HttpClient client = Client();

        SyntheticProbeResult result = await RunAsync(client, Check($"http://127.0.0.1:{server.Port}/"));

        result.Passed.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Failure.Should().BeNull();
        server.Hits.Should().Be(1);
    }

    [Theory]
    [InlineData(null, 200, true)]
    [InlineData(null, 204, true)]
    [InlineData(null, 302, true)]
    [InlineData(null, 399, true)]
    [InlineData(null, 400, false)]
    [InlineData(null, 500, false)]
    [InlineData(null, 199, false)]
    [InlineData(404, 404, true)]
    [InlineData(200, 201, false)]
    public void A_status_passes_by_the_expectation_or_by_being_2xx_or_3xx(int? expected, int status, bool passes)
    {
        SyntheticProbe.StatusPasses(expected, status).Should().Be(passes);
    }

    [Fact]
    public async Task A_500_fails_and_says_what_came_back()
    {
        using CannedServer server = new(status: 500);
        using HttpClient client = Client();

        SyntheticProbeResult result = await RunAsync(client, Check($"http://127.0.0.1:{server.Port}/"));

        result.Passed.Should().BeFalse();
        result.StatusCode.Should().Be(500);
        result.Failure.Should().Be("Answered 500.");
    }

    [Fact]
    public async Task Expected_text_is_looked_for_in_the_body()
    {
        using CannedServer server = new(body: "status: all systems go");
        using HttpClient client = Client();

        (await RunAsync(client, Check($"http://127.0.0.1:{server.Port}/", text: "all systems"))).Passed.Should().BeTrue();

        SyntheticProbeResult missing = await RunAsync(client, Check($"http://127.0.0.1:{server.Port}/", text: "not there"));
        missing.Passed.Should().BeFalse();
        missing.Failure.Should().Contain("expected text");
    }

    [Fact]
    public async Task The_body_never_appears_in_what_is_recorded()
    {
        using CannedServer server = new(status: 500, body: "internal secret page");
        using HttpClient client = Client();

        SyntheticProbeResult result = await RunAsync(client, Check($"http://127.0.0.1:{server.Port}/"));

        result.Failure.Should().NotContain("secret");
    }

    [Fact]
    public async Task Only_the_start_of_a_huge_body_is_read()
    {
        string body = new string('x', SyntheticProbe.MaxBodyBytes) + "needle";
        using CannedServer server = new(body: body);
        using HttpClient client = Client();

        (await RunAsync(client, Check($"http://127.0.0.1:{server.Port}/", text: "needle"))).Passed.Should().BeFalse();
    }

    /// <summary>A redirect to the metadata address is the obvious way past a check of the first URL.</summary>
    [Fact]
    public async Task A_redirect_is_an_answer_and_is_never_followed()
    {
        using CannedServer target = new();
        using CannedServer server = new(status: 302, extraHeaders: $"Location: http://127.0.0.1:{target.Port}/\r\n");
        using HttpClient client = Client();

        SyntheticProbeResult result = await RunAsync(client, Check($"http://127.0.0.1:{server.Port}/"));

        result.StatusCode.Should().Be(302);
        target.Hits.Should().Be(0);
    }

    [Fact]
    public async Task A_slow_answer_fails_as_a_timeout()
    {
        using CannedServer server = new(delay: TimeSpan.FromSeconds(30));
        using HttpClient client = Client();

        SyntheticProbeResult result = await SyntheticProbe.RunAsync(
            client, Check($"http://127.0.0.1:{server.Port}/"), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.Failure.Should().Be("Timed out.");
    }

    [Fact]
    public async Task Shutting_down_is_not_recorded_as_the_site_failing()
    {
        using CannedServer server = new(delay: TimeSpan.FromSeconds(30));
        using HttpClient client = Client();
        using CancellationTokenSource stopping = new(TimeSpan.FromMilliseconds(100));

        Func<Task> run = () => SyntheticProbe.RunAsync(
            client, Check($"http://127.0.0.1:{server.Port}/"), Timeout, stopping.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_closed_port_fails_with_a_fixed_phrase_and_no_exception_text()
    {
        int closed;
        using (CannedServer server = new()) closed = server.Port;

        using HttpClient client = Client();

        (await RunAsync(client, Check($"http://127.0.0.1:{closed}/"))).Failure.Should().Be("Could not connect.");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.1")]
    [InlineData("[::ffff:127.0.0.1]")]
    public async Task A_private_literal_is_refused_at_connection(string host)
    {
        using HttpClient client = Client(allowPrivate: false);

        (await RunAsync(client, Check($"http://{host}/"))).Failure.Should().Contain("not one EntKube may reach");
    }

    /// <summary>
    /// ⭐ DNS rebinding. The name passed when the check was saved and answers with a private address
    /// when connected to. The refusal has to happen here, and the server proves nothing was sent.
    /// </summary>
    [Fact]
    public async Task A_name_that_now_resolves_to_a_private_address_is_refused_before_a_byte_is_sent()
    {
        using CannedServer inside = new();
        using HttpClient client = Client(allowPrivate: false, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        SyntheticProbeResult result = await RunAsync(client, Check($"http://rebind.example:{inside.Port}/"));

        result.Passed.Should().BeFalse();
        result.Failure.Should().Contain("not one EntKube may reach");
        inside.Hits.Should().Be(0);
    }

    [Fact]
    public async Task One_private_address_among_public_ones_is_enough_to_refuse()
    {
        using CannedServer inside = new();
        using HttpClient client = Client(
            allowPrivate: false, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }));

        (await RunAsync(client, Check($"http://mixed.example:{inside.Port}/"))).Failure.Should().Contain("not one EntKube may reach");
        inside.Hits.Should().Be(0);
    }

    /// <summary>
    /// The resolver is asked per connection, so a name cannot be good once and bad forever.
    ///
    /// <para>⚠️ The server must keep its connections open. A first version sent
    /// <c>Connection: close</c>, so nothing could be reused and this passed with pooling switched
    /// back on, which a mutation run caught.</para>
    /// </summary>
    [Fact]
    public async Task Each_run_resolves_afresh_rather_than_reusing_a_connection()
    {
        using CannedServer server = new(keepAlive: true);
        int lookups = 0;
        using HttpClient client = Client(resolve: (_, _) =>
        {
            Interlocked.Increment(ref lookups);
            return Task.FromResult(new[] { IPAddress.Loopback });
        });

        await RunAsync(client, Check($"http://site.example:{server.Port}/"));
        await RunAsync(client, Check($"http://site.example:{server.Port}/"));

        lookups.Should().Be(2);
    }

    [Fact]
    public async Task The_request_goes_to_the_validated_address_with_the_original_host_name()
    {
        using CannedServer server = new();
        using HttpClient client = Client(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        await RunAsync(client, Check($"http://site.example:{server.Port}/health"));

        server.LastRequest.Should().Contain("GET /health").And.Contain($"Host: site.example:{server.Port}");
    }

    [Fact]
    public async Task A_name_that_resolves_to_nothing_is_refused()
    {
        using HttpClient client = Client(resolve: (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

        (await RunAsync(client, Check("http://empty.example/"))).Passed.Should().BeFalse();
    }
}

public sealed class SyntheticCheckServiceTests : IDisposable
{
    private const string PublicUrl = "https://8.8.8.8/health";

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly SyntheticCheckService service;
    private readonly TelemetryAlertRuleService rules;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid strangerClusterId = Guid.NewGuid();
    private readonly DateTime now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    public SyntheticCheckServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            db.Database.EnsureCreated();
            Guid env = Guid.NewGuid(), strangerEnv = Guid.NewGuid();
            db.Tenants.AddRange(new Tenant { Id = tenantId, Name = "A", Slug = "a" }, new Tenant { Id = strangerId, Name = "B", Slug = "b" });
            db.SaveChanges();
            db.Environments.AddRange(
                new EntKube.Web.Data.Environment { Id = env, TenantId = tenantId, Name = "e" },
                new EntKube.Web.Data.Environment { Id = strangerEnv, TenantId = strangerId, Name = "e" });
            db.SaveChanges();
            db.KubernetesClusters.AddRange(
                new KubernetesCluster { Id = clusterId, TenantId = tenantId, EnvironmentId = env, Name = "c", ApiServerUrl = "https://a" },
                new KubernetesCluster { Id = strangerClusterId, TenantId = strangerId, EnvironmentId = strangerEnv, Name = "d", ApiServerUrl = "https://b" });
            db.SaveChanges();
        }

        factory = new TestDbContextFactory(connection);
        rules = new TelemetryAlertRuleService(factory);
        service = new SyntheticCheckService(factory, rules, new ConfigurationBuilder().Build());
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private SyntheticCheck Input(string name = "Home", string url = PublicUrl) =>
        new() { Name = name, Url = url, ClusterId = clusterId, FailuresBeforeAlert = 2, Severity = "critical" };

    private async Task<SyntheticCheck> CreatedAsync(string name = "Home")
    {
        (await service.SaveAsync(tenantId, Input(name))).Should().BeNull();
        return (await service.ListAsync(tenantId)).Single(r => r.Check.Name == name).Check;
    }

    private static SyntheticProbeResult Ok(int ms = 10) => new(true, 200, ms, null);

    private static SyntheticProbeResult Bad() => new(false, 500, 10, "Answered 500.");

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@8.8.8.8/")]
    [InlineData("not a url")]
    public async Task A_url_the_platform_should_not_fetch_is_refused_when_saved(string url)
    {
        (await service.SaveAsync(tenantId, Input(url: url))).Should().NotBeNull();

        (await service.ListAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_creates_a_rule_that_carries_the_check_s_terms()
    {
        SyntheticCheck check = await CreatedAsync();

        TelemetryAlertRule rule = (await rules.ListAsync(tenantId)).Single();

        rule.Kind.Should().Be(TelemetryAlertKind.SyntheticCheck);
        rule.CheckId.Should().Be(check.Id);
        rule.ClusterId.Should().Be(clusterId);
        rule.Threshold.Should().Be(2);
        rule.Severity.Should().Be("critical");
        rule.Name.Should().Contain("Home");
    }

    [Fact]
    public async Task Editing_a_check_moves_its_rule_with_it_rather_than_adding_another()
    {
        SyntheticCheck check = await CreatedAsync();

        check.FailuresBeforeAlert = 5;
        check.Severity = "info";
        (await service.SaveAsync(tenantId, check)).Should().BeNull();

        TelemetryAlertRule rule = (await rules.ListAsync(tenantId)).Single();
        rule.Threshold.Should().Be(5);
        rule.Severity.Should().Be("info");
    }

    [Fact]
    public async Task Disabling_a_check_disables_its_rule_and_resolves_a_firing_incident()
    {
        SyntheticCheck check = await CreatedAsync();
        Guid ruleId = (await rules.ListAsync(tenantId)).Single().Id;
        Guid incidentId = await OpenIncidentAsync(ruleId);

        check.IsEnabled = false;
        (await service.SaveAsync(tenantId, check)).Should().BeNull();

        (await rules.ListAsync(tenantId)).Single().IsEnabled.Should().BeFalse();
        (await IncidentStatusAsync(incidentId)).Should().Be(IncidentStatus.Resolved);
    }

    [Fact]
    public async Task Deleting_a_check_takes_its_rule_and_history_and_resolves_its_incident()
    {
        SyntheticCheck check = await CreatedAsync();
        await service.RecordAsync(tenantId, check.Id, Ok(), now);
        Guid incidentId = await OpenIncidentAsync((await rules.ListAsync(tenantId)).Single().Id);

        await service.DeleteAsync(tenantId, check.Id);

        (await service.ListAsync(tenantId)).Should().BeEmpty();
        (await rules.ListAsync(tenantId)).Should().BeEmpty();
        (await IncidentStatusAsync(incidentId)).Should().Be(IncidentStatus.Resolved);

        await using ApplicationDbContext db = factory.CreateDbContext();
        (await db.SyntheticCheckResults.CountAsync()).Should().Be(0);
    }

    private async Task<Guid> OpenIncidentAsync(Guid ruleId)
    {
        await using ApplicationDbContext db = factory.CreateDbContext();
        AlertIncident incident = new()
        {
            ClusterId = clusterId, Fingerprint = $"telemetry:{ruleId:N}:{clusterId:N}", AlertName = "x",
            Status = IncidentStatus.Active, StartsAt = now, Severity = "critical",
        };
        db.AlertIncidents.Add(incident);
        await db.SaveChangesAsync();
        return incident.Id;
    }

    private async Task<IncidentStatus> IncidentStatusAsync(Guid id)
    {
        await using ApplicationDbContext db = factory.CreateDbContext();
        return await db.AlertIncidents.AsNoTracking().Where(i => i.Id == id).Select(i => i.Status).SingleAsync();
    }

    [Fact]
    public async Task Another_tenant_s_cluster_cannot_be_named()
    {
        SyntheticCheck input = Input();
        input.ClusterId = strangerClusterId;

        (await service.SaveAsync(tenantId, input)).Should().Contain("cluster");
    }

    [Fact]
    public async Task Another_tenant_cannot_read_edit_delete_or_record_against_a_check()
    {
        SyntheticCheck check = await CreatedAsync();

        (await service.ListAsync(strangerId)).Should().BeEmpty();
        (await service.DueAsync(strangerId, now.AddDays(1))).Should().BeEmpty();

        SyntheticCheck hijack = Input("Stolen");
        hijack.Id = check.Id;
        hijack.ClusterId = strangerClusterId;
        (await service.SaveAsync(strangerId, hijack)).Should().Contain("no longer exists");

        await service.DeleteAsync(strangerId, check.Id);
        await service.RecordAsync(strangerId, check.Id, Bad(), now);

        // The positive control: all of it was still there to protect.
        SyntheticCheck after = (await service.ListAsync(tenantId)).Single().Check;
        after.Name.Should().Be("Home");
        after.ConsecutiveFailures.Should().Be(0);
        (await rules.ListAsync(tenantId)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_interval_cannot_go_below_a_minute()
    {
        SyntheticCheck input = Input();
        input.IntervalSeconds = 1;

        await service.SaveAsync(tenantId, input);

        (await service.ListAsync(tenantId)).Single().Check.IntervalSeconds.Should().Be(SyntheticCheckService.MinIntervalSeconds);
    }

    [Fact]
    public async Task A_tenant_has_a_limit_on_checks()
    {
        await using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.SyntheticChecks.AddRange(Enumerable.Range(0, SyntheticCheckService.MaxChecksPerTenant).Select(i => new SyntheticCheck
            {
                Id = Guid.NewGuid(), TenantId = tenantId, ClusterId = clusterId, Name = $"c{i}", Url = PublicUrl,
            }));
            await db.SaveChangesAsync();
        }

        (await service.SaveAsync(tenantId, Input())).Should().Contain("at most");
    }

    [Fact]
    public async Task A_new_check_is_due_at_once_and_a_run_schedules_the_next()
    {
        SyntheticCheck check = await CreatedAsync();

        (await service.DueAsync(tenantId, DateTime.UtcNow)).Select(c => c.Id).Should().Equal(check.Id);

        DateTime ran = DateTime.UtcNow;
        await service.RecordAsync(tenantId, check.Id, Ok(), ran);

        (await service.DueAsync(tenantId, ran)).Should().BeEmpty();
        (await service.DueAsync(tenantId, ran.AddSeconds(check.IntervalSeconds))).Should().ContainSingle();
    }

    [Fact]
    public async Task A_disabled_check_is_never_due()
    {
        SyntheticCheck check = await CreatedAsync();
        check.IsEnabled = false;
        await service.SaveAsync(tenantId, check);

        (await service.DueAsync(tenantId, now.AddDays(1))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_streak_counts_up_and_a_pass_resets_it()
    {
        SyntheticCheck check = await CreatedAsync();

        await service.RecordAsync(tenantId, check.Id, Bad(), now);
        await service.RecordAsync(tenantId, check.Id, Bad(), now.AddMinutes(5));

        SyntheticCheck failing = (await service.ListAsync(tenantId)).Single().Check;
        failing.ConsecutiveFailures.Should().Be(2);
        failing.State.Should().Be(SyntheticCheckState.Failing);
        failing.LastFailure.Should().Be("Answered 500.");

        await service.RecordAsync(tenantId, check.Id, Ok(), now.AddMinutes(10));

        SyntheticCheck passing = (await service.ListAsync(tenantId)).Single().Check;
        passing.ConsecutiveFailures.Should().Be(0);
        passing.State.Should().Be(SyntheticCheckState.Passing);
        passing.LastFailure.Should().BeNull();
    }

    /// <summary>3 of 4 is 75, which a passed count, a failed count or a stuck value cannot produce.</summary>
    [Fact]
    public async Task Uptime_is_the_share_of_runs_passed_in_the_last_day()
    {
        SyntheticCheck check = await CreatedAsync();
        DateTime recent = DateTime.UtcNow.AddHours(-1);

        await service.RecordAsync(tenantId, check.Id, Ok(), recent);
        await service.RecordAsync(tenantId, check.Id, Ok(), recent);
        await service.RecordAsync(tenantId, check.Id, Ok(), recent);
        await service.RecordAsync(tenantId, check.Id, Bad(), recent);

        // Outside the day, so it must not count.
        await service.RecordAsync(tenantId, check.Id, Bad(), DateTime.UtcNow.AddHours(-25));

        (await service.ListAsync(tenantId)).Single().UptimePercent.Should().Be(75);
    }

    [Fact]
    public async Task A_check_with_no_runs_has_unknown_uptime_rather_than_zero()
    {
        await CreatedAsync();

        (await service.ListAsync(tenantId)).Single().UptimePercent.Should().BeNull();
    }

    [Fact]
    public async Task Uptime_is_counted_per_check()
    {
        SyntheticCheck good = await CreatedAsync("good");
        SyntheticCheck bad = await CreatedAsync("bad");
        DateTime recent = DateTime.UtcNow.AddHours(-1);

        await service.RecordAsync(tenantId, good.Id, Ok(), recent);
        await service.RecordAsync(tenantId, bad.Id, Bad(), recent);

        Dictionary<string, double?> uptime = (await service.ListAsync(tenantId)).ToDictionary(r => r.Check.Name, r => r.UptimePercent);

        uptime["good"].Should().Be(100);
        uptime["bad"].Should().Be(0);
    }

    [Fact]
    public async Task Old_runs_age_out_and_recent_ones_stay()
    {
        SyntheticCheck check = await CreatedAsync();
        await service.RecordAsync(tenantId, check.Id, Ok(), now.AddDays(-15));
        await service.RecordAsync(tenantId, check.Id, Ok(), now.AddDays(-13));

        (await service.PruneAsync(now)).Should().Be(1);
    }

    private static SyntheticCheck State(DateTime? lastRun, int failures, int threshold = 2, int interval = 60) => new()
    {
        LastRunAt = lastRun, ConsecutiveFailures = failures, FailuresBeforeAlert = threshold, IntervalSeconds = interval,
    };

    [Fact]
    public void The_incident_follows_the_streak()
    {
        SyntheticCheckService.Decide(State(now, 0), now).Should().Be(SyntheticAlertState.Clear);
        SyntheticCheckService.Decide(State(now, 1), now).Should().Be(SyntheticAlertState.Hold, "one blip neither raises nor clears");
        SyntheticCheckService.Decide(State(now, 2), now).Should().Be(SyntheticAlertState.Fire);
        SyntheticCheckService.Decide(State(now, 3), now).Should().Be(SyntheticAlertState.Fire);
    }

    [Fact]
    public void A_threshold_of_one_fires_on_the_first_failure()
    {
        SyntheticCheckService.Decide(State(now, 1, threshold: 1), now).Should().Be(SyntheticAlertState.Fire);
    }

    [Fact]
    public void A_check_that_has_never_run_says_nothing()
    {
        SyntheticCheckService.Decide(State(null, 0), now).Should().Be(SyntheticAlertState.Hold);
    }

    /// <summary>⚠️ If the runner stops, a failing check going quiet must not read as recovered.</summary>
    [Fact]
    public void A_stale_answer_holds_rather_than_clearing_or_firing()
    {
        DateTime stale = now.AddSeconds(-(60 * 3 + 61));

        SyntheticCheckService.Decide(State(stale, 0), now).Should().Be(SyntheticAlertState.Hold);
        SyntheticCheckService.Decide(State(stale, 5), now).Should().Be(SyntheticAlertState.Hold);

        DateTime justFresh = now.AddSeconds(-(60 * 3 + 60));
        SyntheticCheckService.Decide(State(justFresh, 0), now).Should().Be(SyntheticAlertState.Clear);
    }
}

/// <summary>Boots the real application, so the runner, the migrations and the routes are the real ones.</summary>
public sealed class SyntheticHostFixture : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "synthetic-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; }

    public SyntheticHostFixture()
    {
        Directory.CreateDirectory(tempDir);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("DatabaseProvider", "Sqlite");
            b.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(tempDir, "app.db")}");
            b.UseSetting("Telemetry:DataPath", Path.Combine(tempDir, "telemetry"));
            b.UseSetting("Telemetry:ObjectStorage:Bucket", "");
            b.UseSetting("Vault:RootKey", "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
            b.UseSetting("DataProtection:KeyPath", Path.Combine(tempDir, "keys"));
            b.UseSetting("Seed:AdminEmail", "");
            b.UseSetting("Synthetics:AllowPrivateTargets", "true");

            // The runner under test is singleton work, so say so rather than lean on the default.
            b.UseSetting(EntKube.Web.Modules.HostedWork.RunSingletonWorkKey, "true");
        });
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class SyntheticRunnerLiveTests(SyntheticHostFixture host) : IClassFixture<SyntheticHostFixture>
{
    /// <summary>
    /// Saved through the service and left alone: the hosted runner, on its own schedule, has to make
    /// the real request over a real socket and record what it found.
    /// </summary>
    [Fact]
    public async Task The_runner_requests_a_due_check_and_records_the_answer()
    {
        using CannedServer server = new(status: 503);
        IServiceProvider services = host.Factory.Services;

        Guid tenantId = Guid.NewGuid(), environmentId = Guid.NewGuid(), clusterId = Guid.NewGuid();

        await using (ApplicationDbContext db = services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext())
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = "t-" + tenantId.ToString("N")[..8] });
            await db.SaveChangesAsync();
            db.Environments.Add(new EntKube.Web.Data.Environment { Id = environmentId, TenantId = tenantId, Name = "e" });
            await db.SaveChangesAsync();
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId, Name = "c", ApiServerUrl = "https://a",
            });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = services.CreateScope();
        SyntheticCheckService checks = scope.ServiceProvider.GetRequiredService<SyntheticCheckService>();

        (await checks.SaveAsync(tenantId, new SyntheticCheck
        {
            Name = "Live", Url = $"http://127.0.0.1:{server.Port}/", ClusterId = clusterId,
        })).Should().BeNull();

        SyntheticCheck? seen = null;

        // ⚠️ A Stopwatch, not DateTime.UtcNow. The runner's timer is monotonic, and on a laptop that
        // sleeps mid-run the wall clock jumps ahead while monotonic time stands still: a wall-clock
        // deadline then expired during the sleep, before the runner had ticked once. Three failures
        // looked like a slow machine and like settings leaking between hosts, and were neither; the
        // timestamps showed the check due at 15:34 and the test giving up at 15:49 after "13 s".
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();

        while (waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            seen = (await checks.ListAsync(tenantId)).Single().Check;
            if (seen.State != SyntheticCheckState.Unknown) break;
            await Task.Delay(250);
        }

        seen!.State.Should().Be(SyntheticCheckState.Failing, "the server answered 503");
        seen.LastStatusCode.Should().Be(503);
        seen.ConsecutiveFailures.Should().BeGreaterThanOrEqualTo(1);
        server.Hits.Should().BeGreaterThanOrEqualTo(1);
    }
}

/// <summary><c>GET /synthetic-checks</c>, through a real token.</summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class SyntheticCheckApiTests(GeneratedOpenApiDocumentFixture fixture)
{
    [Fact]
    public async Task It_lists_this_tenant_s_checks_and_never_another_s()
    {
        string marker = Guid.NewGuid().ToString("N");

        await using (ApplicationDbContext db = fixture.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext())
        {
            db.SyntheticChecks.AddRange(
                new SyntheticCheck { Id = Guid.NewGuid(), TenantId = fixture.TokenTenantId, ClusterId = Guid.NewGuid(), Name = marker, Url = "https://a.example/" },
                new SyntheticCheck { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), ClusterId = Guid.NewGuid(), Name = "stranger-" + marker, Url = "https://b.example/" });
            await db.SaveChangesAsync();
        }

        (HttpStatusCode status, _, string body) =
            await fixture.SendForResponseAsync("GET", "/api/v1/synthetic-checks", "ops:read");

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(marker).And.NotContain("stranger-" + marker);
        body.Should().Contain("\"uptimePercent\":null");
    }
}
