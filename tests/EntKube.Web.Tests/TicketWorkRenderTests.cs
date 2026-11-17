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
        Services.AddSingleton(notifier);
        Services.AddSingleton(new TicketOverviews(tickets, factory));
        Services.AddSingleton(new TicketBulkService(tickets, work));

        // Phase 2a: text modules, macros and checklists on the ticket (docs/helpdesk.md).
        Services.AddSingleton(new TextModuleService(factory));
        Services.AddSingleton(new TicketChecklistService(factory));
        Services.AddSingleton(new TicketPresenceService(factory));
        Services.AddSingleton(new KnowledgeBaseService(factory));
        Services.AddSingleton(new TicketAttachmentService(factory, new UnconfiguredAttachmentStore()));
        Services.AddSingleton(TestTicketReference.Instance);
        Services.AddSingleton(new MacroService(factory, tickets, work));
        Services.AddSingleton(new TicketTemplateService(factory));
        Services.AddSingleton(new TicketDraftService(factory));
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

    private sealed class SignedInAs(string name, string? userId = null) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity(
                    userId is null
                        ? [new Claim(ClaimTypes.Name, name)]
                        : [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.NameIdentifier, userId)],
                    "test"))));
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

    // ───────────────────────────── Phase 2b ─────────────────────────────

    private async Task AddNoteThroughTheScreenAsync(IRenderedComponent<TicketDetail> detail, string text)
    {
        await detail.InvokeAsync(() => ButtonSaying(detail, "Add a note").Click());
        await detail.InvokeAsync(() => detail.Find("textarea[placeholder='A status update, or a working note']").Input(text));
        await detail.InvokeAsync(() => detail.FindAll("button").Last(b => b.TextContent.Trim() == "Add").Click());
    }

    [Fact]
    public async Task After_a_note_the_time_it_took_can_be_logged_against_the_ticket()
    {
        Ticket ticket = await Raise();
        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        await AddNoteThroughTheScreenAsync(detail, "Restarted the VPN gateway.");

        IElement prompt = detail.Find("[data-test=time-prompt]");
        prompt.QuerySelector("input[type=number]")!.Change("25");
        await detail.InvokeAsync(() => detail.Find("[data-test=time-prompt] button").Click());

        db.ChangeTracker.Clear();
        TimeEntry entry = db.TimeEntries.Single(e => e.TicketId == ticket.Id);
        (entry.EndedAt - entry.StartedAt).Should().Be(TimeSpan.FromMinutes(25));
        entry.Description.Should().Be("Restarted the VPN gateway.");
        entry.PerformedBy.Should().Be("nils");
    }

    [Fact]
    public async Task Skipping_the_prompt_logs_nothing()
    {
        Ticket ticket = await Raise();
        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        await AddNoteThroughTheScreenAsync(detail, "Looked at it.");
        await detail.InvokeAsync(() => detail.FindAll("[data-test=time-prompt] button").First(b => b.TextContent.Trim() == "Skip").Click());

        detail.FindAll("[data-test=time-prompt]").Should().BeEmpty();
        db.TimeEntries.AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public async Task What_was_being_typed_is_there_again_when_the_note_is_reopened()
    {
        Ticket ticket = await Raise();
        await new TicketDraftService(new TestDbContextFactory(connection))
            .SavePersonalNoteAsync(tenantId, ticket.Id, "user-nils", "Half written", false);

        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils", "user-nils"));
        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);
        await detail.InvokeAsync(() => ButtonSaying(detail, "Add a note").Click());

        detail.Find("textarea[placeholder='A status update, or a working note']").GetAttribute("value").Should().Be("Half written");
    }

    [Fact]
    public async Task A_shared_draft_is_offered_and_goes_once_a_note_is_made_from_it()
    {
        Ticket ticket = await Raise();
        TicketDraftService drafts = new(new TestDbContextFactory(connection));
        await drafts.ShareNoteAsync(tenantId, ticket.Id, "Anna's draft answer", true, "anna", false);

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);
        detail.Find("[data-test=shared-draft]").TextContent.Should().Contain("Draft available").And.Contain("anna");

        await detail.InvokeAsync(() => detail.Find("[data-test=shared-draft] button").Click());
        await detail.InvokeAsync(() => ButtonSaying(detail, "Load into the note").Click());
        await detail.InvokeAsync(() => detail.FindAll("button").Last(b => b.TextContent.Trim() == "Add").Click());

        db.ChangeTracker.Clear();
        db.TicketEvents.Single(e => e.TicketId == ticket.Id && e.Detail == "Anna's draft answer").CustomerVisible.Should().BeTrue();
        (await drafts.GetSharedNoteAsync(tenantId, ticket.Id)).Should().BeNull();
    }

    /// <summary>
    /// ⚠️ The time log converted the picked time with ToUniversalTime(), which on a time with no
    /// zone applies the SERVER's offset. On this developer's machine (Swedish time) the two agree,
    /// so this passes either way here — it is a CI (UTC) regression test, which is where a Swedish
    /// 09:00 used to be stored as 09:00 UTC.
    /// </summary>
    [Fact]
    public async Task Time_logged_by_hand_is_read_as_swedish_time()
    {
        Ticket ticket = await Raise();
        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        await detail.InvokeAsync(() => ButtonSaying(detail, "Log time").Click());
        // The time log's two pickers come first; the side panel's reminder picker follows them.
        detail.FindAll("input[type=datetime-local]")[0].Change("2026-09-22T09:00");
        detail.FindAll("input[type=datetime-local]")[1].Change("2026-09-22T10:00");
        detail.Find("input[placeholder^='Appears on the specification']").Change("Investigated the outage");
        await detail.InvokeAsync(() => detail.FindAll("button").Last(b => b.TextContent.Trim() == "Log").Click());

        db.ChangeTracker.Clear();
        db.TimeEntries.Single(e => e.TicketId == ticket.Id).StartedAt.Should().Be(Tue(9));
    }

    [Fact]
    public async Task A_new_ticket_left_as_a_shared_draft_can_be_finished_by_somebody_else()
    {
        TicketDraftService drafts = new(new TestDbContextFactory(connection));
        TicketDraft left = await drafts.SaveNewTicketDraftAsync(tenantId, null, "Anna's call",
            new NewTicketDraft { Title = "Printer offline", Description = "Floor 3", CustomerId = customerId, Tags = "printer" }, "anna");

        IRenderedComponent<TicketQueue> queue = Render<TicketQueue>(p => p.Add(c => c.TenantId, tenantId));
        await queue.InvokeAsync(() => ButtonSaying(queue, "New ticket").Click());

        await queue.InvokeAsync(() => queue.FindAll("[data-test=new-ticket-drafts] button").First(b => b.TextContent.Trim() == "Load").Click());
        await queue.InvokeAsync(() => queue.Find("[data-test=new-ticket] .text-end button").Click());

        db.ChangeTracker.Clear();
        db.Tickets.Include(t => t.Tags).Single(t => t.Title == "Printer offline").Tags.Select(t => t.Name).Should().Equal("printer");
        (await drafts.ListNewTicketDraftsAsync(tenantId)).Should().NotContain(d => d.Id == left.Id, "creating the ticket uses the draft up");
    }

    /// <summary>
    /// Decision 4: a file the customer sent that could not be kept is shown, with why — never
    /// left out, which would let everyone believe we had it.
    /// </summary>
    [Fact]
    public async Task A_customers_file_that_could_not_be_kept_is_shown_with_the_reason()
    {
        Ticket ticket = await Raise();
        InboundMailMessage mail = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = "karin@entit.example", Subject = "Screenshot", TicketId = ticket.Id,
        };
        db.InboundMailMessages.Add(mail);
        await db.SaveChangesAsync();

        await new TicketAttachmentService(new TestDbContextFactory(connection), new UnconfiguredAttachmentStore())
            .KeepFromMailAsync(tenantId, mail.Id, [new IncomingAttachment("error.png", "image/png", () => new MemoryStream([1, 2, 3]))]);

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        string card = detail.Find("[data-test=attachments]").TextContent;
        card.Should().Contain("error.png").And.Contain(UnconfiguredAttachmentStore.Reason);
        detail.FindAll("[data-test=attachments] input[type=file]").Should().BeEmpty("there is nowhere to put an upload");
    }

    // ───────────────────────────── Phase 2c-2 ─────────────────────────────

    /// <summary>
    /// A rich entry is sanitised again when shown — a row written before a rule changed, or
    /// restored from a backup, is held to today's rules, not trusted because it is ours.
    /// </summary>
    [Fact]
    public async Task A_stored_rich_entry_is_sanitised_again_when_shown()
    {
        Ticket ticket = await Raise();
        db.TicketEvents.Add(new TicketEvent
        {
            Id = Guid.NewGuid(), TicketId = ticket.Id, Kind = TicketEventKind.Note, At = DateTime.UtcNow,
            Detail = "Hello", DetailHtml = "<p>Hello <strong>you</strong></p><script>alert(1)</script><img src=x onerror=alert(2)>",
        });
        await db.SaveChangesAsync();

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        string entry = detail.Find(".rich-entry").InnerHtml;
        entry.Should().Contain("<strong>you</strong>").And.NotContain("script").And.NotContain("onerror").And.NotContain("<img");
    }

    [Fact]
    public async Task A_reply_written_in_the_composer_is_sent_and_recorded_as_rich_text()
    {
        Ticket ticket = await Raise();

        BunitJSModuleInterop editor = JSInterop.SetupModule("./js/ticket-editor.js");
        editor.SetupVoid("create", _ => true);
        editor.SetupVoid("clear", _ => true);
        editor.SetupVoid("dispose", _ => true);
        editor.Setup<string>("html", _ => true).SetResult("<p>We restarted <strong>the gateway</strong>.</p>");

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);
        await detail.InvokeAsync(() => detail.FindAll("button").First(b => b.TextContent.Trim() == "Reply").Click());
        await detail.InvokeAsync(() => detail.Find("[data-test=mail-composer] .d-flex button.btn-primary").Click());

        db.ChangeTracker.Clear();
        // No relay is configured in this suite, so the mail cannot leave — which is itself what
        // has to be recorded: the words, internal, and the clock still running.
        TicketEvent written = db.TicketEvents.Single(e => e.TicketId == ticket.Id && e.DetailHtml != null);
        written.DetailHtml.Should().Be("<p>We restarted <strong>the gateway</strong>.</p>");
        written.Detail.Should().StartWith("A mail could not be sent");
        written.Actor.Should().Be("nils");
    }

    // ───────────────────────────── Phase 3 ─────────────────────────────

    [Fact]
    public async Task A_search_in_the_address_is_run_and_its_results_replace_the_overviews()
    {
        Ticket match = await Raise("Login fails");
        await Raise("Printer offline");

        IRenderedComponent<TicketQueue> queue = Render<TicketQueue>(p => p
            .Add(c => c.TenantId, tenantId).Add(c => c.TenantSlug, "entit").Add(c => c.Search, "title:login"));

        queue.WaitForAssertion(() => queue.Find("[data-test=search-summary]").TextContent.Should().Contain("1 ticket(s)"));
        queue.Markup.Should().Contain("Login fails").And.NotContain("Printer offline");
        queue.FindAll("[data-test=overviews]").Should().BeEmpty();
        _ = match;
    }

    [Fact]
    public void A_search_that_cannot_be_read_says_why_instead_of_showing_nothing()
    {
        IRenderedComponent<TicketQueue> queue = Render<TicketQueue>(p => p
            .Add(c => c.TenantId, tenantId).Add(c => c.TenantSlug, "entit").Add(c => c.Search, "colour:red"));

        queue.WaitForAssertion(() => queue.Find("[data-test=ticket-search] + .alert").TextContent.Should().Contain("There is no field"));
    }

    [Fact]
    public async Task The_ticket_shows_its_customer_with_their_other_open_tickets()
    {
        Ticket ticket = await Raise("Login fails");
        Ticket other = await Raise("Printer offline");

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);

        string card = detail.Find("[data-test=customer-panel]").TextContent;
        card.Should().Contain("Entit AB").And.Contain($"#{other.Number}").And.Contain("2 open");
    }

    // ───────────────────────────── Phase 5 ─────────────────────────────

    [Fact]
    public async Task The_portal_shows_a_customer_their_public_answers_and_nothing_internal()
    {
        KnowledgeBaseService kb = new(new TestDbContextFactory(connection));
        KbCategory cat = await kb.SaveCategoryAsync(tenantId, null, "Inloggning", null);
        await kb.SaveAnswerAsync(tenantId, new KbAnswer { Title = "Reset your password", CategoryId = cat.Id, Visibility = KbVisibility.Public,
            Language = "sv", BodyHtml = "<p>Use <strong>Forgot password</strong>.</p>" }, "nils");
        await kb.SaveAnswerAsync(tenantId, new KbAnswer { Title = "Admin backdoor", CategoryId = cat.Id, Visibility = KbVisibility.Internal,
            Language = "sv", BodyHtml = "<p>secret</p>" }, "nils");

        Customer customer = db.Customers.AsNoTracking().Single(c => c.Id == customerId);
        IRenderedComponent<EntKube.Web.Components.Pages.Portal.CustomerKnowledgePanel> panel =
            Render<EntKube.Web.Components.Pages.Portal.CustomerKnowledgePanel>(p => p.Add(c => c.Customer, customer));

        panel.WaitForAssertion(() => panel.Markup.Should().Contain("Reset your password").And.NotContain("Admin backdoor"));

        await panel.InvokeAsync(() => panel.Find(".list-group-item").Click());
        panel.Find(".kb-answer").InnerHtml.Should().Contain("<strong>Forgot password</strong>");
    }

    [Fact]
    public async Task Typing_question_marks_in_a_reply_offers_answers_for_this_customer_and_inserts_one()
    {
        Ticket ticket = await Raise();
        KnowledgeBaseService kb = new(new TestDbContextFactory(connection));
        KbCategory cat = await kb.SaveCategoryAsync(tenantId, null, "Allmänt", null);
        await kb.SaveAnswerAsync(tenantId, new KbAnswer { Title = "VPN setup", CategoryId = cat.Id, Visibility = KbVisibility.Internal,
            Language = "en", BodyHtml = "<p>Install the client.</p>" }, "nils");
        Guid elsewhere = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = elsewhere, TenantId = tenantId, Name = "Annan kund" });
        await db.SaveChangesAsync();
        await kb.SaveAnswerAsync(tenantId, new KbAnswer { Title = "VPN for Annan kund", CategoryId = cat.Id, Visibility = KbVisibility.Public,
            Language = "sv", CustomerId = elsewhere, BodyHtml = "<p>Theirs.</p>" }, "nils");

        BunitJSModuleInterop editor = JSInterop.SetupModule("./js/ticket-editor.js");
        editor.SetupVoid("create", _ => true);
        editor.SetupVoid("insertAnswer", _ => true);
        editor.SetupVoid("dispose", _ => true);

        IRenderedComponent<TicketDetail> detail = RenderDetail(ticket.Id);
        await detail.InvokeAsync(() => detail.FindAll("button").First(b => b.TextContent.Trim() == "Reply").Click());

        IRenderedComponent<TicketMailComposer> composer = detail.FindComponent<TicketMailComposer>();
        await composer.InvokeAsync(() => composer.Instance.OnKnowledgeTrigger("vpn"));

        string offered = detail.Find("[data-test=composer-answers]").TextContent;
        offered.Should().Contain("VPN setup").And.NotContain("Annan kund");

        await detail.InvokeAsync(() => detail.Find("[data-test=composer-answers] button").Click());
        editor.Invocations["insertAnswer"].Should().ContainSingle()
            .Which.Arguments[1].Should().Be("<p>Install the client.</p>");
    }
}
