using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The PromQL cache in front of the metrics path.
///
/// Single-flight is the property worth defending. A dashboard renders its panels concurrently and several
/// routinely ask the same question, so on a cold cache a plain TTL cache helps not at all — every panel
/// starts its own request before any of them finishes. Collapsing them onto one in-flight request is what
/// turns eight WAN round-trips into one.
/// </summary>
public class PromQueryCacheTests
{
    private static readonly Guid Cluster = Guid.NewGuid();

    [Fact]
    public async Task A_repeated_query_is_served_without_asking_Prometheus_again()
    {
        PromQueryCache cache = new(TimeSpan.FromMinutes(5));
        int calls = 0;

        for (int i = 0; i < 3; i++)
        {
            KubernetesOperationResult<List<string>> result = await cache.GetOrFetchAsync(
                PromQueryCache.Key(Cluster, "labels", "__name__"),
                () => { calls++; return Task.FromResult(KubernetesOperationResult<List<string>>.Success(["up"])); });
            result.Data.Should().Equal("up");
        }

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_identical_queries_share_one_request()
    {
        PromQueryCache cache = new(TimeSpan.FromMinutes(5));
        int calls = 0;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Every caller arrives before any of them completes — the cold-cache case a TTL alone cannot help.
        Task<KubernetesOperationResult<List<string>>>[] queries = [.. Enumerable.Range(0, 8).Select(_ =>
            cache.GetOrFetchAsync(PromQueryCache.Key(Cluster, "range", "sum(rate(x[5m]))"), async () =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return KubernetesOperationResult<List<string>>.Success(["value"]);
            }))];

        gate.SetResult();
        KubernetesOperationResult<List<string>>[] results = await Task.WhenAll(queries);

        calls.Should().Be(1, "eight panels asking the same question should cost one round-trip, not eight");
        results.Should().OnlyContain(r => r.IsSuccess);
        results.Should().OnlyContain(r => r.Data!.Contains("value"));
    }

    [Fact]
    public async Task Different_queries_do_not_share_a_result()
    {
        PromQueryCache cache = new(TimeSpan.FromMinutes(5));

        KubernetesOperationResult<List<string>> a = await cache.GetOrFetchAsync(
            PromQueryCache.Key(Cluster, "range", "query-a"),
            () => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["a"])));
        KubernetesOperationResult<List<string>> b = await cache.GetOrFetchAsync(
            PromQueryCache.Key(Cluster, "range", "query-b"),
            () => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["b"])));

        a.Data.Should().Equal("a");
        b.Data.Should().Equal("b");
    }

    [Fact]
    public async Task One_clusters_results_are_never_served_to_another()
    {
        PromQueryCache cache = new(TimeSpan.FromMinutes(5));
        Guid other = Guid.NewGuid();
        const string query = "sum(rate(http_requests_total[5m]))";

        await cache.GetOrFetchAsync(PromQueryCache.Key(Cluster, "range", query),
            () => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["from-cluster-a"])));
        KubernetesOperationResult<List<string>> second = await cache.GetOrFetchAsync(
            PromQueryCache.Key(other, "range", query),
            () => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["from-cluster-b"])));

        // The same PromQL means different things on different clusters; sharing would show one tenant
        // another's metrics.
        second.Data.Should().Equal("from-cluster-b");
    }

    [Fact]
    public async Task A_failure_is_not_cached()
    {
        PromQueryCache cache = new(TimeSpan.FromMinutes(5));
        int calls = 0;

        KubernetesOperationResult<List<string>> failed = await cache.GetOrFetchAsync(
            PromQueryCache.Key(Cluster, "range", "q"),
            () => { calls++; return Task.FromResult(KubernetesOperationResult<List<string>>.Failure("boom")); });
        failed.IsSuccess.Should().BeFalse();

        // Holding a failure would make a transient blip sticky, and a just-fixed misconfiguration still
        // look broken. The retry must reach Prometheus.
        KubernetesOperationResult<List<string>> recovered = await cache.GetOrFetchAsync(
            PromQueryCache.Key(Cluster, "range", "q"),
            () => { calls++; return Task.FromResult(KubernetesOperationResult<List<string>>.Success(["ok"])); });

        recovered.Data.Should().Equal("ok");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task An_expired_result_is_refetched()
    {
        PromQueryCache cache = new(TimeSpan.FromMilliseconds(30));
        int calls = 0;

        Task<KubernetesOperationResult<List<string>>> Fetch() => cache.GetOrFetchAsync(
            PromQueryCache.Key(Cluster, "range", "q"),
            () => { calls++; return Task.FromResult(KubernetesOperationResult<List<string>>.Success(["v"])); });

        await Fetch();
        await Task.Delay(80);
        await Fetch();

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Invalidating_a_cluster_leaves_other_clusters_alone()
    {
        PromQueryCache cache = new(TimeSpan.FromMinutes(5));
        Guid other = Guid.NewGuid();
        int otherCalls = 0;

        await cache.GetOrFetchAsync(PromQueryCache.Key(Cluster, "range", "q"),
            () => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["a"])));
        await cache.GetOrFetchAsync(PromQueryCache.Key(other, "range", "q"),
            () => { otherCalls++; return Task.FromResult(KubernetesOperationResult<List<string>>.Success(["b"])); });

        cache.InvalidateCluster(Cluster);

        int reCalls = 0;
        await cache.GetOrFetchAsync(PromQueryCache.Key(Cluster, "range", "q"),
            () => { reCalls++; return Task.FromResult(KubernetesOperationResult<List<string>>.Success(["a2"])); });
        await cache.GetOrFetchAsync(PromQueryCache.Key(other, "range", "q"),
            () => { otherCalls++; return Task.FromResult(KubernetesOperationResult<List<string>>.Success(["b2"])); });

        reCalls.Should().Be(1, "the invalidated cluster must be re-queried");
        otherCalls.Should().Be(1, "an unrelated cluster's cached result must survive");
    }
}
