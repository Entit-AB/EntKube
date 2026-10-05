using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Renders a <see cref="StalwartComponentConfig"/> into the three artefacts a Stalwart deployment
/// actually consumes:
///
/// <list type="bullet">
/// <item><description>the pod's <c>config.json</c> — which in v0.16 describes the datastore and
/// nothing else;</description></item>
/// <item><description>the Kubernetes manifest — StatefulSet, Services, ConfigMap;</description></item>
/// <item><description>the declarative apply plan — an NDJSON document replayed by
/// <c>stalwart-cli apply</c> that carries every other setting, because v0.16 keeps domains,
/// listeners, directories, milters and DKIM inside the datastore rather than in a config
/// file.</description></item>
/// </list>
///
/// <para>All of it is pure: same config in, same bytes out, no I/O. That is deliberate — these
/// are the parts worth testing, and the parts most likely to be wrong in a way a running cluster
/// would only reveal slowly.</para>
///
/// <para><b>On JSON encoding.</b> Stalwart's object model does not use plain JSON collections.
/// A <c>Set&lt;T&gt;</c> is an object mapping each member to <c>true</c>; a <c>List&lt;T&gt;</c>
/// is an object keyed by stringified indices starting at <c>"0"</c>; durations are integer
/// milliseconds and sizes integer bytes. Writing a set as a JSON array is accepted by nothing and
/// fails at apply time with an <c>invalidPatch</c>, so the helpers below exist to make the right
/// encoding the easy one.</para>
/// </summary>
public static class StalwartPlanBuilder
{
    /// <summary>Where the RocksDB datastore lives inside the container.</summary>
    public const string DataPath = "/var/lib/stalwart";

    /// <summary>Where the config file is mounted inside the container.</summary>
    public const string ConfigPath = "/etc/stalwart/config.json";

    /// <summary>Directory the TLS certificate and key are mounted into.</summary>
    public const string TlsMountPath = "/etc/stalwart/tls";

    /// <summary>Environment variable the server reads the LDAP bind password from.</summary>
    public const string LdapBindPasswordEnv = "STALWART_LDAP_BIND_PASSWORD";

    /// <summary>Env var the PostgreSQL datastore password is read from in HA mode.</summary>
    public const string DbPasswordEnv = "STALWART_DB_PASSWORD";

    /// <summary>Env var the S3 blob-store secret key is read from in HA mode.</summary>
    public const string S3SecretKeyEnv = "STALWART_S3_SECRET_KEY";

    /// <summary>
    /// Environment variable carrying the coordinator Redis password. <c>RedisClusterStore.authSecret</c>
    /// is a <c>SecretKeyOptional</c> that can name it, and the plan still sets that — but it is not
    /// what makes the connection work, so it is no longer the only place the password appears. See
    /// <see cref="BuildRedisUrl"/>.
    /// </summary>
    public const string RedisPasswordEnv = "STALWART_REDIS_PASSWORD";

    /// <summary>
    /// The Redis user the coordinator authenticates as, named explicitly because the alternative is
    /// not "no user" but the empty one.
    ///
    /// <para>Every Stalwart store that authenticates carries a username beside its secret — the
    /// PostgreSQL datastore has <c>authUsername</c> and <c>authSecret</c>, the S3 blob store
    /// <c>accessKey</c> and <c>secretKey</c>. The Redis cluster store was given only the secret, so
    /// it sent the password with an empty username, and Redis rejects that with <c>WRONGPASS
    /// invalid username-password pair</c> — which Stalwart reports as <c>Password authentication
    /// failed</c>, word for word what it says when there is no password at all.</para>
    ///
    /// <para>Measured on the live cluster with the one correct password: <c>-a pw</c> PONG,
    /// <c>--user '' --pass pw</c> WRONGPASS, <c>--user default --pass pw</c> PONG. <c>default</c> is
    /// the user <c>requirepass</c> configures, so naming it is what the working invocation does.</para>
    /// </summary>
    public const string RedisUsername = "default";

    /// <summary>
    /// The coordinator URL, carrying the password inline when there is one.
    ///
    /// <para>Inline for BOTH store shapes, which is not what the schema suggests. The standalone
    /// <c>RedisStore</c> has no secret field at all, so it never had a choice. The cluster store does
    /// have <c>authSecret</c>, and pointing it at an environment variable looked like the better
    /// shape — the credential stays in the pod's environment and never lands in the applied plan.
    /// It does not work: the client authenticates its seed connection from the URL alone, so a URL
    /// with no credentials sends no password however correct the env var is, and the cluster fails
    /// with <c>Failed to create initial connections … Password authentication failed</c>. Proven on a
    /// live cluster, where the plan named the right store, the pod held the right password in the
    /// right variable, <c>redis-cli</c> authenticated with that very value — and the server still
    /// could not lock a task.</para>
    ///
    /// <para>So the password goes in the URL and <c>authSecret</c> is set as well: harmless where it
    /// is honoured, load-bearing where it is not. The cost is real and deliberate — the applied plan
    /// now contains the credential, and that plan is stored on-cluster as the
    /// <c>&lt;release&gt;-apply-plan</c> Secret. A coordinator reachable only inside the cluster is
    /// the shape that makes this acceptable.</para>
    ///
    /// <para>The username is spelled out as <c>default</c>, and that word is the entire fix. The
    /// obvious spelling — <c>redis://:password@host</c> — names an EMPTY username, not an absent
    /// one, and Redis answers that with <c>WRONGPASS invalid username-password pair</c>: the same
    /// rejection as a wrong password, and indistinguishable from it in Stalwart's log. Measured
    /// against the live cluster, one password and three spellings: <c>-a pw</c> gave PONG,
    /// <c>--user '' --pass pw</c> gave WRONGPASS, <c>--user default --pass pw</c> gave PONG. Both
    /// failing shapes — no credentials at all, and an empty username — surface identically as
    /// <c>Password authentication failed</c>, which is why this cost three attempts to find.</para>
    ///
    /// <para>Naming a user at all requires the two-argument <c>AUTH</c> of Redis 6 (2020), which
    /// every Redis the operator builds is well past. A pre-6 server reached as an unmanaged endpoint
    /// would reject it — the one case this deliberately does not serve.</para>
    /// </summary>
    public static string BuildRedisUrl(string endpoint, string? password) =>
        string.IsNullOrWhiteSpace(password)
            ? $"redis://{endpoint}"
            : $"redis://default:{Uri.EscapeDataString(password)}@{endpoint}";

