namespace EntKube.Web.Services;

/// <summary>
/// Where the DataProtection key ring lives, which decides whether a second instance can read
/// what the first one protected.
///
/// <para>⭐ <b>This is a one-line decision pulled out of <c>Program.cs</c> so it can be asserted.</b>
/// Buried in startup it was unreachable by any test, and the failure it governs is one that only
/// appears with a load balancer in front: each container minting its own ring means an antiforgery
/// token, auth cookie or OIDC nonce issued by instance A cannot be read by instance B, so login
/// fails intermittently rather than visibly (docs/decomposition.md §9.2).</para>
///
/// <para>⚠️ Both config keys are accepted because <c>Presence:Provider=Redis</c> already took
/// either one, and an installation that configured presence has by definition already said where
/// its Redis is. Requiring a second, differently-named key for the same server would be a trap.</para>
/// </summary>
public static class SharedKeyRing
{
    /// <summary>
    /// The Redis connection to back the ring with, or null to keep it on the filesystem.
    ///
    /// <para>Whitespace counts as absent: an empty environment variable is how a half-finished
    /// deployment usually presents, and treating <c>""</c> as "use Redis" would fail at connect
    /// time with a message about Redis rather than about configuration.</para>
    /// </summary>
    public static string? RedisConnection(IConfiguration config)
    {
        string? connection = config.GetConnectionString("Redis")
            ?? config.GetValue<string>("Presence:Redis:Configuration");

        return string.IsNullOrWhiteSpace(connection) ? null : connection;
    }

    /// <summary>
    /// The filesystem location used when there is no Redis.
    ///
    /// <para>⚠️ Shared across instances only if this path is one mount. That is free when every
    /// instance is on one host — the docker-compose case — and impossible when they are not,
    /// which is the whole reason the Redis path exists.</para>
    /// </summary>
    public static string KeyPath(IConfiguration config, string contentRootPath)
        => config.GetValue<string>("DataProtection:KeyPath")
           ?? Path.Combine(contentRootPath, "keys");
}
