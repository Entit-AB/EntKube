using System.Security.Cryptography;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

public class OpenLdapServiceTests
{
    // ── DomainFromBaseDn ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("dc=example,dc=com", "example.com")]
    [InlineData("dc=corp,dc=example,dc=com", "corp.example.com")]
    [InlineData("DC=Example,DC=Com", "Example.Com")]
    [InlineData("ou=people,dc=acme,dc=io", "acme.io")]
    [InlineData("o=NoDomain", "example.com")] // no dc components → fallback
    public void DomainFromBaseDn_DerivesDottedDomain(string baseDn, string expected)
    {
        OpenLdapService.DomainFromBaseDn(baseDn).Should().Be(expected);
    }

    // ── HashSsha ──────────────────────────────────────────────────────────────

    [Fact]
    public void HashSsha_ProducesVerifiableSaltedSha1()
    {
        const string password = "S3cr3t!";
        string hash = OpenLdapService.HashSsha(password);

        hash.Should().StartWith("{SSHA}");

        // Decode: the payload is 20-byte SHA1 digest followed by the salt.
        byte[] payload = Convert.FromBase64String(hash["{SSHA}".Length..]);
        payload.Length.Should().BeGreaterThan(20);

        byte[] digest = payload[..20];
        byte[] salt = payload[20..];
        byte[] recomputed = SHA1.HashData([.. Encoding.UTF8.GetBytes(password), .. salt]);

        recomputed.Should().Equal(digest);
    }

    [Fact]
    public void HashSsha_IsSaltedSoOutputsDiffer()
    {
        OpenLdapService.HashSsha("same").Should().NotBe(OpenLdapService.HashSsha("same"));
    }

    // ── BuildSeedLdif ─────────────────────────────────────────────────────────

    private static (OpenLdapComponentConfig cfg, Dictionary<Guid, OpenLdapUser> byId) SampleDirectory()
    {
        Guid peopleOu = Guid.NewGuid();
        Guid groupsOu = Guid.NewGuid();

        OpenLdapUser alice = new()
        {
            Id = Guid.NewGuid(), Uid = "alice", Cn = "Alice Smith", Sn = "Smith",
            Email = "alice@example.com", OrganizationalUnitId = peopleOu,
            PasswordSsha = "{SSHA}deadbeef", Enabled = true,
        };
        OpenLdapUser svc = new()
        {
            Id = Guid.NewGuid(), Uid = "svc-bind", Cn = "Bind Account",
            OrganizationalUnitId = peopleOu, IsServiceAccount = true, Enabled = false,
        };
        OpenLdapUser posix = new()
        {
            Id = Guid.NewGuid(), Uid = "pat", Cn = "Pat POSIX",
            UidNumber = 10001, GidNumber = 10001, OrganizationalUnitId = peopleOu,
        };

        OpenLdapGroup admins = new()
        {
            Id = Guid.NewGuid(), Cn = "admins", GroupType = OpenLdapGroupType.GroupOfNames,
            OrganizationalUnitId = groupsOu,
            Members = [new OpenLdapGroupMember { UserId = alice.Id }],
        };
        OpenLdapGroup empty = new()
        {
            Id = Guid.NewGuid(), Cn = "empty", GroupType = OpenLdapGroupType.GroupOfNames,
            OrganizationalUnitId = groupsOu, Members = [],
        };
        OpenLdapGroup posixGroup = new()
        {
            Id = Guid.NewGuid(), Cn = "staff", GroupType = OpenLdapGroupType.PosixGroup,
            GidNumber = 20000, OrganizationalUnitId = groupsOu,
            Members = [new OpenLdapGroupMember { UserId = posix.Id }],
        };

        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com",
            AdminUsername = "admin",
            OrganizationalUnits =
            [
                new OpenLdapOrganizationalUnit { Id = peopleOu, Name = "people" },
                new OpenLdapOrganizationalUnit { Id = groupsOu, Name = "groups" },
            ],
            Users = [alice, svc, posix],
            Groups = [admins, empty, posixGroup],
        };

