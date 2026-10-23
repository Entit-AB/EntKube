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
/// Two fields on a bridge connection that are not free-standing values, and what went
/// wrong when they were treated as if they were.
///
/// <para><b>DefaultAppId is only meaningful next to CustomerId.</b> The form filters the
/// application options by the chosen customer, so switching the customer left the previous
/// customer's app id in the bound property with no matching option to show it — the select
/// read as "None — decide per ticket", whose help text promises such a ticket "has no
/// support window and says so", while every inbound ticket on the connection filed against
/// a foreign application and was measured against that other customer's contract.</para>
///
/// <para><b>(System, Instance) is half of the deduplication identity.</b> Deliveries are
/// matched against existing <see cref="ExternalTicketLink"/> rows on
/// (TenantId, System, Instance, ExternalId), built from the <i>connection's</i> current
/// values. Editing the Instance therefore re-keyed deduplication and the next resend of an
/// already-mirrored incident opened a second ticket.</para>
///
/// <para>⚠️ Each fix is tested from both ends where both ends exist: the form has to clear
/// the application, and the save has to refuse a foreign one even if something else calls
/// it. A guard on one side only is a guard somebody removes with the next form.</para>
/// </summary>
public class TicketBridgeConnectionIdentityTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketBridgeAdmin admin;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid otherCustomerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid otherAppId = Guid.NewGuid();

    public TicketBridgeConnectionIdentityTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Customers.Add(new Customer
        {
            Id = otherCustomerId, TenantId = tenantId, Name = "Entit Europe",
        });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
        db.Apps.Add(new App { Id = otherAppId, CustomerId = otherCustomerId, Name = "Europaportalen" });
        db.SaveChanges();

        admin = new TicketBridgeAdmin(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<TicketBridgeConnection> Existing(Guid? defaultAppId = null) =>
        admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.ServiceNow,
            Instance = "entit.service-now.com",
            DefaultAppId = defaultAppId,
        });

    // ---- The application has to belong to the customer -----------------------------------

    /// <summary>
    /// ⭐ The defect, stated as what it produced: a connection whose default application
    /// belongs to a different customer than the connection does.
    ///
    /// <para>Nothing downstream catches it. <c>ResolveAppAsync</c> customer-scopes only its
    /// name-hint path and falls back to a bare <c>return connection.DefaultAppId;</c>, and
    /// <c>TicketService.CreateAsync</c> takes the id without an owner check — unlike
    /// <c>MoveAsync</c>, which refuses the same pairing as "the one combination that would
    /// be silently wrong rather than obviously wrong".</para>
    /// </summary>
    [Fact]
    public async Task A_default_application_belonging_to_another_customer_is_refused()
    {
        // The positive first, so the refusal below is a refusal and not an empty table:
        // this customer's own application saves.
        TicketBridgeConnection ours = await Existing(appId);
        ours.DefaultAppId.Should().Be(appId);

        Func<Task> foreignApp = () => admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.Jira,
            Instance = "entit.atlassian.net",
            DefaultAppId = otherAppId,      // Entit Europe's, on an Entit AB connection
        });

        await foreignApp.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("That application belongs to another customer*");

        (await db.TicketBridgeConnections.AsNoTracking().CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// The edit path, which is how this was actually reached: a saved connection with its
    /// own customer's application, then the customer switched under it.
    /// </summary>
    [Fact]
    public async Task Switching_the_customer_cannot_leave_the_old_customers_application_on_the_row()
    {
        TicketBridgeConnection saved = await Existing(appId);

        Func<Task> switchCustomer = () => admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            Id = saved.Id,
            TenantId = tenantId,
            CustomerId = otherCustomerId,   // now Entit Europe's connection
            System = TicketSystem.ServiceNow,
            Instance = "entit.service-now.com",
            DefaultAppId = appId,           // but still Entit AB's application
        });

        await switchCustomer.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("That application belongs to another customer*");

        db.ChangeTracker.Clear();
        TicketBridgeConnection stored = await db.TicketBridgeConnections.AsNoTracking()
            .FirstAsync(c => c.Id == saved.Id);

        stored.CustomerId.Should().Be(customerId, "the refused save changed nothing");
        stored.DefaultAppId.Should().Be(appId);
    }

    /// <summary>No application at all is the documented, deliberate choice — never refused.</summary>
    [Fact]
    public async Task No_default_application_is_always_allowed()
    {
        TicketBridgeConnection saved = await Existing(defaultAppId: null);
        saved.DefaultAppId.Should().BeNull();
    }

    // ---- Deduplication keeps working across an Instance edit -----------------------------

    private Guid SeedLinkedTicket(TicketBridgeConnection on, string externalId)
    {
        Guid ticketId = Guid.NewGuid();

        db.Tickets.Add(new Ticket
        {
            Id = ticketId,
            TenantId = on.TenantId,
            CustomerId = on.CustomerId,
            Number = Random.Shared.Next(1000, 99999),
            Title = "Fel i journalen",
            Channel = TicketChannel.Integration,
            ReportedAt = DateTime.UtcNow,
        });

        db.ExternalTicketLinks.Add(new ExternalTicketLink
        {
            Id = Guid.NewGuid(),
            TenantId = on.TenantId,
            TicketId = ticketId,
            ConnectionId = on.Id,
            System = on.System,
            Instance = on.Instance,
            ExternalId = externalId,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });

        db.SaveChanges();
        db.ChangeTracker.Clear();
        return ticketId;
    }

    /// <summary>
    /// ⭐ The defect, stated as the delivery that follows the edit.
    ///
    /// <para>A sending system resends on every field change. After the instance was
    /// corrected, the lookup <c>TicketBridgeService.DeliverInternalAsync</c> performs —
    /// tenant + the connection's <i>current</i> System and Instance + the external id —
    /// matched nothing, so the resend took the raise branch and opened a second ticket for
    /// an incident that was already open, each with its own SLA clock. The assertion is
    /// written as that exact lookup rather than as "the column changed", because the column
    /// is only interesting insofar as the lookup finds it.</para>
    /// </summary>
    [Fact]
    public async Task An_instance_edit_does_not_re_key_an_existing_incident()
    {
        TicketBridgeConnection saved = await Existing();
        SeedLinkedTicket(saved, "INC0012345");

        await admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            Id = saved.Id,
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.ServiceNow,
            Instance = "entit2.service-now.com",     // the customer moved host
            DefaultAppId = null,
        });

        db.ChangeTracker.Clear();
        TicketBridgeConnection after = await db.TicketBridgeConnections.AsNoTracking()
            .FirstAsync(c => c.Id == saved.Id);

        after.Instance.Should().Be("entit2.service-now.com", "the edit itself still applies");

        // The dedup lookup, exactly as the delivery path builds it.
        ExternalTicketLink? found = await db.ExternalTicketLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.TenantId == after.TenantId
                                      && l.System == after.System
                                      && l.Instance == after.Instance
                                      && l.ExternalId == "INC0012345");

        found.Should().NotBeNull(
            "a resend about this incident must append to the ticket it already has, "
            + "not open a second one");

        (await db.ExternalTicketLinks.AsNoTracking().CountAsync()).Should().Be(1,
            "re-keying moves the link, it does not copy it");
    }

    /// <summary>
    /// <c>System</c> is in the same key and is edited by the same form, so it gets the same
    /// treatment. ⚠️ Without this the two guards would be mutually redundant and either
    /// could be deleted with the test still green.
    /// </summary>
    [Fact]
    public async Task A_system_edit_does_not_re_key_an_existing_incident_either()
    {
        TicketBridgeConnection saved = await Existing();
        SeedLinkedTicket(saved, "SUP-481");

        await admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            Id = saved.Id,
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.Jira,              // they moved off ServiceNow
            Instance = "entit.service-now.com",
            DefaultAppId = null,
        });

        db.ChangeTracker.Clear();
        TicketBridgeConnection after = await db.TicketBridgeConnections.AsNoTracking()
            .FirstAsync(c => c.Id == saved.Id);

        after.System.Should().Be(TicketSystem.Jira);

        (await db.ExternalTicketLinks.AsNoTracking()
            .AnyAsync(l => l.TenantId == after.TenantId
                           && l.System == after.System
                           && l.Instance == after.Instance
                           && l.ExternalId == "SUP-481"))
            .Should().BeTrue();
    }

    /// <summary>
    /// A save that changes neither field leaves the links alone — the ordinary case, and the
    /// positive that stops the test above passing for the wrong reason (a re-key that fires
    /// on every save would also satisfy it).
    /// </summary>
    [Fact]
    public async Task A_save_that_changes_nothing_leaves_the_links_where_they_are()
    {
        TicketBridgeConnection saved = await Existing();
        SeedLinkedTicket(saved, "INC0000001");

        await admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            Id = saved.Id,
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.ServiceNow,
            Instance = "entit.service-now.com",
            IsEnabled = true,
        });

        db.ChangeTracker.Clear();
        ExternalTicketLink link = await db.ExternalTicketLinks.AsNoTracking().FirstAsync();

        link.Instance.Should().Be("entit.service-now.com");
        link.System.Should().Be(TicketSystem.ServiceNow);
    }

    /// <summary>
    /// ⚠️ The unique index spans the tenant, not the connection, so a move onto an address
    /// where another connection already carries the same reference would have thrown from
    /// <c>SaveChangesAsync</c> with nothing on screen to explain it. Refused by name
    /// instead, and the first connection is left untouched.
    /// </summary>
    [Fact]
    public async Task A_move_onto_another_connections_reference_is_refused_by_name()
    {
        TicketBridgeConnection first = await Existing();
        SeedLinkedTicket(first, "INC0012345");

        TicketBridgeConnection second = await admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            TenantId = tenantId,
            CustomerId = otherCustomerId,
            System = TicketSystem.ServiceNow,
            Instance = "europe.service-now.com",
        });
        SeedLinkedTicket(second, "INC0012345");

        Func<Task> collide = () => admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            Id = first.Id,
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.ServiceNow,
            Instance = "europe.service-now.com",
        });

        await collide.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*same reference*");

        db.ChangeTracker.Clear();
        (await db.TicketBridgeConnections.AsNoTracking().FirstAsync(c => c.Id == first.Id))
            .Instance.Should().Be("entit.service-now.com");
        (await db.ExternalTicketLinks.AsNoTracking()
            .CountAsync(l => l.Instance == "entit.service-now.com")).Should().Be(1);
    }
}

