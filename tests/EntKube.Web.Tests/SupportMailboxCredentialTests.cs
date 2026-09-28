using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The credential for a support mailbox on a Stalwart server EntKube manages.
///
/// <para>Choosing a server and an account is meant to be the whole of the configuration — the screen
/// deliberately does not ask for a password, because on a managed server the operator has no way to
/// know one. That only works if something creates it, and for a server that checks passwords itself
/// nothing did: a Stalwart account is written by the Mailboxes tab with no credential at all (only the
/// administrator gets one), so the mailbox could never authenticate and the server answered every
/// login with a temporary failure.</para>
///
/// <para>The other half is the window between creating a password and the mail server being told it.
/// Until the configuration is applied, EntKube holds a credential Stalwart has never seen — which
/// looks exactly like a rejected one and needs the opposite treatment.</para>
/// </summary>
public class SupportMailboxCredentialTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly SupportMailboxService mailboxes;
    private readonly VaultService vault;
    private readonly Guid tenantId = Guid.NewGuid();

    public SupportMailboxCredentialTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        vault = new VaultService(factory, new VaultEncryptionService(TestRootKey));

        // A real vault: creating the password is the thing under test, and a null one would be
        // swallowed by the same catch that reports a Keycloak failure — so every test here would pass
        // while nothing was minted.
        mailboxes = new SupportMailboxService(
            factory, vault, null!, null!, null!,
            NullLogger<SupportMailboxService>.Instance);
    }

    private async Task<(Guid ComponentId, Guid AccountId, StalwartComponentConfig Config)>
        GivenAServerAsync(StalwartAuthMode mode)
    {
        Guid clusterId = Guid.NewGuid();
        Guid componentId = Guid.NewGuid();
        Guid environmentId = Guid.NewGuid();

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
            Id = componentId, ClusterId = clusterId, Name = "stalwart",
            ComponentType = "Manifest", ReleaseName = "mail", Namespace = "messaging",
            Status = ComponentStatus.Installed,
        });

        StalwartComponentConfig config = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClusterComponentId = componentId,
            Hostname = "mail.example.com", AuthMode = mode,
            // Already converged, so a password minted after this is the pending case and one minted
            // before it is live. Tests that care set it explicitly.
            LastAppliedAt = DateTime.UtcNow,
        };
        StalwartMailDomain domain = new()
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, Name = "example.com", IsPrimary = true,
        };
        StalwartMailAccount account = new()
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, DomainId = domain.Id, LocalPart = "support",
        };

        db.StalwartComponentConfigs.Add(config);
        db.StalwartMailDomains.Add(domain);
        db.StalwartMailAccounts.Add(account);
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);

        return (componentId, account.Id, config);
    }

    private Task<SupportMailbox> ChooseAsync(Guid componentId, Guid accountId) =>
        mailboxes.SaveAsync(new SupportMailbox
        {
            TenantId = tenantId,
            StalwartComponentId = componentId,
            StalwartAccountId = accountId,
            Host = "",
            Username = "",
            IsEnabled = true,
        }, password: null);

    private Task<string?> StoredPasswordAsync(Guid componentId, Guid accountId) =>
        vault.GetComponentSecretValueAsync(
            tenantId, componentId, StalwartManifestBuilder.AccountPasswordSecretName(accountId));

    [Fact]
    public async Task Choosing_a_mailbox_on_a_password_server_creates_its_password()
    {
        (Guid componentId, Guid accountId, _) = await GivenAServerAsync(StalwartAuthMode.Internal);

        await ChooseAsync(componentId, accountId);

        (await StoredPasswordAsync(componentId, accountId)).Should().NotBeNullOrWhiteSpace(
            "choosing a server and an account is meant to be the whole of the configuration");

        StalwartMailAccount account = await db.StalwartMailAccounts
            .AsNoTracking().FirstAsync(a => a.Id == accountId);

        account.PasswordSetAt.Should().NotBeNull(
            "without this there is no way to tell a password the server has not been given from one "
            + "it has rejected");
    }

    [Fact]
    public async Task Saving_again_does_not_change_the_password()
    {
        // Rotating on every save would invalidate the password the running server is checking against,
        // so any save — including one that changed nothing — would break fetching until the mail
        // server was applied again.
        (Guid componentId, Guid accountId, _) = await GivenAServerAsync(StalwartAuthMode.Internal);

        await ChooseAsync(componentId, accountId);
        string? first = await StoredPasswordAsync(componentId, accountId);
        DateTime? firstSetAt = (await db.StalwartMailAccounts
            .AsNoTracking().FirstAsync(a => a.Id == accountId)).PasswordSetAt;

        await ChooseAsync(componentId, accountId);

        (await StoredPasswordAsync(componentId, accountId)).Should().Be(first);
        (await db.StalwartMailAccounts.AsNoTracking().FirstAsync(a => a.Id == accountId))
            .PasswordSetAt.Should().Be(firstSetAt);
    }

    [Fact]
    public async Task A_password_the_server_has_not_been_given_says_to_apply_the_mail_server()
    {
        (Guid componentId, Guid accountId, StalwartComponentConfig config) =
            await GivenAServerAsync(StalwartAuthMode.Internal);

        // Never converged, so whatever is minted now cannot be known to the running server.
        config.LastAppliedAt = null;
        await db.SaveChangesAsync();

        await ChooseAsync(componentId, accountId);

        MailPollResult result = await mailboxes.TestAsync(tenantId);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("has not been applied");
    }

    [Fact]
    public async Task That_is_not_counted_as_a_failure_so_fetching_is_not_given_up_on()
    {
        // The bug this exists for. Backing off after five failures is right for a rejected password —
        // presenting one repeatedly is how an account gets locked out — and exactly wrong here: nothing
        // was presented, so nothing can be locked, and the remedy is applying the mail server. Counted,
        // the mailbox would still be backed off after the operator did precisely what it asked.
        (Guid componentId, Guid accountId, StalwartComponentConfig config) =
            await GivenAServerAsync(StalwartAuthMode.Internal);

        config.LastAppliedAt = null;
        await db.SaveChangesAsync();

        await ChooseAsync(componentId, accountId);

        for (int i = 0; i < SupportMailboxService.FailuresBeforeBackingOff + 2; i++)
        {
            await mailboxes.PollAsync(tenantId);
        }

        SupportMailbox mailbox = await db.SupportMailboxes
            .AsNoTracking().FirstAsync(m => m.TenantId == tenantId);

        mailbox.ConsecutiveFailures.Should().Be(0);
        mailbox.LastError.Should().Contain("has not been applied");
    }

    [Fact]
    public async Task Once_the_server_has_been_applied_the_password_is_presented()
    {
        // The other side of the same comparison, without which the test above would pass just as well
        // if every mailbox were treated as pending for ever.
        (Guid componentId, Guid accountId, StalwartComponentConfig config) =
            await GivenAServerAsync(StalwartAuthMode.Internal);

        await ChooseAsync(componentId, accountId);

        config.LastAppliedAt = DateTime.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();

        MailPollResult result = await mailboxes.TestAsync(tenantId);

        // It still fails — there is no mail server at that Service name — but on the connection, which
        // means the credential was resolved and presented rather than refused before it was tried.
        result.Ok.Should().BeFalse();
        result.Error.Should().NotContain("has not been applied");
        result.Error.Should().NotContain("No password has been created");
    }

    [Fact]
    public async Task A_connection_that_never_completed_is_not_recorded_as_a_rejection()
    {
        // What the screen says about giving up has to match what happened. It used to say a password was
        // being rejected for every kind of failure, including this one — where the connection never got
        // as far as authenticating and no credential was presented at all — and that wording sent two
        // debugging sessions after the credential while the error beside it said otherwise.
        (Guid componentId, Guid accountId, StalwartComponentConfig config) =
            await GivenAServerAsync(StalwartAuthMode.Internal);

        await ChooseAsync(componentId, accountId);

        config.LastAppliedAt = DateTime.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();

        await mailboxes.PollAsync(tenantId);

        SupportMailbox mailbox = await db.SupportMailboxes
            .AsNoTracking().FirstAsync(m => m.TenantId == tenantId);

        mailbox.ConsecutiveFailures.Should().Be(1, "there is no mail server at that Service name");
        mailbox.LastErrorWasRejection.Should().BeFalse();

        // And the message points at the thing that actually could not be done, naming the address —
        // which is derived from the component, never typed, so an operator has no other way to see it.
        mailbox.LastError.Should().Contain("Nothing was authenticated");
        mailbox.LastError.Should().Contain("mail.messaging.svc.cluster.local:993");
    }

    [Fact]
    public async Task A_hand_entered_password_is_removed_when_the_mailbox_moves_to_a_managed_server()
    {
        // It is a credential for a different account on a different server, and no code path can reach
        // it any more. Left in the vault it still makes the settings screen report that a password is
        // stored, for an account nobody polls.
        (Guid componentId, Guid accountId, _) = await GivenAServerAsync(StalwartAuthMode.Internal);

        SupportMailbox typed = await mailboxes.SaveAsync(new SupportMailbox
        {
            TenantId = tenantId,
            Host = "imap.elsewhere.example",
            Port = 993,
            UseSsl = true,
            Username = "support@elsewhere.example",
        }, password: "the-old-one");

        (await vault.HasSupportMailboxPasswordAsync(tenantId, typed.Id)).Should().BeTrue();

        await ChooseAsync(componentId, accountId);

        (await vault.HasSupportMailboxPasswordAsync(tenantId, typed.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task An_LDAP_server_says_EntKube_cannot_create_the_credential()
    {
        // Honest rather than silent. The credential is an entry's password in a directory EntKube does
        // not write, so minting one here would store a password nothing ever checks — and the mailbox
        // would look configured and never work.
        (Guid componentId, Guid accountId, _) = await GivenAServerAsync(StalwartAuthMode.Ldap);

        SupportMailbox saved = await ChooseAsync(componentId, accountId);

        saved.LastError.Should().Contain("EntKube cannot create one");
        (await StoredPasswordAsync(componentId, accountId)).Should().BeNull();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
