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
    /// Coordinator / in-memory Redis URL, e.g. <c>redis://redis.redis.svc:6379</c>. Stalwart's
    /// standalone-Redis store has no separate secret field — unlike the datastore and blob store —
    /// so a password, when there is one, is carried inline in this URL
    /// (<c>redis://:password@host:port</c>). That means it is visible in the applied plan; a
    /// network-isolated Redis with no password is the cleaner choice for an in-cluster coordinator.
    /// </param>
    /// <param name="Replicas">Node count.</param>
    public sealed record StalwartHaBackend(
        string DbHost, int DbPort, string DbName, string DbUser,
        string S3Endpoint, string S3Region, string S3Bucket, string S3AccessKey,
        string RedisUrl, int Replicas);

    /// <summary>The HTTP port serving JMAP, the admin UI, autoconfig and the OAuth endpoints.</summary>
    public const int HttpPort = 8080;

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
        StalwartHaBackend? ha = null)
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
            lines.Add(Op("upsert", "Certificate", MatchAll, new()
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

            // Coordinator + in-memory store: both the standalone Redis. The Redis store struct has
            // no secret field (only the cluster variant does), so there is no authSecret here — a
            // password lives inside the URL. Pointing the in-memory store (rate limits, caches,
            // ephemeral cluster state) at the same Redis keeps that hot-path state off the shared
            // PostgreSQL, which it would otherwise default to.
            lines.Add(Update("Coordinator", new()
            {
                ["@type"] = "Redis",
                ["url"] = ha.RedisUrl,
            }));
            lines.Add(Update("InMemoryStore", new()
            {
                ["@type"] = "Redis",
                ["url"] = ha.RedisUrl,
            }));

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
        lines.Add(Update("SystemSettings", system));

        // ── Listeners ──
        lines.Add(Op("reconcile", "NetworkListener", MatchOn("name"), BuildListeners(config)));

        // ── Auto-ban: make a mistake recoverable, and the gateway un-bannable ──
        //
        // The default ban never expires. An hour is long enough to stop an attacker and short
        // enough that a misconfiguration corrects itself instead of waiting for someone to find
        // the Blocked IPs page in recovery mode.
        lines.Add(Update("Security", new()
        {
            ["authBanPeriod"] = 3_600_000,
        }));

        // is_ip_blocked() is "blocked AND NOT allowed", so an AllowedIp entry for the gateway is
        // a hard guarantee that it cannot be banned even when a request arrives without a usable
        // forwarding header and falls back to the TCP peer. Belt to useXForwarded's braces.
        if (trustedProxyAddresses is { Count: > 0 })
        {
            Dictionary<string, object?> allowed = [];
            int i = 0;
            foreach (string address in trustedProxyAddresses.Distinct(StringComparer.Ordinal))
            {
                allowed[$"proxy-{i++}"] = new Dictionary<string, object?>
                {
                    ["address"] = address,
                    ["reason"] = "EntKube ingress gateway — banning this address bans every user",
                };
            }
            lines.Add(Op("upsert", "AllowedIp", MatchOn("address"), allowed));
        }

        // reconcile with an empty value set removes every object in scope — the same idiom the
        // milter uses to express "none". Deliberately gated: applying must never quietly lift a
        // ban that is doing its job.
        if (clearBlockedIps)
        {
            lines.Add(Op("reconcile", "BlockedIp", MatchAll, []));
        }

        // ── Logging ──
        //
        // Without a tracer the server logs nothing at all — not even startup — and the last
        // incident was debugged against an empty `kubectl logs`. Stdout is where a pod's logs
        // belong; the cluster's log pipeline takes it from there.
        //
        // At info the server rejects a login without saying why: a 401 with a 300 ms LDAP round
        // trip behind it and not one line about which step refused. debug is a one-shot diagnostic
        // — the switch is not remembered, so the next ordinary apply restores info.
        lines.Add(Op("upsert", "Tracer", MatchAll, new()
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
        // reconcile with an empty value map is how "no milter" is expressed: turning rspamd off has
        // to remove the hook, or every message keeps being handed to a filter that is no longer there.
        Dictionary<string, object?> milters = [];
        if (config.RspamdEnabled && !string.IsNullOrWhiteSpace(config.RspamdHost))
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
        Dictionary<string, object?> account, StalwartComponentConfig config, string? adminPassword)
    {
        if (config.AuthMode != StalwartAuthMode.Internal || string.IsNullOrWhiteSpace(adminPassword))
        {
            return;
        }

        account["credentials"] = List([new Dictionary<string, object?>
        {
            ["@type"] = "Password",
            ["secret"] = adminPassword,
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
    /// listeners exist only so the CA can complete a challenge, and Stalwart's endpoint policy is
    /// global rather than per-listener — so without this, publishing port 80 would put the admin
    /// interface and JMAP on a public address in cleartext. The rules deny everything on the ACME
    /// listeners except the challenge path itself, and leave every other listener untouched.</para>
    ///
    /// <para>Written as separate match arms rather than one boolean expression on purpose: each arm
    /// is a single comparison, so there is no operator precedence to get subtly wrong in a rule
    /// whose failure mode is an exposed admin interface.</para>
    /// </summary>
    private static Dictionary<string, object?> BuildEndpointPolicy(StalwartComponentConfig config)
    {
        List<object?> arms = [];

        if (StalwartManifestBuilder.NeedsAcmeHttpPort(config) || StalwartManifestBuilder.NeedsAcmeTlsPort(config))
        {
            // `path` is the HttpVariable for the request path. The access-control doc page shows
            // `url_path` in its example and the server rejects it with "Invalid variable or
            // constant" — which aborted a real apply at this very line. HttpVariable is the
            // authoritative list; the test below pins every identifier used here to it.
            // The mail LoadBalancer's HTTP(S) ports are public. Three kinds of request are safe to
            // answer there and one is not: the ACME challenge and the client-autodiscovery
            // endpoints are public by nature, while the admin UI and JMAP must never be reachable on
            // a public address. So allow exactly the public paths and 403 the rest of the ACME
            // listeners — /.well-known/ covers the ACME challenge, MTA-STS and PACC; the other two
            // are Thunderbird's and Outlook's fixed paths (Outlook varies the case).
            foreach (string prefix in PublicHttpPaths)
            {
                arms.Add(new Dictionary<string, object?>
                {
                    ["if"] = $"starts_with(path, '{prefix}')",
                    ["then"] = "200",
                });
            }
            arms.Add(new Dictionary<string, object?>
            {
                ["if"] = $"listener == '{StalwartManifestBuilder.AcmeHttpListener}'",
                ["then"] = "403",
            });
            arms.Add(new Dictionary<string, object?>
            {
                ["if"] = $"listener == '{StalwartManifestBuilder.AcmeTlsListener}'",
                ["then"] = "403",
            });
        }
        else
        {
            // Nothing to restrict, so write the documented default — {"else": "200"}, no match key —
            // and nothing else. Two earlier attempts here invented shapes (an empty match set, then
            // an always-true arm) for a global setting that gates every HTTP request on the server;
            // the only shape with evidence behind it is the one the reference lists as the default.
            // Written rather than skipped so that turning ACME off repairs a deployment that had
            // the restrictive form applied.
            return new Dictionary<string, object?> { ["else"] = "200" };
        }

        return new Dictionary<string, object?>
        {
            ["match"] = List(arms),
            ["else"] = "200",
        };
    }

    /// <summary>Path prefix a CA fetches an HTTP-01 challenge response from.</summary>
    private const string AcmeChallengePath = "/.well-known/acme-challenge";

    /// <summary>
    /// The request paths that are safe to answer on the public mail address: everything under
    /// <c>/.well-known/</c> (ACME challenge, MTA-STS, PACC), Thunderbird's autoconfig path, and
    /// Outlook's autodiscover path in both the casings it uses. Everything else on those listeners —
    /// the admin UI, JMAP — is refused.
    /// </summary>
    private static readonly string[] PublicHttpPaths =
        ["/.well-known/", "/mail/config", "/autodiscover/", "/Autodiscover/"];

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

        // Stalwart answers its own ACME challenge, so in ACME mode the CA has to be able to reach
        // it on the port that challenge type uses. These carry nothing else — see BuildEndpointPolicy.
        if (StalwartManifestBuilder.NeedsAcmeHttpPort(config))
        {
            Add(StalwartManifestBuilder.AcmeHttpListener, MailPorts.AcmeHttp, "http",
                implicitTls: false, useTls: false);
        }
        if (StalwartManifestBuilder.NeedsAcmeTlsPort(config))
        {
            // The challenge is answered during the handshake via the acme-tls/1 ALPN protocol, so
            // this listener never has to serve an HTTP request to do its job.
            Add(StalwartManifestBuilder.AcmeTlsListener, MailPorts.AcmeTls, "http",
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
    /// <para>DKIM is absent by design — Stalwart generates those keys itself, and the selector and
    /// public key only exist once the domain has been applied, so they are read back from the
    /// running server rather than predicted here.</para>
    /// </summary>
    public static IReadOnlyList<DnsRecord> DnsRecordsFor(StalwartComponentConfig config, StalwartMailDomain domain)
    {
        string host = config.Hostname.Trim().TrimEnd('.');
        string name = domain.Name.Trim().TrimEnd('.');

        List<DnsRecord> records =
        [
            new(name, "MX", $"10 {host}.", "Where other servers deliver mail for this domain."),
            new(name, "TXT", $"v=spf1 mx -all",
                "SPF — authorises this server to send for the domain and refuses everything else."),
            new($"_dmarc.{name}", "TXT", $"v=DMARC1; p=reject; rua=mailto:postmaster@{name}",
                "DMARC — tells receivers what to do with mail that fails SPF and DKIM."),
        ];

        // Client autodiscovery (autoconfig / autodiscover / MTA-STS / PACC). Where these point
        // depends on who serves and certifies them, which the TLS mode decides:
        //
        //  • ACME mode: Stalwart serves them on the mail address and its own ACME certificate
        //    covers those names — so they MUST resolve to the mail host, or the certificate order
        //    fails for every one that does not (which is exactly what exhausted a Let's Encrypt
        //    rate limit here: the records pointed at the gateway and the order kept failing).
        //  • cert-manager mode: Stalwart runs no ACME; these are served through the cluster gateway,
        //    so they point at the web host. Publishing routes + a certificate for them there is not
        //    yet automatic, so in this mode they are optional client-convenience records.
        bool acme = config.TlsMode == StalwartTlsMode.Acme;
        string target = acme
            ? host
            : (string.IsNullOrWhiteSpace(config.AdminHostname) ? host : config.AdminHostname!.Trim().TrimEnd('.'));
        string note = acme
            ? "Required in ACME TLS mode — the mail server's certificate covers this name, so it must resolve to the mail host."
            : "Optional — client auto-configuration, served through the gateway.";

        // In cert-manager mode with no web host there is nowhere sensible to point these, so skip them.
        if (acme || !string.IsNullOrWhiteSpace(config.AdminHostname))
        {
            records.Add(new($"autoconfig.{name}", "CNAME", $"{target}.", "Thunderbird / Apple Mail autoconfig. " + note));
            records.Add(new($"autodiscover.{name}", "CNAME", $"{target}.", "Outlook autodiscover. " + note));
            records.Add(new($"mta-sts.{name}", "CNAME", $"{target}.", "MTA-STS policy host. " + note));
            records.Add(new($"ua-auto-config.{name}", "CNAME", $"{target}.", "PACC (RFC-standard client autoconfig). " + note));
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

    /// <summary>Where a CA opens the TLS-ALPN-01 handshake. Also not negotiable.</summary>
    public const int AcmeTls = 443;
}
