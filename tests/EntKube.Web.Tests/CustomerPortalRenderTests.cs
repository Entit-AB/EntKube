using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Portal;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The customer's own side of a ticket, rendered the way the portal renders it.
///
/// <para>Attribution here has an extra twist: the portal used to fall back to the
/// customer's <em>company</em> name when it could not name the person, which is the right
/// answer — but it reached that fallback every single time, because the parameter carrying
/// the person was never passed. Every reply and every approval on a customer's portal was
/// filed as "Capio" rather than as whoever at Capio actually did it.</para>
///
/// <para>§14.4 makes accepting a resolution something the customer does, and §11.1 makes
/// approving work beyond the hour bank a commitment of their money. Both deserve a person.</para>
/// </summary>
public class CustomerPortalRenderTests : BunitContext, IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly Customer customer;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public CustomerPortalRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        customer = new Customer { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Capio" };

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(customer);
        db.Apps.Add(new App { Id = appId, CustomerId = customer.Id, Name = "Journalportalen" });

        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppId = appId,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = Swedish(2026, 1, 1, 0),
        };
        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = ManagementLevel.Standard,
            SupportWindow = SupportWindow.S1,
            EffectiveFrom = Swedish(2026, 1, 1, 0),
            Reason = "On-boarding",
        });
        db.ApplicationContracts.Add(contract);
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        tickets = new TicketService(factory, contracts);

        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
        Services.AddSingleton(contracts);
        Services.AddSingleton(tickets);
        Services.AddSingleton(new TimeService(factory, contracts));
        Services.AddSingleton(new ToastService());
        Services.AddScoped<CurrentActor>();
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs(string? name) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                name is null
                    ? new ClaimsIdentity()
                    : new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"))));
    }

    private void SignIn(string? name) =>
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs(name));

    /// <summary>Rendered as the portal renders it: a customer and a role, and no actor.</summary>
    private IRenderedComponent<CustomerTicketsPanel> RenderTickets() =>
        Render<CustomerTicketsPanel>(p => p
            .Add(c => c.Customer, customer)
            .Add(c => c.AccessRole, CustomerAccessRole.Operator));

    private Task<Ticket> Raise() =>
        tickets.CreateAsync(
            tenantId, customer.Id, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Portal, TicketPriority.P3, Tue(9), "Karin", "karin@capio.example");

    // ---- Attribution --------------------------------------------------------------------------

    /// <summary>
    /// <b>The regression.</b> A reply from the portal is recorded against the person who
    /// wrote it, not against their company. The fallback to the company name is correct
    /// and was being reached every time.
    /// </summary>
    [Fact]
    public async Task A_reply_is_recorded_against_the_person_who_wrote_it()
    {
        SignIn("karin@capio.example");

        Ticket ticket = await Raise();

        IRenderedComponent<CustomerTicketsPanel> panel = RenderTickets();

        // The queue is a table; a person clicks the row.
        await panel.InvokeAsync(() => panel.FindAll("tr")
            .First(r => r.TextContent.Contains("Journalen", StringComparison.Ordinal)).Click());

        panel.Find("textarea").Change("Det gäller fortfarande avdelning 4.");

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Trim().StartsWith("Send", StringComparison.OrdinalIgnoreCase))
            .Click());

        db.ChangeTracker.Clear();

        db.Set<TicketEvent>()
            .Where(e => e.TicketId == ticket.Id && e.Kind == TicketEventKind.Note)
            .Should().Contain(e => e.Actor == "karin@capio.example");
    }

    /// <summary>
    /// The company name is still the right answer when nobody can be named — an action by
    /// somebody at Capio is better recorded as Capio's than as nobody's.
    /// </summary>
    [Fact]
    public async Task An_unnamed_portal_user_falls_back_to_the_customers_name()
    {
        SignIn(null);

        Ticket ticket = await Raise();

        IRenderedComponent<CustomerTicketsPanel> panel = RenderTickets();

        // The queue is a table; a person clicks the row.
        await panel.InvokeAsync(() => panel.FindAll("tr")
            .First(r => r.TextContent.Contains("Journalen", StringComparison.Ordinal)).Click());

        panel.Find("textarea").Change("Fortfarande nere.");

        await panel.InvokeAsync(() => panel.FindAll("button")
            .First(b => b.TextContent.Trim().StartsWith("Send", StringComparison.OrdinalIgnoreCase))
            .Click());

        db.ChangeTracker.Clear();

        db.Set<TicketEvent>()
            .Where(e => e.TicketId == ticket.Id && e.Kind == TicketEventKind.Note)
            .Should().Contain(e => e.Actor == "Capio")
            .And.NotContain(e => e.Actor == CurrentActor.Unattributed);
    }

    // ---- What a customer is allowed to see ----------------------------------------------------

    /// <summary>
    /// A viewer reads; an operator acts. The gate is a parameter the portal does pass, and
    /// rendering is the only thing that checks the markup honours it.
    /// </summary>
    [Fact]
    public async Task A_viewer_is_not_offered_the_actions_an_operator_gets()
    {
        SignIn("lasse@capio.example");
        await Raise();

        IRenderedComponent<CustomerTicketsPanel> viewer = Render<CustomerTicketsPanel>(p => p
            .Add(c => c.Customer, customer)
            .Add(c => c.AccessRole, CustomerAccessRole.Viewer));

        viewer.FindAll("textarea").Should().BeEmpty("a viewer has nothing to write with");
    }

    [Fact]
    public void An_empty_queue_says_so()
    {
        SignIn("karin@capio.example");

        RenderTickets().Markup.Should().NotContain("Journalen svarar inte");
    }
}
