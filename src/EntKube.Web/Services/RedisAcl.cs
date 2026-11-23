using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EntKube.Web.Services;

/// <summary>One app's Redis user, as the users file and the live <c>ACL SETUSER</c> both need it.</summary>
/// <param name="BindingId">The binding the user belongs to; the username is derived from it.</param>
/// <param name="Password">The plaintext password. Only its SHA-256 ever leaves EntKube toward Redis.</param>
/// <param name="KeyPrefix">The prefix the user is confined to, without the colon; null for all keys.</param>
public sealed record RedisAppUser(Guid BindingId, string Password, string? KeyPrefix);

/// <summary>
/// The Redis ACL users EntKube gives each app bound to a managed Redis: names, rules, the users file
/// every node loads at start, and the commands that apply the same users to running nodes.
///
/// <para><b>Why there are two delivery paths.</b> In a sharded Redis Cluster an ACL user exists only
/// on the node it was created on — <c>ACL SETUSER</c> is never replicated, written to the AOF or
/// carried by the cluster bus. So the users go out twice: as a file in a ConfigMap that every leader
/// and follower <c>include</c>s from its redis.conf, which is what a restarted or newly promoted
/// node reads; and as <c>ACL SETUSER</c> on every running node, which is what makes a change take
/// effect without a restart. <c>ACL LOAD</c> is no substitute for the second: in 7.0 it disconnects
/// every client of every non-default user, changed or not.</para>
///
/// <para><b>Why the <c>default</c> user is never in the file.</b> It stays governed by the cluster's
/// <c>requirepass</c>, exactly as before. The operator's other route — a full ACL file through
/// <c>spec.acl</c> — makes Redis ignore <c>requirepass</c>, and a file that then lacks a
/// <c>default</c> line leaves <c>default</c> passwordless: an open cluster whose health checks still
/// pass. Adding lines through the include cannot do that.</para>
///
/// <para>Only the password's SHA-256 is written anywhere Redis reads (<c>#&lt;hex&gt;</c>), so the
/// ConfigMap and the exec stream carry no usable credential. The passwords are 256-bit random
/// values, which is what makes an unsalted hash safe to publish to the cluster.</para>
/// </summary>
public static partial class RedisAcl
{
    /// <summary>
    /// Every user EntKube manages starts with this. A prune deletes only users with this prefix, so a
    /// user someone created by hand on the cluster is never touched.
    /// </summary>
    public const string UserPrefix = "ek-";

    /// <summary>
    /// The key in the users ConfigMap, which becomes the file name under the include directory.
    ///
    /// <para>⚠️ <b>Not a name EntKube chooses.</b> The opstree image's entrypoint includes exactly one
    /// file — <c>include /etc/redis/external.conf.d/redis-additional.conf</c>, and only if it exists —
    /// so the operator's <c>additionalRedisConfig</c> ConfigMap must carry this key. Any other key is
    /// mounted beside it and never read: the pods start, healthy, with no app users at all.</para>
    /// </summary>
    public const string UsersConfigKey = "redis-additional.conf";

    public static string UsernameFor(Guid bindingId) => UserPrefix + bindingId.ToString("N");

    /// <summary>The vault name of a binding's password, stored on the Redis cluster like its admin password.</summary>
    public static string PasswordSecretName(Guid bindingId) => $"REDIS_USER_{bindingId:N}_PASSWORD";

    /// <summary>The binding a vault row named by <see cref="PasswordSecretName"/> belongs to, if it is one.</summary>
    public static bool TryParsePasswordSecretName(string name, out Guid bindingId)
    {
        Match m = PasswordSecretNamePattern().Match(name);
        bindingId = m.Success ? Guid.ParseExact(m.Groups[1].Value, "N") : Guid.Empty;
        return m.Success;
    }

    public static string UsersConfigMapName(string redisClusterName) => $"{redisClusterName}-entkube-users";

    /// <summary>
    /// Why a key prefix is unusable, or null when it is fine. Lowercase letters, digits and hyphens,
    /// starting with a letter or digit: the prefix goes into ACL glob patterns, so <c>*</c>, <c>?</c>,
    /// <c>[</c> or <c>\</c> would widen what the user may touch, and <c>{</c>…<c>}</c> would make it a
    /// Redis Cluster hash tag and put every one of the app's keys on a single shard.
    /// </summary>
    public static string? DescribeInvalidPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;

        if (prefix.Length > Data.CacheBinding.MaxKeyPrefixLength)
            return $"A key prefix can be at most {Data.CacheBinding.MaxKeyPrefixLength} characters.";

