using EntKube.Web;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>Boots the real application with its real segment store, once, and writes real lines.</summary>
public sealed class LogPipelineLiveFixture : IAsyncLifetime
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "log-pipeline-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid ClusterId { get; } = Guid.NewGuid();
    public DateTime Now { get; } = DateTime.UtcNow;

    public async Task InitializeAsync()
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
        });

        IServiceProvider services = Factory.Services;

        await using (ApplicationDbContext db = services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext())
        {
            Guid environmentId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "Acme", Slug = "acme" });
            await db.SaveChangesAsync();
            db.Environments.Add(new Environment { Id = environmentId, TenantId = TenantId, Name = "prod" });
            await db.SaveChangesAsync();
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = ClusterId, TenantId = TenantId, EnvironmentId = environmentId,
                Name = "c1", ApiServerUrl = "https://k8s.example.com",
            });
            await db.SaveChangesAsync();
        }

        await services.GetRequiredService<ITelemetryIngest>().WriteLogsAsync(TenantId, ClusterId,
        [
            Line("shop", "api-1", LogLevel.Error, "Connection Timeout talking to db-01.internal"),
            Line("shop", "api-1", LogLevel.Error, "connection refused by payment_gateway"),
            Line("shop", "api-2", LogLevel.Warn, "slow timeouts on checkout"),
            Line("shop", "api-2", LogLevel.Info, "request ok"),
            Line("jobs", "worker-1", LogLevel.Fatal, "out of memory"),
            Line("jobs", "worker-1", LogLevel.Info, "ÅTERSTÄLLNING av kö klar"),
        ]);
    }

    private LogIngestRecord Line(string ns, string pod, LogLevel level, string body) =>
        new(Now.AddSeconds(-30), ns, pod, "app", (short)level, body, TraceId: null, AttributesJson: null);

    public async Task<LogPipelineOutcome> RunAsync(string query)
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LogPipelineService>()
            .RunAsync(TenantId, ClusterId, query, Now.AddHours(-1), Now.AddMinutes(1));
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// ⭐ The claim the engine rests on, measured against the real analyzer rather than a stub: a condition
/// pushed into the store returns a superset of what it means, so pushing it can never lose a line.
/// The engine's stubs cannot show this, because they do not tokenize.
/// </summary>
public sealed class LogPipelineLiveTests(LogPipelineLiveFixture host) : IClassFixture<LogPipelineLiveFixture>
{
    private async Task<int> RowsAsync(string query)
    {
        LogPipelineOutcome outcome = await host.RunAsync(query);
        outcome.Failure.Should().BeNull();
        outcome.SyntaxError.Should().BeNull();
        return outcome.Result!.Rows.Count;
    }

    [Theory]
    [InlineData("timeout", 1)]              // the case differs from the stored "Timeout"
    [InlineData("TIMEOUT", 1)]
    [InlineData("timeouts", 1)]
    [InlineData("connection", 2)]
    [InlineData("connection refused", 1)]   // more than one term
    [InlineData("db-01", 1)]                // hyphenated
    [InlineData("db-01.internal", 1)]       // and dotted
    [InlineData("payment_gateway", 1)]      // underscored
    [InlineData("återställning", 1)]        // non-ASCII, and the case differs
    public async Task A_term_is_found_by_has_through_the_real_store(string term, int rows)
    {
        (await RowsAsync($"logs | where body has \"{term}\"")).Should().Be(rows);
    }

    [Fact]
    public async Task Contains_finds_a_substring_the_analyzer_never_indexed_as_a_token()
    {
        (await RowsAsync("logs | where body contains \"timeou\"")).Should().Be(2);
    }

    [Fact]
    public async Task Every_namespace_is_read_when_none_is_named()
    {
        (await RowsAsync("logs")).Should().Be(6);
    }

    [Fact]
    public async Task A_named_namespace_narrows_the_read()
    {
        (await host.RunAsync("logs | where namespace == \"jobs\"")).Result!.LinesScanned.Should().Be(2);
    }

    [Fact]
    public async Task A_level_condition_narrows_the_read_and_matches_what_it_says()
    {
        LogPipelineOutcome outcome = await host.RunAsync("logs | where level >= error");

        outcome.Result!.Rows.Should().HaveCount(3);
        outcome.Result.LinesScanned.Should().Be(3, "the store applied it, rather than the engine after reading everything");
    }

    [Fact]
    public async Task A_pod_condition_matches_exactly_although_the_store_filters_by_prefix()
    {
        (await RowsAsync("logs | where pod == \"api-1\"")).Should().Be(2);
        (await RowsAsync("logs | where pod == \"api\"")).Should().Be(0);
        (await RowsAsync("logs | where pod startswith \"api\"")).Should().Be(4);
    }

    [Fact]
    public async Task A_summary_counts_real_lines()
    {
        LogPipelineOutcome outcome = await host.RunAsync("logs | summarize n = count(), pods = dcount(pod) by namespace | sort by n desc");

        outcome.Result!.Rows.Select(r => (r[0], r[1], r[2])).Should().Equal(("shop", 4d, 2d), ("jobs", 2d, 1d));
    }

    [Fact]
    public async Task Another_cluster_s_id_reads_nothing_of_this_one()
    {
        using IServiceScope scope = host.Factory.Services.CreateScope();
        LogPipelineOutcome outcome = await scope.ServiceProvider.GetRequiredService<LogPipelineService>()
            .RunAsync(host.TenantId, Guid.NewGuid(), "logs", host.Now.AddHours(-1), host.Now.AddMinutes(1));

        (outcome.Result?.Rows.Count ?? 0).Should().Be(0);
    }
}
