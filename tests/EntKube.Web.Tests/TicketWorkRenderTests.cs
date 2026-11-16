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
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The helpdesk work as an operator does it on screen (docs/helpdesk.md, phase 1): the side
/// panel, merge, split, and the queue's overviews, selection and address. The service rules
/// are pinned in <see cref="TicketWorkTests"/>; these pin that the screen reaches them and
/// records who did it.
/// </summary>
public class TicketWorkRenderTests : BunitContext, IDisposable
{
    private static DateTime Tue(int hour, int minute = 0) => TimeZoneInfo.ConvertTimeToUtc(
        new DateTime(2026, 9, 22, hour, minute, 0, DateTimeKind.Unspecified), BusinessCalendar.SwedishTime);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketWorkService work;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public TicketWorkRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        TicketNotifier notifier = SilentTicketNotifier.For(factory);
        SupportDutyService duty = new(factory);

        tickets = new TicketService(factory, contracts, notifier);
        work = new TicketWorkService(factory, tickets, notifier, duty);

        Services.AddEveryContext(factory);
        Services.AddSingleton(contracts);
        Services.AddSingleton(tickets);
        Services.AddSingleton(work);
        Services.AddSingleton(new TicketOverviews(tickets, factory));
        Services.AddSingleton(new TicketBulkService(tickets, work));
        Services.AddSingleton(new TimeService(factory, contracts));
        Services.AddSingleton(duty);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils"));
        Services.AddScoped<CurrentActor>();
        Services.AddSingleton(new UserAccessService(factory));
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

    private Task<Ticket> Raise(string title = "Journalen svarar inte", DateTime? at = null) =>
        tickets.CreateAsync(
            tenantId, customerId, appId, title, "Ingen kommer in.",
            TicketChannel.Email, TicketPriority.P3, at ?? Tue(9), "Karin", "karin@entit.example");

