namespace EntKube.Web.Data;

/// <summary>
/// Which directory Stalwart authenticates mailbox logins against.
///
/// Stalwart's <c>Authentication</c> singleton carries exactly ONE <c>directoryId</c> in the
/// community edition (per-domain directories are an enterprise feature), so these modes are
/// mutually exclusive — a deployment cannot have LDAP passwords and Keycloak tokens at the
/// same time. Which one to pick is a real decision, not a detail:
/// see <see cref="StalwartComponentConfig.AuthMode"/>.
/// </summary>
public enum StalwartAuthMode
{
    /// <summary>Stalwart's own account store. Passwords live in Stalwart; no external directory.</summary>
    Internal = 0,

    /// <summary>
    /// Accounts and passwords come from OpenLDAP. Every mail client works (plain
    /// username/password over IMAP/SMTP), and mailboxes appear as soon as the LDAP entry does.
    /// Webmail signs in with the same LDAP password — there is no interactive SSO.
    /// </summary>
    Ldap = 1,

    /// <summary>
    /// Logins are Keycloak-issued OIDC access tokens, validated by Stalwart over
    /// <c>OAUTHBEARER</c>/<c>XOAUTH2</c>. This is what makes single sign-on through the webmail
    /// work. Two consequences follow from Stalwart's OIDC backend and both are load-bearing:
    /// accounts must already exist in Stalwart before their first login (EntKube provisions them
    /// from <see cref="StalwartMailAccount"/>), and desktop clients that cannot do OAUTHBEARER —
    /// Outlook, Thunderbird, Apple Mail — need app passwords.
    /// </summary>
    Oidc = 2,
}

/// <summary>How the mail listeners (SMTP/IMAP/POP3/ManageSieve) obtain their TLS certificate.</summary>
public enum StalwartTlsMode
{
    /// <summary>
    /// cert-manager issues the certificate into a Kubernetes Secret, which is mounted into the
    /// pod and registered with Stalwart as a file-backed <c>Certificate</c>. The default: it
    /// reuses the cluster's existing ClusterIssuer (including its DNS-01 solver), so the mail
    /// LoadBalancer never has to be reachable on port 80 or 443 for a challenge.
    /// </summary>
    ClusterIssuer = 0,

    /// <summary>
    /// Stalwart's own ACME client obtains and renews the certificate. Requires the mail
    /// LoadBalancer to be reachable from the internet on port 443 (TLS-ALPN-01) or 80 (HTTP-01).
    /// </summary>
    Acme = 1,

    /// <summary>An operator-supplied certificate + key, held in the vault and synced to a Secret.</summary>
    Manual = 2,
}

/// <summary>ACME challenge Stalwart uses when <see cref="StalwartTlsMode.Acme"/> is selected.</summary>
public enum StalwartAcmeChallenge
{
    TlsAlpn01 = 0,
    Http01 = 1,
    Dns01 = 2,
}

/// <summary>How the mail ports (25/465/587/143/993/110/995/4190) are published outside the cluster.</summary>
public enum StalwartMailExposeMode
{
    /// <summary>
    /// A dedicated <c>LoadBalancer</c> Service carrying only the mail ports. The default, and the
    /// right answer for real mail: SMTP needs its own address with matching forward and reverse
    /// DNS, and <c>externalTrafficPolicy: Local</c> preserves the client IP that every spam check
    /// depends on.
    /// </summary>
    LoadBalancer = 0,

    /// <summary>
    /// ClusterIP only — nothing outside the cluster reaches the mail ports. Useful for an
    /// internal-only relay, or while DNS is still being arranged.
    /// </summary>
    ClusterIp = 1,
}

