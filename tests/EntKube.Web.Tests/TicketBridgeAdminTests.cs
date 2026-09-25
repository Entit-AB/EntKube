using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Tickets.Bridge;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// Configuring a customer's ticketing system.
///
/// <para>The reflective test here is the same one the mailbox settings have, for the same
/// reason: a field left out of a field-by-field copy saves silently, the screen reloads
/// showing the value from the form, and the bridge goes on using the old one. It fails the
/// moment a new setting is added and not copied.</para>
/// </summary>
public class TicketBridgeAdminTests : IDisposable
{
    /// <summary>
    /// What the bridge writes for itself, and what nobody edits on the form. Everything
    /// else is configuration and must survive a save — including whatever is added next.
    /// </summary>
    private static readonly HashSet<string> NotConfiguration =
    [
        nameof(TicketBridgeConnection.Id),
        nameof(TicketBridgeConnection.TenantId),
        // Issued, never typed: it has its own door, and it is a hash by the time it is here.
        nameof(TicketBridgeConnection.SecretHash),
        nameof(TicketBridgeConnection.LastDeliveryAt),
        nameof(TicketBridgeConnection.LastError),
        nameof(TicketBridgeConnection.ConsecutiveFailures),
        nameof(TicketBridgeConnection.CreatedAt),
        nameof(TicketBridgeConnection.UpdatedAt),
        nameof(TicketBridgeConnection.Tenant),
        nameof(TicketBridgeConnection.Customer),
        nameof(TicketBridgeConnection.DefaultApp),
    ];

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketBridgeAdmin admin;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid otherCustomerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public TicketBridgeAdminTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Customers.Add(new Customer { Id = otherCustomerId, TenantId = tenantId, Name = "Entit Europe" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
        db.SaveChanges();

        admin = new TicketBridgeAdmin(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<TicketBridgeConnection> Existing() => admin.SaveAsync(new TicketBridgeConnection
    {
        TenantId = tenantId,
        CustomerId = customerId,
        System = TicketSystem.ServiceNow,
        Instance = "entit.service-now.com",
    });

    private static IEnumerable<PropertyInfo> Settings =>
        typeof(TicketBridgeConnection).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !NotConfiguration.Contains(p.Name));

    [Fact]
    public async Task Every_setting_survives_a_save_over_an_existing_connection()
    {
        TicketBridgeConnection saved = await Existing();

        TicketBridgeConnection changed = new()
        {
            Id = saved.Id,
            TenantId = tenantId,
            Instance = "",
        };

        Dictionary<string, object?> intended = [];

        foreach (PropertyInfo property in Settings)
        {
            object? value = Distinctive(property);
            property.SetValue(changed, value);
            intended[property.Name] = value;
        }

        await admin.SaveAsync(changed);

        TicketBridgeConnection? stored = await db.TicketBridgeConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == saved.Id);

        stored.Should().NotBeNull();

        foreach ((string name, object? value) in intended)
        {
            typeof(TicketBridgeConnection).GetProperty(name)!.GetValue(stored)
                .Should().Be(value, $"the {name} somebody chose is what the bridge must use");
        }
    }

    private object? Distinctive(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string))
        {
            return $"{property.Name}-set-by-the-test";
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(Guid))
        {
            // A real row, because these are foreign keys: CustomerId to a different
            // customer than the one saved, DefaultAppId to an application.
            return property.Name == nameof(TicketBridgeConnection.DefaultAppId)
                ? appId
                : otherCustomerId;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object>().First(v => (int)v != 0);
        }