    private IRenderedComponent<TicketDetail> RenderDetail(Guid ticketId) =>
        Render<TicketDetail>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.TicketId, ticketId)
            .Add(c => c.TenantSlug, "entit"));

    private static IElement ButtonSaying<T>(IRenderedComponent<T> view, string text) where T : IComponent =>
        view.FindAll("button").First(b => b.TextContent.Trim().StartsWith(text, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task A_tag_typed_into_the_panel_is_added_under_the_operators_name()
    {
        Ticket ticket = await Raise();
        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        IElement box = detail.Find("input[placeholder='Add a tag']");
        await detail.InvokeAsync(() => box.Input("VPN"));
        await detail.InvokeAsync(() => detail.Find("input[placeholder='Add a tag']").KeyDown("Enter"));

        detail.WaitForAssertion(() => detail.Find("[data-test=tags]").TextContent.Should().Contain("vpn"));

        db.ChangeTracker.Clear();
        db.TicketTags.Single().AddedBy.Should().Be("nils");
    }

    [Fact]
    public async Task Merging_from_the_screen_folds_the_ticket_in_and_says_where_it_went()
    {
        Ticket original = await Raise("Original", Tue(9));
        Ticket duplicate = await Raise("Duplicate", Tue(10));

        IRenderedComponent<TicketDetail> detail = RenderDetail(duplicate.Id);

        await detail.InvokeAsync(() => ButtonSaying(detail, "Merge into").Click());
        detail.Find("[data-test=merge-form] input[type=number]").Change(original.Number.ToString());
        await detail.InvokeAsync(() => detail.Find("[data-test=merge-form] .text-end button").Click());

        detail.WaitForAssertion(() =>
            detail.Markup.Should().Contain($"Merged into <a href=\"/tenants/entit?section=tickets&amp;ticket={original.Number}\">#{original.Number}</a>"));

        db.ChangeTracker.Clear();
        Ticket after = db.Tickets.Single(t => t.Id == duplicate.Id);
        after.Status.Should().Be(TicketStatus.Merged);
        db.TicketEvents.Single(e => e.TicketId == duplicate.Id && e.Kind == TicketEventKind.Merged).Actor.Should().Be("nils");
    }

    [Fact]
    public async Task A_merged_ticket_offers_no_more_work_on_it()
    {
        Ticket original = await Raise("Original", Tue(9));
        Ticket duplicate = await Raise("Duplicate", Tue(10));
        await work.MergeAsync(tenantId, duplicate.Id, original.Number, false, null, "nils", Tue(11));

        IRenderedComponent<TicketDetail> detail = RenderDetail(duplicate.Id);

        detail.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain(t => t.StartsWith("Resolve") || t.StartsWith("Merge into") || t.StartsWith("Reject"));
    }

    [Fact]
    public async Task The_original_shows_the_duplicates_entries_marked_with_its_number()
    {
        Ticket original = await Raise("Original", Tue(9));
        Ticket duplicate = await Raise("Duplicate", Tue(10));
        await tickets.AddEventAsync(tenantId, duplicate.Id, TicketEventKind.Note, "Second caller.", "nils", Tue(10, 5));
        await work.MergeAsync(tenantId, duplicate.Id, original.Number, false, null, "nils", Tue(11));

        IRenderedComponent<TicketDetail> detail = RenderDetail(original.Id);

        detail.FindAll(".list-group-item.bg-light").Should().Contain(i =>
            i.TextContent.Contains($"#{duplicate.Number}") && i.TextContent.Contains("Second caller."));
    }

    [Fact]
    public async Task Splitting_an_entry_opens_a_new_ticket_with_the_title_given()
    {
        Ticket ticket = await Raise();
        await tickets.AddEventAsync(tenantId, ticket.Id, TicketEventKind.Note, "Printing fails too.", "Karin", Tue(9, 30));

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        IElement split = detail.FindAll(".list-group-item")
            .First(i => i.TextContent.Contains("Printing fails too."))
            .QuerySelector("button[title='Open a new ticket from this entry']")!;
        await detail.InvokeAsync(() => split.Click());

        detail.Find("[data-test=split-form] input").Change("Printing fails");
        await detail.InvokeAsync(() => ButtonSaying(detail, "Open the new ticket").Click());

        db.ChangeTracker.Clear();
        Ticket created = db.Tickets.Single(t => t.Title == "Printing fails");
        created.ReportedAt.Should().Be(Tue(9, 30));
    }

    [Fact]
    public async Task The_queue_opens_the_ticket_its_address_names()
    {
        Ticket ticket = await Raise("Named in the address");

        IRenderedComponent<TicketQueue> queue = Render<TicketQueue>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.TenantSlug, "entit")
            .Add(c => c.FocusTicketNumber, ticket.Number));

        queue.WaitForAssertion(() => queue.Markup.Should().Contain("Back to the queue").And.Contain("Named in the address"));
    }

    [Fact]
    public async Task The_queue_does_not_open_another_tenants_ticket_by_its_number()
    {
        Guid elsewhere = Guid.NewGuid();
        Guid theirCustomer = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = elsewhere, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = elsewhere, Name = "Theirs" });
        await db.SaveChangesAsync();

        Ticket theirs = await tickets.CreateAsync(
            elsewhere, theirCustomer, null, "Their secret", "", TicketChannel.Email, null, Tue(9));

        IRenderedComponent<TicketQueue> queue = Render<TicketQueue>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.TenantSlug, "entit")
            .Add(c => c.FocusTicketNumber, theirs.Number));

        queue.WaitForAssertion(() => queue.Markup.Should().NotContain("Their secret").And.NotContain("Back to the queue"));
    }

    [Fact]
    public async Task The_queue_shows_each_overview_with_its_count_and_a_bulk_bar_for_a_selection()
    {
        await Raise("One");
        await Raise("Two");

        IRenderedComponent<TicketQueue> queue = Render<TicketQueue>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.TenantSlug, "entit"));

        queue.WaitForAssertion(() =>
            queue.Find("[data-test=overviews]").TextContent.Should().Contain("Unassigned").And.Contain("2"));

        await queue.InvokeAsync(() => queue.Find("input[aria-label='Select all']").Change(true));

        queue.Find("[data-test=bulk-bar]").TextContent.Should().Contain("2 selected");
    }
}
