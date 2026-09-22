using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
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
/// One ticket, rendered the way the queue renders it.
///
/// <para>This is where §14.6's money is. Confirming a priority is a written and reasoned
/// act under §14.3, and a mis-clocked P1 costs ten percent of the month's window fee — so
/// a confirmation that cannot say who made it, or a resolution recorded against nobody, is
/// not evidence of anything. The ticket queue passed no actor for the whole of this
/// branch, and the service tests could not see it because they were handed one.</para>
/// </summary>
public class TicketDetailRenderTests : BunitContext, IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>Tuesday 22 September 2026, inside S1.</summary>
    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public TicketDetailRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });

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

        // The detail renders the time log, which wants its own service — one of the things
        // only a render finds, since nothing else knows the two are related.
        Services.AddSingleton(new TimeService(factory, contracts));
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils"));
        Services.AddScoped<CurrentActor>();
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class SignedInAs(string name) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"))));
    }

    private Task<Ticket> Raise(TicketPriority priority = TicketPriority.P3) =>
        tickets.CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Email, priority, Tue(9), "Karin", "karin@capio.example");

    /// <summary>
    /// Rendered as the queue renders it: an id and two callbacks, and no actor — which is
    /// exactly the wiring that filed everything under nobody.
    /// </summary>
    private IRenderedComponent<TicketDetail> RenderDetail(Guid ticketId) =>
        Render<TicketDetail>(p => p.Add(c => c.TicketId, ticketId));

    private static IElement ButtonSaying(IRenderedComponent<TicketDetail> detail, string text) =>
        detail.FindAll("button")
            .First(b => b.TextContent.Trim().StartsWith(text, StringComparison.OrdinalIgnoreCase));

    // ---- Attribution, where the money is ------------------------------------------------------

    /// <summary>
    /// <b>The regression.</b> §14.3 makes confirming a priority a written and reasoned act,
    /// and §14.6 prices getting it wrong at ten percent of the window fee. A confirmation
    /// recorded against "unattributed" is not a written act by anybody.
    /// </summary>
    [Fact]
    public async Task Confirming_a_priority_records_who_confirmed_it()
    {
        Ticket ticket = await Raise();

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        // The form is behind its own button, so the test opens it the way a person does.
        await detail.InvokeAsync(() => ButtonSaying(detail, "Confirm priority").Click());

        detail.Find("input.form-control-sm").Change("Two wards cannot admit patients.");
        await detail.InvokeAsync(() => ButtonSaying(detail, "Save").Click());

        db.ChangeTracker.Clear();
        Ticket saved = db.Tickets.Single(t => t.Id == ticket.Id);

        saved.PriorityConfirmedBy.Should().Be("nils");
        saved.PriorityConfirmedBy.Should().NotBe(CurrentActor.Unattributed);
        saved.PriorityReason.Should().Be("Two wards cannot admit patients.");
    }

    /// <summary>
    /// §14.4 makes a resolution something the customer accepts, which means the record has
    /// to say who offered it.
    /// </summary>
    [Fact]
    public async Task Resolving_records_who_resolved_it()
    {
        Ticket ticket = await Raise();

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        await detail.InvokeAsync(() => ButtonSaying(detail, "Resolve").Click());

        detail.Find("textarea").Change("Restarted the pod; the ward is back in.");

        // The second "Resolve" is the one inside the form the first opened.
        await detail.InvokeAsync(() => detail.FindAll("button")
            .Last(b => b.TextContent.Trim().StartsWith("Resolve", StringComparison.Ordinal))
            .Click());

        db.ChangeTracker.Clear();

        db.Set<TicketEvent>()
            .Where(e => e.TicketId == ticket.Id && e.Kind == TicketEventKind.Resolved)
            .Should().OnlyContain(e => e.Actor == "nils");
    }

    /// <summary>
    /// The first response stops the only clock that carries a penalty, so who stopped it
    /// and when is the whole of the evidence.
    /// </summary>
    [Fact]
    public async Task Recording_a_response_records_who_made_it()
    {
        Ticket ticket = await Raise(TicketPriority.P1);

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        await detail.InvokeAsync(() => ButtonSaying(detail, "Record first response").Click());

        db.ChangeTracker.Clear();
        Ticket saved = db.Tickets.Single(t => t.Id == ticket.Id);

        saved.FirstResponseAt.Should().NotBeNull();

        db.Set<TicketEvent>()
            .Where(e => e.TicketId == ticket.Id && e.Kind == TicketEventKind.Responded)
            .Should().OnlyContain(e => e.Actor == "nils");
    }

    // ---- What the screen shows ----------------------------------------------------------------

    /// <summary>
    /// The clocks are what an operator is looking at. Rendering them wrong is not caught by
    /// testing the arithmetic, which is exhaustively covered elsewhere.
    /// </summary>
    [Fact]
    public async Task The_ticket_shows_its_priority_and_its_clocks()
    {
        Ticket ticket = await Raise(TicketPriority.P1);

        string markup = RenderDetail(ticket.Id).Markup;

        markup.Should().Contain("Journalen svarar inte");
        markup.Should().Contain("P1");
        markup.Should().Contain("Response", "the clock that carries the penalty is named");
    }

    /// <summary>A ticket that does not exist must not render as a blank ticket.</summary>
    [Fact]
    public void A_ticket_that_is_not_there_says_so() =>
        RenderDetail(Guid.NewGuid()).Markup.Should().NotContain("Journalen svarar inte");
}
