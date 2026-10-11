using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// What the preflight says about an OIDC mail server that is not ready.
///
/// <para>The preflight is the thing standing between an operator and a mail server nobody can sign in
/// to — applying restarts the server twice, so finding out afterwards costs an outage as well as the
/// debugging. It had no tests at all, which is a poor state for the component whose whole job is to be
/// right about what is wrong.</para>
///
/// <para>These cover the two states that look identical and are not: an OIDC server nobody configured,
/// and one where a realm was chosen and reaching Keycloak failed. Telling an operator to type an issuer
/// URL is the right answer to the first and precisely the wrong one to the second, where it works round
/// a transient failure by hardcoding past it.</para>
/// </summary>
public class StalwartOidcPreflightTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly StalwartService mail;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid componentId = Guid.NewGuid();

    public StalwartOidcPreflightTests()
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

        // Only the database is reached on the OIDC path: the vault is consulted for an internal
        // administrator password and the Kubernetes client for a certificate, neither of which this
        // exercises. Passing nulls says so, and would fail loudly rather than quietly if that changed.
        TestDbContextFactory oidcFactory = new(connection);
        mail = new StalwartService(
            oidcFactory,
            null!, null!, null!, null!, null!, null!,
            new EntKube.Web.Modules.Api.CatalogApi(oidcFactory),
            NullLogger<StalwartService>.Instance);
    }

    private async Task<StalwartComponentConfig> GivenAnOidcServerAsync(
        Action<StalwartComponentConfig> edit)
    {
        StalwartComponentConfig config = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterComponentId = componentId,
            Hostname = "mail.example.com",
            AdminUsername = "admin",
            AuthMode = StalwartAuthMode.Oidc,
        };
        edit(config);

        StalwartMailDomain domain = new()
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, Name = "example.com", IsPrimary = true,
        };

        db.StalwartComponentConfigs.Add(config);
        db.StalwartMailDomains.Add(domain);

        // OIDC cannot discover accounts, so one has to exist or that is the finding instead.
        db.StalwartMailAccounts.Add(new StalwartMailAccount
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, DomainId = domain.Id, LocalPart = "admin",
        });

        await db.SaveChangesAsync();
        return config;
    }

    [Fact]
    public async Task Nothing_chosen_is_told_to_choose_a_realm_not_to_type_a_URL()
    {
        await GivenAnOidcServerAsync(c =>
        {
            c.OidcKeycloakRealmId = null;
            c.OidcIssuerUrl = null;
        });

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        StalwartService.MailPreflightIssue issue = issues.Should()
            .ContainSingle(i => i.Problem.Contains("no identity provider")).Subject;

        issue.IsBlocking.Should().BeTrue();
        issue.Remedy.Should().Contain("Choose a Keycloak realm");
    }

    [Fact]
    public async Task A_realm_whose_issuer_did_not_resolve_says_Keycloak_could_not_be_reached()
    {
        // The distinction that matters. An issuer typed by hand here would work round a transient
        // failure by hardcoding past it, and then the realm and the URL can drift apart silently.
        await GivenAnOidcServerAsync(c =>
        {
            c.OidcKeycloakRealmId = Guid.NewGuid();
            c.OidcIssuerUrl = null;
        });

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        StalwartService.MailPreflightIssue issue = issues.Should()
            .ContainSingle(i => i.Problem.Contains("could not be resolved")).Subject;

        issue.IsBlocking.Should().BeTrue();
        issue.Remedy.Should().Contain("rather than entering the issuer by hand");
    }

    [Fact]
    public async Task A_resolved_realm_with_no_audience_client_is_the_silent_failure_and_blocks()
    {
        // This one looks configured: the issuer is there. What did not happen is the client that
        // stamps the audience, and without it the server requires an audience of "stalwart" that no
        // token from that realm carries — so every sign-in is refused by a server reporting itself
        // healthy. Blocking, because an apply restarts the server twice to reach that state.
        await GivenAnOidcServerAsync(c =>
        {
            c.OidcKeycloakRealmId = Guid.NewGuid();
            c.OidcIssuerUrl = "https://sso.example.com/realms/mail";
            c.OidcRequireAudience = null;
        });

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().ContainSingle(i => i.Problem.Contains("audience"))
            .Which.IsBlocking.Should().BeTrue();
    }

    [Fact]
    public async Task A_realm_that_resolved_and_has_its_client_raises_nothing()
    {
        await GivenAnOidcServerAsync(c =>
        {
            c.OidcKeycloakRealmId = Guid.NewGuid();
            c.OidcIssuerUrl = "https://sso.example.com/realms/mail";
            c.OidcRequireAudience = "stalwart-mail.example.com";
        });

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Where(i => i.IsBlocking).Should().BeEmpty(
            string.Join(" | ", issues.Select(i => i.Problem)));
    }

    /// <summary>
    /// An issuer typed by hand, with no realm, is still a valid way to configure a provider EntKube
    /// does not run — so it must not be dragged into the audience finding, which only applies where
    /// EntKube was the one that could have created the client.
    /// </summary>
    [Fact]
    public async Task A_hand_entered_issuer_is_not_asked_for_a_client_EntKube_cannot_create()
    {
        await GivenAnOidcServerAsync(c =>
        {
            c.OidcKeycloakRealmId = null;
            c.OidcIssuerUrl = "https://login.microsoftonline.com/tenant/v2.0";
            c.OidcRequireAudience = null;
        });

        List<StalwartService.MailPreflightIssue> issues =
            await mail.PreflightAsync(tenantId, componentId);

        issues.Should().NotContain(i => i.Problem.Contains("audience"));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