/// <summary>
/// The form, where the stale application id actually came from.
///
/// <para>The server guard above refuses a foreign application; on its own that would turn
/// the defect into an error message somebody has to work out. The form has to not produce
/// the pairing in the first place, which is what the two sibling forms in this repo already
/// do — <c>SlaTargetManager.razor</c> via <c>@bind-Value:after="OnCustomerChanged"</c> and
/// <c>TicketDetail.razor</c>'s move panel via its <c>@onchange</c>.</para>
/// </summary>
public class TicketBridgeCustomerSwitchRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketBridgeAdmin admin;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid otherCustomerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public TicketBridgeCustomerSwitchRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Customers.Add(new Customer
        {
            Id = otherCustomerId, TenantId = tenantId, Name = "Entit Europe",
        });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        admin = new TicketBridgeAdmin(factory);

        Services.AddEveryContext(factory);
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

    /// <summary>
    /// ⭐ Switching the customer in the open form drops the application, and the save that
    /// follows stores a connection with none — rather than silently keeping the one the
    /// select can no longer show.
    ///
    /// <para>Asserted through the saved row, not through the markup: the markup for an
    /// unselectable value is an absence (no <c>selected</c> attribute anywhere), and an
    /// absence assertion on its own would pass against a blank form. The row says what the
    /// bridge will actually use.</para>
    /// </summary>
    [Fact]
    public async Task Switching_the_customer_drops_the_application_the_form_can_no_longer_show()
    {
        TicketBridgeConnection saved = await admin.SaveAsync(tenantId, new TicketBridgeConnection
        {
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.ServiceNow,
            Instance = "entit.service-now.com",
            DefaultAppId = appId,
            IsEnabled = true,
        });

        IRenderedComponent<TicketBridgeTab> tab =
            Render<TicketBridgeTab>(p => p.Add(c => c.TenantId, tenantId));

        tab.FindAll("button").First(b => b.TextContent.Contains("Edit")).Click();

        // The positive: the form opened on this customer's application, so the drop below is
        // a drop and not an empty form.
        tab.FindAll("select")
            .First(s => s.InnerHtml.Contains("None — decide per ticket"))
            .InnerHtml.Should().Contain("Journalportalen");

        tab.FindAll("select")
            .First(s => s.InnerHtml.Contains("Choose…"))
            .Change(otherCustomerId.ToString());

        tab.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        tab.WaitForAssertion(
            () => tab.Markup.Should().Contain("Entit Europe"),
            TimeSpan.FromSeconds(5));

        db.ChangeTracker.Clear();
        TicketBridgeConnection stored = await db.TicketBridgeConnections.AsNoTracking()
            .FirstAsync(c => c.Id == saved.Id);

        stored.CustomerId.Should().Be(otherCustomerId, "the customer switch is what was asked for");
        stored.DefaultAppId.Should().BeNull(
            "the form showed no application, and that is what must be stored — a ticket with "
            + "no application says so, one on a foreign application looks fine and is measured "
            + "against the wrong contract");
    }
}
