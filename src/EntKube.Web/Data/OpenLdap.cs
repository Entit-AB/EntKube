namespace EntKube.Web.Data;

/// <summary>How a managed OpenLDAP instance obtains its LDAPS/StartTLS certificate.</summary>
public enum OpenLdapTlsMode
{
    /// <summary>No TLS listener — LDAP on 389 only (rely on NetworkPolicy/mesh for isolation).</summary>
    Off = 0,

    /// <summary>cert-manager issues the server certificate from a ClusterIssuer (requires cert-manager + a ready CA ClusterIssuer).</summary>
    ClusterIssuer = 1,

    /// <summary>Operator supplies a certificate + key manually (a pre-created <c>openldap-tls</c> Secret).</summary>
    Manual = 2,

    /// <summary>
    /// The chart generates a self-signed certificate at startup — zero external dependencies, works
    /// immediately. Replication verification is relaxed (tls_reqcert=never) so multi-master still works.
    /// The safe default.
    /// </summary>
    SelfSigned = 3,
}

/// <summary>How a bundled web UI (phpLDAPadmin / LTB) is published externally.</summary>
public enum OpenLdapExposeMode
{
    /// <summary>Not exposed — the UI Service stays ClusterIP-only (reach it via port-forward).</summary>
    None = 0,

    /// <summary>EntKube ExternalRoute — Gateway API HTTPRoute on the cluster's gateway (traefik/istio) + cert-manager TLS.</summary>
    Gateway = 1,

    /// <summary>The chart's own classic Ingress with a chosen ingressClassName (nginx, traefik, …) — no API gateway.</summary>
    Ingress = 2,
}

/// <summary>The LDAP object class used to model a group entry.</summary>
public enum OpenLdapGroupType
{
    /// <summary>RFC 2307-bis <c>groupOfNames</c> (member = full DNs). The default.</summary>
    GroupOfNames = 0,

    /// <summary>POSIX <c>posixGroup</c> (memberUid = uid values, requires a gidNumber).</summary>
    PosixGroup = 1,
}

