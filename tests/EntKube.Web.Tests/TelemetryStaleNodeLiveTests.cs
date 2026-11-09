using System.Collections.Concurrent;
using EntKube.Telemetry;
using EntKube.Web;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>
/// Boots the real application with its real segment store and two clusters that both still have an old
/// in-cluster telemetry node, faked at the Kubernetes seam. The node answers "I hold data" for both, as a
/// node does for a week after its collector stops sending to it.
/// <list type="bullet">
/// <item><b>Moved</b> has been cut back: the management plane received its spans and lines a moment ago.</item>
/// <item><b>NotMoved</b> has not: the management plane holds only data from three hours ago.</item>
/// </list>
/// </summary>
public sealed class TelemetryStaleNodeFixture : IAsyncLifetime
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "stale-node-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid Moved { get; } = Guid.NewGuid();
    public Guid NotMoved { get; } = Guid.NewGuid();
    public DateTime Now { get; } = DateTime.UtcNow;

    /// <summary>Every path the fake node was asked for, in order.</summary>
    public ConcurrentQueue<string> NodeCalls { get; } = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(tempDir);

        Mock<IKubernetesClientFactory> k8s = new();
        k8s.Setup(x => x.GetJsonAsync("services", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
                {"items":[{"metadata":{"name":"tel-querier","labels":{"app.kubernetes.io/component":"querier"}},
                           "spec":{"ports":[{"name":"http","port":8081}]}}]}
                """);
        k8s.Setup(x => x.ProxyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, int _, HttpMethod _, string path, string _, object? _,
                IReadOnlyDictionary<string, string>? _, CancellationToken _) =>
            {
                NodeCalls.Enqueue(path);
                string body = path.Contains("has-data", StringComparison.Ordinal) ? "true"
                    : path.StartsWith("api/traces/services", StringComparison.Ordinal) ? """["from-the-node"]"""
                    : path.StartsWith("api/logs/namespaces", StringComparison.Ordinal) ? """["node-ns"]"""
                    : "[]";
                return Task.FromResult(new ServiceProxyResponse(200, "OK", body));
            });

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
            b.UseSetting("Workers:RunSingletonWork", "false");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IKubernetesClientFactory>();
                s.AddSingleton(k8s.Object);
            });
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
            foreach ((Guid id, string name) in new[] { (Moved, "moved"), (NotMoved, "not-moved") })
            {
                db.KubernetesClusters.Add(new KubernetesCluster
                {
                    Id = id, TenantId = TenantId, EnvironmentId = environmentId,
                    Name = name, ApiServerUrl = "https://k8s.example.com",
                });
                await db.SaveChangesAsync();
                db.ClusterComponents.Add(new ClusterComponent
                {
                    Id = Guid.NewGuid(), ClusterId = id, Name = "entkube-telemetry-query",
                    ComponentType = "HelmChart", HelmChartName = "entkube-telemetry",
                    Status = ComponentStatus.Installed, Namespace = "monitoring",
                });
                await db.SaveChangesAsync();
            }
        }

        using (IServiceScope scope = services.CreateScope())
        {
            VaultService vault = scope.ServiceProvider.GetRequiredService<VaultService>();
            await vault.InitializeVaultAsync(TenantId);
            foreach (Guid id in new[] { Moved, NotMoved })
            {
                (bool ok, string? error, _) = await vault.SetClusterKubeconfigAsync(
                    TenantId, id, new KubeconfigBundle { ConfigYaml = TestKubeconfig.Valid }, "test");
                ok.Should().BeTrue(error);
            }
        }

        ITelemetryIngest ingest = services.GetRequiredService<ITelemetryIngest>();
        await ingest.WriteSpansAsync(TenantId, Moved, [Span(Now.AddSeconds(-30), "checkout")]);
        await ingest.WriteLogsAsync(TenantId, Moved, [Line(Now.AddSeconds(-30), "shop")]);
        await ingest.WriteSpansAsync(TenantId, NotMoved, [Span(Now.AddHours(-3), "old-central")]);
        await ingest.WriteLogsAsync(TenantId, NotMoved, [Line(Now.AddHours(-3), "old-ns")]);
    }

    private static SpanIngestRecord Span(DateTime at, string service) =>
        new(at, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")[..16], null, "GET /", service,
            Kind: 2, DurationMs: 12, StatusCode: 0, Namespace: "shop", Pod: "api-1", AttributesJson: null);

    private static LogIngestRecord Line(DateTime at, string ns) =>
        new(at, ns, "api-1", "app", (short)LogLevel.Info, "request ok", TraceId: null, AttributesJson: null);

    public async Task DisposeAsync()
    {
        TelemetryNodeClient.Invalidate(Moved);
        TelemetryNodeClient.Invalidate(NotMoved);
        await Factory.DisposeAsync();
        try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The Logs and Traces pages after telemetry moved back to the management plane: reads went to a node that
/// still held a week of data and received nothing new, through the cluster's API server, so the trace
/// service list for the last hour was empty and a slow node held the page.
/// </summary>
public class TelemetryStaleNodeLiveTests(TelemetryStaleNodeFixture f) : IClassFixture<TelemetryStaleNodeFixture>
{
    [Fact]
    public async Task A_cluster_the_management_plane_receives_now_reads_from_it_and_never_asks_the_node()
    {
        using IServiceScope scope = f.Factory.Services.CreateScope();
        ITelemetryBackends backends = scope.ServiceProvider.GetRequiredService<ITelemetryBackends>();
        int before = f.NodeCalls.Count;

        KubernetesOperationResult<List<string>> services =
            await backends.TracesFor(f.TenantId).GetServicesAsync(f.Moved);
        KubernetesOperationResult<List<string>> namespaces =
            await backends.LogsFor(f.TenantId).GetNamespacesAsync(f.Moved);

        services.IsSuccess.Should().BeTrue(services.Error);
        services.Data.Should().Equal(["checkout"], "the stale node's list is what the page showed instead");
        namespaces.IsSuccess.Should().BeTrue(namespaces.Error);
        namespaces.Data.Should().Equal(["shop"]);
        f.NodeCalls.Count.Should().Be(before, "routing a moved cluster must cost no call through its API server");
    }

    /// <summary>
    /// The other half, so the first test cannot pass by never reaching the node at all: this cluster's
    /// management-plane data is three hours old, so the node holding data still wins, as it did before.
    /// </summary>
    [Fact]
    public async Task A_cluster_the_management_plane_has_not_heard_from_this_hour_still_reads_its_node()
    {
        using IServiceScope scope = f.Factory.Services.CreateScope();
        ITelemetryBackends backends = scope.ServiceProvider.GetRequiredService<ITelemetryBackends>();

        KubernetesOperationResult<List<string>> services =
            await backends.TracesFor(f.TenantId).GetServicesAsync(f.NotMoved);
        KubernetesOperationResult<List<string>> namespaces =
            await backends.LogsFor(f.TenantId).GetNamespacesAsync(f.NotMoved);

        services.Data.Should().Equal(["from-the-node"]);
        namespaces.Data.Should().Equal(["node-ns"]);
    }

    /// <summary>
    /// "Since" has to be in the query, not only in which segments are opened: the open segment holds the
    /// three-hour-old data too, so a segment-level bound alone answers yes for a cluster that has been
    /// quiet all afternoon, and routing would send it to an empty hour.
    /// </summary>
    [Fact]
    public async Task Received_since_counts_only_data_after_the_bound_in_both_stores()
    {
        using IServiceScope scope = f.Factory.Services.CreateScope();
        SegmentLogService logs = scope.ServiceProvider.GetRequiredService<SegmentLogService>();
        SegmentTraceService traces = scope.ServiceProvider.GetRequiredService<SegmentTraceService>();
        DateTime anHourAgo = f.Now.AddHours(-1);

        (await logs.HasDataAsync(f.NotMoved)).Should().BeTrue("three hours old is inside the week");
        (await traces.HasDataAsync(f.NotMoved)).Should().BeTrue("three hours old is inside the week");
        (await logs.HasDataSinceAsync(f.NotMoved, anHourAgo)).Should().BeFalse();
        (await traces.HasDataSinceAsync(f.NotMoved, anHourAgo)).Should().BeFalse();
        (await logs.HasDataSinceAsync(f.Moved, anHourAgo)).Should().BeTrue();
        (await traces.HasDataSinceAsync(f.Moved, anHourAgo)).Should().BeTrue();
    }
}