/// <summary>
/// EntKube-managed configuration for a Stalwart mail server deployed as a catalog component.
/// Mirrors <see cref="OpenLdapComponentConfig"/>: it attaches to an installed
/// <see cref="ClusterComponent"/>, secrets live in the vault rather than in columns, and the
/// server's state is authored here and converged onto the running deployment.
///
/// <para>Stalwart v0.16 keeps only the datastore in its <c>config.json</c>; every other setting —
/// domains, listeners, the directory, the spam filter's milter, DKIM — lives inside the datastore
/// and is reconciled through <c>stalwart-cli apply</c> with a declarative NDJSON plan. So EntKube
/// renders two things from this record: the pod's <c>config.json</c> (via the component manifest)
/// and the apply plan (via <c>StalwartService.BuildApplyPlan</c>).</para>
/// </summary>
public class StalwartComponentConfig
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The installed Stalwart ClusterComponent this config belongs to.</summary>
    public Guid? ClusterComponentId { get; set; }

    /// <summary>Human-readable name for externally-deployed instances (ClusterComponentId null).</summary>
    public string? DisplayName { get; set; }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The server's own hostname — its SMTP EHLO greeting, the name on its certificate, and the
    /// name every MX record should point at. e.g. <c>mail.example.com</c>.
    /// </summary>
    public required string Hostname { get; set; }

    /// <summary>
    /// Public hostname for the web surfaces: the admin UI, JMAP, autoconfig/autodiscover and the
    /// OAuth endpoints. Published through the cluster's gateway (Istio or Traefik) as an
    /// ExternalRoute, so it is a normal HTTPS hostname and not one of the mail ports. Null leaves
    /// the HTTP listener reachable only inside the cluster.
    /// </summary>
    public string? AdminHostname { get; set; }

    /// <summary>Administrator login used by EntKube when applying configuration in recovery mode.</summary>
    public string AdminUsername { get; set; } = "admin";

    // ── Storage ───────────────────────────────────────────────────────────────

    /// <summary>Size of the RocksDB data volume (messages, indexes and all configuration).</summary>
    public string StorageSize { get; set; } = "20Gi";

    public string? StorageClass { get; set; }

    // ── Authentication ────────────────────────────────────────────────────────

    public StalwartAuthMode AuthMode { get; set; } = StalwartAuthMode.Ldap;

    /// <summary>
    /// The EntKube-managed OpenLDAP directory to authenticate against. When set, the LDAP URL,
    /// base DN and bind DN are derived from it, so the two components stay consistent without the
    /// operator restating the directory's shape. Leave null and fill the explicit fields below to
    /// point at an LDAP server EntKube does not manage.
    /// </summary>
    public Guid? OpenLdapConfigId { get; set; }

    /// <summary>Explicit LDAP URL, e.g. <c>ldap://openldap.openldap.svc.cluster.local:389</c>.</summary>
    public string? LdapUrl { get; set; }

    public string? LdapBaseDn { get; set; }

    public string? LdapBindDn { get; set; }

    /// <summary>
    /// LDAP filter that resolves a login to an entry. The <c>?</c> is replaced with the login name,
    /// which in v0.16 is a full email address — a filter matching a bare uid will never match.
    /// </summary>
    public string LdapLoginFilter { get; set; } = "(&(objectClass=inetOrgPerson)(mail=?))";

    /// <summary>Filter used to resolve a recipient address to a mailbox (users and groups).</summary>
    public string LdapMailboxFilter { get; set; } =
        "(&(|(objectClass=inetOrgPerson)(objectClass=groupOfNames))(|(mail=?)(mailAlias=?)))";

    /// <summary>Filter that resolves a user's group memberships.</summary>
    public string LdapMemberOfFilter { get; set; } = "(&(objectClass=groupOfNames)(member=?))";

    /// <summary>Use LDAPS/StartTLS for the directory connection.</summary>
    public bool LdapUseTls { get; set; }

    /// <summary>Accept a directory certificate that does not validate (self-signed in-cluster CA).</summary>
    public bool LdapAllowInvalidCerts { get; set; } = true;

    // ── OIDC (Keycloak) ───────────────────────────────────────────────────────

    /// <summary>
    /// Keycloak realm issuer URL, e.g. <c>https://login.example.com/auth/realms/mail</c>. Stalwart
    /// validates access tokens against this issuer's discovery document.
    /// </summary>
    public string? OidcIssuerUrl { get; set; }

    /// <summary>The claim carrying the login name.</summary>
    public string OidcClaimUsername { get; set; } = "preferred_username";

    /// <summary>
    /// Domain appended when the username claim has no <c>@</c>. Account names are email addresses
    /// in v0.16, so a realm whose usernames are bare needs this set or no login will resolve.
    /// </summary>
    public string? OidcUsernameDomain { get; set; }

    /// <summary>Claim carrying group memberships, mapped onto Stalwart groups.</summary>
    public string? OidcClaimGroups { get; set; } = "groups";

    /// <summary>Audience a token must carry to be accepted. Null accepts any audience from the issuer.</summary>
    public string? OidcRequireAudience { get; set; }

    /// <summary>Space-separated scopes a token must carry.</summary>
    public string OidcRequireScopes { get; set; } = "openid email";

    /// <summary>
    /// A stored OAuth/OIDC app registration (vault secret) to configure OIDC from — e.g. a Microsoft
    /// Entra registration. When set, the issuer, username claim, required audience, scopes and group
    /// claim are derived from it on save (the provider-correct values from
    /// <see cref="EntKube.Web.Services.OAuthClientHelper.Resolve"/>), so an operator picks the
    /// registration instead of hand-typing the issuer and getting Entra's aud/scope rules wrong.
    /// Null keeps the manually-entered OIDC fields.
    /// </summary>
    public Guid? OidcAppRegistrationSecretId { get; set; }

    // ── TLS ───────────────────────────────────────────────────────────────────

    public StalwartTlsMode TlsMode { get; set; } = StalwartTlsMode.ClusterIssuer;

    /// <summary>cert-manager ClusterIssuer for the mail certificate when TlsMode is ClusterIssuer.</summary>
    public string? ClusterIssuer { get; set; } = "letsencrypt-prod";

    /// <summary>Contact address registered with the ACME account when TlsMode is Acme.</summary>
    public string? AcmeContact { get; set; }

    public StalwartAcmeChallenge AcmeChallenge { get; set; } = StalwartAcmeChallenge.TlsAlpn01;

    /// <summary>
    /// cert-manager ClusterIssuer for the PUBLIC web hostname (admin UI / JMAP). A separate setting
    /// from <see cref="ClusterIssuer"/> because the two can legitimately differ — the web hostname
    /// is public and wants a public ACME issuer, while the mail certificate may come from a CA
    /// issuer chosen to match the deployment's DNS.
    /// </summary>
    public string? WebClusterIssuer { get; set; } = "letsencrypt-prod";

    // ── Exposure ──────────────────────────────────────────────────────────────

    public StalwartMailExposeMode ExposeMode { get; set; } = StalwartMailExposeMode.LoadBalancer;

    /// <summary>Requested static address for the mail LoadBalancer, when the cloud provider honours one.</summary>
    public string? LoadBalancerIp { get; set; }

    /// <summary>
    /// Extra annotations for the mail LoadBalancer Service, one <c>key: value</c> per line. This is
    /// where provider-specific settings go (an OpenStack floating IP, an internal-LB flag, a
    /// pre-allocated address pool).
    /// </summary>
    public string? LoadBalancerAnnotations { get; set; }

    // ── Protocols ─────────────────────────────────────────────────────────────

    public bool SmtpEnabled { get; set; } = true;
    public bool SubmissionEnabled { get; set; } = true;
    public bool ImapEnabled { get; set; } = true;
    public bool Pop3Enabled { get; set; }
    public bool ManageSieveEnabled { get; set; } = true;

    // ── Spam filtering (rspamd) ───────────────────────────────────────────────

    /// <summary>Hand every incoming message to rspamd over the milter protocol at the DATA stage.</summary>
    public bool RspamdEnabled { get; set; }

    /// <summary>
    /// rspamd's milter endpoint. Defaults to the in-cluster Service the EntKube rspamd component
    /// creates; point it elsewhere for an rspamd this cluster does not own.
    /// </summary>
    public string? RspamdHost { get; set; }

    public int RspamdPort { get; set; } = 11332;

    /// <summary>
    /// Reject the message with a temporary failure when rspamd is unreachable. On by default, and
    /// deliberately: the alternative is that an rspamd outage silently turns the spam filter off.
    /// </summary>
    public bool RspamdTempFailOnError { get; set; } = true;

    // ── High availability ─────────────────────────────────────────────────────

    /// <summary>
    /// Run more than one replica. Off by default, and a real decision — not a slider. Single-node
    /// Stalwart keeps everything in an embedded RocksDB on one volume; HA needs the state to live
    /// somewhere every node can reach, so turning this on requires a shared datastore
    /// (<see cref="CnpgDatabaseId"/>), a shared blob store (<see cref="BlobStorageLinkId"/>) and a
    /// coordinator (<see cref="CoordinatorRedisHost"/>). config.json switches from RocksDb to that
    /// PostgreSQL datastore; the blob store, coordinator and cluster role are written by the apply
    /// plan; and every node runs the <c>all-roles</c> role.
    ///
    /// <para><b>This is the one setting whose misuse is not recoverable by re-applying.</b> Stalwart's
    /// own v0.16 guidance warns that nodes disagreeing on the shared datastore can corrupt it, so the
    /// preflight blocks an apply until the shared backends are all in place.</para>
    /// </summary>
    public bool HighAvailability { get; set; }

    /// <summary>Replica count when <see cref="HighAvailability"/> is on. Ignored otherwise.</summary>
    public int Replicas { get; set; } = 2;

    /// <summary>The CNPG PostgreSQL database that becomes the shared datastore in HA mode.</summary>
    public Guid? CnpgDatabaseId { get; set; }

    /// <summary>The S3 storage link that holds message bodies and attachments in HA mode.</summary>
    public Guid? BlobStorageLinkId { get; set; }

    /// <summary>Host of the Redis the cluster coordinates through (nodes share state via it).</summary>
    public string? CoordinatorRedisHost { get; set; }

    public int CoordinatorRedisPort { get; set; } = 6379;

    // ── Bookkeeping ───────────────────────────────────────────────────────────

    /// <summary>When EntKube last converged the running server onto this configuration.</summary>
    public DateTime? LastAppliedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ClusterComponent? ClusterComponent { get; set; }
    public ICollection<StalwartMailDomain> Domains { get; set; } = [];
    public ICollection<StalwartMailAccount> Accounts { get; set; } = [];
}

