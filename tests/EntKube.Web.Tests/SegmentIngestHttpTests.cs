using EntKube.Web;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>
/// Live boot of the real application (Sqlite control-plane DB + local-disk object storage), verifying
/// that the actual composition root wires the segment engine
/// end to end: startup migrations create the segment catalog, the ingest store resolves to
/// <see cref="SegmentTelemetryStore"/>, a write flows through <see cref="LogSegmentManager"/>, and the log
/// backend resolves to <see cref="SegmentLogService"/> and reads it back — plus the OTLP/RUM HTTP ingest
/// endpoints are registered on the real router.
///
/// (The HTTP transport layer itself — OtlpIngest/parsers, unchanged pre-existing code — is not driven here:
/// its endpoints call DisableAntiforgery(), which is honored under real Kestrel but not under
/// WebApplicationFactory's TestServer, an antiforgery quirk unrelated to the engine. This exercises
/// everything from the ingest contract inward.)
/// </summary>
public sealed class SegmentIngestHttpTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "seg-http-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> BuildFactory()
    {
        Directory.CreateDirectory(_tempDir);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            // UseSetting (host configuration) is visible to Program's inline builder.Configuration reads
            // during service registration; ConfigureAppConfiguration is applied too late for those.
            b.UseSetting("DatabaseProvider", "Sqlite");
            b.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(_tempDir, "app.db")}");
            b.UseSetting("Telemetry:DataPath", Path.Combine(_tempDir, "telemetry"));
            b.UseSetting("Telemetry:ObjectStorage:Bucket", "");
            b.UseSetting("Vault:RootKey", "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
            b.UseSetting("DataProtection:KeyPath", Path.Combine(_tempDir, "keys"));
            b.UseSetting("Seed:AdminEmail", "");
        });
    }

    [Fact]
    public async Task LiveApp_WithSegmentsEngine_WiresIngestAndQueryEndToEnd()
    {
        using WebApplicationFactory<Program> factory = BuildFactory();
        IServiceProvider services = factory.Services; // builds the host + runs startup migrations

        Guid tenantId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();

        // Startup migrations created the segment catalog, and the cluster resolves tenant for queries.
        var dbFactory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            (await db.TelemetrySegments.CountAsync()).Should().Be(0); // table exists (migration applied)
            Guid envId = Guid.NewGuid();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme-" + tenantId.ToString("N")[..8] });
            db.Environments.Add(new Environment { Id = envId, TenantId = tenantId, Name = "prod" });
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
                Name = "c1", ApiServerUrl = "https://k8s.example.com",
            });
            await db.SaveChangesAsync();
        }

        // The OTLP/RUM ingest endpoints are registered on the real router.
        var routes = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        routes.Should().Contain("/ingest/otlp/v1/logs");
        routes.Should().Contain("/ingest/otlp/v1/traces");

        // The real composition root selected the segment engine for both ingest and query.
        var ingest = services.GetRequiredService<ITelemetryIngest>();
        ingest.Should().BeOfType<SegmentTelemetryStore>();

        // Ingest a log through the live ingest store (identity stamped from the verified token in prod).
        DateTime now = DateTime.UtcNow;
        int written = await ingest.WriteLogsAsync(tenantId, clusterId,
        [
            new LogIngestRecord(now, "prod", "api-1", "app", (short)LogLevel.Error,
                "ERROR failed to reach database timeout", TraceId: null, AttributesJson: null),
        ]);
        written.Should().Be(1);

        // Read it back through the live log backend.
        using IServiceScope scope = services.CreateScope();
        var logs = scope.ServiceProvider.GetRequiredService<ILogBackend>();
        logs.Should().BeOfType<SegmentLogService>();

        var result = await logs.QueryAsync(
            clusterId,
            new LogQueryFilter { Namespaces = ["prod"], Text = "database", MinLevel = LogLevel.Error },
            now.AddMinutes(-5), now.AddMinutes(5));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Data!.SelectMany(s => s.Entries).Should().ContainSingle()
            .Which.Line.Should().Contain("failed to reach database");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* temp */ }
    }
}