    /// <summary>
    /// Every address range a connection can reach this server from inside the cluster, as CIDR.
    ///
    /// <para>Ranges rather than addresses because nothing here is stable: pods are rescheduled and
    /// the autoscaler adds nodes. Deliberately broad — it covers the private space of RFC 1918, the
    /// carrier-grade NAT block that several CNIs allocate pod addresses from (Kubernetes clusters are
    /// commonly on 100.64.0.0/10, which a list of "the private ranges" would miss), loopback, and
    /// IPv6 unique-local. None of these can be a real remote client, so exempting them costs nothing
    /// that was protecting anything.</para>
    /// </summary>
    /// <summary>
    /// The address ranges that are inside the cluster, and so can never be banned.
    ///
    /// <para>Every prefix here is /8 or longer, and that is a constraint rather than a preference.
    /// Stalwart parses an address or mask with <c>(8..=32)</c> for IPv4 and <c>(8..=128)</c> for IPv6,
    /// so anything shorter is rejected outright — and a rejected entry does not merely go missing. The
    /// apply plan stops at its first failed operation, and this one is emitted before the accounts, so
    /// a single unparseable range strands the administrator, every mailbox, the spam settings and the
    /// milter. The IPv6 unique-local range is therefore written as the two /8s it is made of rather
    /// than as <c>fc00::/7</c>, which is what it is normally called and which Stalwart will not take.
    /// </para>
    /// </summary>
    public static readonly string[] InternalRanges =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "127.0.0.0/8",
        // fc00::/7 in the two halves Stalwart will accept: together these are exactly that range.
        "fc00::/8",
        "fd00::/8",
        // Not ::1/128. A full-length prefix is dropped when Stalwart stores the value, so writing it
        // here would mean matching on a string the server never holds — see CanonicalIpOrMask.
        "::1",
    ];

    /// <summary>
    /// Whether Stalwart will accept this as an address or mask, decided the same way it decides.
    ///
    /// <para>Mirrors <c>IpAddrOrMask::from_str</c>: a bare address, or an address and a prefix length
    /// of 8–32 for IPv4 and 8–128 for IPv6. Checked here because the consequence of sending one it
    /// refuses is out of all proportion to the mistake — the plan aborts at that operation and
    /// everything after it, the accounts included, is never applied. Skipping the entry loses one
    /// allow-list range; sending it loses the rest of the configuration.</para>
    ///
    /// <para>The address half must also be in its canonical form. Stalwart parses with Rust's
    /// <c>IpAddr</c>, which takes only full dotted-quad and standard IPv6 notation, while .NET accepts
    /// abbreviations such as <c>10.1</c> and silently reads them as something else — so agreeing with
    /// .NET is not enough to know Stalwart will agree.</para>
    /// </summary>
    public static bool IsAcceptableIpOrMask(string value)
    {
        string text = (value ?? "").Trim();
        int slash = text.LastIndexOf('/');

        string addressPart = slash < 0 ? text : text[..slash].Trim();

        if (!System.Net.IPAddress.TryParse(addressPart, out System.Net.IPAddress? address)
            || !string.Equals(address.ToString(), addressPart, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Stalwart's own is_valid() refuses the unspecified address, so an allow-list entry built on it
        // is rejected however the mask is written.
        if (address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any))
        {
            return false;
        }

        if (slash < 0)
        {
            return true;
        }

        if (!int.TryParse(text[(slash + 1)..].Trim(), out int prefix))
        {
            return false;
        }

        int longest = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;

        return prefix >= 8 && prefix <= longest;
    }

    /// <summary>
    /// An address or mask written the way Stalwart itself writes it back.
    ///
    /// <para>Mirrors <c>Display for IpAddrOrMask</c>, whose one surprise is that a full-length prefix
    /// is not printed: <c>::1/128</c> is stored and serialised as <c>::1</c>, and <c>10.0.0.5/32</c> as
    /// <c>10.0.0.5</c>. Shorter prefixes are kept, recovered from the mask's bit count, so
    /// <c>10.0.0.0/8</c> stays as it is.</para>
    ///
    /// <para>This matters because the allow-list is upserted with <c>address</c> as the match key. Send
    /// <c>::1/128</c> and the match finds nothing — the stored object's address is <c>::1</c> — so the
    /// operation becomes a create, and the create collides with the row that was already there:
    /// <c>primaryKeyViolation</c> on an entry that exists and was asked for by a name it is not kept
    /// under. Since the plan stops at its first failure, that again strands every operation after the
    /// allow-list, the accounts included.</para>
    /// </summary>
    public static string CanonicalIpOrMask(string value)
    {
        string text = (value ?? "").Trim();
        int slash = text.LastIndexOf('/');

        if (!System.Net.IPAddress.TryParse(slash < 0 ? text : text[..slash].Trim(),
                out System.Net.IPAddress? address))
        {
            return text;
        }

        string canonical = address.ToString();

        if (slash < 0 || !int.TryParse(text[(slash + 1)..].Trim(), out int prefix))
        {
            return canonical;
        }

        int longest = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;

        return prefix >= longest ? canonical : $"{canonical}/{prefix}";
    }

    /// <summary>
    /// The single cluster role EntKube gives every node: run every task and every listener. Real
    /// deployments split roles (maintenance tasks on one node), but an all-equal cluster is the
    /// documented starting point and the one whose behaviour is unsurprising.
    /// </summary>
    public const string ClusterRoleName = "all-roles";

    /// <summary>
    /// The resolved shared backends an HA deployment runs on. All the connection facts a node needs,
    /// with the secrets left as env-var references (their values are injected into the pod, never
    /// written into config.json or the plan). Built by the service from the selected CNPG database,
    /// S3 storage link and coordinator Redis; consumed by the otherwise-pure builders here.
    /// </summary>
    /// <param name="DbHost">PostgreSQL host (the CNPG <c>-rw</c> Service).</param>
    /// <param name="DbPort">PostgreSQL port.</param>
    /// <param name="DbName">Database name.</param>
    /// <param name="DbUser">Database user (the CNPG database owner).</param>
    /// <param name="S3Endpoint">Full S3 endpoint URL.</param>
    /// <param name="S3Region">S3 region name.</param>
    /// <param name="S3Bucket">Bucket holding message bodies and attachments.</param>
    /// <param name="S3AccessKey">S3 access key (public half; the secret half is an env var).</param>
    /// <param name="RedisUrl">
    /// Coordinator / in-memory Redis URL, e.g. <c>redis://redis.redis.svc:6379</c>. A password, when
    /// there is one, is carried inline (<c>redis://:password@host:port</c>) for BOTH store shapes —
    /// the standalone one has no secret field to put it in, and the cluster one has a secret field
    /// that does not govern the connection. Built by <see cref="BuildRedisUrl"/>, whose remarks say
    /// why. It is therefore visible in the applied plan, which is what makes an in-cluster-only
    /// coordinator the assumed shape.
    /// </param>
    /// <param name="Replicas">Node count.</param>
    public sealed record StalwartHaBackend(
        string DbHost, int DbPort, string DbName, string DbUser,
        string S3Endpoint, string S3Region, string S3Bucket, string S3AccessKey,
        string RedisUrl, bool RedisIsCluster, int Replicas);

    /// <summary>
    /// The in-cluster HTTP port: JMAP, the admin UI, the OAuth endpoints — and the public
    /// autodiscovery paths, which are also answered on the mail address's own HTTPS listener.
    /// </summary>
    public const int HttpPort = 8080;

    /// <summary>
    /// The addresses a PROXY header is honoured from, one per line in the operator's field.
    /// </summary>
    public static List<string> ProxyTrustedNetworks(StalwartComponentConfig config) =>
        !config.ProxyProtocol
            ? []
            : (config.ProxyTrustedNetworks ?? "")
                .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !n.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>
    /// Whether a trusted-networks entry reaches inside the cluster.
    ///
    /// <para><b>The check that matters, and the one that was missing.</b> Covering the ingress
    /// gateway was tested for, because those addresses were in hand. But the entry that does the
    /// most damage is a broad private or CGNAT range, because it also covers the pod network — and
    /// the client that then cannot connect is <em>EntKube's own apply Job</em>. The server becomes
    /// unconfigurable by the thing that configures it, and the failure is a connection that never
    /// completes, which reads as a network fault rather than as the setting that caused it.</para>
    ///
    /// <para>Verified on a running server rather than reasoned about: with such a list in force, a
    /// request from a pod is closed before any HTTP is read, and the identical request sending a
    /// PROXY v1 greeting is answered.</para>
    ///
    /// <para>Overlap, not containment: <c>10.0.0.0/8</c> and <c>10.240.3.0/24</c> both reach inside,
    /// and either breaks every in-cluster client.</para>
    /// </summary>
    public static bool CoversClusterAddresses(string network) =>
        InternalRanges.Any(internalRange => RangesOverlap(internalRange, network));

    /// <summary>Whether two CIDR entries have any address in common.</summary>
    private static bool RangesOverlap(string left, string right)
    {
        (IPAddress? leftAddress, int leftPrefix) = ParseCidr(left);
        (IPAddress? rightAddress, int rightPrefix) = ParseCidr(right);

        if (leftAddress is null
            || rightAddress is null
            || leftAddress.AddressFamily != rightAddress.AddressFamily)
        {
            return false;
        }

        // Either the wider one contains the narrower one's base address, or they are disjoint.
        try
        {
            return leftPrefix <= rightPrefix
                ? new IPNetwork(leftAddress, leftPrefix).Contains(rightAddress)
                : new IPNetwork(rightAddress, rightPrefix).Contains(leftAddress);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>An entry as an address and a prefix length, defaulting to a full-length prefix.</summary>
    private static (IPAddress? Address, int Prefix) ParseCidr(string entry)
    {
        string text = (entry ?? "").Trim();
        int slash = text.LastIndexOf('/');

        if (!IPAddress.TryParse(slash < 0 ? text : text[..slash].Trim(), out IPAddress? address))
        {
            return (null, 0);
        }

        int full = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;

        return slash < 0 || !int.TryParse(text[(slash + 1)..].Trim(), out int prefix)
            ? (address, full)
            : (address, prefix);
    }

    /// <summary>
    /// Whether a trusted-networks entry covers this address.
    ///
    /// <para>Used to catch the misconfiguration that has no per-listener escape. Stalwart requires a
    /// PROXY header from every peer matching <c>proxyTrustedNetworks</c>, on every listener — the
    /// listener-level override inherits the global list when it is empty, so there is no way to say
    /// "not on this port". An entry that also matches addresses inside the cluster therefore makes the
    /// server wait for a header that in-cluster clients never send: the support mailbox poller, webmail,
    /// and the admin UI and JMAP arriving through the gateway. The list has to name the load balancer's
    /// own subnet and nothing wider.</para>
    /// </summary>
    public static bool TrustedNetworkCovers(string network, System.Net.IPAddress address)
    {
        string text = (network ?? "").Trim();
        int slash = text.LastIndexOf('/');

        if (!System.Net.IPAddress.TryParse(slash < 0 ? text : text[..slash].Trim(),
                out System.Net.IPAddress? prefixAddress))
        {
            return false;
        }

        if (prefixAddress.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        if (slash < 0)
        {
            return prefixAddress.Equals(address);
        }

        if (!int.TryParse(text[(slash + 1)..].Trim(), out int prefix))
        {
            return false;
        }

        try
        {
            return new System.Net.IPNetwork(prefixAddress, prefix).Contains(address);
        }
        catch (ArgumentException)
        {
            // A prefix length the family cannot carry. Not ours to report here — the plan refuses it
            // separately — and certainly not a reason to throw out of a diagnostic.
            return false;
        }
    }

    /// <summary>
    /// Hands a tag back to the rule set's own score, undoing a previous override.
    ///
    /// <para>Removing the override object would be the obvious way and is the wrong one: the plan
    /// upserts these, so an object simply left out stays exactly as it was on a server that already
    /// has it. Restoring a suppressed tag has to be something written, not something omitted.</para>
    /// </summary>
    private static Dictionary<string, object?> DefaultScore(string tag) => new()
    {
        ["@type"] = "Score",
        ["tag"] = tag,
        ["score"] = DefaultTagScores[tag],
    };

    /// <summary>
    /// What the rule set scores the tags this plan is willing to override, so one can be put back.
    /// Taken from the values observed on a live 0.16 server rather than guessed.
    /// </summary>
    private static readonly Dictionary<string, double> DefaultTagScores = new()
    {
        ["VIOLATED_DIRECT_SPF"] = 3.50,
        ["RDNS_NONE"] = 2.00,
    };

    /// <summary>A <c>SpamTag</c> override that stops a tag contributing to the score.</summary>
    private static Dictionary<string, object?> ZeroScore(string tag) => new()
    {
        ["@type"] = "Score",
        ["tag"] = tag,
        ["score"] = 0.0,
    };

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // ── config.json ───────────────────────────────────────────────────────────

    /// <summary>
    /// The on-disk startup configuration. In v0.16 this holds the DataStore object and only the
    /// DataStore object — every other setting lives inside the store that this points at.
    /// </summary>
    public static string BuildConfigJson(StalwartHaBackend? ha = null)
    {
        // Single node: an embedded RocksDB is the whole store. HA: the datastore is the shared
        // PostgreSQL, and everything else (blob store, coordinator, cluster roles) is written by the
        // apply plan — config.json still describes only the datastore, exactly as v0.16 requires.
        Dictionary<string, object?> store = ha is null
            ? new() { ["@type"] = "RocksDb", ["path"] = DataPath }
            : new()
            {
                ["@type"] = "PostgreSql",
                ["host"] = ha.DbHost,
                ["port"] = ha.DbPort,
                ["database"] = ha.DbName,
                ["authUsername"] = ha.DbUser,
                ["authSecret"] = new Dictionary<string, object?>
                {
                    ["@type"] = "EnvironmentVariable",
                    ["variableName"] = DbPasswordEnv,
                },
            };
        return JsonSerializer.Serialize(store, Indented);
    }

    // ── Apply plan ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the NDJSON plan that converges a running server onto <paramref name="config"/>.
    /// One JSON operation per line, no enclosing array.
    ///
    /// <para>Operation choice per object type is a judgement, not a formality. Listeners and the
    /// milter are <c>reconcile</c>d, so turning a protocol or the spam filter off actually removes
    /// it rather than leaving a live port behind. Mailboxes are only ever <c>upsert</c>ed: a
    /// <c>reconcile</c> there would mean that removing an account row in EntKube silently deletes
    /// somebody's mail, and no configuration change should be able to do that.</para>
    /// </summary>
    /// <param name="adminPassword">
    /// Password for the administrator account, used only when authentication is
    /// <see cref="StalwartAuthMode.Internal"/> — in LDAP or OIDC mode the credential lives in the
    /// directory and an internally-stored one would be a second, divergent copy of it. Null omits
    /// the credential, which is what every other mode wants.
    /// </param>
    /// <param name="trustedProxyAddresses">
    /// Addresses the cluster's ingress gateway connects from, allow-listed so Stalwart can never ban
    /// them. Resolved by the caller at apply time, because gateway pod IPs change on every restart.
    /// </param>
    /// <param name="clearBlockedIps">
    /// Remove every existing ban. An explicit operator action, never a side effect of applying:
    /// it un-bans genuine attackers along with the gateway, so it must be a deliberate choice.
    /// </param>
    public static string BuildApplyPlan(
        StalwartComponentConfig config,
        IReadOnlyList<StalwartMailDomain> domains,
        IReadOnlyList<StalwartMailAccount> accounts,
        string? adminPassword = null,
        IReadOnlyList<string>? trustedProxyAddresses = null,
        bool clearBlockedIps = false,
        bool verboseLogging = false,
        StalwartHaBackend? ha = null,
        IReadOnlyList<string>? trustedSenderDomains = null,
        IReadOnlyDictionary<Guid, string>? accountPasswords = null)
    {
        List<string> lines = [];

        StalwartMailDomain? primary = domains.FirstOrDefault(d => d.IsPrimary) ?? domains.FirstOrDefault();

        // Stable client ids so a re-run produces byte-identical refs.
        Dictionary<Guid, string> domainRefs = [];
        for (int i = 0; i < domains.Count; i++)
        {
            domainRefs[domains[i].Id] = $"dom-{i}";
        }

        // ── ACME provider (before the domains that reference it) ──
        bool acme = config.TlsMode == StalwartTlsMode.Acme && !string.IsNullOrWhiteSpace(config.AcmeContact);
        if (acme)
        {
            lines.Add(Op("upsert", "AcmeProvider", MatchOn("contact"), new()
            {
                ["acme"] = new Dictionary<string, object?>
                {
                    ["contact"] = Set([config.AcmeContact!.Trim()]),
                    ["challengeType"] = config.AcmeChallenge switch
                    {
                        StalwartAcmeChallenge.Http01 => "Http01",
                        StalwartAcmeChallenge.Dns01 => "Dns01",
                        _ => "TlsAlpn01",
                    },
                }
            }));
        }

        // ── Certificate (cert-manager or operator-supplied, mounted as files) ──
        //
        // File-backed rather than inlined: cert-manager rewrites the mounted Secret on renewal, so
        // the certificate stays current without EntKube having to notice the renewal and push a new
        // PEM into the datastore every ninety days.
        bool fileCert = config.TlsMode is StalwartTlsMode.ClusterIssuer or StalwartTlsMode.Manual;
        if (fileCert)
        {
            // reconcile, not upsert: the certificate set becomes exactly this one.
            //
            // An upsert only ever adds. Nothing removed the certificates left behind by earlier
            // configurations — a different TLS mode, an earlier install against the same datastore, a
            // hostname that has since changed — so they accumulated, and a live server reached five of
            // them and said so on every handshake: "Multiple TLS certificates available, total = 5".
            // Stalwart then has to choose, and a wrong choice is served as a name mismatch on a server
            // whose configuration looks right. The support mailbox now connects on the public hostname
            // and checks the name, so that choice stopped being cosmetic.
            //
            // Only in the modes where EntKube owns the certificate. Under ACME Stalwart obtains and
            // renews its own, and replacing the set would delete them — which is why this is inside the
            // file-backed branch rather than emitted unconditionally with an empty set for ACME.
            lines.Add(Op("reconcile", "Certificate", MatchAll, new()
            {
                ["cert"] = new Dictionary<string, object?>
                {
                    ["certificate"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "File",
                        ["filePath"] = $"{TlsMountPath}/tls.crt",
                    },
                    ["privateKey"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "File",
                        ["filePath"] = $"{TlsMountPath}/tls.key",
                    },
                }
            }));
        }

        // ── High-availability backends ──
        //
        // These make the deployment multi-node: the message bodies move to shared S3, the nodes
        // coordinate through Redis, and every node runs the all-roles role. The datastore itself is
        // already the shared PostgreSQL by virtue of config.json, so it is not repeated here.
        if (ha is not null)
        {
            // Blob store: S3, path-style (Stalwart forces it — right for Ceph/MinIO/Cleura). The
            // access key is public; the secret key is an env var so it never lands in the plan.
            lines.Add(Update("BlobStore", new()
            {
                ["@type"] = "S3",
                ["region"] = new Dictionary<string, object?>
                {
                    ["@type"] = "Custom",
                    ["customRegion"] = ha.S3Region,
                    ["customEndpoint"] = ha.S3Endpoint,
                },
                ["bucket"] = ha.S3Bucket,
                ["accessKey"] = new Dictionary<string, object?> { ["@type"] = "Value", ["value"] = ha.S3AccessKey },
                ["secretKey"] = new Dictionary<string, object?>
                {
                    ["@type"] = "EnvironmentVariable",
                    ["variableName"] = S3SecretKeyEnv,
                },
            }));

            // Coordinator + in-memory store: the same Redis for both. Pointing the in-memory store
            // (rate limits, caches, ephemeral cluster state) at it keeps that hot-path state off the
            // shared PostgreSQL, which it would otherwise default to.
            //
            // Which of the two Redis shapes this is matters more than it looks. A sharded Redis
            // Cluster answers a key it does not own with a MOVED redirection, and the standalone
            // client does not follow one — so a cluster addressed as `{"@type":"Redis"}` starts
            // cleanly, authenticates a login, and then fails every lookup behind it with
            // `Redis error … reason = "Moved: 5938 10.0.0.1:6379"`. Logging in is what breaks, and
            // nothing in the failure names the address. EntKube's own managed Redis is always a
            // cluster, so this is the ordinary case rather than the exotic one.
            Dictionary<string, object?> redisStore = ha.RedisIsCluster
                // urls is a Map<String>, which serialises as {member: true} — the same encoding as a
                // listener bind, not an array. One bootstrap URL is enough: the client discovers the
                // rest of the topology from it.
                ? new()
                {
                    ["@type"] = "RedisCluster",
                    ["urls"] = Set([ha.RedisUrl]),
                    // Beside the secret, never without it: a secret with no username is sent as a
                    // username of "", which Redis refuses exactly as it refuses a wrong password.
                    // See RedisUsername.
                    ["authUsername"] = RedisUsername,
                    ["authSecret"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "EnvironmentVariable",
                        ["variableName"] = RedisPasswordEnv,
                    },
                }
                : new()
                {
                    ["@type"] = "Redis",
                    ["url"] = ha.RedisUrl,
                };

            lines.Add(Update("Coordinator", new(redisStore)));
            lines.Add(Update("InMemoryStore", new(redisStore)));

            // Cluster role every node runs, named by the STALWART_ROLE env each pod carries.
            lines.Add(Op("upsert", "ClusterRole", MatchOn("name"), new()
            {
                ["role"] = new Dictionary<string, object?>
                {
                    ["name"] = ClusterRoleName,
                    ["description"] = "EntKube — full functionality on every node",
                    ["tasks"] = new Dictionary<string, object?> { ["@type"] = "EnableAll" },
                    ["listeners"] = new Dictionary<string, object?> { ["@type"] = "EnableAll" },
                },
            }));
        }

        // ── Domains ──
        if (domains.Count > 0)
        {
            Dictionary<string, object?> values = [];
            foreach (StalwartMailDomain domain in domains)
            {
                Dictionary<string, object?> body = new()
                {
                    ["name"] = domain.Name.Trim().ToLowerInvariant(),
                    ["isEnabled"] = true,
                    ["allowRelaying"] = domain.AllowRelaying,
                    ["subAddressing"] = Union(domain.SubAddressing ? "Enabled" : "Disabled"),
                    ["dnsManagement"] = Union("Manual"),
                    // Automatic carries its own defaults for algorithms, selector template and
                    // rotation, which are sensible and version-tracked upstream; restating them
                    // here would only pin this deployment to today's answer.
                    ["dkimManagement"] = Union(domain.AutomaticDkim ? "Automatic" : "Manual"),
                    ["certificateManagement"] = acme
                        ? new Dictionary<string, object?> { ["@type"] = "Automatic", ["acmeProviderId"] = "#acme" }
                        : Union("Manual"),
                };

                if (!string.IsNullOrWhiteSpace(domain.Description))
                {
                    body["description"] = domain.Description.Trim();
                }
                if (!string.IsNullOrWhiteSpace(domain.CatchAllLocalPart))
                {
                    body["catchAllAddress"] = $"{domain.CatchAllLocalPart.Trim()}@{domain.Name.Trim()}";
                }

                values[domainRefs[domain.Id]] = body;
            }

            lines.Add(Op("upsert", "Domain", MatchOn("name"), values));
        }

        // ── Directory ──
        string? directoryRef = BuildDirectory(config, lines);

        // ── Authentication: which directory backs logins ──
        lines.Add(Update("Authentication", new()
        {
            ["directoryId"] = directoryRef is null ? null : $"#{directoryRef}",
        }));

        // ── System settings ──
        Dictionary<string, object?> system = new()
        {
            ["defaultHostname"] = config.Hostname.Trim(),
        };
        if (primary is not null)
        {
            system["defaultDomainId"] = $"#{domainRefs[primary.Id]}";
        }
        if (fileCert)
        {
            system["defaultCertificateId"] = "#cert";
        }

        // Where a PROXY header is accepted from. Declaring the set is what enables the protocol, so an
        // empty list means it is off: a trusted address may send the header or not and is processed
        // either way, which is what lets webmail and the apply Job keep connecting to the same
        // listeners without one, while an untrusted address that sends one is refused.
        List<string> proxyNetworks = ProxyTrustedNetworks(config);
        if (proxyNetworks.Count > 0)
        {
            system["proxyTrustedNetworks"] = Set(proxyNetworks);
        }

        lines.Add(Update("SystemSettings", system));

        // ── Listeners ──
        lines.Add(Op("reconcile", "NetworkListener", MatchOn("name"), BuildListeners(config)));

        // ── Logging ──
        //
        // Without a tracer the server logs nothing at all — not even startup — and the last
        // incident was debugged against an empty `kubectl logs`. Stdout is where a pod's logs
        // belong; the cluster's log pipeline takes it from there.
        //
        // At info the server rejects a login without saying why: a 401 with a 300 ms LDAP round
        // trip behind it and not one line about which step refused. debug is a one-shot diagnostic
        // — the switch is not remembered, so the next ordinary apply restores info.
        // reconcile, not upsert: the tracer set becomes exactly this one.
        //
        // An upsert here matches on every property, so raising the level for one apply and lowering
        // it on the next created a SECOND console tracer rather than editing the first — and Stalwart
        // permits only one, rejecting the extra on every start with "Configuration build error …
        // Only one console tracer is allowed". It also leaves the default file tracer enabled,
        // writing to a /var/log path that does not exist in the container, so every start also warns
        // that it cannot create its log file. Replacing the set fixes both and makes the verbose
        // switch idempotent instead of cumulative.
        lines.Add(Op("reconcile", "Tracer", MatchAll, new()
        {
            ["stdout"] = new Dictionary<string, object?>
            {
                ["@type"] = "Stdout",
                ["enable"] = true,
                ["level"] = verboseLogging ? "debug" : "info",
            }
        }));

        // ── Spam filtering ──
        //
        // Exactly one filter decides. Stalwart ships its own, enabled by default, with its own Bayes
        // classifier and a scoreSpam threshold of 5 — so installing rspamd beside it does not replace
        // it, it adds a second opinion, and the two then read each other's headers.
        //
        // What that looked like: every message filed to Junk, and rspamd innocent. rspamd scored a
        // legitimate PGP-signed message 3.00 against its own threshold of 15 and returned "no
        // action"; Stalwart's filter had already scored the same message 6.00, stamped
        // X-Spam-Status: Yes and X-Spam-Result, and filed it. rspamd then saw that header and fired
        // SPAM_FLAG (+5.00) on the rescan — the filters inflating each other. Days were spent on
        // rspamd's Redis and DNS, all of it real and none of it the cause, because the filter making
        // the decision was never rspamd.
        //
        // The mechanism is narrower than "two filters disagree", and knowing which way round it goes
        // decides the fix. SPAM_FLAG is STALWART's tag, and it scores +5 when the message already
        // carries an X-Spam header — a real anti-evasion rule, because spammers do forge
        // "X-Spam-Flag: No". rspamd's milter adds X-Spam-Status at DATA, before the built-in filter
        // runs. So rspamd's own header was triggering Stalwart's verdict: 1.00 became 6.00, crossed
        // scoreSpam of 5, and the message was filed to Junk.
        //
        // Which means the built-in filter is the half to keep, not the half to switch off. It is
        // what files into Junk, and nothing else can: a SieveSystemScript runs at SMTP stages and
        // cannot use fileinto, so only a per-account script could file — and writing one onto every
        // mailbox, including accounts EntKube did not create, is not a mechanism worth having.
        // Disabling it leaves rspamd adding headers that nothing acts on and spam in the inbox.
        //
        // So: enabled, always, and written explicitly rather than left at its default so that a
        // server an earlier version of this plan switched off is repaired by the next apply.
        bool rspamdIsTheFilter =
            config.RspamdEnabled && !string.IsNullOrWhiteSpace(config.RspamdHost);

        lines.Add(Update("SpamSettings", new()
        {
            ["enable"] = true,
        }));

        // ── Tags that cannot mean what they say here ──
        //
        // Two of the filter's rules score the CONNECTION rather than the message, and the connection
        // is not the sender's. EntKube fronts the mail listeners with a Kubernetes LoadBalancer
        // Service, and whether that preserves the client address is the cloud provider's choice, not
        // ours: an OpenStack Octavia load balancer is an HAProxy amphora that proxies and SNATs
        // whatever externalTrafficPolicy says. Every sender on the internet then arrives as one
        // private address.
        //
        // Scored against that address:
        //
        //   VIOLATED_DIRECT_SPF  +3.50  the peer is in nobody's SPF record, because it is ours
        //   RDNS_NONE            +2.00  a private address has no PTR, and never will
        //
        // 5.50 against a scoreSpam of 5, on every message, for ever. A real one measured: a
        // PGP-signed message whose DKIM verified and whose DMARC passed — DKIM_ALLOW and
        // DMARC_POLICY_ALLOW both fired — scored 6.00 and was filed to Junk. Provably authentic mail,
        // rejected on an address that was never the sender's.
        //
        // So they are not scored. What still works is scored, and it is the half that matters:
        // DKIM and DMARC sign and align the MESSAGE, so they survive a rewritten peer address
        // entirely, and the content and Bayes tags never depended on it.
        //
        // Unconditional, not an operator setting, because the operator has no way to know either:
        // the answer depends on a load balancer implementation Kubernetes does not expose. If a
        // deployment ever gains the real client address — PROXY protocol between the balancer and the
        // listener is the only way, and Stalwart would have to accept it — these two come back, and
        // this comment is where to start.
        Dictionary<string, object?> tagScores = [];

        if (proxyNetworks.Count == 0)
        {
            tagScores["violated-direct-spf"] = ZeroScore("VIOLATED_DIRECT_SPF");
            tagScores["rdns-none"] = ZeroScore("RDNS_NONE");
        }
        else
        {
            // The PROXY header carries the sender's own address, so both tags are measuring the
            // sender again and are restored to whatever the rule set scores them. Written back
            // explicitly rather than left out: a deployment that had them suppressed must be repaired
            // by the apply that turns the protocol on, not silently keep ignoring SPF for ever.
            tagScores["violated-direct-spf"] = DefaultScore("VIOLATED_DIRECT_SPF");
            tagScores["rdns-none"] = DefaultScore("RDNS_NONE");
        }

        // And the tag that reacts to an upstream filter's header, while there is one. Scoped to that,
        // because without rspamd the rule is doing real work — spammers forge "X-Spam-Flag: No" — and
        // zeroing it then would hand back the evasion it exists to catch.
        if (rspamdIsTheFilter)
        {
            tagScores["spam-flag"] = ZeroScore("SPAM_FLAG");
        }

        lines.Add(Op("upsert", "SpamTag", MatchOn("tag"), tagScores));

        // ── Customer domains, as far as the filter can be told to trust them ──
        //
        // Deliberately modest, because the mechanism is. Listing a domain here exempts it from DNS
        // block-list checks and nothing else — it would not have stopped the SPF and reverse-DNS
        // score that junked a real customer's support request, and Stalwart has no allow-list that
        // would: that is an open feature request upstream. So this reduces one class of false
        // positive and is not a guarantee, and the guarantee lives where it can actually be made —
        // SupportMailboxService.SweepJunkAsync takes a customer's mail in even from Junk.
        //
        // upsert rather than reconcile: reconcile removes every object in scope, and the scope here
        // would have to be every MemoryLookupKey — including the blocked-domains and spam-trap lists
        // an operator maintains by hand. A domain removed from the register therefore stays trusted
        // until someone deletes it in Stalwart, which is the safe direction to be wrong in.
        if (trustedSenderDomains is { Count: > 0 })
        {
            Dictionary<string, object?> trusted = [];
            int trustedIndex = 0;

            foreach (string domain in trustedSenderDomains
                         .Select(d => d.Trim().ToLowerInvariant())
                         .Where(d => d.Length > 0)
                         .Distinct(StringComparer.Ordinal))
            {
                trusted[$"trusted-{trustedIndex++}"] = new Dictionary<string, object?>
                {
                    ["namespace"] = "trusted-domains",
                    ["key"] = domain,
                };
            }

            lines.Add(Op("upsert", "MemoryLookupKey", MatchOn("namespace", "key"), trusted));
        }

        // reconcile with an empty value map is how "no milter" is expressed: turning rspamd off has
        // to remove the hook, or every message keeps being handed to a filter that is no longer there.
        Dictionary<string, object?> milters = [];
        if (rspamdIsTheFilter)
        {
            milters["rspamd"] = new Dictionary<string, object?>
            {
                ["hostname"] = config.RspamdHost!.Trim(),
                ["port"] = config.RspamdPort,
                ["useTls"] = false,
                ["protocolVersion"] = "v6",
                // DATA is the only stage worth paying for: rspamd's verdict needs the body, and
                // hooking the earlier stages buys a round trip per command for nothing.
                ["stages"] = Set(["data"]),
                ["enable"] = new Dictionary<string, object?> { ["else"] = "true" },
                ["tempFailOnError"] = config.RspamdTempFailOnError,
            };
        }
        lines.Add(Op("reconcile", "MtaMilter", MatchAll, milters));

        // ── Mailboxes ──
        //
        // The administrator is one of these, not a separate concept: Stalwart has no admin account
        // type, only accounts carrying an Admin role. Without one nothing can sign in to the web
        // interface at all — the recovery administrator is honoured only in recovery mode, so a
        // server that has left it has no reachable administrator unless this emits one.
        // Local part or full address — either way the mailbox is the local part in its domain.
        (string LocalPart, string Domain, string Address)? adminIdentity =
            StalwartService.ResolveAdminIdentity(config, domains);
        string adminName = adminIdentity?.LocalPart ?? "";
        StalwartMailDomain? adminDomain = adminIdentity is null
            ? null
            : domains.FirstOrDefault(d => string.Equals(d.Name.Trim(), adminIdentity.Value.Domain, StringComparison.OrdinalIgnoreCase));
        bool wantsAdmin = adminName.Length > 0 && adminDomain is not null;

        if (accounts.Count > 0 || wantsAdmin)
        {
            Dictionary<string, object?> values = [];
            int index = 0;
            bool adminEmitted = false;

            foreach (StalwartMailAccount account in accounts)
            {
                if (!domainRefs.TryGetValue(account.DomainId, out string? domainRef))
                {
                    continue;
                }

                string localPart = account.LocalPart.Trim().ToLowerInvariant();

                // A mailbox that happens to be the administrator is promoted in place rather than
                // emitted twice: two entries sharing a match key in one upsert is ambiguous.
                bool isAdmin = wantsAdmin && localPart == adminName && account.DomainId == adminDomain!.Id;
                adminEmitted |= isAdmin;

                Dictionary<string, object?> body = new()
                {
                    ["@type"] = "User",
                    ["name"] = localPart,
                    ["domainId"] = $"#{domainRef}",
                    ["roles"] = Union(isAdmin ? "Admin" : "User"),
                    ["permissions"] = Union("Inherit"),
                    ["encryptionAtRest"] = Union("Disabled"),
                };

                if (isAdmin)
                {
                    AddAdminCredential(body, config, adminPassword);
                }
                else if (accountPasswords is not null
                         && accountPasswords.TryGetValue(account.Id, out string? accountPassword))
                {
                    // A mailbox EntKube minted a password for — the support mailbox is the case that
                    // needs one, because something has to log in as it unattended. Without this the
                    // account exists and has no credential at all, so every login attempt is answered
                    // with a temporary failure and the password stored on our side is never checked
                    // against anything.
                    AddPasswordCredential(body, config, accountPassword);
                }

                if (!string.IsNullOrWhiteSpace(account.DisplayName))
                {
                    body["description"] = account.DisplayName.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(account.Description))
                {
                    body["description"] = account.Description.Trim();
                }

                string[] aliases = SplitLines(account.Aliases);
                if (aliases.Length > 0)
                {
                    body["aliases"] = List(aliases.Select(a => (object?)new Dictionary<string, object?>
                    {
                        ["name"] = a.ToLowerInvariant(),
                        ["domainId"] = $"#{domainRef}",
                    }));
                }

                values[$"acc-{index++}"] = body;
            }

            if (wantsAdmin && !adminEmitted)
            {
                Dictionary<string, object?> admin = new()
                {
                    ["@type"] = "User",
                    ["name"] = adminName,
                    ["domainId"] = $"#{domainRefs[adminDomain!.Id]}",
                    ["roles"] = Union("Admin"),
                    ["permissions"] = Union("Inherit"),
                    ["encryptionAtRest"] = Union("Disabled"),
                    ["description"] = "EntKube administrator",
                };
                AddAdminCredential(admin, config, adminPassword);
                values["acc-admin"] = admin;
            }

            if (values.Count > 0)
            {
                lines.Add(Op("upsert", "Account", MatchOn("name", "domainId"), values));
            }
        }

        // Emitted here, after the accounts, and that position is load-bearing. apply stops at its
        // first failed operation, so anything that can fail must come after everything a mail server
        // cannot work without. This section failed twice on a live cluster — once on a prefix Stalwart
        // will not parse, once on an address spelling that turned a match into a colliding create —
        // and both times it took the administrator account and every mailbox down with it, because it
        // used to run at operation #12 while the accounts ran at #19. A server that cannot ban an
        // address is worse off than before; a server with no account to sign in as is unusable.
        // ── Auto-ban: make a mistake recoverable, and the gateway un-bannable ──
        //
        // The default ban never expires. An hour is long enough to stop an attacker and short
        // enough that a misconfiguration corrects itself instead of waiting for someone to find
        // the Blocked IPs page in recovery mode.
        lines.Add(Update("Security", new()
        {
            ["authBanPeriod"] = 3_600_000,
        }));

        // is_ip_blocked() is "blocked AND NOT allowed", so an AllowedIp entry is a hard guarantee
        // that an address cannot be banned even when a connection arrives with no usable forwarding
        // header and falls back to the TCP peer.
        //
        // Every internal range, not merely the gateway pods that could be enumerated. Two reasons,
        // and the second is the one that cost a delivery outage.
        //
        // The enumerable half does not stay enumerated: pods move between nodes, and a cluster that
        // autoscales invents node addresses that did not exist when the plan was written. An
        // allow-list built by listing what was running is correct only until the next scale event.
        //
        // And where the load balancer cannot preserve the client address — an OpenStack Octavia
        // amphora proxies and SNATs, whatever externalTrafficPolicy says — every sender on the
        // internet arrives as one internal address. Auto-ban then counts the whole world's failures
        // against a single peer and blocks it, which is not one sender banned but all mail refused
        // at TCP accept, before the filter, with nothing in the inbox and nothing in the spam folder.
        // That happened.
        //
        // The cost is stated rather than hidden: on such a deployment this leaves auto-ban with
        // nothing it can act on for inbound mail. That protection was never real there — the only
        // address it could ever have banned was the operator's own load balancer — so what this
        // removes is a hazard, not a defence. SPF and DMARC are equally meaningless against a
        // SNAT'd peer, and no allow-list can fix that; DKIM still works, because it signs the
        // message rather than trusting the connection.
        //
        // Filtered by what Stalwart will parse, because this operation is emitted before the accounts
        // and the plan stops at its first failure: one address it refuses costs the administrator,
        // every mailbox, the spam settings and the milter, all of which come after. Dropping an entry
        // costs one range. Those are not comparable, so anything doubtful is left out rather than sent.
        Dictionary<string, object?> allowed = [];
        int allowIndex = 0;
        // One entry per canonical address across both sources. Two entries sharing the upsert's match
        // key is ambiguous — the same shape the account loop avoids by promoting the administrator in
        // place rather than emitting it twice — and a gateway address inside a range named here would
        // otherwise produce exactly that.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string range in InternalRanges.Where(IsAcceptableIpOrMask))
        {
            if (seen.Add(CanonicalIpOrMask(range)))
            {
                allowed[$"internal-{allowIndex++}"] = new Dictionary<string, object?>
                {
                    ["address"] = CanonicalIpOrMask(range),
                    ["reason"] = "Inside the cluster — mail and web traffic reach this server through it",
                };
            }
        }
        foreach (string address in (trustedProxyAddresses ?? []).Where(IsAcceptableIpOrMask))
        {
            if (seen.Add(CanonicalIpOrMask(address)))
            {
                allowed[$"proxy-{allowIndex++}"] = new Dictionary<string, object?>
                {
                    ["address"] = CanonicalIpOrMask(address),
                    ["reason"] = "EntKube ingress gateway — banning this address bans every user",
                };
            }
        }
        lines.Add(Op("upsert", "AllowedIp", MatchOn("address"), allowed));

        // reconcile with an empty value set removes every object in scope — the same idiom the
        // milter uses to express "none". Deliberately gated: applying must never quietly lift a
        // ban that is doing its job.
        if (clearBlockedIps)
        {
            lines.Add(Op("reconcile", "BlockedIp", MatchAll, []));
        }

        // ── What the HTTP listeners are allowed to serve, and whose address a request carries ──
        //
        // Deliberately LAST. apply stops at the first failed operation, and this is the one
        // operation in the plan built from an expression the server parses at apply time. When it
        // failed once it aborted the plan after the listeners had been reconciled and before the
        // administrator account, the log tracer, the auto-ban settings and the gateway allow-list
        // had been written — a half-applied server with open ports and no way in. Everything a
        // mail server cannot work without now lands before this line can fail.
        //
        // useXForwarded is the load-bearing setting here. Every web request arrives through the
        // cluster's gateway, so without it the TCP peer Stalwart sees is the gateway pod — for
        // every user. Stalwart's auto-ban counts authentication failures per remote address and
        // blocks that address indefinitely after a hundred of them, so a handful of people
        // mistyping a password bans the gateway, which is to say everyone, with a bare 403 that
        // nothing logs. (It happened.) With the header honoured, the address counted is the real
        // client's.
        lines.Add(Update("Http", new()
        {
            ["allowedEndpoints"] = BuildEndpointPolicy(config),
            ["useXForwarded"] = true,
        }));

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Gives the administrator account a password, but only when Stalwart is the thing checking it.
    ///
    /// <para>In LDAP or OIDC mode the credential belongs to the directory: authentication is routed
    /// there, and an internally-stored password would be a second copy that drifts the moment the
    /// directory's changes. It also means the administrator in those modes has to be a real
    /// directory user — an account here with no credential is the local half of that identity,
    /// carrying the Admin role that the directory has no way to express.</para>
    /// </summary>
    private static void AddAdminCredential(
        Dictionary<string, object?> account, StalwartComponentConfig config, string? adminPassword) =>
        AddPasswordCredential(account, config, adminPassword);

    /// <summary>
    /// Gives an account a password, on the same terms and for the same reason as the administrator's.
    ///
    /// <para>Internal mode only, because that is the only mode where Stalwart is the thing checking.
    /// An LDAP or OIDC directory serves every protocol and both credential types, and a password
    /// written here would never be consulted — so emitting one would produce an account that looks
    /// credentialed and cannot log in, which is worse than one that plainly has no password.</para>
    /// </summary>
    private static void AddPasswordCredential(
        Dictionary<string, object?> account, StalwartComponentConfig config, string? password)
    {
        if (config.AuthMode != StalwartAuthMode.Internal || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        account["credentials"] = List([new Dictionary<string, object?>
        {
            ["@type"] = "Password",
            ["secret"] = password,
        }]);
    }

    /// <summary>
    /// Emits the directory operation and returns its client id, or null when logins are served by
    /// Stalwart's own account store.
    /// </summary>
    private static string? BuildDirectory(StalwartComponentConfig config, List<string> lines)
    {
        switch (config.AuthMode)
        {
            case StalwartAuthMode.Ldap when !string.IsNullOrWhiteSpace(config.LdapUrl)
                                            && !string.IsNullOrWhiteSpace(config.LdapBaseDn):
            {
                Dictionary<string, object?> body = new()
                {
                    ["@type"] = "Ldap",
                    ["description"] = "EntKube LDAP directory",
                    ["url"] = config.LdapUrl!.Trim(),
                    ["baseDn"] = config.LdapBaseDn!.Trim(),
                    ["useTls"] = config.LdapUseTls,
                    ["allowInvalidCerts"] = config.LdapAllowInvalidCerts,
                    ["bindAuthentication"] = true,
                    ["filterLogin"] = config.LdapLoginFilter,
                    ["filterMailbox"] = config.LdapMailboxFilter,
                    ["filterMemberOf"] = config.LdapMemberOfFilter,
                    // The bind password never enters the plan. It reaches the server as an
                    // environment variable sourced from the component's Kubernetes Secret, so the
                    // plan can be logged, diffed and stored without leaking a directory credential.
                    ["bindSecret"] = new Dictionary<string, object?>
                    {
                        ["@type"] = "EnvironmentVariable",
                        ["variableName"] = LdapBindPasswordEnv,
                    },
                };

                if (!string.IsNullOrWhiteSpace(config.LdapBindDn))
                {
                    body["bindDn"] = config.LdapBindDn!.Trim();
                }

                lines.Add(Op("upsert", "Directory", MatchOn("description"), new() { ["dir"] = body }));
                return "dir";
            }

            case StalwartAuthMode.Oidc when !string.IsNullOrWhiteSpace(config.OidcIssuerUrl):
            {
                Dictionary<string, object?> body = new()
                {
                    ["@type"] = "Oidc",
                    ["description"] = "EntKube OIDC directory",
                    ["issuerUrl"] = config.OidcIssuerUrl!.Trim().TrimEnd('/'),
                    ["claimUsername"] = config.OidcClaimUsername,
                    ["requireScopes"] = Set(SplitScopes(config.OidcRequireScopes)),
                };

                if (!string.IsNullOrWhiteSpace(config.OidcUsernameDomain))
                {
                    body["usernameDomain"] = config.OidcUsernameDomain!.Trim();
                }
                if (!string.IsNullOrWhiteSpace(config.OidcClaimGroups))
                {
                    body["claimGroups"] = config.OidcClaimGroups!.Trim();
                }
                if (!string.IsNullOrWhiteSpace(config.OidcRequireAudience))
                {
                    body["requireAudience"] = config.OidcRequireAudience!.Trim();
                }

                lines.Add(Op("upsert", "Directory", MatchOn("description"), new() { ["dir"] = body }));
                return "dir";
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The <c>Http.allowedEndpoints</c> expression: which requests each HTTP listener will answer.
    ///
    /// <para>This is what makes opening ports 80 and 443 on the public mail address safe. Those
    /// listeners exist only for requests that are public by nature — a CA completing a challenge, a
    /// client asking where to find its mailbox, a sending server fetching an MTA-STS policy — and
    /// Stalwart's endpoint policy is global rather than per-listener, so without this, publishing
    /// them would put the admin interface and JMAP on a public address as well. The rules allow the
    /// public paths, refuse everything else on those listeners by name, and leave every other
    /// listener untouched.</para>
    ///
    /// <para>Written as separate match arms rather than one boolean expression on purpose: each arm
    /// is a single comparison, so there is no operator precedence to get subtly wrong in a rule
    /// whose failure mode is an exposed admin interface.</para>
    /// </summary>
    private static Dictionary<string, object?> BuildEndpointPolicy(StalwartComponentConfig config)
    {
        // Every listener published on the mail LoadBalancer's web ports. Whatever is not explicitly
        // allowed below is refused on each of them by name — an allow-list keyed on the listener,
        // never on "everything else", because this expression gates every HTTP request the server
        // serves and the failure mode of getting it wrong is a bare Forbidden on the admin UI.
        List<string> publicListeners = [];
        if (StalwartManifestBuilder.NeedsAcmeHttpPort(config))
        {
            publicListeners.Add(StalwartManifestBuilder.AcmeHttpListener);
        }
        if (StalwartManifestBuilder.NeedsPublicWebPort(config))
        {
            publicListeners.Add(StalwartManifestBuilder.PublicWebListener);
        }

        if (publicListeners.Count == 0)
        {
            // Nothing is published on the mail address, so there is nothing to restrict: write the
            // documented default — {"else": "200"}, no match key — and nothing else. Two earlier
            // attempts here invented shapes (an empty match set, then an always-true arm) for a
            // global setting that gates every HTTP request on the server; the only shape with
            // evidence behind it is the one the reference lists as the default. Written rather than
            // skipped so that unpublishing repairs a deployment that had the restrictive form.
            return new Dictionary<string, object?> { ["else"] = "200" };
        }

        // `path` is the HttpVariable for the request path. The access-control doc page shows
        // `url_path` in its example and the server rejects it with "Invalid variable or
        // constant" — which aborted a real apply at this very line. HttpVariable is the
        // authoritative list; the test below pins every identifier used here to it.
        //
        // Two kinds of request are safe to answer on a public mail address and one is not: the ACME
        // challenge and the client-autodiscovery endpoints are public by nature, while the admin UI
        // and JMAP must never be reachable there. So allow exactly the public paths and refuse the
        // rest on those listeners — /.well-known/ covers the ACME challenge, MTA-STS and PACC; the
        // other two are Thunderbird's and Outlook's fixed paths (Outlook varies the case).
        List<object?> arms = [];
        foreach (string prefix in PublicHttpPaths)
        {
            arms.Add(new Dictionary<string, object?>
            {
                ["if"] = $"starts_with(path, '{prefix}')",
                ["then"] = "200",
            });
        }
        foreach (string listener in publicListeners)
        {
            arms.Add(new Dictionary<string, object?>
            {
                ["if"] = $"listener == '{listener}'",
                ["then"] = "403",
            });
        }

        return new Dictionary<string, object?>
        {
            ["match"] = List(arms),
            ["else"] = "200",
        };
    }

    /// <summary>
    /// The request paths that are safe to answer on the public mail address, taken from the server's
    /// own router (<c>crates/http/src/request.rs</c>) rather than from what a client is expected to
    /// ask for:
    ///
    /// <list type="bullet">
    /// <item><c>/.well-known/</c> — the ACME challenge, MTA-STS, PACC
    /// (<c>user-agent-configuration.json</c>), <c>mail-v1.xml</c>, the nested autoconfig path and
    /// the OAuth/OpenID discovery documents, which are public metadata by definition. The
    /// <c>jmap</c>, <c>caldav</c> and <c>carddav</c> entries under it are redirects, and the paths
    /// they redirect TO are not on this list — so JMAP stays refused on a public address.</item>
    /// <item><c>/mail/config</c> — Thunderbird's <c>config-v1.1.xml</c>.</item>
    /// <item>Outlook's autodiscover path in <b>all three</b> casings the router matches. It accepts
    /// <c>autodiscover</c>, <c>Autodiscover</c> and <c>AutoDiscover</c>; allowing only the first two
    /// leaves the clients that send the third refused by the listener that exists to serve
    /// them.</item>
    /// </list>
    ///
    /// Everything else on those listeners — the admin UI, JMAP — is refused.
    /// </summary>
    private static readonly string[] PublicHttpPaths =
        ["/.well-known/", "/mail/config", "/autodiscover/", "/Autodiscover/", "/AutoDiscover/"];

    /// <summary>The listener set implied by the enabled protocols. Keyed by listener name.</summary>
    private static Dictionary<string, object?> BuildListeners(StalwartComponentConfig config)
    {
        Dictionary<string, object?> listeners = [];

        void Add(string name, int port, string protocol, bool implicitTls, bool useTls = true)
        {
            listeners[name] = new Dictionary<string, object?>
            {
                ["name"] = name,
                // [::] is dual-stack on Linux, so one bind covers IPv4 and IPv6 rather than
                // quietly serving only one of them.
                ["bind"] = Set([$"[::]:{port}"]),
                ["protocol"] = protocol,
                ["useTls"] = useTls,
                ["tlsImplicit"] = implicitTls,
            };
        }

        if (config.SmtpEnabled)
        {
            Add("smtp", MailPorts.Smtp, "smtp", implicitTls: false);
        }
        if (config.SubmissionEnabled)
        {
            Add("submission", MailPorts.Submission, "smtp", implicitTls: false);
            Add("submissions", MailPorts.Submissions, "smtp", implicitTls: true);
        }
        if (config.ImapEnabled)
        {
            Add("imap", MailPorts.Imap, "imap", implicitTls: false);
            Add("imaps", MailPorts.Imaps, "imap", implicitTls: true);
        }
        if (config.Pop3Enabled)
        {
            Add("pop3", MailPorts.Pop3, "pop3", implicitTls: false);
            Add("pop3s", MailPorts.Pop3s, "pop3", implicitTls: true);
        }
        if (config.ManageSieveEnabled)
        {
            Add("sieve", MailPorts.ManageSieve, "manageSieve", implicitTls: false);
        }

        // TLS for the web surfaces is terminated at the cluster's gateway, so this listener speaks
        // cleartext inside the mesh. Giving it a certificate as well would mean maintaining a
        // second one for a hop nothing outside the cluster can reach.
        Add("http", HttpPort, "http", implicitTls: false, useTls: false);

        // Stalwart answers its own ACME challenge, so in HTTP-01 mode the CA has to be able to reach
        // it on port 80. That listener carries nothing else — see BuildEndpointPolicy.
        if (StalwartManifestBuilder.NeedsAcmeHttpPort(config))
        {
            Add(StalwartManifestBuilder.AcmeHttpListener, MailPorts.AcmeHttp, "http",
                implicitTls: false, useTls: false);
        }

        // HTTPS on the mail address, in every TLS mode. This is what answers the lookups a client
        // makes before it has any settings — autoconfig, autodiscover, MTA-STS, PACC — on the names
        // that belong to the mail service rather than to the admin UI. In ACME TLS-ALPN-01 mode the
        // CA's handshake arrives here too, which needs no extra binding: the challenge is answered
        // during the handshake via the acme-tls/1 ALPN protocol, before any HTTP request exists.
        if (StalwartManifestBuilder.NeedsPublicWebPort(config))
        {
            Add(StalwartManifestBuilder.PublicWebListener, MailPorts.PublicWeb, "http",
                implicitTls: true, useTls: true);
        }

        return listeners;
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    /// <summary>Match on every property — the natural key for objects that carry no label field.</summary>
    private const string MatchAll = "*";

    /// <summary>The properties forming an object's match key.</summary>
    private static string[] MatchOn(params string[] keys) => keys;

    private static string Op(string type, string obj, object matchOn, Dictionary<string, object?> values)
    {
        Dictionary<string, object?> op = new()
        {
            ["@type"] = type,
            ["object"] = obj,
            ["matchOn"] = matchOn,
            ["value"] = values,
        };
        return JsonSerializer.Serialize(op, Compact);
    }

    private static string Update(string obj, Dictionary<string, object?> value)
    {
        Dictionary<string, object?> op = new()
        {
            ["@type"] = "update",
            ["object"] = obj,
            ["value"] = value,
        };
        return JsonSerializer.Serialize(op, Compact);
    }

    /// <summary>Encodes a Stalwart <c>Set&lt;T&gt;</c>: an object mapping each member to true.</summary>
    private static Dictionary<string, object?> Set(IEnumerable<string> members)
    {
        Dictionary<string, object?> set = [];
        foreach (string member in members)
        {
            set[member] = true;
        }
        return set;
    }

    /// <summary>Encodes a Stalwart <c>List&lt;T&gt;</c>: an object keyed by stringified indices.</summary>
    private static Dictionary<string, object?> List(IEnumerable<object?> items)
    {
        Dictionary<string, object?> list = [];
        int index = 0;
        foreach (object? item in items)
        {
            list[index.ToString()] = item;
            index++;
        }
        return list;
    }

    /// <summary>A discriminated-union variant that carries no properties of its own.</summary>
    private static Dictionary<string, object?> Union(string type) => new() { ["@type"] = type };

    internal static string[] SplitScopes(string? scopes) =>
        (scopes ?? "").Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static string[] SplitLines(string? text) =>
        (text ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── DNS ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The host prefixes a mail client or a sending server looks up before it has any settings, and
    /// what each one is for. One list, used three ways: the DNS records an operator is told to
    /// publish, the names on the mail certificate, and — in ACME mode — the names Stalwart orders
    /// for itself. They must agree, because each of these lookups is an HTTPS request that validates
    /// the name it asked for.
    /// </summary>
    public static readonly (string Prefix, string Purpose)[] AutodiscoveryHosts =
    [
        ("autoconfig", "Thunderbird / Apple Mail autoconfig."),
        ("autodiscover", "Outlook autodiscover."),
        ("mta-sts", "MTA-STS policy host — how a sending server learns this domain requires TLS."),
        ("ua-auto-config", "PACC (RFC-standard client autoconfig)."),
    ];

    /// <summary>
    /// Every name the mail certificate has to carry: the server's own hostname, plus the
    /// autodiscovery names of each served domain. Deduplicated and lower-cased, hostname first, so
    /// the common name is also the first SAN.
    /// </summary>
    public static IReadOnlyList<string> CertificateNames(
        string hostname, IReadOnlyList<StalwartMailDomain> domains)
    {
        List<string> names = [hostname.Trim().TrimEnd('.').ToLowerInvariant()];

        foreach (StalwartMailDomain domain in domains)
        {
            string name = domain.Name.Trim().TrimEnd('.').ToLowerInvariant();
            if (name.Length == 0)
            {
                continue;
            }
            foreach ((string prefix, _) in AutodiscoveryHosts)
            {
                names.Add($"{prefix}.{name}");
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One DNS record an operator has to publish for a domain to send and receive mail.</summary>
    /// <param name="Name">Record name, relative to the zone where that is natural.</param>
    /// <param name="Type">Record type (MX, TXT, CNAME…).</param>
    /// <param name="Value">Record value.</param>
    /// <param name="Note">Why it is needed.</param>
    public sealed record DnsRecord(string Name, string Type, string Value, string Note);

    /// <summary>
    /// The DNS a domain needs before mail works. Deliberately shown rather than published: the zone
    /// almost never lives anywhere EntKube can reach, and mail that half-works because one record is
    /// missing is the most expensive kind of mail misconfiguration.
    ///
    /// <para><b>DKIM is here now, and it is the record most likely to be missing.</b> It used to be
    /// left out with a note saying to read it from the mail server's admin UI, which is true and is
    /// also how a domain ends up signing every message with a key nobody published. That failure is
    /// silent and total — the receiver looks the key up, finds nothing, and
    /// <c>dkim=fail reason="key not found in DNS"</c> takes DMARC down with it. Stalwart still owns
    /// the keys; what the domain carries is a copy of the records to publish, pasted once, so that
    /// the list handed to whoever runs the zone is the whole list.</para>
    /// </summary>
    public static IReadOnlyList<DnsRecord> DnsRecordsFor(StalwartComponentConfig config, StalwartMailDomain domain)
    {
        string host = config.Hostname.Trim().TrimEnd('.');
        string name = domain.Name.Trim().TrimEnd('.');

        List<DnsRecord> records =
        [
            new(name, "MX", $"10 {host}.", "Where other servers deliver mail for this domain."),
            new(name, "TXT", Spf(config),
                "SPF — authorises this server to send for the domain and refuses everything else. "
                + "`mx` covers the address mail is delivered TO; outbound usually leaves from "
                + "another one, which has to be listed or every message fails SPF."),
            new($"_dmarc.{name}", "TXT", $"v=DMARC1; p=none; rua=mailto:postmaster@{name}",
                "DMARC — tells receivers what to do with mail that fails SPF and DKIM. Start at "
                + "p=none and move to p=reject once a report shows both passing: p=reject while "
                + "either is unpublished junks every message the domain sends."),

            // The mail host's own A record, which the list never had — it is what the MX
            // above points at and what other servers connect IN on.
            new(host, "A", config.LoadBalancerIp ?? "the mail LoadBalancer address",
                "The mail host itself, where mail is delivered TO. It must resolve to the "
                + "balancer's address, which is a different thing from the address outbound "
                + "mail leaves from — see the sending rows below."),
        ];

        records.AddRange(DkimRecords(domain));

        // The sending address's own name, and its reverse. SPF already lists the address
        // (Spf above), which says we are allowed to send from it — and says nothing about
        // the two records a receiver checks before it will take the connection at all: that
        // the address has a PTR, and that the name the PTR gives resolves back to it.
        //
        // It needs a name of its own. The mail host cannot serve: it has to resolve to the
        // balancer so other servers can deliver, so it will never resolve back to the
        // sending address. One name for all of them is fine — each address's PTR names it,
        // and it resolves to the set, which contains that address.
        IReadOnlyList<string> sending = SendingAddressesOf(config);

        if (sending.Count > 0)
        {
            string sendingHost = $"smtp-out.{name}";

            records.Add(new(sendingHost, "A", string.Join(", ", sending),
                "A name for the address outbound mail leaves from. Its own name rather than "
                + "the mail host, which has to point at the balancer and cannot point at "
                + "both."));

            foreach (string address in sending)
            {
                records.Add(new(
                    $"{Reverse(address)} (not in this zone)", "PTR", $"{sendingHost}.",
                    "Reverse DNS for the sending address, published by whoever owns it — the "
                    + "cloud provider, not here, and usually a console field or a support "
                    + "request. Gmail and Microsoft refuse mail from an address with no PTR "
                    + "outright, before anything about the message is considered, and both "
                    + "check that the name it gives resolves back to the same address."));
            }
        }

        // Client autodiscovery (autoconfig / autodiscover / MTA-STS / PACC) always points at the
        // mail host, in every TLS mode. These are names of the MAIL service: Stalwart answers them
        // itself, on the public web listener of its own address, and the certificate on that address
        // carries them — its own ACME order in ACME mode, the cert-manager Certificate otherwise.
        //
        // They used to point at the gateway's web host in cert-manager mode, which made client
        // auto-configuration and MTA-STS depend on the admin UI being published — so a deployment
        // that kept the admin interface internal had neither, and the records were omitted entirely
        // when no web host was set. (In ACME mode, pointing them at the gateway also failed the
        // certificate order for every name that did not resolve to the mail host, which is what
        // exhausted a Let's Encrypt rate limit here.)
        foreach ((string prefix, string note) in AutodiscoveryHosts)
        {
            records.Add(new($"{prefix}.{name}", "CNAME", $"{host}.",
                note + " Answered by the mail server on its own address, over HTTPS — so this must "
                + "resolve to the mail host, whose certificate covers the name."));
        }

        return records;
    }

    /// <summary>
    /// The SPF value: the MX hosts, plus every address the server actually sends from.
    ///
    /// <para><c>mx</c> alone is the common recommendation and it is wrong for a mail server in a
    /// cluster. It authorises what the MX records resolve to — the address other servers deliver
    /// <em>to</em> — while outbound connections leave translated to a node's or a gateway's address.
    /// The receiver evaluates SPF against an address no term matches and <c>-all</c> makes that a
    /// hard fail.</para>
    /// </summary>
    /// <summary>
    /// An address as its reverse-lookup name, which is the form the record is published
    /// under. Shown rather than the bare address because somebody has to hand this to a
    /// provider, and the address itself is not what goes in the request.
    /// </summary>
    private static string Reverse(string address) =>
        System.Net.IPAddress.TryParse(address.Trim(), out System.Net.IPAddress? ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? string.Join('.', ip.ToString().Split('.').Reverse()) + ".in-addr.arpa"
            : address.Trim();

    public static string Spf(StalwartComponentConfig config)
    {
        IEnumerable<string> terms = SendingAddressesOf(config)
            .Select(a => a.Contains(':', StringComparison.Ordinal) ? $"ip6:{a}" : $"ip4:{a}");

        return string.Join(' ', ["v=spf1", "mx", .. terms, "-all"]);
    }

    /// <summary>The addresses an operator recorded as this server's outbound ones.</summary>
    public static IReadOnlyList<string> SendingAddressesOf(StalwartComponentConfig config) =>
        [.. (config.SendingIpAddresses ?? "")
            .Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries
                                                  | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The DKIM records the operator copied off the mail server, one per line as
    /// <c>selector value</c> or <c>selector: value</c>.
    ///
    /// <para>A line already naming <c>_domainkey</c> is taken as a complete record name and left
    /// alone — that is what the server's own DNS page prints, and retyping it is where a selector
    /// gets mangled.</para>
    /// </summary>
    public static IReadOnlyList<DnsRecord> DkimRecords(StalwartMailDomain domain)
    {
        string name = domain.Name.Trim().TrimEnd('.');

        List<DnsRecord> records = [];

        foreach (string line in (domain.DkimDnsRecords ?? "")
                 .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = line.IndexOfAny([' ', '\t', ':']);

            if (split <= 0)
            {
                continue;
            }

            string selector = line[..split].Trim().TrimEnd(':');
            string value = line[(split + 1)..].Trim().TrimStart(':').Trim();

            if (selector.Length == 0 || value.Length == 0)
            {
                continue;
            }

            records.Add(new(
                selector.Contains("_domainkey", StringComparison.OrdinalIgnoreCase)
                    ? selector
                    : $"{selector}._domainkey.{name}",
                "TXT",
                value,
                "DKIM — the public half of a key this server signs with. Unpublished, every "
                + "signature fails as \"key not found in DNS\" and takes DMARC with it."));
        }

        return records;
    }
}

/// <summary>The ports a mail server listens on. Named because 465 and 587 are not interchangeable.</summary>
public static class MailPorts
{
    /// <summary>Server-to-server delivery. STARTTLS, never implicit TLS.</summary>
    public const int Smtp = 25;

    /// <summary>Client submission over STARTTLS.</summary>
    public const int Submission = 587;

    /// <summary>Client submission over implicit TLS.</summary>
    public const int Submissions = 465;

    public const int Imap = 143;
    public const int Imaps = 993;
    public const int Pop3 = 110;
    public const int Pop3s = 995;

    /// <summary>ManageSieve — server-side mail filters.</summary>
    public const int ManageSieve = 4190;

    /// <summary>Where a CA looks for an HTTP-01 challenge response. Not negotiable.</summary>
    public const int AcmeHttp = 80;

    /// <summary>
    /// HTTPS on the mail address. Serves the endpoints a mail client looks up before it has any
    /// settings — autoconfig, autodiscover, MTA-STS, PACC — and, in ACME TLS-ALPN-01 mode, the
    /// handshake the CA opens. Both are fixed by the protocols, so this is not negotiable either.
    /// </summary>
    public const int PublicWeb = 443;
}