        return (cfg, new[] { alice, svc, posix }.ToDictionary(u => u.Id));
    }

    [Fact]
    public void BuildSeedLdif_EmitsOusUsersAndGroups()
    {
        (OpenLdapComponentConfig cfg, Dictionary<Guid, OpenLdapUser> byId) = SampleDirectory();

        string ldif = OpenLdapService.BuildSeedLdif(cfg, byId);

        // OUs
        ldif.Should().Contain("dn: ou=people,dc=example,dc=com");
        ldif.Should().Contain("dn: ou=groups,dc=example,dc=com");
        ldif.Should().Contain("objectClass: organizationalUnit");

        // inetOrgPerson user with password + mail, placed in its OU
        ldif.Should().Contain("dn: uid=alice,ou=people,dc=example,dc=com");
        ldif.Should().Contain("objectClass: inetOrgPerson");
        ldif.Should().Contain("mail: alice@example.com");
        ldif.Should().Contain("userPassword: {SSHA}deadbeef");

        // disabled account is locked
        ldif.Should().Contain("dn: uid=svc-bind,ou=people,dc=example,dc=com");
        ldif.Should().Contain("pwdAccountLockedTime:");

        // posixAccount attributes
        ldif.Should().Contain("objectClass: posixAccount");
        ldif.Should().Contain("uidNumber: 10001");
        ldif.Should().Contain("homeDirectory: /home/pat");
    }

    [Fact]
    public void BuildSeedLdif_GroupOfNames_MemberDnAndEmptyPlaceholder()
    {
        (OpenLdapComponentConfig cfg, Dictionary<Guid, OpenLdapUser> byId) = SampleDirectory();

        string ldif = OpenLdapService.BuildSeedLdif(cfg, byId);

        // groupOfNames member is the full DN of the member user
        ldif.Should().Contain("dn: cn=admins,ou=groups,dc=example,dc=com");
        ldif.Should().Contain("objectClass: groupOfNames");
        ldif.Should().Contain("member: uid=alice,ou=people,dc=example,dc=com");

        // an empty groupOfNames must still have a member — the admin DN placeholder
        ldif.Should().Contain("dn: cn=empty,ou=groups,dc=example,dc=com");
        ldif.Should().Contain("member: cn=admin,dc=example,dc=com");
    }

    [Fact]
    public void BuildSeedLdif_PosixGroup_UsesMemberUid()
    {
        (OpenLdapComponentConfig cfg, Dictionary<Guid, OpenLdapUser> byId) = SampleDirectory();

        string ldif = OpenLdapService.BuildSeedLdif(cfg, byId);

        ldif.Should().Contain("dn: cn=staff,ou=groups,dc=example,dc=com");
        ldif.Should().Contain("objectClass: posixGroup");
        ldif.Should().Contain("gidNumber: 20000");
        ldif.Should().Contain("memberUid: pat");
        ldif.Should().NotContain("member: uid=pat"); // posix uses memberUid, not member DN
    }

    [Fact]
    public void BuildSeedLdif_UserWithNoOu_FallsBackToBaseDn()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=acme,dc=io", AdminUsername = "admin",
            Users = [new OpenLdapUser { Id = Guid.NewGuid(), Uid = "root", Cn = "Root", OrganizationalUnitId = null }],
        };

        string ldif = OpenLdapService.BuildSeedLdif(cfg, cfg.Users.ToDictionary(u => u.Id));

        ldif.Should().Contain("dn: uid=root,dc=acme,dc=io");
    }

    // ── BuildHelmValues ───────────────────────────────────────────────────────

    [Fact]
    public void BuildHelmValues_EncodesDomainReplicasSecretAndSeed()
    {
        (OpenLdapComponentConfig cfg, Dictionary<Guid, OpenLdapUser> byId) = SampleDirectory();
        cfg.ReplicaCount = 3;
        cfg.ReplicationEnabled = true;
        cfg.StorageSize = "20Gi";
        cfg.TlsMode = OpenLdapTlsMode.ClusterIssuer;
        cfg.ClusterIssuer = "letsencrypt-prod";

        string seed = OpenLdapService.BuildSeedLdif(cfg, byId);
        string values = OpenLdapService.BuildHelmValues(cfg, seed, "openldap-credentials");

        // openldap-stack-ha (jp-gouin) schema.
        values.Should().Contain("replicaCount: 3");
        values.Should().Contain("ldapDomain: \"dc=example,dc=com\""); // explicit DN passed verbatim
        values.Should().Contain("existingSecret: \"openldap-credentials\"");
        values.Should().Contain("replication:\n  enabled: true");
        values.Should().Contain("size: 20Gi");
        values.Should().Contain("initTLSSecret:\n  tls_enabled: true");
        values.Should().Contain("secret: \"openldap-tls\"");

        // Root org entry MUST precede the seed (chart skips default-tree creation with customLdifFiles).
        values.Should().Contain("customLdifFiles:");
        values.Should().Contain("01-entkube-root.ldif: |-");
        values.Should().Contain("    objectClass: dcObject");

        // The seed LDIF is embedded and indented under the customLdifFiles block scalar.
        values.Should().Contain("02-entkube-seed.ldif: |-");
        values.Should().Contain("    dn: uid=alice,ou=people,dc=example,dc=com");
    }

    [Fact]
    public void BuildHelmValues_ReplicationDisabledWhenSingleReplica()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com",
            AdminUsername = "admin", ReplicaCount = 1, ReplicationEnabled = true, // enabled but only 1 replica
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "creds");

        values.Should().Contain("replication:\n  enabled: false");
    }

    [Fact]
    public void BuildHelmValues_TlsOff_DisablesTls()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com",
            AdminUsername = "admin", TlsMode = OpenLdapTlsMode.Off,
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "creds");

        values.Should().Contain("initTLSSecret:\n  tls_enabled: false");
        values.Should().NotContain("tls_enabled: true");
    }

    [Fact]
    public void BuildTlsCertificateManifest_CoversServiceAndPodSans()
    {
        string manifest = OpenLdapService.BuildTlsCertificateManifest("ca-issuer", "openldap", "openldap");

        manifest.Should().Contain("kind: Certificate");
        manifest.Should().Contain("apiVersion: cert-manager.io/v1");
        manifest.Should().Contain("secretName: openldap-tls");
        manifest.Should().Contain("commonName: openldap.openldap.svc.cluster.local");
        // ClusterIP service SAN
        manifest.Should().Contain("- openldap.openldap.svc.cluster.local");
        // per-pod headless wildcard SAN for replication tls_reqcert=demand
        manifest.Should().Contain("- '*.openldap-headless.openldap.svc.cluster.local'");
        manifest.Should().Contain("name: ca-issuer");
        manifest.Should().Contain("kind: ClusterIssuer");
    }

    // ── Conditional cert-manager dependency ───────────────────────────────────

    private static CatalogEntry OpenLdapEntry() =>
        ComponentCatalog.Entries.Single(e => e.Key == OpenLdapService.CatalogKey);

    [Fact]
    public void CertManager_IsRequired_WhenTlsModeIsClusterIssuer()
    {
        var check = ComponentCatalog.CheckDependencies(
            OpenLdapEntry(), installedComponentNames: [],
            formValues: new Dictionary<string, string> { ["tls-mode"] = "ClusterIssuer" });

        check.MissingDependencies.Should().Contain("cert-manager");
        check.IsSatisfied.Should().BeFalse();
    }

    [Fact]
    public void CertManager_NotRequired_WhenTlsModeIsOff()
    {
        var check = ComponentCatalog.CheckDependencies(
            OpenLdapEntry(), installedComponentNames: [],
            formValues: new Dictionary<string, string> { ["tls-mode"] = "Off" });

        check.MissingDependencies.Should().NotContain("cert-manager");
        check.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CertManager_Satisfied_WhenInstalled()
    {
        var check = ComponentCatalog.CheckDependencies(
            OpenLdapEntry(), installedComponentNames: ["cert-manager"],
            formValues: new Dictionary<string, string> { ["tls-mode"] = "ClusterIssuer" });

        check.MissingDependencies.Should().NotContain("cert-manager");
        check.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CertManager_UsesFieldDefault_WhenNoFormValues()
    {
        // Default tls-mode is ClusterIssuer, so the dependency shows without any form input.
        var check = ComponentCatalog.CheckDependencies(OpenLdapEntry(), installedComponentNames: []);

        check.MissingDependencies.Should().Contain("cert-manager");
    }

    [Fact]
    public void BuildRootLdif_EmitsDcObjectOrganization()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=corp,dc=example,dc=com",
            AdminUsername = "admin", Organization = "Corp Inc",
        };

        string root = OpenLdapService.BuildRootLdif(cfg);

        root.Should().Contain("dn: dc=corp,dc=example,dc=com");
        root.Should().Contain("objectClass: dcObject");
        root.Should().Contain("objectClass: organization");
        root.Should().Contain("o: Corp Inc");
        root.Should().Contain("dc: corp"); // first RDN value only
    }
}
