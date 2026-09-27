using System.Reflection;
using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Saving the mailbox settings over the ones already stored.
///
/// <para><b>Why a reflective test rather than a few assertions.</b> Updating an existing
/// mailbox copies the settings across field by field, and a field left out of that list
/// fails in the quietest way there is: the screen accepts the change, says it saved,
/// reloads showing the new value from the form — and the poller goes on using the old one
/// until somebody reopens the page in a fresh session. Nothing builds wrong and no test
/// about the feature itself notices, because the feature works; only the saving does not.
/// This one fails the moment a new setting is added and not copied.</para>
/// </summary>
public class SupportMailboxSettingsTests : IDisposable
{
    /// <summary>
    /// What the poller writes for itself, and what nobody sets. Everything else on the
    /// entity is configuration, and so must survive a save — including whatever is added
    /// next, which is the whole point of listing the exceptions rather than the rule.
    /// </summary>
    /// <summary>
    /// The chain a mailbox on a managed server needs: a cluster, the component, its config, a domain
    /// and the account. Written out because the foreign keys are real — an account cannot exist
    /// without the config it belongs to, which cannot exist without the component it configures.
    /// </summary>
    private async Task<(Guid ComponentId, Guid AccountId)> GivenAStalwartMailboxAsync(
        string localPart, string domainName)
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
        });

        StalwartComponentConfig config = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClusterComponentId = componentId,
            Hostname = "mail.example.com",
        };
        StalwartMailDomain domain = new()
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, Name = domainName, IsPrimary = true,
        };
        StalwartMailAccount account = new()
        {
            Id = Guid.NewGuid(), ConfigId = config.Id, DomainId = domain.Id, LocalPart = localPart,
        };

        db.StalwartComponentConfigs.Add(config);
        db.StalwartMailDomains.Add(domain);
        db.StalwartMailAccounts.Add(account);
        await db.SaveChangesAsync();

        return (componentId, account.Id);
    }

    [Fact]
    public async Task A_chosen_mailbox_supplies_the_address_replies_come_from()
    {
        // Left blank, TicketNotifier falls back to the From in appsettings — so acknowledgements go to
        // customers from whatever that happens to be. Fetching works, the mailbox reports healthy, and
        // only the replies are addressed from somewhere nobody reads.
        (Guid componentId, Guid accountId) =
            await GivenAStalwartMailboxAsync("Support", "Example.COM");

        SupportMailbox saved = await mailboxes.SaveAsync(new SupportMailbox
        {
            TenantId = tenantId,
            StalwartComponentId = componentId,
            StalwartAccountId = accountId,
            Host = "",
            Username = "",
            Address = null,
        }, password: null);

        // Lower-cased and joined, however the account and domain were capitalised.
        saved.Address.Should().Be("support@example.com");
    }

    [Fact]
    public async Task An_address_somebody_set_is_left_alone()
    {
        // Replying from an alias rather than the mailbox the mail arrived in is a legitimate thing to
        // want, and filling this in on every save would make it impossible to express.
        (Guid componentId, Guid accountId) =
            await GivenAStalwartMailboxAsync("support", "example.com");

        SupportMailbox saved = await mailboxes.SaveAsync(new SupportMailbox
        {
            TenantId = tenantId,
            StalwartComponentId = componentId,
            StalwartAccountId = accountId,
            Host = "",
            Username = "",
            Address = "helpdesk@example.com",
        }, password: null);

        saved.Address.Should().Be("helpdesk@example.com");
    }

    /// <summary>
    /// What an operator is told when a mailbox points at something that has gone, or at a server that
    /// cannot serve it.
    ///
    /// <para>These all resolve before any connection is attempted, which is the point: the message
    /// names what to fix rather than arriving as a timeout, a null reference, or a mailbox that simply
    /// stays empty. Every one of them is reachable in normal use — a component uninstalled, a mailbox
    /// removed on the Mail tab, IMAP switched off, a server changed to OIDC after the mailbox was
    /// configured.</para>
    /// </summary>
    [Theory]
    [InlineData("component", "no longer exists")]
    [InlineData("account", "has been removed")]
    [InlineData("imap-off", "IMAP is switched off")]
    [InlineData("no-service-account", "no service account has been created")]
    public async Task A_mailbox_that_cannot_be_resolved_says_why(string broken, string expected)
    {
        (Guid componentId, Guid accountId) =
            await GivenAStalwartMailboxAsync("support", "example.com");

        StalwartComponentConfig config = await db.StalwartComponentConfigs
            .FirstAsync(c => c.ClusterComponentId == componentId);

        switch (broken)
        {
            case "component":
                db.ClusterComponents.Remove(await db.ClusterComponents.FirstAsync(c => c.Id == componentId));
                break;

            case "account":
                db.StalwartMailAccounts.Remove(
                    await db.StalwartMailAccounts.FirstAsync(a => a.Id == accountId));
                break;

            case "imap-off":
                config.ImapEnabled = false;
                break;

            case "no-service-account":
                // The server was changed to OIDC after the mailbox was set up, so the token it now
                // needs has nothing to mint it. Saying "save it again" is the actual fix.
                config.AuthMode = StalwartAuthMode.Oidc;
                break;
        }

        await db.SaveChangesAsync();

        await mailboxes.SaveAsync(new SupportMailbox
        {
            TenantId = tenantId,
            StalwartComponentId = componentId,
            StalwartAccountId = accountId,
            Host = "",
            Username = "",
            Address = "support@example.com",
        }, password: null);

        MailPollResult result = await mailboxes.TestAsync(tenantId);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain(expected);
    }

    private static readonly HashSet<string> NotConfiguration =
    [
        nameof(SupportMailbox.Id),
        nameof(SupportMailbox.TenantId),
        nameof(SupportMailbox.LastSeenUid),
        nameof(SupportMailbox.LastUidValidity),
        nameof(SupportMailbox.LastPolledAt),
        nameof(SupportMailbox.LastMessageAt),
        nameof(SupportMailbox.LastError),
        nameof(SupportMailbox.ConsecutiveFailures),
        nameof(SupportMailbox.CreatedAt),
        nameof(SupportMailbox.UpdatedAt),
        nameof(SupportMailbox.Tenant),

        // Not settings: EntKube writes these itself when a mailbox names a Stalwart server whose
        // directory is OIDC, by creating the service account that mints its tokens. There is nothing
        // for an operator to type, and copying them from a submitted form would let one be pointed at
        // a client they do not own.
        nameof(SupportMailbox.OAuthClientId),
        nameof(SupportMailbox.OAuthTokenEndpoint),
        nameof(SupportMailbox.OAuthScopes),
    ];

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly SupportMailboxService mailboxes;
    private readonly Guid tenantId = Guid.NewGuid();

    public SupportMailboxSettingsTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        // The vault, the ingest pipeline, the token provider and Keycloak are only reached by a
        // poll or by saving a mailbox that names a Stalwart server, none of which happens here.
        mailboxes = new SupportMailboxService(
            new TestDbContextFactory(connection), null!, null!, null!, null!,
            NullLogger<SupportMailboxService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IEnumerable<PropertyInfo> Settings =>
        typeof(SupportMailbox).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !NotConfiguration.Contains(p.Name));

    [Fact]
    public async Task Every_setting_survives_a_save_over_an_existing_mailbox()
    {
        await mailboxes.SaveAsync(
            new SupportMailbox
            {
                TenantId = tenantId,
                Host = "imap.entit.example",
                Username = "support@entit.example",
            },
            password: null);

        SupportMailbox changed = new() { TenantId = tenantId, Host = "", Username = "" };
        Dictionary<string, object?> intended = [];

        foreach (PropertyInfo property in Settings)
        {
            object? value = Distinctive(property);
            property.SetValue(changed, value);
            intended[property.Name] = value;
        }

        await mailboxes.SaveAsync(changed, password: null);

        SupportMailbox? stored = await db.SupportMailboxes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId);

        stored.Should().NotBeNull();

        foreach ((string name, object? value) in intended)
        {
            typeof(SupportMailbox).GetProperty(name)!.GetValue(stored)
                .Should().Be(value, $"the {name} somebody typed is what the poller must use");
        }
    }

    /// <summary>A value nothing else would produce, so a field left uncopied cannot pass.</summary>
    private static object? Distinctive(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string))
        {
            return $"{property.Name}-set-by-the-test";
        }

        if (type == typeof(bool))
        {
            // The opposite of whatever a new mailbox starts with.
            return !(bool)(property.GetValue(new SupportMailbox { Host = "", Username = "" })
                           ?? false);
        }

        if (type == typeof(int))
        {
            return 3607;
        }

        if (type == typeof(Guid))
        {
            // A reference to a mail server or an account. Any value will do — what is being checked is
            // that the save copies it, which is the mistake this test exists for: a new field added to
            // the form and to the entity, and left out of the assignments in between, looks saved and
            // is not.
            return Guid.Parse("00000000-0000-0000-0000-0000000000ff");
        }

        if (type.IsEnum)
        {
            // Any value that is not the default, so leaving the field out cannot match.
            return Enum.GetValues(type).Cast<object>()
                .First(v => (int)v != 0);
        }

        throw new InvalidOperationException(
            $"{property.Name} is a {type.Name}, which this test does not know how to set. "
            + "Teach it, or add the property to NotConfiguration and say why.");
    }
}
