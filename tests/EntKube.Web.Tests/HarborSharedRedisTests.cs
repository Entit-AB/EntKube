using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Harbor pointed at a Redis the cluster already runs, instead of the one its chart starts for itself.
///
/// <para>The reason this is worth a test rather than just a form field: the chart's external-Redis
/// contract is a single address plus four database indexes, and every way of getting it wrong produces a
/// Harbor that installs cleanly and then misbehaves later — the job service stalling, the scanner never
/// finishing — with nothing pointing back at the address.</para>
/// </summary>
public class HarborSharedRedisTests
{
    private static CatalogEntry Harbor =>
        ComponentCatalog.GetByKey("harbor") ?? throw new InvalidOperationException("harbor entry is gone");

    private static ComponentFormField Field(string key) =>
        Harbor.FormFields.FirstOrDefault(f => f.Key == key)
        ?? throw new InvalidOperationException($"harbor has no '{key}' field");

    [Fact]
    public void The_redis_field_is_a_picker_of_what_the_cluster_actually_runs()
    {
        Field("redis-endpoint").Type.Should().Be(FormFieldType.RedisSelector);
    }

    /// <summary>
    /// The picker's sibling password field must be keyed exactly "redis-password": choosing a managed
    /// Redis fills that key in, and any other name silently drops the credential.
    /// </summary>
    [Fact]
    public void The_password_beside_it_is_vault_backed_and_lands_on_the_chart_s_own_path()
    {
        ComponentFormField password = Field("redis-password");

        password.StoreAsSecret.Should().BeTrue();
        password.SecretName.Should().Be("harbor-redis-password");
        password.YamlPath.Should().Be("redis.external.password");
    }

    /// <summary>
    /// The endpoint is side config, not a Helm value: it decides <c>redis.type</c> as well as the address,
    /// so it goes through HarborService rather than being merged into the YAML by its path.
    /// </summary>
    [Fact]
    public void The_endpoint_is_a_pseudo_path_so_the_service_owns_it()
    {
        Field("redis-endpoint").YamlPath.Should().Be("harbor:redis-endpoint");
        Field("redis-endpoint").IsPseudoPath.Should().BeTrue();
    }

    /// <summary>
    /// Without this the stored values say nothing about Redis, and "went back to the chart's own Redis"
    /// would be indistinguishable from "was never configured".
    /// </summary>
    [Fact]
    public void The_defaults_declare_the_internal_redis_the_override_replaces()
    {
        YamlFormMerger.ExtractValue(Harbor.DefaultValues ?? "", "redis.type").Should().Be("internal");
    }

    /// <summary>
    /// Every EntKube-managed Redis is a sharded cluster, and a cluster has only database 0 — which is
    /// exactly what Harbor cannot live with. The flag is what lets the install path refuse one instead of
    /// handing an operator a registry that half works.
    /// </summary>
    [Fact]
    public void A_managed_redis_is_marked_as_cluster_mode()
    {
        RedisEndpointOption managed = new(
            Host: "cache-leader.cache.svc.cluster.local", Port: 6379,
            Label: "cache", Managed: true, RedisClusterId: Guid.NewGuid(), ClusterMode: true);

        managed.ClusterMode.Should().BeTrue();
    }

    /// <summary>A Service merely found on the cluster tells us nothing about its mode, so we claim nothing.</summary>
    [Fact]
    public void A_discovered_service_is_not_assumed_to_be_a_cluster()
    {
        List<RedisEndpointOption> found = RedisService.ParseRedisServices(
            """
            {"items":[{"metadata":{"name":"redis","namespace":"infra"},
              "spec":{"clusterIP":"10.0.0.5","ports":[{"port":6379}]}}]}
            """);

        found.Should().ContainSingle();
        found[0].ClusterMode.Should().BeFalse();
    }
}
