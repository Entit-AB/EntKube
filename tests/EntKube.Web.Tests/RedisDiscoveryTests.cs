using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Finding the Redis services a cluster already runs, so the rspamd component (and anything else that
/// needs one) offers a list instead of asking an operator to type a DNS name from memory. A mistyped
/// endpoint does not fail the install — the component starts, connects to nothing, and the symptom
/// arrives days later as a spam filter that never learned anything.
/// </summary>
public class RedisDiscoveryTests
{
    private static string Services(params string[] items) =>
        "{\"items\":[" + string.Join(",", items) + "]}";

    private static string Service(string name, string ns, string ports, string clusterIp = "10.0.0.1") =>
        $"{{\"metadata\":{{\"name\":\"{name}\",\"namespace\":\"{ns}\"}},"
        + $"\"spec\":{{\"clusterIP\":\"{clusterIp}\",\"ports\":[{ports}]}}}}";

    /// <summary>One entry of a Service's spec.ports.</summary>
    private static string Port(int number, string? name = null) =>
        name is null ? $"{{\"port\":{number}}}" : $"{{\"name\":\"{name}\",\"port\":{number}}}";

    [Fact]
    public void A_service_on_the_redis_port_is_offered_by_its_in_cluster_dns_name()
    {
        List<RedisEndpointOption> found = RedisService.ParseRedisServices(
            Services(Service("cache-leader", "cache", Port(6379, "redis-client"))));

        found.Should().ContainSingle();
        found[0].Host.Should().Be("cache-leader.cache.svc.cluster.local");
        found[0].Port.Should().Be(6379);
        found[0].Endpoint.Should().Be("cache-leader.cache.svc.cluster.local:6379");
        // Discovered, not created here: EntKube does not know this one's password.
        found[0].Managed.Should().BeFalse();
    }

    [Fact]
    public void A_port_named_for_redis_counts_even_on_a_nonstandard_number()
    {
        List<RedisEndpointOption> found = RedisService.ParseRedisServices(
            Services(Service("shared", "infra", Port(6380, "redis"))));

        found.Should().ContainSingle();
        found[0].Port.Should().Be(6380);
    }

    [Fact]
    public void Headless_services_are_skipped()
    {
        // A headless Service resolves to pod IPs. Handing one to a client as a stable endpoint means it
        // connects to whichever pod DNS returned first and reconnects somewhere else after a restart.
        RedisService.ParseRedisServices(
            Services(Service("cache-headless", "cache", Port(6379), clusterIp: "None")))
            .Should().BeEmpty();
    }

    [Fact]
    public void Services_that_are_not_redis_are_ignored()
    {
        RedisService.ParseRedisServices(
            Services(Service("web", "apps", Port(80, "http"))))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_service_exposing_several_ports_is_offered_once()
    {
        List<RedisEndpointOption> found = RedisService.ParseRedisServices(
            Services(Service("cache", "cache", $"{Port(6379, "redis-client")},{Port(9121, "redis-exporter")}")));

        found.Should().ContainSingle("one Service is one choice, however many ports it publishes");
    }

    [Fact]
    public void Nothing_is_returned_for_an_unreadable_cluster_rather_than_throwing()
    {
        // The picker falls back to a free-text box; an exception here would break the whole form.
        RedisService.ParseRedisServices("").Should().BeEmpty();
        RedisService.ParseRedisServices(Services()).Should().BeEmpty();
    }
}
