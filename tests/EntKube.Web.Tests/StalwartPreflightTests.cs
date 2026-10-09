using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The preflight's other branches: domains, the administrator, internal passwords, and high
/// availability.
///
/// <para>Applying restarts the mail server twice, so every one of these is the difference between an
/// operator being told what is wrong and finding out from an outage. They are also the failures that
/// look like success — a server with no administrator starts perfectly and simply cannot be signed
/// into — which is why the preflight exists and why it is worth holding to its word.</para>
/// </summary>
public class StalwartPreflightTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly StalwartService mail;
    private readonly VaultService vault;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid componentId = Guid.NewGuid();

    public StalwartPreflightTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid environmentId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId, Name = "prod-1",
            ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "stalwart", ComponentType = "Manifest",
            ReleaseName = "stalwart", Namespace = "stalwart",
        });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        vault = new VaultService(factory, new VaultEncryptionService(TestRootKey), TestServices.NoClusterAccess);

        // A real vault, because the internal branch asks it whether an administrator password exists
        // and that question is the point of these tests. The Kubernetes client is only reached for a
        // cert-manager certificate on an installed component, which none of these is.
        mail = new StalwartService(
            factory, vault, null!, null!, null!, null!, null!,
            NullLogger<StalwartService>.Instance);
    }

    private async Task<StalwartComponentConfig> GivenAServerAsync(
        Action<StalwartComponentConfig> edit, bool withDomain = true, string adminLocalPart = "admin")
    {
        // The vault has to be unsealable before it can be asked whether a secret is there, and an
        // uninitialised one throws rather than answering "no".
        await vault.InitializeVaultAsync(tenantId);

        StalwartComponentConfig config = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterComponentId = componentId,
            Hostname = "mail.example.com",
            AdminUsername = adminLocalPart,
            AuthMode = StalwartAuthMode.Internal,
        };
        edit(config);
        db.StalwartComponentConfigs.Add(config);

        if (withDomain)
        {
            db.StalwartMailDomains.Add(new StalwartMailDomain
            {
                Id = Guid.NewGuid(), ConfigId = config.Id, Name = "example.com", IsPrimary = true,
            });
        }

        await db.SaveChangesAsync();
        return config;
    }

    [Fact]
    public async Task With_no_domains_that_is_the_only_thing_said()
    {
        // Deliberately the whole answer rather than one finding among several. Without a primary
        // domain nothing else can be evaluated — the administrator's address is a local part plus that
        // domain — so listing consequential findings beside it would bury the one that has to be
        // fixed first under ones that are only true because it is.
        await GivenAServerAsync(_ => { }, withDomain: false);

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().ContainSingle()
            .Which.Problem.Should().Contain("No mail domains");
        issues[0].IsBlocking.Should().BeTrue();
    }

    [Fact]
    public async Task An_internal_server_with_no_stored_password_cannot_be_signed_into()
    {
        // The failure that looks exactly like success: the server starts, every listener opens, and
        // there is no credential for the one account carrying the Admin role. Nothing about a running
        // pod shows it.
        await GivenAServerAsync(c => c.AuthMode = StalwartAuthMode.Internal);

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().Contain(i => i.IsBlocking && i.Problem.Contains("no administrator password"));
    }

    [Fact]
    public async Task With_a_password_stored_the_internal_server_is_clean()
    {
        // The other half, without which the test above would pass just as well if the preflight
        // flagged every server.
        await GivenAServerAsync(c => c.AuthMode = StalwartAuthMode.Internal);

        await vault.SetComponentSecretAsync(
            tenantId, componentId, StalwartManifestBuilder.AdminPasswordSecretName, "s3cr3t");

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Where(i => i.IsBlocking).Should().BeEmpty(
            string.Join(" | ", issues.Select(i => i.Problem)));
    }

    [Fact]
    public async Task An_administrator_in_a_domain_the_server_does_not_serve_is_refused()
    {
        // A full address is accepted for the administrator, because an operator told "it must be a
        // directory user" reasonably types one. Which makes it possible to name a domain this server
        // does not host, and then the account is created somewhere nothing routes to.
        await GivenAServerAsync(
            c => c.AuthMode = StalwartAuthMode.Internal,
            adminLocalPart: "nils@somewhere.else");

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().Contain(i =>
            i.IsBlocking && i.Problem.Contains("is in a domain this server does not serve"));
    }

    [Fact]
    public async Task High_availability_needs_the_two_backends_that_hold_state()
    {
        // In HA the datastore cannot be the node's local disk and the message bodies cannot be on a
        // volume only one node can mount. Both are blocking; the replica count is not, because one
        // replica is a bad idea rather than a broken one.
        await GivenAServerAsync(c =>
        {
            c.AuthMode = StalwartAuthMode.Internal;
            c.HighAvailability = true;
            c.CnpgDatabaseId = null;
            c.BlobStorageLinkId = null;
            c.Replicas = 1;
        });

        await vault.SetComponentSecretAsync(
            tenantId, componentId, StalwartManifestBuilder.AdminPasswordSecretName, "s3cr3t");

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().Contain(i => i.IsBlocking && i.Problem.Contains("shared database"));
        issues.Should().Contain(i => i.IsBlocking && i.Problem.Contains("shared blob store"));

        // Advice, not a refusal — and the distinction is load-bearing, because a blocking finding
        // here would stop an operator applying anything at all while they thought about replicas.
        issues.Should().Contain(i => !i.IsBlocking && i.Problem.Contains("only one replica"));
    }

    [Fact]
    public async Task An_LDAP_server_with_no_directory_connection_says_only_that()
    {
        // Returns before the live check, and should: there is nothing to query, so a second finding
        // saying the directory could not be checked would be noise about a consequence.
        await GivenAServerAsync(c =>
        {
            c.AuthMode = StalwartAuthMode.Ldap;
            c.LdapUrl = null;
            c.LdapBaseDn = null;
        });

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().ContainSingle()
            .Which.Problem.Should().Contain("directory connection is incomplete");
    }

    [Fact]
    public async Task A_bind_DN_with_no_password_blocks_and_the_check_that_could_not_run_does_not()
    {
        // The finding that names the original outage: a bind that fails makes Stalwart answer every
        // login with a temporary server failure, which reads as a broken server rather than an
        // unconfigured one.
        //
        // The second assertion is the invariant in CheckLdapAsync's own words — a check that cannot
        // run must not masquerade as a check that failed. This directory is not EntKube-managed, so
        // there is no pod to run ldapsearch in; that has to stay advisory or an unmanaged directory
        // could never be applied at all.
        await GivenAServerAsync(c =>
        {
            c.AuthMode = StalwartAuthMode.Ldap;
            c.LdapUrl = "ldap://ldap.example.com:389";
            c.LdapBaseDn = "dc=example,dc=com";
            c.LdapBindDn = "cn=stalwart,ou=services,dc=example,dc=com";
            c.OpenLdapConfigId = null;
        });

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().Contain(i => i.IsBlocking && i.Problem.Contains("no bind password is stored"));

        issues.Should().Contain(i =>
            !i.IsBlocking && i.Problem.Contains("could not be checked from here"));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