/// <summary>
/// EntKube-managed configuration for an OpenLDAP directory deployed as a catalog
/// component (symas/openldap Helm chart). Mirrors <see cref="KeycloakComponentConfig"/>:
/// it attaches to an installed <see cref="ClusterComponent"/> (ClusterComponentId set)
/// or describes an externally-deployed instance (ClusterComponentId null, DisplayName used).
///
/// The directory itself is authored declaratively in EntKube — the base DN, TLS,
/// replication, overlays, password policy, and the seed entries (OUs, users, groups)
/// are applied to the running server via Helm values + a generated bootstrap LDIF.
/// The admin/config passwords are stored as component vault secrets (never columns)
/// and synced to the K8s Secret the chart consumes.
/// </summary>
public class OpenLdapComponentConfig
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The installed OpenLDAP ClusterComponent this config belongs to. Null for external instances.</summary>
    public Guid? ClusterComponentId { get; set; }

    /// <summary>Human-readable name for externally-deployed instances (ClusterComponentId null).</summary>
    public string? DisplayName { get; set; }

    // ── Directory identity ────────────────────────────────────────────────────

    /// <summary>Root/base DN of the directory, e.g. <c>dc=example,dc=com</c>.</summary>
    public required string BaseDn { get; set; }

    /// <summary>The <c>o=</c> organization name for the base entry.</summary>
    public string Organization { get; set; } = "EntKube";

    /// <summary>The admin account's RDN value; the admin DN is <c>cn={AdminUsername},{BaseDn}</c>.</summary>
    public string AdminUsername { get; set; } = "admin";

    // ── Ports ─────────────────────────────────────────────────────────────────

    public int LdapPort { get; set; } = 389;
    public int LdapsPort { get; set; } = 636;

    // ── TLS ───────────────────────────────────────────────────────────────────

    public OpenLdapTlsMode TlsMode { get; set; } = OpenLdapTlsMode.SelfSigned;

    /// <summary>cert-manager ClusterIssuer name when <see cref="TlsMode"/> is ClusterIssuer.</summary>
    public string? ClusterIssuer { get; set; }

    /// <summary>Advertise StartTLS on the plaintext port (in addition to LDAPS).</summary>
    public bool StartTlsEnabled { get; set; } = true;

    // ── Replication / HA ──────────────────────────────────────────────────────

    /// <summary>Enable N-way multi-master replication (syncrepl mirror mode).</summary>
    public bool ReplicationEnabled { get; set; }

    /// <summary>Number of directory replicas. 1 = single instance; ≥2 requires replication for HA.</summary>
    public int ReplicaCount { get; set; } = 1;

    // ── Overlays ──────────────────────────────────────────────────────────────

    /// <summary><c>memberof</c> overlay — maintains reverse group membership on user entries.</summary>
    public bool MemberOfEnabled { get; set; } = true;

    /// <summary><c>refint</c> overlay — referential integrity of member DNs on delete/rename.</summary>
    public bool RefIntEnabled { get; set; } = true;

    /// <summary><c>ppolicy</c> overlay — password quality, lockout, expiry, history.</summary>
    public bool PasswordPolicyEnabled { get; set; } = true;

    // ── Password policy (ppolicy) ─────────────────────────────────────────────

    public int PpolicyMinLength { get; set; } = 8;

    /// <summary>Failed binds before lockout (pwdMaxFailure). 0 disables lockout.</summary>
    public int PpolicyMaxFailure { get; set; } = 5;

    /// <summary>Lockout duration in seconds (pwdLockoutDuration).</summary>
    public int PpolicyLockoutDurationSeconds { get; set; } = 300;

    /// <summary>Max password age in days (pwdMaxAge). 0 = never expires.</summary>
    public int PpolicyMaxAgeDays { get; set; }

    /// <summary>Passwords remembered to prevent reuse (pwdInHistory).</summary>
    public int PpolicyInHistory { get; set; } = 3;

    // ── Storage ───────────────────────────────────────────────────────────────

    public string StorageSize { get; set; } = "8Gi";

    public string? StorageClass { get; set; }

    // ── Bundled web UIs (chart subcharts) ─────────────────────────────────────

    /// <summary>Deploy phpLDAPadmin (directory admin web UI). Requires <see cref="PhpLdapAdminHostname"/> + an ingress controller/gateway.</summary>
    public bool PhpLdapAdminEnabled { get; set; }

    /// <summary>External hostname for phpLDAPadmin (required when exposed).</summary>
    public string? PhpLdapAdminHostname { get; set; }

    /// <summary>How phpLDAPadmin is published (Gateway API route vs classic Ingress).</summary>
    public OpenLdapExposeMode PhpLdapAdminExposeMode { get; set; } = OpenLdapExposeMode.Gateway;

    /// <summary>ingressClassName (e.g. "nginx") when <see cref="PhpLdapAdminExposeMode"/> is Ingress.</summary>
    public string? PhpLdapAdminIngressClass { get; set; }

    /// <summary>Optional container image override ("repository:tag") for phpLDAPadmin. Null uses the chart default (osixia/phpldapadmin).</summary>
    public string? PhpLdapAdminImage { get; set; }

    /// <summary>Deploy LTB Self-Service-Password (user password self-service web UI). Requires <see cref="LtbPasswdHostname"/> + an ingress controller/gateway.</summary>
    public bool LtbPasswdEnabled { get; set; }

    /// <summary>External hostname for the self-service password portal (required when exposed).</summary>
    public string? LtbPasswdHostname { get; set; }

    /// <summary>How the self-service password portal is published (Gateway API route vs classic Ingress).</summary>
    public OpenLdapExposeMode LtbPasswdExposeMode { get; set; } = OpenLdapExposeMode.Gateway;

    /// <summary>ingressClassName (e.g. "nginx") when <see cref="LtbPasswdExposeMode"/> is Ingress.</summary>
    public string? LtbPasswdIngressClass { get; set; }

    /// <summary>
    /// Container image override ("repository:tag") for the self-service password portal. REQUIRED to
    /// deploy it: the chart's default image (tiredofit/self-service-password) has been removed from all
    /// public registries, so an image implementing that env interface (LDAP_SERVER/LDAP_BINDDN/…) must be
    /// supplied. When null, the LTB subchart is not deployed even if <see cref="LtbPasswdEnabled"/> is set.
    /// </summary>
    public string? LtbPasswdImage { get; set; }

    /// <summary>
    /// cert-manager ClusterIssuer for the PUBLIC web-UI certificates (Gateway route TLS and the classic
    /// Ingress cert-manager annotation). Public hostnames, so a public ACME issuer (e.g. letsencrypt-prod)
    /// is appropriate here — distinct from <see cref="ClusterIssuer"/> which secures the internal LDAP service.
    /// </summary>
    public string? WebUiClusterIssuer { get; set; } = "letsencrypt-prod";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ClusterComponent? ClusterComponent { get; set; }
    public ICollection<OpenLdapOrganizationalUnit> OrganizationalUnits { get; set; } = [];
    public ICollection<OpenLdapGroup> Groups { get; set; } = [];
    public ICollection<OpenLdapUser> Users { get; set; } = [];
}

