using System.Text;
using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Contracts.Delivery;
using EntKube.Web.Data;
using Environment = System.Environment;
using ComponentStatus = EntKube.Contracts.Catalog.ComponentStatus;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.ScrapeMonitors;
using EntKube.Web.Services.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>Runs only where <c>ENTKUBE_LIVE_KUBECONFIG</c> names a kubeconfig; skipped everywhere else.</summary>
public sealed class LiveClusterFactAttribute : FactAttribute
{
    public LiveClusterFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveScrapeMonitorHarness.KubeconfigVariable)))
        {
            Skip = $"Needs a real cluster: set {LiveScrapeMonitorHarness.KubeconfigVariable}.";
        }
    }
}

/// <summary>
/// The real monitor engine against a real cluster: the SDK reads, Prometheus's own target list, and
/// kubectl writes. Nothing is faked but EntKube's database: the components and data services are
/// the ones the kind cluster runs, written out by hand.
///
/// <para>Driven by environment variables, because each step of the live check is a separate run:
/// <c>ENTKUBE_LIVE_MODE</c> plan | apply | reconcile, <c>ENTKUBE_LIVE_WITHOUT</c> a comma list of
/// data service or component names to leave out, <c>ENTKUBE_LIVE_OUT</c> where to write the result.</para>
/// </summary>
public sealed class LiveScrapeMonitorHarness
{
    public const string KubeconfigVariable = "ENTKUBE_LIVE_KUBECONFIG";

    private static readonly Guid TenantId = Guid.Parse("5b1e5f0c-94c9-4f0e-8f5c-0a8f9b5a6d22");
    private static readonly Guid ClusterId = Guid.Parse("7a1e5f0c-94c9-4f0e-8f5c-0a8f9b5a6d11");

    private readonly StringBuilder report = new();

