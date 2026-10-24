using System.Text.RegularExpressions;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// Where the DataProtection key ring lives, which decides whether a second instance can read what
/// the first one protected.
///
/// <para><b>The defect this guards.</b> The ring was persisted to a per-container filesystem path
/// with no alternative. Behind a load balancer each instance mints its own, so an antiforgery
/// token, auth cookie or OIDC correlation/nonce issued by instance A cannot be unprotected by
/// instance B — login fails about half the time and presents as flaky rather than
/// misconfigured. It is the second thing to break when a second instance appears
/// (docs/decomposition.md §9.2).</para>
/// </summary>
public class SharedKeyRingTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    /// <summary>⭐ Redis is used when there is a connection string to use.</summary>
    [Fact]
    public void A_configured_redis_backs_the_ring()
        => SharedKeyRing.RedisConnection(Config(("ConnectionStrings:Redis", "redis:6379")))
            .Should().Be("redis:6379");

    /// <summary>
    /// ⚠️ Either key is accepted, because <c>Presence:Provider=Redis</c> already took either one.
    /// An installation that configured presence has said where its Redis is; demanding a second,
    /// differently-named key for the same server would be a trap rather than a safeguard.
    /// </summary>
    [Fact]
    public void The_presence_redis_key_is_accepted_too()
        => SharedKeyRing.RedisConnection(Config(("Presence:Redis:Configuration", "redis:6379")))
            .Should().Be("redis:6379");

    /// <summary>The single-instance default is unchanged: no Redis, filesystem ring.</summary>
    [Fact]
    public void Without_redis_the_ring_stays_on_disk()
        => SharedKeyRing.RedisConnection(Config()).Should().BeNull();

    /// <summary>
    /// ⚠️ Whitespace counts as absent. An empty environment variable is how a half-finished
    /// deployment usually presents, and treating <c>""</c> as "use Redis" would fail at connect
    /// time with a message about Redis rather than about configuration.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_setting_is_not_a_redis(string configured)
        => SharedKeyRing.RedisConnection(Config(("ConnectionStrings:Redis", configured)))
            .Should().BeNull();

    /// <summary>An explicit key path wins over the content-root default.</summary>
    [Fact]
    public void The_key_path_is_configurable()
    {
        SharedKeyRing.KeyPath(Config(("DataProtection:KeyPath", "/mnt/keys")), "/app")
            .Should().Be("/mnt/keys");

        SharedKeyRing.KeyPath(Config(), "/app").Should().Be(Path.Combine("/app", "keys"));
    }

    /// <summary>
    /// ⭐ <c>SetApplicationName</c> must survive, and it is the part most easily lost.
    ///
    /// <para>Shared storage is only half of a shared ring. Without a fixed application name the
    /// purpose string is derived per application, so two instances reading the same Redis key or
    /// the same mounted directory still cannot unprotect each other's payloads — and the symptom
    /// is identical to having no shared storage at all, which is what makes it worth a test
    /// rather than a comment.</para>
    ///
    /// <para>⚠️ Asserted against the source because this is startup wiring: there is no object to
    /// interrogate, and a test that built the whole host to check one call would be slower and
    /// more fragile than reading the line.</para>
    /// </summary>
    [Fact]
    public void The_application_name_is_pinned_so_a_shared_ring_is_actually_shared()
    {
        string program = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web, "Program.cs"));
        File.Exists(program).Should().BeTrue("the test reads startup wiring; if it moved, follow it");

        string code = File.ReadAllText(program);

        Regex.IsMatch(code, @"SetApplicationName\(\s*""EntKube""\s*\)").Should().BeTrue(
            "without a fixed application name two instances sharing a key ring still derive "
            + "different purposes and cannot read each other's cookies — the same symptom as no "
            + "sharing at all");
    }
}