        return PrefixPattern().IsMatch(prefix)
            ? null
            : "A key prefix may contain only lowercase letters, digits and hyphens, and must start with a letter or digit.";
    }

    /// <summary>Trims the prefix and drops a trailing colon an operator typed; empty becomes null (all keys).</summary>
    public static string? NormalizePrefix(string? prefix)
    {
        string trimmed = (prefix ?? "").Trim().TrimEnd(':');
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>A prefix to suggest for a new binding, derived from the deployment's name.</summary>
    public static string SuggestPrefix(string deploymentName)
    {
        string s = NonPrefixChars().Replace(deploymentName.ToLowerInvariant(), "-").Trim('-');
        if (s.Length > Data.CacheBinding.MaxKeyPrefixLength)
            s = s[..Data.CacheBinding.MaxKeyPrefixLength].TrimEnd('-');
        return s.Length == 0 ? "app" : s;
    }

    /// <summary>What the app receives as REDIS_KEY_PREFIX: the prefix with its colon, or empty for all keys.</summary>
    public static string KeyPrefixValue(string? keyPrefix) => keyPrefix is null ? "" : keyPrefix + ":";

    public static string Hash(string password) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    /// <summary>
    /// The permissions after the password: which keys, channels and commands the user gets.
    ///
    /// <para><b>Confined to a prefix.</b> ACL key patterns only bind commands that name keys, so every
    /// keyless command that reads the keyspace is denied by name: <c>SCAN</c>, <c>RANDOMKEY</c>,
    /// <c>DBSIZE</c> and <c>CLUSTER GETKEYSINSLOT/COUNTKEYSINSLOT</c> list or count other apps' keys,
    /// <c>PUBSUB</c> lists their channels, <c>CLIENT TRACKING</c> in broadcast mode reports their key
    /// names. <c>@dangerous</c> takes KEYS, FLUSHALL, SORT, MIGRATE, RESTORE and every admin command.
    /// Scripts stay: each <c>redis.call</c> inside one is checked as this user. Script and function
    /// <em>management</em> does not, because the script cache and libraries are shared by every app on a
    /// node. <c>INFO</c> is given back after <c>@dangerous</c>: StackExchange.Redis and Lettuce use it
    /// to learn the topology, and it reveals counts, never key names.</para>
    ///
    /// <para><b>All keys.</b> The app can still read and write every key, as it could with the shared
    /// password, so nothing it does today breaks — but it is no longer the admin: no CONFIG, DEBUG,
    /// MONITOR, ACL, CLUSTER RESET or other admin command, no FLUSHALL/FLUSHDB/SWAPDB that wipe every
    /// app at once, and no flushing or killing the scripts other apps share.</para>
    /// </summary>
    public static string Rules(string? keyPrefix) => keyPrefix is null
        ? "~* &* +@all -@admin -flushall -flushdb -swapdb -migrate -restore -restore-asking "
          + "-script|flush -script|kill -function|flush -function|delete -function|kill -function|restore"
        : $"~{keyPrefix}:* resetchannels &{keyPrefix}:* &__keyspace@0__:{keyPrefix}:* "
          + "+@all -@dangerous -@scripting +eval +evalsha +eval_ro +evalsha_ro +script|load +script|exists "
          + "+info -scan -randomkey -dbsize -cluster|getkeysinslot -cluster|countkeysinslot -cluster|links "
          + "-pubsub -client|tracking -client|caching -memory +memory|usage";

    /// <summary>One line of the users file: <c>user ek-… on #&lt;sha256&gt; &lt;rules&gt;</c>.</summary>
    public static string ConfigLine(RedisAppUser user) =>
        $"user {UsernameFor(user.BindingId)} on #{Hash(user.Password)} {Rules(user.KeyPrefix)}";

    /// <summary>
    /// The live command for one user. <c>reset</c> first, so a user whose prefix was tightened loses
    /// the old patterns instead of keeping them beside the new ones. Changing an existing user does
    /// not disconnect it; its open connections pick up the new rules on their next command.
    /// </summary>
    public static string SetUserCommand(RedisAppUser user) =>
        $"ACL SETUSER {UsernameFor(user.BindingId)} reset on #{Hash(user.Password)} {Rules(user.KeyPrefix)}";

    public static string DelUserCommand(string username) => $"ACL DELUSER {username}";

    /// <summary>
    /// What one running node is sent, given the users it already has: every wanted user set (again —
    /// harmless, and it repairs a node that missed an earlier change), and every EntKube user it
    /// still has that is no longer wanted deleted, which disconnects that app's open connections.
    ///
    /// <para>⚠️ <paramref name="wanted"/> must be <b>every</b> binding of the cluster. Anything absent
    /// from it is revoked — that is how a deleted binding loses access — so a partial list would
    /// revoke apps that are still bound. Only users carrying <see cref="UserPrefix"/> are ever
    /// deleted; <c>default</c> and anything created by hand are left alone.</para>
    /// </summary>
    public static string BuildApplyScript(IEnumerable<string> existingUsers, IReadOnlyCollection<RedisAppUser> wanted)
    {
        HashSet<string> wantedNames = [.. wanted.Select(u => UsernameFor(u.BindingId))];
        StringBuilder sb = new();

        foreach (RedisAppUser user in wanted.OrderBy(u => u.BindingId))
            sb.Append(SetUserCommand(user)).Append('\n');

        foreach (string stale in existingUsers
                     .Where(u => u.StartsWith(UserPrefix, StringComparison.Ordinal) && !wantedNames.Contains(u))
                     .Order(StringComparer.Ordinal))
            sb.Append(DelUserCommand(stale)).Append('\n');

        return sb.ToString();
    }

    /// <summary>
    /// Whether a binding's Secret still holds the admin password although its cluster has switched to
    /// per-app users: it has not been synced since the switch. The app keeps working on it — the
    /// admin password is unchanged — but it is not yet confined.
    /// </summary>
    public static bool StillOnAdminPassword(Data.CacheBinding binding, Data.RedisCluster cluster) =>
        cluster.PerAppUsersSince is DateTime since
        && (binding.LastSyncedAt is not DateTime synced || synced < since);

    /// <summary>
    /// Whether a binding was ever handed the admin password: its cluster never switched, it was bound
    /// before the switch (its earlier syncs wrote the admin password), or it has not synced since. Such
    /// an app may still know the password after its own user is revoked — running pods keep the value
    /// they started with, and anyone who read the Secret has it — so only changing it takes that back.
    /// </summary>
    public static bool EverHeldAdminPassword(Data.CacheBinding binding, Data.RedisCluster cluster) =>
        cluster.PerAppUsersSince is not DateTime since
        || binding.CreatedAt < since
        || StillOnAdminPassword(binding, cluster);

    /// <summary>The users file for a cluster, sorted so an unchanged set produces an unchanged ConfigMap.</summary>
    public static string BuildUsersConfig(IEnumerable<RedisAppUser> users)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Managed by EntKube: one Redis user per app bound to this cluster.");
        sb.AppendLine("# Edits are overwritten. The default user is not here; it stays on requirepass.");
        foreach (RedisAppUser user in users.OrderBy(u => u.BindingId))
            sb.AppendLine(ConfigLine(user));
        return sb.ToString();
    }

    public static string BuildUsersConfigMap(string redisClusterName, string ns, IEnumerable<RedisAppUser> users)
    {
        string body = BuildUsersConfig(users);
        StringBuilder sb = new();
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ConfigMap");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {UsersConfigMapName(redisClusterName)}");
        sb.AppendLine($"  namespace: {ns}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    app.kubernetes.io/managed-by: entkube");
        sb.AppendLine("data:");
        sb.AppendLine($"  {UsersConfigKey}: |");
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            sb.AppendLine("    " + line.TrimEnd('\r'));
        return sb.ToString();
    }

    /// <summary>
    /// Whether a Redis version can carry these rules. Per-app users need 7.0: the rules use
    /// <c>resetchannels</c>, subcommand rules and <c>EVAL_RO</c>, and 6.x either rejects them or
    /// defaults new users to every channel. The version is free text on the create form, so an
    /// unreadable one is treated as unsupported rather than guessed at.
    /// </summary>
    public static bool SupportsPerAppUsers(string? redisVersion)
    {
        Match m = MajorVersionPattern().Match(redisVersion ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out int major) && major >= 7;
    }

    /// <summary>The usernames in an <c>ACL USERS</c> reply, one per line as redis-cli prints them.</summary>
    public static IReadOnlyList<string> ParseUsers(string output) =>
        [.. output.Split('\n')
            .Select(l => l.Trim())
            .Select(l => QuotedListItem().Match(l) is { Success: true } m ? m.Groups[1].Value : l)
            .Where(l => l.Length > 0 && !l.Contains(' '))];

    /// <summary>
    /// The first Redis error in redis-cli's output, or null. redis-cli exits 0 when a command it read
    /// from stdin fails, so the output is the only place a refused <c>ACL SETUSER</c> shows up.
    /// </summary>
    public static string? FindError(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => RedisErrorLine().IsMatch(l));

    [GeneratedRegex("^REDIS_USER_([0-9a-f]{32})_PASSWORD$")]
    private static partial Regex PasswordSecretNamePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonPrefixChars();

    [GeneratedRegex(@"^v?(\d+)")]
    private static partial Regex MajorVersionPattern();

    [GeneratedRegex("^\\d+\\) \"(.*)\"$")]
    private static partial Regex QuotedListItem();

    [GeneratedRegex(@"^(\(error\)\s+)?(ERR|NOPERM|NOAUTH|WRONGPASS|LOADING|BUSY|MISCONF|READONLY|CLUSTERDOWN)\b")]
    private static partial Regex RedisErrorLine();
}