/// <summary>
/// An organizational unit (<c>ou=</c>) under the base DN. Kept flat (one level under
/// the base) for a straightforward directory layout; DN is <c>ou={Name},{BaseDn}</c>.
/// </summary>
public class OpenLdapOrganizationalUnit
{
    public Guid Id { get; set; }
    public Guid ConfigId { get; set; }

    /// <summary>The <c>ou</c> value, e.g. "people" or "groups". Lowercase, DN-safe.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public OpenLdapComponentConfig Config { get; set; } = null!;
}

/// <summary>
/// A directory user entry (<c>inetOrgPerson</c>, plus <c>posixAccount</c> when a
/// uidNumber is set). Service/bind accounts are the same entry with
/// <see cref="IsServiceAccount"/> set. Passwords are stored pre-hashed as a
/// salted-SHA1 (<c>{SSHA}</c>) value in <see cref="PasswordSsha"/> — never plaintext.
/// </summary>
public class OpenLdapUser
{
    public Guid Id { get; set; }
    public Guid ConfigId { get; set; }

    /// <summary>The OU this user lives in (null = directly under the base DN).</summary>
    public Guid? OrganizationalUnitId { get; set; }

    /// <summary>Login/uid value; also the RDN (<c>uid={Uid},ou=…,{BaseDn}</c>).</summary>
    public required string Uid { get; set; }

    /// <summary>Common name (cn) — usually the display/full name.</summary>
    public required string Cn { get; set; }

    /// <summary>Surname (sn) — required by inetOrgPerson; defaults from Cn when blank.</summary>
    public string? Sn { get; set; }

    public string? GivenName { get; set; }

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    // POSIX attributes (written only when UidNumber is set).
    public int? UidNumber { get; set; }
    public int? GidNumber { get; set; }
    public string? HomeDirectory { get; set; }
    public string? LoginShell { get; set; }

    /// <summary>Marks a bind/service account (still an inetOrgPerson entry).</summary>
    public bool IsServiceAccount { get; set; }

    /// <summary>Salted-SHA1 <c>{SSHA}</c> hash of the user's password; null = no password set.</summary>
    public string? PasswordSsha { get; set; }

    /// <summary>When false, the entry is written with <c>pwdAccountLockedTime</c> so binds are refused.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public OpenLdapComponentConfig Config { get; set; } = null!;
    public OpenLdapOrganizationalUnit? OrganizationalUnit { get; set; }
}

/// <summary>A directory group entry (<c>groupOfNames</c> or <c>posixGroup</c>).</summary>
public class OpenLdapGroup
{
    public Guid Id { get; set; }
    public Guid ConfigId { get; set; }

    /// <summary>The OU this group lives in (null = directly under the base DN).</summary>
    public Guid? OrganizationalUnitId { get; set; }

    /// <summary>Group common name (cn); also the RDN.</summary>
    public required string Cn { get; set; }

    public string? Description { get; set; }

    public OpenLdapGroupType GroupType { get; set; } = OpenLdapGroupType.GroupOfNames;

    /// <summary>gidNumber for posixGroup members. Required when <see cref="GroupType"/> is PosixGroup.</summary>
    public int? GidNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public OpenLdapComponentConfig Config { get; set; } = null!;
    public OpenLdapOrganizationalUnit? OrganizationalUnit { get; set; }
    public ICollection<OpenLdapGroupMember> Members { get; set; } = [];
}

/// <summary>Join row: a <see cref="OpenLdapUser"/>'s membership in an <see cref="OpenLdapGroup"/>.</summary>
public class OpenLdapGroupMember
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }

    public OpenLdapGroup Group { get; set; } = null!;
    public OpenLdapUser User { get; set; } = null!;
}
