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

        // Phase 2a: text modules, macros and checklists on the ticket (docs/helpdesk.md).
        Services.AddSingleton(new TextModuleService(factory));
        Services.AddSingleton(new TicketChecklistService(factory));
        Services.AddSingleton(TestTicketReference.Instance);
        Services.AddSingleton(new MacroService(factory, tickets, work));
        Services.AddSingleton(new TicketTemplateService(factory));
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

    // ───────────────────────────── Phase 2a ─────────────────────────────

    [Fact]
    public async Task A_text_module_is_inserted_with_its_placeholders_filled_from_the_ticket()
    {
        Ticket ticket = await Raise("Login fails");
        await new TextModuleService(new TestDbContextFactory(connection)).SaveAsync(tenantId,
            new TextModule { Name = "Ack", Content = "Hi #{requester.name}, we are on #{ticket.number}." }, "admin");

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);
        await detail.InvokeAsync(() => ButtonSaying(detail, "Add a note").Click());
        await detail.InvokeAsync(() => detail.Find("[data-test=text-module-picker] input").Input("ack"));
        await detail.InvokeAsync(() => detail.FindAll("button.list-group-item").First(b => b.TextContent.Contains("Ack")).Click());
        await detail.InvokeAsync(() => detail.FindAll("button").Last(b => b.TextContent.Trim() == "Add").Click());

        db.ChangeTracker.Clear();
        db.TicketEvents.Should().Contain(e => e.TicketId == ticket.Id && e.Detail == $"Hi Karin, we are on {ticket.Number}.");
    }

    [Fact]
    public async Task A_macro_run_from_the_screen_is_recorded_under_the_operator_and_the_macro()
    {
        Ticket ticket = await Raise();
        Macro macro = await new MacroService(new TestDbContextFactory(connection), tickets, work)
            .SaveAsync(tenantId, new Macro { Name = "Tag network", AddTags = "network" }, "admin");

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);
        detail.Find("[data-test=macro-runner] select").Change(macro.Id.ToString());
        await detail.InvokeAsync(() => detail.Find("[data-test=macro-runner] button").Click());

        db.ChangeTracker.Clear();
        db.TicketEvents.Single(e => e.TicketId == ticket.Id && e.Kind == TicketEventKind.Tagged)
            .Actor.Should().Be("nils (macro \u201cTag network\u201d)");
    }

    [Fact]
    public async Task Resolving_with_open_checklist_items_asks_first_and_goes_ahead_when_told()
    {
        Ticket ticket = await Raise();
        TicketChecklistService checklists = new(new TestDbContextFactory(connection));
        await checklists.CreateAsync(tenantId, ticket.Id, null, "nils");
        await checklists.AddItemAsync(tenantId, ticket.Id, "Check the logs");

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);
        detail.WaitForAssertion(() => detail.Find("[data-test=checklist]").TextContent.Should().Contain("Check the logs"));

        await detail.InvokeAsync(() => ButtonSaying(detail, "Resolve").Click());
        detail.FindAll("textarea").First().Change("Restarted the service.");
        await detail.InvokeAsync(() => detail.FindAll("button").Last(b => b.TextContent.Trim() == "Resolve").Click());

        detail.Find("[data-test=checklist-guard]").TextContent.Should().Contain("1 checklist item(s) are still open");
        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).Status.Should().NotBe(TicketStatus.Resolved);

        await detail.InvokeAsync(() => ButtonSaying(detail, "Resolve anyway").Click());

        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).Status.Should().Be(TicketStatus.Resolved);
    }

    /// <summary>A call registered afterwards is reported when it was made, not when it was typed.</summary>
    [Fact]
    public async Task A_ticket_registered_by_hand_keeps_the_time_it_was_reported_and_its_tags()
    {
        IRenderedComponent<TicketQueue> queue = Render<TicketQueue>(p => p
            .Add(c => c.TenantId, tenantId).Add(c => c.TenantSlug, (string?)null));

        await queue.InvokeAsync(() => ButtonSaying(queue, "New ticket").Click());
        IElement form = queue.Find("[data-test=new-ticket]");
        form.QuerySelectorAll("select")[1].Change(customerId.ToString());

        queue.Find("[data-test=new-ticket]").QuerySelectorAll("input")[2].Change("Printer offline");
        queue.Find("[data-test=new-ticket] input[type=datetime-local]").Change("2026-09-22T08:15");
        queue.Find("[data-test=new-ticket] input[placeholder='vpn, login']").Change("printer");
        await queue.InvokeAsync(() => queue.Find("[data-test=new-ticket] .text-end button").Click());

        db.ChangeTracker.Clear();
        Ticket created = db.Tickets.Include(t => t.Tags).Single(t => t.Title == "Printer offline");
        created.Channel.Should().Be(TicketChannel.Conversation);
        created.ReportedAt.Should().Be(Tue(8, 15));
        created.Tags.Select(t => t.Name).Should().Equal("printer");
    }

    /// <summary>
    /// ⚠️ A browser's datetime-local sends "2026-09-22T08:15" — no seconds. Bound without an
    /// explicit format, Blazor expected seconds, failed to parse it, and kept the old value: the
    /// Set button did nothing at all, silently. Pinned here for the reminder; the new-ticket test
    /// pins it for "reported at".
    /// </summary>
    [Fact]
    public async Task A_reminder_picked_in_the_panel_is_the_time_that_was_picked()
    {
        Ticket ticket = await Raise();
        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        detail.Find("input[type=datetime-local]").Change("2026-09-23T09:30");
        await detail.InvokeAsync(() => detail.FindAll("button").First(b => b.TextContent.Trim() == "Set").Click());

        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).ReminderAt.Should().Be(TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 9, 23, 9, 30, 0, DateTimeKind.Unspecified), BusinessCalendar.SwedishTime));
    }
}
