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
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>Tuesday 22 September 2026, inside S1.</summary>
    private static DateTime Tue(int hour, int minute = 0) =>
        Swedish(2026, 9, 22, hour, minute);

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
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
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
        tickets = new TicketService(factory, contracts, SilentTicketNotifier.For(factory));

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
            TicketChannel.Email, priority, Tue(9), "Karin", "karin@entit.example");

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

    // ---- Who is holding it ---------------------------------------------------------------

    /// <summary>
    /// Taking a ticket records the person who took it, through the same wiring as
    /// everything else — and the column it writes to sat unused from the day it was added.
    /// </summary>
    [Fact]
    public async Task Taking_a_ticket_records_who_took_it()
    {
        Ticket ticket = await Raise();

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        detail.Markup.Should().Contain("Take this");

        await detail.InvokeAsync(() => ButtonSaying(detail, "Take this").Click());

        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).Assignee.Should().Be("nils");
    }

    /// <summary>
    /// And it can be put back. A queue where taking something is irreversible is a queue
    /// people stop taking things from.
    /// </summary>
    [Fact]
    public async Task A_ticket_can_be_put_back_from_the_screen()
    {
        Ticket ticket = await Raise();
        await tickets.AssignAsync(ticket.Id, "nils", "nils", Tue(10));

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        await detail.InvokeAsync(() => ButtonSaying(detail, "Put it back").Click());

        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).Assignee.Should().BeNull();
    }

    /// <summary>
    /// Somebody else holding it is shown by name, and taking it says so plainly rather
    /// than quietly reassigning.
    /// </summary>
    [Fact]
    public async Task A_ticket_somebody_else_holds_says_whose_it_is()
    {
        Ticket ticket = await Raise();
        await tickets.AssignAsync(ticket.Id, "karin", "karin", Tue(10));

        RenderDetail(ticket.Id).Markup.Should().Contain("Take it from karin");
    }

    // ---- §14.4 on the screen ---------------------------------------------------------------

    /// <summary>
    /// Enforcing a rule and showing it are one piece of work. A computed obligation nobody
    /// can see is the same as no obligation — which is what §14.4's update interval was
    /// for the whole life of this branch.
    /// </summary>
    [Fact]
    public async Task A_P1_shows_when_its_next_update_is_due()
    {
        await Raise(TicketPriority.P1);

        RenderDetail(db.Tickets.Single().Id).Markup.Should()
            .Contain("Next update").And.Contain("§14.4");
    }

    /// <summary>
    /// Telling the customer resets the silence. A note they can see is what §14.4 counts,
    /// and an internal one is not.
    /// </summary>
    [Fact]
    public async Task Telling_the_customer_something_pushes_the_next_update_out()
    {
        Ticket ticket = await Raise(TicketPriority.P1);

        TicketSlaStatus before = TicketService.StatusOf(
            (await tickets.GetAsync(ticket.Id))!, Tue(10));

        await tickets.AddEventAsync(
            ticket.Id, TicketEventKind.Note, "Still working on it.", "nils", Tue(10),
            customerVisible: true);

        TicketSlaStatus after = TicketService.StatusOf(
            (await tickets.GetAsync(ticket.Id))!, Tue(10));

        after.UpdateDue.Should().BeAfter(before.UpdateDue!.Value);
        after.LastUpdateAt.Should().Be(Tue(10));
    }

    /// <summary>
    /// <b>Through the queue, which is the path that was wrong.</b> The queue loaders did
    /// not fetch the events, so every ticket there looked as though nobody had said
    /// anything since it arrived — a whole column of red on tickets being handled
    /// perfectly well. Nothing failed; it was simply untrue.
    /// </summary>
    [Fact]
    public async Task The_queue_sees_that_the_customer_was_told()
    {
        Ticket ticket = await Raise(TicketPriority.P1);

        await tickets.AddEventAsync(
            ticket.Id, TicketEventKind.Note, "Still working on it.", "nils", Tue(11),
            customerVisible: true);

        TicketSlaStatus fromQueue = (await tickets.GetOpenQueueAsync(customerId, Tue(12)))
            .Single(t => t.Ticket.Id == ticket.Id);

        fromQueue.LastUpdateAt.Should().Be(Tue(11), "the queue has to read the same events");
        fromQueue.UpdateOverdue(Tue(11, 30)).Should().BeFalse();
    }

    /// <summary>
    /// A note the customer cannot see does not count. Writing to ourselves is not keeping
    /// them informed, and treating it as such would let the obligation be discharged in
    /// private.
    /// </summary>
    [Fact]
    public async Task A_note_the_customer_cannot_see_does_not_reset_the_clock()
    {
        Ticket ticket = await Raise(TicketPriority.P1);

        TicketSlaStatus before = TicketService.StatusOf(
            (await tickets.GetAsync(ticket.Id))!, Tue(10));

        await tickets.AddEventAsync(
            ticket.Id, TicketEventKind.Note, "Asked the supplier.", "nils", Tue(10),
            customerVisible: false);

        TicketSlaStatus after = TicketService.StatusOf(
            (await tickets.GetAsync(ticket.Id))!, Tue(10));

        after.UpdateDue.Should().Be(before.UpdateDue);
    }
}
