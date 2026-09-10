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
    public void BuildHelmValues_WebUi_GatewayMode_DeploysButChartIngressOff()
    {
        // Gateway mode: the subchart is deployed but its own Ingress is off — EntKube ExternalRoutes
        // publish it, so the hostname is NOT in the Helm values.
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com", AdminUsername = "admin",
            PhpLdapAdminEnabled = true, PhpLdapAdminHostname = "ldapadmin.example.com",
            PhpLdapAdminExposeMode = OpenLdapExposeMode.Gateway,
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "creds");

        values.Should().Contain("phpldapadmin:\n  enabled: true\n  ingress:\n    enabled: false");
        values.Should().NotContain("ldapadmin.example.com"); // host goes into an ExternalRoute, not values
    }

    [Fact]
    public void BuildHelmValues_WebUi_IngressMode_EmitsClassicIngress()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com", AdminUsername = "admin",
            PhpLdapAdminEnabled = true, PhpLdapAdminHostname = "ldapadmin.example.com",
            PhpLdapAdminExposeMode = OpenLdapExposeMode.Ingress, PhpLdapAdminIngressClass = "nginx",
            WebUiClusterIssuer = "letsencrypt-prod",
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "creds");

        values.Should().Contain("phpldapadmin:\n  enabled: true\n  ingress:\n    enabled: true");
        values.Should().Contain("ingressClassName: nginx");
        values.Should().Contain("cert-manager.io/cluster-issuer: letsencrypt-prod");
        values.Should().Contain("- ldapadmin.example.com");
        values.Should().Contain("secretName: phpldapadmin-tls");
    }

    [Fact]
    public void BuildHelmValues_Ltb_NotDeployedWithoutImage_DeployedWithImage()
    {
        OpenLdapComponentConfig noImage = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com", AdminUsername = "admin",
            LtbPasswdEnabled = true, LtbPasswdHostname = "ssp.example.com", LtbPasswdImage = null,
        };
        // Enabled but no image → left disabled (chart default image is gone → would ImagePullBackOff).
        OpenLdapService.BuildHelmValues(noImage, "", "creds").Should().Contain("ltb-passwd:\n  enabled: false");

        OpenLdapComponentConfig withImage = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com", AdminUsername = "admin",
            LtbPasswdEnabled = true, LtbPasswdImage = "myrepo/self-service-password:1.0",
            LtbPasswdExposeMode = OpenLdapExposeMode.None,
        };
        string v = OpenLdapService.BuildHelmValues(withImage, "", "creds");
        v.Should().Contain("ltb-passwd:\n  enabled: true");
        v.Should().Contain("repository: myrepo/self-service-password");
        v.Should().Contain("tag: \"1.0\"");
    }

    [Theory]
    [InlineData("myrepo/self-service-password:1.0", "myrepo/self-service-password", "1.0")]
    [InlineData("osixia/phpldapadmin", "osixia/phpldapadmin", null)]
    [InlineData("registry.example.com:5000/team/app:2.3", "registry.example.com:5000/team/app", "2.3")]
    public void ParseImage_SplitsRepoAndTag(string image, string repo, string? tag)
    {
        (string r, string? t) = OpenLdapService.ParseImage(image);
        r.Should().Be(repo);
        t.Should().Be(tag);
    }

    [Fact]
    public void BuildHelmValues_WebUis_DefaultDisabled()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com", AdminUsername = "admin",
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "creds");

        values.Should().Contain("phpldapadmin:\n  enabled: false");
        values.Should().Contain("ltb-passwd:\n  enabled: false");
    }

    [Fact]
    public void BuildHelmValues_SelfSigned_ChartGeneratesCert_NoExternalSecret()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com",
            AdminUsername = "admin", TlsMode = OpenLdapTlsMode.SelfSigned,
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "creds");

        // tls_enabled:false → chart self-signs into an emptyDir (no openldap-tls Secret to wait for → no hang)
        values.Should().Contain("initTLSSecret:\n  tls_enabled: false");
        values.Should().Contain("LDAP_ENABLE_TLS: \"yes\"");
        values.Should().NotContain($"secret: \"{OpenLdapService.TlsSecretName}\"");
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
    public void CertManager_NotRequiredByDefault_SinceSelfSignedIsDefault()
    {
        // Default tls-mode is now SelfSigned (no cert infra), so cert-manager is NOT required by default.
        var check = ComponentCatalog.CheckDependencies(OpenLdapEntry(), installedComponentNames: []);

        check.MissingDependencies.Should().NotContain("cert-manager");
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

    // ── ApplyFormValues (catalog install/edit form → directory config) ────────

    [Fact]
    public void ApplyFormValues_CapturesEveryFieldOfTheInstallForm()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=example,dc=com",
        };

        OpenLdapService.ApplyFormValues(cfg, new Dictionary<string, string>
        {
            ["base-dn"] = "dc=acme,dc=io",
            ["organization"] = "Acme AB",
            ["tls-mode"] = "ClusterIssuer",
            ["cluster-issuer"] = "internal-ca",
            ["replica-count"] = "3",
            ["storage-size"] = "20Gi",
            ["phpldapadmin-enabled"] = "true",
            ["phpldapadmin-hostname"] = "ldapadmin.acme.io",
        });

        cfg.BaseDn.Should().Be("dc=acme,dc=io");
        cfg.Organization.Should().Be("Acme AB");
        cfg.TlsMode.Should().Be(OpenLdapTlsMode.ClusterIssuer);
        cfg.ClusterIssuer.Should().Be("internal-ca");
        cfg.ReplicaCount.Should().Be(3);
        cfg.ReplicationEnabled.Should().BeTrue();
        cfg.StorageSize.Should().Be("20Gi");
        cfg.PhpLdapAdminEnabled.Should().BeTrue();
        cfg.PhpLdapAdminHostname.Should().Be("ldapadmin.acme.io");
    }

    [Fact]
    public void ApplyFormValues_LeavesSettingsTheFormDoesNotCarryAlone()
    {
        // The component form has no say over the LTB portal, the overlays or the ppolicy —
        // those are authored in the Directory (LDAP) tab. Saving the component form must not
        // reset them, and must not reset the base DN just because the field arrived blank.
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=acme,dc=io",
            Organization = "Acme AB",
            LtbPasswdEnabled = true, LtbPasswdHostname = "passwd.acme.io", LtbPasswdImage = "acme/ssp:1.5",
            PpolicyMinLength = 14, MemberOfEnabled = false, StorageSize = "20Gi",
        };

        OpenLdapService.ApplyFormValues(cfg, new Dictionary<string, string>
        {
            ["base-dn"] = "",
            ["tls-mode"] = "SelfSigned",
        });

        cfg.BaseDn.Should().Be("dc=acme,dc=io");
        cfg.Organization.Should().Be("Acme AB");
        cfg.StorageSize.Should().Be("20Gi");
        cfg.LtbPasswdEnabled.Should().BeTrue();
        cfg.LtbPasswdHostname.Should().Be("passwd.acme.io");
        cfg.PpolicyMinLength.Should().Be(14);
        cfg.MemberOfEnabled.Should().BeFalse();
    }

    [Fact]
    public void ApplyFormValues_DisablingPhpLdapAdmin_ClearsItsHostname()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=acme,dc=io",
            PhpLdapAdminEnabled = true, PhpLdapAdminHostname = "ldapadmin.acme.io",
        };

        OpenLdapService.ApplyFormValues(cfg, new Dictionary<string, string>
        {
            ["phpldapadmin-enabled"] = "false",
            ["phpldapadmin-hostname"] = "ldapadmin.acme.io",
        });

        cfg.PhpLdapAdminEnabled.Should().BeFalse();
        cfg.PhpLdapAdminHostname.Should().BeNull();
    }

    [Fact]
    public void ApplyFormValues_LeavingClusterIssuerMode_DropsTheIssuer()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=acme,dc=io",
            TlsMode = OpenLdapTlsMode.ClusterIssuer, ClusterIssuer = "internal-ca",
        };

        OpenLdapService.ApplyFormValues(cfg, new Dictionary<string, string> { ["tls-mode"] = "SelfSigned" });

        cfg.TlsMode.Should().Be(OpenLdapTlsMode.SelfSigned);
        cfg.ClusterIssuer.Should().BeNull();
    }

    // ── Pseudo-paths never reach the values YAML ──────────────────────────────

    [Fact]
    public void MergeFormValues_DoesNotWriteLdapPseudoPathsOrPasswordsIntoTheYaml()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(OpenLdapService.CatalogKey)!;

        string values = CatalogComponentRegistrar.MergeFormValues(entry, new Dictionary<string, string>
        {
            ["base-dn"] = "dc=acme,dc=io",
            ["admin-password"] = "sup3rs3cret",
            ["config-password"] = "c0nfigs3cret",
            ["storage-size"] = "20Gi",
        }, []);

        values.Should().NotContain("ldap:");
        values.Should().NotContain("sup3rs3cret");
        values.Should().NotContain("c0nfigs3cret");
    }

    // ── The form has to survive being reopened ────────────────────────────────

    [Fact]
    public void EveryFormFieldSurvivesAReopenOfTheComponentsTab()
    {
        // The fields are ldap: pseudo-paths, so the stored Helm values hold none of them and the
        // Components tab has nothing to re-read except BuildFormValues. Without it the form reopens
        // on catalog defaults and saving writes a fresh base DN over a live directory.
        OpenLdapComponentConfig saved = new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            BaseDn = "dc=acme,dc=io",
            Organization = "Acme",
            TlsMode = OpenLdapTlsMode.ClusterIssuer,
            ClusterIssuer = "internal-ca",
            ReplicaCount = 3,
            StorageSize = "40Gi",
            PhpLdapAdminEnabled = true,
            PhpLdapAdminHostname = "ldapadmin.acme.io",
        };

        OpenLdapComponentConfig reopened = new()
        {
            Id = Guid.NewGuid(),
            TenantId = saved.TenantId,
            BaseDn = "dc=example,dc=com",
        };

        OpenLdapService.ApplyFormValues(reopened, OpenLdapService.BuildFormValues(saved));

        reopened.BaseDn.Should().Be(saved.BaseDn);
        reopened.Organization.Should().Be(saved.Organization);
        reopened.TlsMode.Should().Be(saved.TlsMode);
        reopened.ClusterIssuer.Should().Be(saved.ClusterIssuer);
        reopened.ReplicaCount.Should().Be(saved.ReplicaCount);
        // ReplicaCount > 1 is what replication means, so the round trip has to carry it too.
        reopened.ReplicationEnabled.Should().BeTrue();
        reopened.StorageSize.Should().Be(saved.StorageSize);
        reopened.PhpLdapAdminEnabled.Should().Be(saved.PhpLdapAdminEnabled);
        reopened.PhpLdapAdminHostname.Should().Be(saved.PhpLdapAdminHostname);
    }

    [Fact]
    public void EveryNonSecretFormFieldHasAReadBack()
    {
        // The hand-written read-back this replaced was complete when it was written. What it lacked
        // was anything to stop the next field added to the catalog entry from silently resetting to
        // its default on every reopen — which is exactly how the mail server's oidc-realm slipped in.
        CatalogEntry entry = ComponentCatalog.GetByKey(OpenLdapService.CatalogKey)!;
        Dictionary<string, string> readBack = OpenLdapService.BuildFormValues(new OpenLdapComponentConfig
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            BaseDn = "dc=example,dc=com",
        });

        List<string> missing = entry.FormFields
            .Select(f => f.Key)
            .Where(k => !OpenLdapService.SecretFormKeys.Contains(k))
            .Where(k => !readBack.ContainsKey(k))
            .ToList();

        missing.Should().BeEmpty(
            "every non-secret OpenLDAP form field must be readable back out of the stored config");

        // A password must never be echoed into the form, and a blank one on re-save must leave the
        // stored value alone rather than clearing it.
        readBack.Keys.Should().NotIntersectWith(OpenLdapService.SecretFormKeys);

        // The exemption list must name fields that exist, or a rename leaves a stale exemption
        // quietly excusing the next real omission.
        entry.FormFields.Select(f => f.Key).Should().Contain(OpenLdapService.SecretFormKeys);
    }

    [Fact]
    public void TheDirectoryFormDoesNotClaimTheReservedRouteHostnameKey()
    {
        // A field keyed "hostname" is treated across the Components tab as this component's external
        // route hostname, and is overwritten from the stored route along with tls-mode and
        // cluster-issuer. OpenLDAP's routes belong to its bundled web UIs, so claiming that key would
        // put a web UI's public issuer into the fields that configure the directory's own certificate.
        CatalogEntry entry = ComponentCatalog.GetByKey(OpenLdapService.CatalogKey)!;

        entry.FormFields.Should().NotContain(f => f.Key == "hostname");
    }

    [Fact]
    public void CatalogFormFields_EveryPseudoPathBelongsToASideConfigThatHandlesIt()
    {
        // A pseudo-path is only inert in the values YAML because something else consumes it.
        // A new prefix that nothing handles would be dropped from the YAML *and* never stored.
        string[] handled =
        [
            "subchart:", "cnpg:", "harbor:", "ldap:", "loki:", "mimir:",
            "tempo:", "velero:", "headscale:", "entkube-telemetry:",
            // The mail components, all via CatalogComponentRegistrar.SaveMailConfigIfNeededAsync:
            // stalwart: reaches StalwartService.ApplyFormValues and its config record, while the
            // other three are secret-backed and are read out of the vault when their manifest is
            // rendered.
            "stalwart:", "rspamd:", "roundcube:", "snappymail:",
        ];

        foreach (CatalogEntry entry in ComponentCatalog.Entries)
        {
            foreach (ComponentFormField field in entry.FormFields.Where(f => f.IsPseudoPath))
            {
                handled.Should().Contain(p => field.YamlPath.StartsWith(p, StringComparison.Ordinal),
                    $"{entry.Key}/{field.Key} ({field.YamlPath}) must be handled by a side config");

                // A pseudo-path must never also look like a dot-notation Helm values path.
                field.YamlPath.Should().NotContain(".", $"{entry.Key}/{field.Key}");
            }
        }
    }

    // ── The admin DN the container creates must match the one the UI binds ────

    [Fact]
    public void BuildHelmValues_PassesAdminUsernameToTheContainer()
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=entit,dc=se",
            AdminUsername = "directory-admin",
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "openldap-credentials");

        // global.adminUser only builds phpLDAPadmin's bind DN; LDAP_ADMIN_USERNAME is what the
        // container creates the account from. They must agree or no password can ever bind.
        values.Should().Contain("adminUser: \"directory-admin\"");
        values.Should().Contain("LDAP_ADMIN_USERNAME: \"directory-admin\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildHelmValues_BlankAdminUsername_FallsBackToAdmin(string adminUser)
    {
        OpenLdapComponentConfig cfg = new()
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), BaseDn = "dc=entit,dc=se",
            AdminUsername = adminUser,
        };

        string values = OpenLdapService.BuildHelmValues(cfg, "", "openldap-credentials");

        // "cn=,dc=entit,dc=se" is rejected by the server as invalid DN syntax.
        values.Should().Contain("adminUser: \"admin\"");
        values.Should().Contain("LDAP_ADMIN_USERNAME: \"admin\"");
    }

    // ── Replication credential substitution ──────────────────────────────────

    [Theory]
    [InlineData("plainPassw0rd", false)]
    [InlineData("has$dollar*and^caret", false)]
    [InlineData("amp&ersand", true)]
    [InlineData("slash/es", true)]
    [InlineData("back\\slash", true)]
    [InlineData("", false)]
    public void PasswordBreaksReplicationCredentials_FlagsWhatSedRewrites(string password, bool expected)
    {
        // sed's replacement treats & as the whole match and / as the delimiter, so the chart's
        // "s/%%ADMIN_PASSWORD%%/$LDAP_ADMIN_PASSWORD/g" silently corrupts those passwords.
        OpenLdapService.PasswordBreaksReplicationCredentials(password).Should().Be(expected);
    }
}