    [LiveClusterFact]
    public async Task Run_the_engine_against_the_cluster()
    {
        string kubeconfig = await File.ReadAllTextAsync(Environment.GetEnvironmentVariable(KubeconfigVariable)!);
        string mode = Environment.GetEnvironmentVariable("ENTKUBE_LIVE_MODE") ?? "plan";
        HashSet<string> without = [.. (Environment.GetEnvironmentVariable("ENTKUBE_LIVE_WITHOUT") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        List<PlannedClusterChange> writes = [];
        Mock<IClusterChangeGate> gate = new();
        gate.Setup(g => g.AcknowledgeAsync(It.IsAny<PlannedClusterChange>(), It.IsAny<CancellationToken>()))
            .Callback((PlannedClusterChange change, CancellationToken _) => writes.Add(change))
            .Returns(Task.CompletedTask);

        KubernetesProxyClientPool pool = new(NullLogger<KubernetesProxyClientPool>.Instance);
        ClusterClient cluster = new(
            new KubernetesClientFactory(gate.Object, pool), ClusterId, "kind-mon-spike", kubeconfig, pool);

        InstalledComponent prometheus = Component("kube-prometheus-stack", "monitoring", "kube-prometheus-stack");
        List<InstalledComponent> components =
        [
            prometheus,
            Component("cloudnative-pg", "cnpg-system", "cnpg"),
            Component("redis-operator", "redis-operator", "redis-operator"),
            Component("reloader", "reloader", "reloader"),
            Component("kyverno", "kyverno", "kyverno"),
            Component("keda", "keda", "keda"),
            Component("seaweedfs", "seaweedfs", "seaweedfs"),
            Component("", "rabbitmq-system", null, name: "rabbitmq-cluster-operator"),
        ];
        List<ManagedServiceSummary> services =
        [
            Service(ManagedServiceKind.Postgres, "databases", "orders"),
            Service(ManagedServiceKind.Postgres, "databases", "billing"),
            Service(ManagedServiceKind.Redis, "ot-operators", "cache"),
            Service(ManagedServiceKind.RabbitMq, "messaging", "bus"),
        ];
        components.RemoveAll(c => without.Contains(c.Name));
        services.RemoveAll(s => without.Contains(s.Name));

        Mock<ICatalogApi> catalog = new();
        catalog.Setup(c => c.FindPrometheusAsync(TenantId, ClusterId, It.IsAny<CancellationToken>())).ReturnsAsync(prometheus);
        catalog.Setup(c => c.GetComponentsForTenantAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(components);
        catalog.Setup(c => c.GetComponentsForClusterAsync(TenantId, ClusterId, It.IsAny<CancellationToken>())).ReturnsAsync(components);

        Mock<IDataServicesApi> dataServices = new();
        dataServices.Setup(d => d.GetManagedServicesForTenantAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(services);

        Mock<IClusterClientFactory> clusters = new();
        clusters.Setup(c => c.ForAsync(TenantId, ClusterId, It.IsAny<CancellationToken>())).ReturnsAsync(cluster);

        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();
        TestDbContextFactory db = new(connection);
        using (ApplicationDbContext context = db.CreateDbContext())
        {
            context.Database.EnsureCreated();
            context.Tenants.Add(new Tenant { Id = TenantId, Name = "live", Slug = "live" });
            context.SaveChanges();
        }

        LiveScrapeStateReader reader = new(new PrometheusService(
            Mock.Of<IDeliveryApi>(), clusters.Object, catalog.Object, new PromQueryCache(),
            NullLogger<PrometheusService>.Instance));

        ScrapeMonitorService service = new(
            db, catalog.Object, dataServices.Object, clusters.Object, reader, gate.Object,
            NullLogger<ScrapeMonitorService>.Instance);

        report.AppendLine($"mode={mode} without=[{string.Join(",", without)}] at {DateTime.UtcNow:O}");

        switch (mode)
        {
            case "seaweed-values":
                // What the apply-time code makes of the release's values: run it, the chart renders it.
                string values = await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("ENTKUBE_LIVE_VALUES_IN")!);
                await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("ENTKUBE_LIVE_VALUES_OUT")!,
                    YamlFormMerger.LetPrometheusReachSeaweedFs(values, prometheus.Namespace));
                break;
            case "apply":
                Print("apply result", await service.ApplyAsync(TenantId, ClusterId));
                break;
            case "reconcile":
                await service.ReconcileAllAsync(new ScrapeMonitorBackoff(TimeProvider.System));
                Print("plan after reconcile", await service.PlanAsync(TenantId, ClusterId));
                break;
            default:
                Print("plan", await service.PlanAsync(TenantId, ClusterId));
                break;
        }

        report.AppendLine($"== writes: {writes.Count}");
        foreach (PlannedClusterChange write in writes)
        {
            report.AppendLine($"-- {write.Verb}: {write.Summary}");
            report.AppendLine(write.Manifest);
        }

        string text = report.ToString();
        string? outPath = Environment.GetEnvironmentVariable("ENTKUBE_LIVE_OUT");
        if (!string.IsNullOrWhiteSpace(outPath)) await File.WriteAllTextAsync(outPath, text);
        Console.WriteLine(text);
    }

    private void Print(string title, KubernetesOperationResult<ScrapeMonitorPlan> result)
    {
        report.AppendLine($"== {title}: success={result.IsSuccess} error={result.Error}");
        if (result.Data is not { } plan) return;

        report.AppendLine($"scope={plan.Scope} hasChanges={plan.HasChanges} apply={plan.Apply.Count} prune={plan.Prune.Count}");
        foreach (MonitorPlanRow row in plan.Rows)
        {
            report.AppendLine($"[{row.State}/{row.Change}] {row.Key} monitor={row.Monitor} up={row.TargetsUp}/{row.TargetsTotal}");
            report.AppendLine($"    {row.Detail}");
            foreach (PlannedEndpoint e in row.Endpoints) report.AppendLine($"    - {e.Pod} {e.Address}{e.Path}");
        }

        foreach (PlannedMonitor m in plan.Apply) report.AppendLine($"APPLY {m.Change} {m.Identity}");
        foreach (MonitorIdentity m in plan.Prune) report.AppendLine($"PRUNE {m}");
    }

    private static InstalledComponent Component(string chart, string ns, string? release, string? name = null)
        => new(
            Guid.NewGuid(), TenantId, ClusterId, "kind-mon-spike", Guid.NewGuid(), name ?? chart,
            string.IsNullOrEmpty(chart) ? "Manifest" : "HelmChart", ComponentStatus.Installed, ns, release, chart,
            null, null, null, null, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

    private static ManagedServiceSummary Service(ManagedServiceKind kind, string ns, string name)
        => new(Guid.NewGuid(), TenantId, ClusterId, kind, name, ns, ManagedServiceStatus.Running, true, true, false);
}
