using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// The per-app Redis users as text: the rules each app gets, the users file every node loads,
/// and the script a running node is sent. All pure, so every rule that decides what an app may
/// touch is pinned here without a cluster.
/// </summary>
public class RedisAclTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Pw = "s3cret-Password_value-that-must-never-leak";

    // ── Rules ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_prefixed_user_is_confined_to_its_keys_and_channels()
    {
        string rules = RedisAcl.Rules("shop");

        rules.Should().StartWith("~shop:* ");
        rules.Should().Contain("resetchannels &shop:* &__keyspace@0__:shop:*");
        rules.Should().NotContain("~*").And.NotContain("&*");
    }

    /// <summary>
    /// Key patterns bind only commands that name keys. These are the keyless commands that would
    /// otherwise list, count or broadcast other apps' key and channel names, or touch the script
    /// state every app on a node shares.
    /// </summary>
    [Theory]
    [InlineData("-@dangerous")]
    [InlineData("-@scripting")]
    [InlineData("-scan")]
    [InlineData("-randomkey")]
    [InlineData("-dbsize")]
    [InlineData("-cluster|getkeysinslot")]
    [InlineData("-cluster|countkeysinslot")]
    [InlineData("-pubsub")]
    [InlineData("-client|tracking")]
    [InlineData("-memory")]
    public void A_prefixed_user_cannot_see_past_its_prefix(string rule)
    {
        RedisAcl.Rules("shop").Split(' ').Should().Contain(rule);
    }

    /// <summary>
    /// Rules apply left to right, so what is given back must come after what took it away:
    /// INFO (the cluster clients need it) after @dangerous, EVAL after @scripting.
    /// </summary>
    [Theory]
    [InlineData("-@dangerous", "+info")]
    [InlineData("-@scripting", "+eval")]
    [InlineData("-@scripting", "+evalsha")]
    [InlineData("-memory", "+memory|usage")]
    public void What_a_prefixed_user_is_given_back_comes_after_what_took_it(string removed, string restored)
    {
        List<string> parts = [.. RedisAcl.Rules("shop").Split(' ')];
        parts.IndexOf(restored).Should().BeGreaterThan(parts.IndexOf(removed));
    }

    /// <summary>
    /// Rules apply left to right. A <c>+@all</c> after the denials would grant back everything they
    /// took away — every rule string would still contain each denial, so only the order shows it.
    /// </summary>
    [Theory]
    [InlineData("shop")]
    [InlineData(null)]
    public void Everything_is_granted_once_and_before_every_denial(string? prefix)
    {
        List<string> parts = [.. RedisAcl.Rules(prefix).Split(' ')];

        parts.Count(p => p == "+@all").Should().Be(1);
        int grantAll = parts.IndexOf("+@all");
        parts.Select((p, i) => (p, i)).Where(x => x.p.StartsWith('-'))
            .Should().OnlyContain(x => x.i > grantAll, "+@all after a denial undoes it");
    }

    [Theory]
    [InlineData("-restore")]
    [InlineData("-restore-asking")]
    [InlineData("-migrate")]
    public void An_all_keys_user_cannot_write_raw_serialised_values(string rule)
    {
        // RESTORE-ASKING does what RESTORE does; leaving it open would undo denying RESTORE.
        RedisAcl.Rules(null).Split(' ').Should().Contain(rule);
    }

    [Theory]
    [InlineData("-@admin")]
    [InlineData("-flushall")]
    [InlineData("-flushdb")]
    [InlineData("-swapdb")]
    [InlineData("-script|flush")]
    [InlineData("-function|flush")]
    public void An_all_keys_user_is_still_not_the_admin(string rule)
    {
        RedisAcl.Rules(null).Split(' ').Should().Contain(rule);
    }

    [Fact]
    public void An_all_keys_user_keeps_every_key_so_existing_apps_do_not_break()
    {
        RedisAcl.Rules(null).Should().StartWith("~* &* +@all ");
    }

    // ── Where the password goes ──────────────────────────────────────────────

    [Fact]
    public void Only_the_password_hash_reaches_redis()
    {
        RedisAppUser user = new(A, Pw, "shop");

        RedisAcl.ConfigLine(user).Should().NotContain(Pw)
            .And.Contain($"#{RedisAcl.Hash(Pw)}");
        RedisAcl.SetUserCommand(user).Should().NotContain(Pw);
        RedisAcl.BuildUsersConfigMap("cache", "caching", [user]).Should().NotContain(Pw);
        RedisAcl.Hash(Pw).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Setting_a_user_resets_it_first_so_a_tightened_prefix_drops_the_old_patterns()
    {
        RedisAcl.SetUserCommand(new RedisAppUser(A, Pw, "shop"))
            .Should().StartWith($"ACL SETUSER ek-{A:N} reset on #");
    }

    [Fact]
    public void The_users_file_never_mentions_the_default_user()
    {
        // The default user stays on requirepass. A users file that redefined it could leave it
        // passwordless — an open cluster whose health checks still pass.
        string config = RedisAcl.BuildUsersConfig([new RedisAppUser(A, Pw, null), new RedisAppUser(B, Pw, "x")]);

        config.Split('\n').Where(l => l.StartsWith("user ", StringComparison.Ordinal))
            .Should().HaveCount(2)
            .And.AllSatisfy(l => l.Should().StartWith("user ek-"));
        config.Should().NotContain("user default");
    }

    [Fact]
    public void The_users_configmap_is_valid_yaml_carrying_the_file()
    {
        string manifest = RedisAcl.BuildUsersConfigMap(
            "cache", "caching", [new RedisAppUser(B, Pw, "x"), new RedisAppUser(A, Pw, null)]);

        YamlStream yaml = [];
        yaml.Load(new StringReader(manifest));
        YamlMappingNode root = (YamlMappingNode)yaml.Documents[0].RootNode;
        YamlMappingNode data = (YamlMappingNode)root["data"];
        string file = ((YamlScalarNode)data[RedisAcl.UsersConfigKey]).Value!;

        ((YamlScalarNode)((YamlMappingNode)root["metadata"])["name"]).Value.Should().Be("cache-entkube-users");
        file.Should().Be(RedisAcl.BuildUsersConfig([new RedisAppUser(A, Pw, null), new RedisAppUser(B, Pw, "x")]),
            "the file is sorted, so the same users always produce the same ConfigMap");
    }

    /// <summary>
    /// The image's entrypoint includes <c>/etc/redis/external.conf.d/redis-additional.conf</c> and
    /// nothing else. A ConfigMap under any other key mounts fine and is silently ignored, so every
    /// node would start without the apps' users — found on kind, not by reading the operator.
    /// </summary>
    [Fact]
    public void The_users_file_has_the_one_name_the_redis_image_includes()
    {
        RedisAcl.UsersConfigKey.Should().Be("redis-additional.conf");
    }

    [Fact]
    public void An_empty_users_file_is_still_a_valid_configmap()
    {
        // Written before a new cluster's pods exist: the include names it, and a pod whose volume
        // source is missing never starts.
        string manifest = RedisAcl.BuildUsersConfigMap("cache", "caching", []);

        YamlStream yaml = [];
        yaml.Load(new StringReader(manifest));
        ((YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["data"])
            .Children.Should().ContainKey(new YamlScalarNode(RedisAcl.UsersConfigKey));
    }

    // ── What a running node is sent ──────────────────────────────────────────

    [Fact]
    public void A_node_is_given_every_wanted_user_and_loses_only_entkube_users_no_longer_wanted()
    {
        string script = RedisAcl.BuildApplyScript(
            ["default", $"ek-{A:N}", $"ek-{B:N}", "someone-by-hand"],
            [new RedisAppUser(A, Pw, "shop")]);

        string[] lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith($"ACL SETUSER ek-{A:N} ");
        lines[1].Should().Be($"ACL DELUSER ek-{B:N}");
        script.Should().NotContain("DELUSER default").And.NotContain("someone-by-hand");
    }

    [Fact]
    public void A_node_with_no_wanted_users_has_every_entkube_user_revoked()
    {
        RedisAcl.BuildApplyScript(["default", $"ek-{A:N}"], [])
            .Should().Be($"ACL DELUSER ek-{A:N}\n");
    }

    [Theory]
    [InlineData("default\nek-1\n", new[] { "default", "ek-1" })]
    [InlineData("1) \"default\"\n2) \"ek-1\"\n", new[] { "default", "ek-1" })]
    [InlineData("", new string[0])]
    public void Acl_users_output_is_read_in_both_redis_cli_formats(string output, string[] expected)
    {
        RedisAcl.ParseUsers(output).Should().Equal(expected);
    }

    [Theory]
    [InlineData("OK\nOK\n", null)]
    [InlineData("OK\nERR Error in ACL SETUSER modifier '+nosuch': Unknown command\n", "ERR Error in ACL SETUSER modifier '+nosuch': Unknown command")]
    [InlineData("(error) NOPERM this user has no permissions\n", "(error) NOPERM this user has no permissions")]
    [InlineData("NOAUTH Authentication required.\n", "NOAUTH Authentication required.")]
    [InlineData("default\nek-1\n", null)]
    public void A_refused_command_is_found_in_the_output_because_redis_cli_still_exits_zero(string output, string? error)
    {
        RedisAcl.FindError(output).Should().Be(error);
    }

    // ── Prefixes ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("shop")]
    [InlineData("shop-eu-1")]
    [InlineData("1shop")]
    [InlineData(null)]
    public void A_plain_prefix_is_accepted(string? prefix)
    {
        RedisAcl.DescribeInvalidPrefix(prefix).Should().BeNull();
    }

    /// <summary>A glob character would widen what the user may touch; braces would make a hash tag.</summary>
    [Theory]
    [InlineData("shop*")]
    [InlineData("sh?p")]
    [InlineData("[shop]")]
    [InlineData("{shop}")]
    [InlineData("Shop")]
    [InlineData("-shop")]
    [InlineData("shop:cart")]
    [InlineData("shop cart")]
    public void A_prefix_that_would_change_the_pattern_is_refused(string prefix)
    {
        RedisAcl.DescribeInvalidPrefix(prefix).Should().NotBeNull();
    }

    [Fact]
    public void A_prefix_longer_than_the_column_is_refused()
    {
        RedisAcl.DescribeInvalidPrefix(new string('a', CacheBinding.MaxKeyPrefixLength + 1)).Should().NotBeNull();
        RedisAcl.DescribeInvalidPrefix(new string('a', CacheBinding.MaxKeyPrefixLength)).Should().BeNull();
    }

    [Theory]
    [InlineData("  shop: ", "shop")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_typed_prefix_is_normalised(string? typed, string? expected)
    {
        RedisAcl.NormalizePrefix(typed).Should().Be(expected);
    }

    [Theory]
    [InlineData("Shop Frontend", "shop-frontend")]
    [InlineData("api_v2", "api-v2")]
    [InlineData("___", "app")]
    public void A_suggested_prefix_is_always_valid(string deployment, string expected)
    {
        string suggested = RedisAcl.SuggestPrefix(deployment);
        suggested.Should().Be(expected);
        RedisAcl.DescribeInvalidPrefix(suggested).Should().BeNull();
    }

    [Fact]
    public void A_long_deployment_name_still_suggests_a_valid_prefix()
    {
        RedisAcl.DescribeInvalidPrefix(RedisAcl.SuggestPrefix(new string('x', 40) + "-" + new string('y', 40)))
            .Should().BeNull();
    }

    [Fact]
    public void The_app_receives_the_prefix_with_its_colon()
    {
        RedisAcl.KeyPrefixValue("shop").Should().Be("shop:");
        RedisAcl.KeyPrefixValue(null).Should().Be("");
    }

    // ── Versions, names, state ───────────────────────────────────────────────

    [Theory]
    [InlineData("v7.0.15", true)]
    [InlineData("7.2", true)]
    [InlineData("v8", true)]
    [InlineData("v6.2.14", false)]
    [InlineData("latest", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Per_app_users_need_redis_7(string? version, bool supported)
    {
        RedisAcl.SupportsPerAppUsers(version).Should().Be(supported);
    }

    [Fact]
    public void A_binding_password_name_round_trips_and_nothing_else_parses_as_one()
    {
        RedisAcl.TryParsePasswordSecretName(RedisAcl.PasswordSecretName(A), out Guid parsed).Should().BeTrue();
        parsed.Should().Be(A);

        // The admin password lives beside them; a prune that misread it would lock out the operator.
        RedisAcl.TryParsePasswordSecretName("REDIS_PASSWORD", out _).Should().BeFalse();
        RedisAcl.TryParsePasswordSecretName("REDIS_HOST", out _).Should().BeFalse();
    }

    [Fact]
    public void A_binding_synced_before_the_switch_still_holds_the_admin_password()
    {
        DateTime since = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        RedisCluster cluster = new() { Name = "c", Namespace = "n", RedisVersion = "v7", StorageSize = "1Gi", PerAppUsersSince = since };

        RedisAcl.StillOnAdminPassword(Binding(since.AddMinutes(-1)), cluster).Should().BeTrue();
        RedisAcl.StillOnAdminPassword(Binding(null), cluster).Should().BeTrue();
        RedisAcl.StillOnAdminPassword(Binding(since.AddMinutes(1)), cluster).Should().BeFalse();

        cluster.PerAppUsersSince = null;
        RedisAcl.StillOnAdminPassword(Binding(null), cluster).Should().BeFalse(
            "a cluster that never switched has no per-app users to be behind on");

        static CacheBinding Binding(DateTime? synced) => new() { KubernetesSecretName = "s", LastSyncedAt = synced };
    }

    // ── What the app receives ────────────────────────────────────────────────

    [Fact]
    public void The_app_receives_its_own_username_in_the_url()
    {
        Dictionary<string, string?> keys = RedisService.CredentialKeys("h", "6379", $"ek-{A:N}", Pw, "shop");

        keys["REDIS_URL"].Should().Be($"redis://ek-{A:N}:{Pw}@h:6379");
        keys["REDIS_USERNAME"].Should().Be($"ek-{A:N}");
        keys["REDIS_KEY_PREFIX"].Should().Be("shop:");
    }

    [Fact]
    public void Every_binding_writes_the_same_six_keys_so_switching_leaves_none_behind()
    {
        RedisService.CredentialKeys("h", "1", "default", "pw", null).Keys
            .Should().BeEquivalentTo(RedisService.CredentialKeys("h", "1", "ek-x", "pw", "p").Keys);
    }
}
