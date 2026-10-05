using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services.Mail;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// Where outbound mail goes.
///
/// <para><b>The failure these were written for.</b> Everything about <em>fetching</em> support
/// mail is derived from the mail server an operator picked — address, port, TLS, login,
/// credential — and sending was left to a notification-provider row that nothing ever said was
/// needed. So a support mailbox set up by choosing a server and a mailbox could fetch perfectly
/// and send nothing: mail arrived, tickets opened, receipts went nowhere, and every screen
/// looked healthy.</para>
///
/// <para>The rule now is that the tenant's own mail server answers first. That is not only
/// convenience — the receipt is sent <em>from the support address</em>, whose domain that server
/// publishes SPF and DKIM for, so any other relay produces mail that fails both.</para>
/// </summary>
public class SmtpSettingsResolverTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();

    public SmtpSettingsResolverTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        factory = new TestDbContextFactory(connection);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private SmtpSettingsResolver Resolver(params (string Key, string Value)[] configuration) =>
        new(
            factory,
            new ConfigurationBuilder()
                .AddInMemoryCollection(configuration.Select(
                    c => new KeyValuePair<string, string?>(c.Key, c.Value)))
                .Build());

    private void ProviderRow(bool enabled, Guid? owner = null, string host = "relay.example")
    {
        db.NotificationProviderConfigs.Add(new NotificationProviderConfig
        {
            TenantId = owner ?? tenantId,
            ProviderType = NotificationProviderType.Smtp,
            IsEnabled = enabled,
            ConfigurationJson = JsonSerializer.Serialize(new
            {
                host,
                port = 587,
                from = "alerts@entit.se",
            }),
            UpdatedAt = DateTime.UtcNow,
        });

        db.SaveChanges();
    }

    /// <summary>
    /// <b>The state the installation was actually in.</b> No provider row, nothing in
    /// configuration — and nothing anywhere saying that a support mailbox which fetches happily
    /// has no way to answer.
    /// </summary>
    [Fact]
    public async Task With_nothing_configured_there_is_nowhere_to_send()
    {
        SmtpSettings settings = await Resolver().ResolveAsync(tenantId);

        settings.IsConfigured.Should().BeFalse();
        settings.Source.Should().Be("nothing configured");
    }

    /// <summary>
    /// A provider row that exists but is switched off is not a configuration. It used to fall
    /// through silently to an empty <c>Smtp:</c> section, which is the same nowhere.
    /// </summary>
    [Fact]
    public async Task A_disabled_provider_is_not_a_place_to_send()
    {
        ProviderRow(enabled: false);

        (await Resolver().ResolveAsync(tenantId)).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task An_enabled_provider_answers_and_says_it_did()
    {
        ProviderRow(enabled: true);

        SmtpSettings settings = await Resolver().ResolveAsync(tenantId);

        settings.Host.Should().Be("relay.example");
        settings.From.Should().Be("alerts@entit.se");
        settings.Source.Should().Be("the tenant's SMTP notification provider");
    }

    /// <summary>
    /// The configuration section is the last answer, and it says so too. Every one of these
    /// three states produces the same symptom — no mail — and they have three different
    /// remedies, so the answer carries where it came from.
    /// </summary>
    [Fact]
    public async Task The_configuration_section_is_the_last_answer()
    {
        SmtpSettings settings = await Resolver(
            ("Smtp:Host", "smtp.example"), ("Smtp:FromAddress", "noreply@entit.se"))
            .ResolveAsync(tenantId);

        settings.Host.Should().Be("smtp.example");
        settings.Source.Should().Be("the Smtp: configuration section");
    }

    /// <summary>
    /// <b>A mailbox somebody typed in is an address to read, not a relay to send through.</b>
    /// Nothing about being able to fetch from a server says we may submit to it, and guessing
    /// would present the mailbox's password to a port nobody said was open.
    /// </summary>
    [Fact]
    public async Task A_hand_entered_mailbox_is_not_treated_as_a_way_to_send()
    {
        db.SupportMailboxes.Add(new SupportMailbox
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Host = "imap.kund.example",
            Username = "support@kund.example",
            Address = "support@kund.example",
            IsEnabled = true,
        });
        await db.SaveChangesAsync();

        (await Resolver().ResolveAsync(tenantId)).IsConfigured.Should().BeFalse();
    }

    /// <summary>
    /// <b>Without a tenant there is no provider to find.</b> Provider rows belong to a tenant,
    /// so a caller that does not say whose mail this is gets the installation configuration or
    /// nothing — never some tenant's relay picked arbitrarily. Returning the first row found
    /// would mean sending one customer's mail through another customer's authenticated relay.
    /// </summary>
    [Fact]
    public async Task Without_a_tenant_no_tenants_relay_is_borrowed()
    {
        ProviderRow(enabled: true);

        (await Resolver().ResolveAsync()).IsConfigured.Should().BeFalse();

        SmtpSettings configured = await Resolver(("Smtp:Host", "smtp.example")).ResolveAsync();
        configured.Source.Should().Be("the Smtp: configuration section");
    }

    /// <summary>
    /// <b>One tenant's relay is invisible to another.</b> The whole reason these rows moved
    /// under a tenant: the row holds an SMTP password, and the unique index is on
    /// (tenant, kind) so each tenant configures its own without displacing anyone else's.
    /// </summary>
    [Fact]
    public async Task A_tenants_provider_is_not_visible_to_another_tenant()
    {
        Guid other = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = other, Name = "Other", Slug = "other" });
        db.SaveChanges();

        ProviderRow(enabled: true, owner: other, host: "relay.other.example");

        // Our tenant has configured nothing, and must not inherit theirs.
        (await Resolver().ResolveAsync(tenantId)).IsConfigured.Should().BeFalse();

        // Theirs still answers for them.
        (await Resolver().ResolveAsync(other)).Host.Should().Be("relay.other.example");
    }

    /// <summary>
    /// Both tenants configuring SMTP is the ordinary case, not a unique-index collision —
    /// which is exactly what the old single-column index on ProviderType made it.
    /// </summary>
    [Fact]
    public async Task Two_tenants_can_each_configure_their_own()
    {
        Guid other = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = other, Name = "Other", Slug = "other" });
        db.SaveChanges();

        ProviderRow(enabled: true, host: "relay.ours.example");
        ProviderRow(enabled: true, owner: other, host: "relay.theirs.example");

        (await Resolver().ResolveAsync(tenantId)).Host.Should().Be("relay.ours.example");
        (await Resolver().ResolveAsync(other)).Host.Should().Be("relay.theirs.example");
    }
}