        throw new InvalidOperationException(
            $"{property.Name} is a {type.Name}, which this test does not know how to set. "
            + "Teach it, or add the property to NotConfiguration and say why.");
    }

    // ---- The secret ---------------------------------------------------------------------

    /// <summary>
    /// Issued once and stored as a hash. What comes back is the only copy there will ever
    /// be, and what is kept cannot be turned back into it.
    /// </summary>
    [Fact]
    public async Task An_issued_secret_is_returned_once_and_stored_hashed()
    {
        TicketBridgeConnection saved = await Existing();

        string? secret = await admin.IssueSecretAsync(saved.Id);

        secret.Should().NotBeNullOrWhiteSpace();

        TicketBridgeConnection stored = await db.TicketBridgeConnections
            .AsNoTracking().FirstAsync(c => c.Id == saved.Id);

        stored.SecretHash.Should().NotBeNullOrWhiteSpace();
        stored.SecretHash.Should().NotContain(secret!);
        BridgeSecret.Matches(secret, stored.SecretHash).Should().BeTrue();
    }

    /// <summary>
    /// Reissuing is what somebody does when a secret has leaked, so the old one has to stop
    /// working at once — a grace period would be the whole point missed.
    /// </summary>
    [Fact]
    public async Task Reissuing_stops_the_old_secret_working()
    {
        TicketBridgeConnection saved = await Existing();

        string? first = await admin.IssueSecretAsync(saved.Id);
        await admin.IssueSecretAsync(saved.Id);

        TicketBridgeConnection stored = await db.TicketBridgeConnections
            .AsNoTracking().FirstAsync(c => c.Id == saved.Id);

        BridgeSecret.Matches(first, stored.SecretHash).Should().BeFalse();
    }

    // ---- Deleting ------------------------------------------------------------------------

    [Fact]
    public async Task A_connection_nothing_came_in_on_can_be_removed()
    {
        TicketBridgeConnection saved = await Existing();

        (await admin.DeleteAsync(saved.Id)).Should().BeNull();
        (await db.TicketBridgeConnections.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// <b>Refused once tickets point at it.</b> The links are how a ticket says where it
    /// came from, and §14.6 makes that part of the record between the parties. Switching it
    /// off stops the traffic and keeps the history.
    /// </summary>
    [Fact]
    public async Task A_connection_tickets_came_in_on_is_not_deleted()
    {
        TicketBridgeConnection saved = await Existing();

        Guid ticketId = Guid.NewGuid();

        db.Tickets.Add(new Ticket
        {
            Id = ticketId,
            TenantId = tenantId,
            CustomerId = customerId,
            Number = 1,
            Title = "Fel",
            Channel = TicketChannel.Integration,
            ReportedAt = DateTime.UtcNow,
        });

        db.ExternalTicketLinks.Add(new ExternalTicketLink
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TicketId = ticketId,
            ConnectionId = saved.Id,
            System = TicketSystem.ServiceNow,
            Instance = "entit.service-now.com",
            ExternalId = "abc",
        });

        await db.SaveChangesAsync();

        (await admin.DeleteAsync(saved.Id)).Should().NotBeNullOrWhiteSpace();
        (await db.TicketBridgeConnections.CountAsync()).Should().Be(1);
    }
}

/// <summary>
/// The connection screen as it is actually rendered.
///
/// <para>The same category of bug this branch keeps finding lives here: a form that saves
/// and a value that never reaches the row. These check the wiring, not the logic.</para>
/// </summary>
public class TicketBridgeRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketBridgeAdmin admin;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();

    public TicketBridgeRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        admin = new TicketBridgeAdmin(factory);

        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
        Services.AddSingleton(admin);
        Services.AddSingleton(new ToastService());
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private IRenderedComponent<TicketBridgeTab> Tab() =>
        Render<TicketBridgeTab>(p => p.Add(c => c.TenantId, tenantId));

    [Fact]
    public void With_nothing_connected_the_screen_says_so() =>
        Tab().Markup.Should().Contain("No customer system is connected");

    /// <summary>
    /// The endpoint their system posts to has to be on screen. An integration whose address
    /// is only in the source is one nobody can configure.
    /// </summary>
    [Fact]
    public async Task An_existing_connection_shows_the_address_to_post_to()
    {
        TicketBridgeConnection saved = await admin.SaveAsync(new TicketBridgeConnection
        {
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.ServiceNow,
            Instance = "entit.service-now.com",
            IsEnabled = true,
        });

        IRenderedComponent<TicketBridgeTab> tab = Tab();
        tab.FindAll("button").First(b => b.TextContent.Contains("Edit")).Click();

        tab.Markup.Should().Contain($"/api/tickets/inbound/{saved.Id}");
    }

    /// <summary>
    /// A connection with no secret cannot receive anything, so the list has to say that
    /// rather than showing it as on.
    /// </summary>
    [Fact]
    public async Task A_connection_without_a_secret_is_flagged()
    {
        await admin.SaveAsync(new TicketBridgeConnection
        {
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.Jira,
            Instance = "entit.atlassian.net",
            IsEnabled = true,
        });

        Tab().Markup.Should().Contain("no secret");
    }

    /// <summary>
    /// The secret is shown once. If the screen does not put it in front of somebody at the
    /// moment it is issued, it is gone — so this is the one piece of markup that has to be
    /// there.
    /// </summary>
    [Fact]
    public async Task A_newly_issued_secret_is_shown_once()
    {
        TicketBridgeConnection saved = await admin.SaveAsync(new TicketBridgeConnection
        {
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.Jira,
            Instance = "entit.atlassian.net",
        });

        IRenderedComponent<TicketBridgeTab> tab = Tab();
        tab.FindAll("button").First(b => b.TextContent.Contains("New secret")).Click();

        // The button issues the secret asynchronously and re-renders when it finishes.
        // Asserted on a phrase that does not straddle a line break in the markup.
        tab.WaitForAssertion(
            () => tab.Markup.Should().Contain("time anybody sees it"),
            TimeSpan.FromSeconds(5));

        TicketBridgeConnection stored = await db.TicketBridgeConnections
            .AsNoTracking().FirstAsync(c => c.Id == saved.Id);

        // The markup carries the real secret, not a placeholder.
        string shown = tab.Find("code.user-select-all").TextContent.Trim();
        BridgeSecret.Matches(shown, stored.SecretHash).Should().BeTrue();
    }
}
