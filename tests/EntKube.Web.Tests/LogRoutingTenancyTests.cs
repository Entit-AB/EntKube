using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Which backend a cluster's logs come from is decided by asking Catalog whether EntKube's
/// collector is installed — and that question is now asked as a tenant.
///
/// <para><b>The hazard this pins.</b> The probe is memoized for a few seconds, because it used to
/// run before every facade call (~5 extra round-trips per log-panel load). The cache was keyed on
/// the cluster id, which was right while the probe took nothing else. Asked as a tenant it is a
/// different question with a different answer: a tenant who may not see the cluster gets an empty
/// component list and therefore "no collector". Cached under the cluster id alone, that answer
/// would be handed to the <em>owner</em> on their next page load, routing their logs to Loki —
/// silently, since a Loki-backed cluster with no Loki simply shows nothing.</para>
/// </summary>
public class LogRoutingTenancyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly LogQueryService sut;

    private Guid owner, stranger, clusterId;

    public LogRoutingTenancyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();

        // Loki is deliberately not constructed: this asserts the Catalog-backed half of the
        // routing decision, which is the only half the tenant changes, and the Loki half needs a
        // vault, a storage service and a cluster to reach. Nothing here routes to it.
        sut = new LogQueryService(
            new NoNativeData(),
            loki: null!,
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<LogQueryService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync()
    {
        owner = Guid.NewGuid();
        stranger = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = owner, Name = "Owner", Slug = $"owner-{owner:N}" });
        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = owner, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = owner, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(), ClusterId = clusterId,
            Name = "otel-collector", ComponentType = "Observability",
            Status = ComponentStatus.Installed, Namespace = "entkube-telemetry",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_owner_sees_the_collector_and_a_stranger_does_not()
    {
        await SeedAsync();

        (await sut.NativeCollectorInstalledAsync(owner, clusterId)).Should().BeTrue();
        (await sut.NativeCollectorInstalledAsync(stranger, clusterId)).Should().BeFalse(
            "the component list a stranger gets back from Catalog is empty");
    }

    /// <summary>
    /// The same pair of questions in the other order. This is the one that fails if the memo is
    /// keyed on the cluster alone: the stranger's "no" is cached first and the owner then reads
    /// it, which is indistinguishable from the collector having been uninstalled.
    /// </summary>
    [Fact]
    public async Task A_strangers_answer_is_not_cached_for_the_owner()
    {
        await SeedAsync();

        (await sut.NativeCollectorInstalledAsync(stranger, clusterId)).Should().BeFalse();
        (await sut.NativeCollectorInstalledAsync(owner, clusterId)).Should().BeTrue(
            "asked second, the owner must still see their own cluster's collector");
    }

    /// <summary>
    /// And the memo still memoizes — removing the component does not change the answer within the
    /// window, which is what the cache is for. Without this the test above would pass just as well
    /// against a cache that had been deleted.
    /// </summary>
    [Fact]
    public async Task The_answer_is_memoized_within_its_window()
    {
        await SeedAsync();

        (await sut.NativeCollectorInstalledAsync(owner, clusterId)).Should().BeTrue();

        db.ClusterComponents.RemoveRange(db.ClusterComponents);
        await db.SaveChangesAsync();

        (await sut.NativeCollectorInstalledAsync(owner, clusterId)).Should().BeTrue(
            "the probe is memoized for a few seconds, which is why it is not re-read here");
    }

    /// <summary>A native backend that is configured but holds nothing, so routing turns on the probe alone.</summary>
    private sealed class NoNativeData : ILogBackend
    {
        public bool IsEnabled => true;

        public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
            Guid clusterId, int windowMinutes = 60, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));

        public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
            Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));

        public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
            Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));

        public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
            Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200,
            CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Success([]));

        public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
            Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Success([]));

        public Task<KubernetesOperationResult<long>> CountAsync(
            Guid clusterId, string? ns, string? matchText, LogLevel minLevel,
            DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<long>.Success(0));

        public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
            Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48,
            CancellationToken ct = default)
            => Task.FromResult(KubernetesOperationResult<List<LogHistogramBucket>>.Success([]));
    }
}
