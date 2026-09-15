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

    /// <summary>
    /// The one that actually happened: a browser password manager saw a text box above a password box
    /// and filled in the saved login. "nils.blomgren@gmail.com" resolves, so the registry's core spent
    /// every restart dialling a mail provider on port 6379 — the address appearing nowhere but a pod log.
    /// </summary>
    [Fact]
    public void An_email_address_is_not_a_redis_endpoint()
    {
        string? reason = RedisService.DescribeInvalidEndpoint("nils.blomgren@gmail.com");

        reason.Should().NotBeNull();
        reason.Should().Contain("browser");
    }

    [Theory]
    // Empty is not an address, it is "no external Redis" — the caller reads it as that.
    [InlineData("")]
    [InlineData("redis.redis.svc.cluster.local:6379")]
    [InlineData("redis.redis.svc.cluster.local")]
    [InlineData("10.0.0.5:6379")]
    // A sentinel set is a list of addresses, and all of them have to be addresses.
    [InlineData("s1.redis:26379,s2.redis:26379,s3.redis:26379")]
    [InlineData("[2001:db8::1]:6379")]
    public void A_real_address_is_accepted(string endpoint)
    {
        RedisService.DescribeInvalidEndpoint(endpoint).Should().BeNull();
    }

    [Theory]
    // A login, not a host.
    [InlineData("user@redis.example.com:6379")]
    // A URL: the scheme is Harbor's to add, and a string with one in it is not host:port.
    [InlineData("redis://redis.redis.svc.cluster.local:6379")]
    // A path, a space, a port out of range, a half-written list.
    [InlineData("redis.redis.svc.cluster.local/0")]
    [InlineData("redis host:6379")]
    [InlineData("redis.redis.svc.cluster.local:99999")]
    [InlineData("redis.redis.svc.cluster.local:")]
    [InlineData("s1.redis:26379,")]
    public void Anything_that_cannot_be_an_address_is_refused(string endpoint)
    {
        RedisService.DescribeInvalidEndpoint(endpoint).Should().NotBeNull();
    }

    /// <summary>
    /// An address stored before the shape check existed still has to be caught, because the install it
    /// would otherwise run ends in a Helm deadline with a pod log nobody is watching.
    /// </summary>
    [Fact]
    public void A_stored_address_that_cannot_be_one_is_refused_before_the_install()
    {
        string message = HarborService.RedisEndpointUnusable(
            "'nils.blomgren@gmail.com' is not a host name.", fromValues: false);

        message.Should().Contain("nils.blomgren@gmail.com");
        message.Should().Contain("Clear the Redis field");
    }

    /// <summary>
    /// The refusal an operator reads instead of waiting out a Helm deadline. It has to name the address —
    /// the whole difficulty of this failure is that the address appears nowhere in what Helm reports.
    /// </summary>
    [Fact]
    public void A_redis_that_is_not_on_the_cluster_is_refused_by_name()
    {
        string message = HarborService.RedisEndpointMissing(
            "cache-leader.cache.svc.cluster.local", fromValues: false);

        message.Should().Contain("cache-leader.cache.svc.cluster.local");
        message.Should().Contain("Clear the Redis field");
    }

    /// <summary>
    /// A component with no config record has no field to clear — its address lives in the values, and
    /// telling the operator to clear a field they cannot see would send them looking for one.
    /// </summary>
    [Fact]
    public void An_address_that_came_from_the_values_is_described_as_a_values_change()
    {
        string message = HarborService.RedisEndpointMissing("redis.redis.svc.cluster.local", fromValues: true);

        message.Should().Contain("redis.external.addr");
        message.Should().NotContain("Clear the Redis field");
    }

    /// <summary>
    /// The existence check only judges cluster-local names. Everything else — a managed cloud Redis, a
    /// bare IP, a sentinel list — is somebody else's network, and refusing an install over it would be a
    /// guess dressed as a fact.
    /// </summary>
    [Theory]
    [InlineData("{\"items\":[{\"metadata\":{\"name\":\"redis\",\"namespace\":\"cache\"}}]}", "redis.cache.svc.cluster.local", true)]
    [InlineData("{\"items\":[{\"metadata\":{\"name\":\"redis\",\"namespace\":\"cache\"}}]}", "redis.other.svc.cluster.local", false)]
    [InlineData("{\"items\":[]}", "redis.cache.svc.cluster.local", false)]
    public void A_service_is_found_by_its_cluster_local_name(string json, string host, bool expected)
    {
        RedisService.ServiceDnsNames(json)
            .Any(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase))
            .Should().Be(expected);
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