/// <summary>A mail domain served by this Stalwart instance.</summary>
public class StalwartMailDomain
{
    public Guid Id { get; set; }
    public Guid ConfigId { get; set; }

    /// <summary>The domain name, e.g. <c>example.com</c>.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The deployment's default domain. Exactly one domain is primary: v0.16 appends it to any
    /// login that arrives without one, so it is what makes a bare username still work.
    /// </summary>
    public bool IsPrimary { get; set; }

    public string? Description { get; set; }

    /// <summary>Local part that receives mail for unknown recipients. Null disables catch-all.</summary>
    public string? CatchAllLocalPart { get; set; }

    /// <summary>Let authenticated senders relay to recipients outside this domain.</summary>
    public bool AllowRelaying { get; set; }

    /// <summary>Let Stalwart generate and rotate the DKIM keys for this domain itself.</summary>
    public bool AutomaticDkim { get; set; } = true;

    /// <summary>Enable plus-addressing (<c>user+tag@domain</c>).</summary>
    public bool SubAddressing { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public StalwartComponentConfig Config { get; set; } = null!;
}

/// <summary>
/// A mailbox EntKube provisions in Stalwart.
///
/// <para>In <see cref="StalwartAuthMode.Ldap"/> mode these are optional — Stalwart resolves
/// mailboxes from the directory on demand. In <see cref="StalwartAuthMode.Oidc"/> mode they are
/// required, because Stalwart's OIDC backend can validate a token but cannot discover an account
/// that does not exist yet; a user whose account was never created is refused at first login.</para>
/// </summary>
public class StalwartMailAccount
{
    public Guid Id { get; set; }
    public Guid ConfigId { get; set; }

    /// <summary>The domain this mailbox belongs to.</summary>
    public Guid DomainId { get; set; }

    /// <summary>Local part of the address — <c>alice</c> in <c>alice@example.com</c>.</summary>
    public required string LocalPart { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Additional local parts in the same domain that deliver here, one per line. Aliases, not
    /// separate mailboxes.
    /// </summary>
    public string? Aliases { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public StalwartComponentConfig Config { get; set; } = null!;
    public StalwartMailDomain Domain { get; set; } = null!;
}
