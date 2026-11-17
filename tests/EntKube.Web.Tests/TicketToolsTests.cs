using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 2a of helpdesk parity (docs/helpdesk.md): text modules, ticket templates, macros and
/// checklists — and where each meets a rule of the agreement or of tenancy.
/// </summary>
public class TicketToolsTests : IDisposable
{
    private static DateTime Tue(int hour, int minute = 0) => TimeZoneInfo.ConvertTimeToUtc(
        new DateTime(2026, 9, 22, hour, minute, 0, DateTimeKind.Unspecified), BusinessCalendar.SwedishTime);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketWorkService work;
    private readonly TextModuleService modules;
    private readonly TicketTemplateService templates;
    private readonly MacroService macros;
    private readonly TicketChecklistService checklists;
    private readonly TicketBulkService bulk;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid otherCustomer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    public TicketToolsTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = customer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = otherCustomer, TenantId = ours, Name = "Skolbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        TicketNotifier notifier = SilentTicketNotifier.For(factory);
        tickets = new TicketService(factory, new ContractService(factory), notifier);
        work = new TicketWorkService(factory, tickets, notifier, new SupportDutyService(factory));
        modules = new TextModuleService(factory);
        templates = new TicketTemplateService(factory);
        macros = new MacroService(factory, tickets, work);
        checklists = new TicketChecklistService(factory);
        bulk = new TicketBulkService(tickets, work, macros);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise(string title = "Login fails", Guid? tenant = null, Guid? cust = null) =>
        tickets.CreateAsync(tenant ?? ours, cust ?? customer, null, title, "", TicketChannel.Email,
            TicketPriority.P3, Tue(9), "Karin", "karin@kund.example");

    private Ticket Fresh(Guid id)
    {
        db.ChangeTracker.Clear();
        return db.Tickets.Include(t => t.Tags).Include(t => t.Events).Single(t => t.Id == id);
    }

    // ───────────────────────────── Text modules ─────────────────────────────

    [Fact]
    public void Placeholders_are_filled_and_an_unknown_or_empty_one_becomes_a_dash()
    {
        string text = TextModuleRenderer.Render(
            "Hej #{requester.name}, ärende #{ticket.number} (#{app.name}) #{ticket.nubmer}",
            new Dictionary<string, string?> { ["requester.name"] = "Karin", ["ticket.number"] = "1042", ["app.name"] = null });

        text.Should().Be("Hej Karin, ärende 1042 (-) -",
            "a placeholder left in place would put our template syntax in front of the customer");

        TextModuleRenderer.Unknown("#{ticket.nubmer} #{ticket.number}").Should().Equal("ticket.nubmer");
    }

    [Fact]
    public async Task A_customers_own_module_is_offered_only_on_that_customers_tickets()
    {
        await modules.SaveAsync(ours, new TextModule { Name = "Greeting", Content = "Hello" }, "nils");
        await modules.SaveAsync(ours, new TextModule { Name = "Vård signature", CustomerId = customer, Content = "…" }, "nils");
        await modules.SaveAsync(ours, new TextModule { Name = "Old", Content = "x", IsActive = false }, "nils");

        (await modules.OfferedAsync(ours, customer)).Select(m => m.Name).Should().Equal("Greeting", "Vård signature");
        (await modules.OfferedAsync(ours, otherCustomer)).Select(m => m.Name).Should().Equal("Greeting");
        (await modules.OfferedAsync(ours, customer, "sign")).Select(m => m.Name).Should().Equal("Vård signature");
    }

    [Fact]
    public async Task A_module_cannot_be_narrowed_to_another_tenants_customer()
    {
        Func<Task> save = () => modules.SaveAsync(ours, new TextModule { Name = "X", CustomerId = theirCustomer }, "nils");
        await save.Should().ThrowAsync<InvalidOperationException>();
    }

    // ───────────────────────────── Templates ─────────────────────────────

    [Fact]
    public void A_template_fills_only_what_is_still_empty()
    {
        NewTicketDraft draft = new() { Title = "Typed by the agent" };

        TicketTemplateService.Apply(new TicketTemplate
        {
            Name = "Password reset", Title = "Password reset", Description = "Reset requested by phone.",
            Priority = TicketPriority.P4, Tags = "password",
        }, draft);

        draft.Title.Should().Be("Typed by the agent");
        draft.Description.Should().Be("Reset requested by phone.");
        draft.Priority.Should().Be(TicketPriority.P4);
        draft.Tags.Should().Be("password");
    }

    // ───────────────────────────── Macros ─────────────────────────────

    [Fact]
    public async Task A_macro_that_sets_a_priority_must_carry_the_reason_14_3_requires()
    {
        Func<Task> save = () => macros.SaveAsync(ours, new Macro { Name = "Escalate", Priority = TicketPriority.P2 }, "nils");
        await save.Should().ThrowAsync<ArgumentException>().WithMessage("*§14.3*");
    }

    [Fact]
    public async Task A_macro_runs_as_the_person_who_ran_it_and_names_itself_on_the_history()
    {
        Ticket t = await Raise();
        Macro m = await macros.SaveAsync(ours, new Macro
        {
            Name = "Take and tag", Assign = MacroAssign.Me, AddTags = "vpn, network",
            Priority = TicketPriority.P2, PriorityReason = "Whole site affected.",
            Note = "Looking into the VPN.",
        }, "admin");

        MacroRun? run = await macros.RunAsync(ours, t.Id, m.Id, "nils", ("nils", "user-nils", null), Tue(10));

        run!.Value.Applied.Should().Contain(["assigned to nils", "priority P2", "internal note"]);

        Ticket after = Fresh(t.Id);
        after.Assignee.Should().Be("nils");
        after.Priority.Should().Be(TicketPriority.P2);
        after.PriorityConfirmedBy.Should().Be("nils (macro “Take and tag”)");
        after.Tags.Select(x => x.Name).Should().BeEquivalentTo(["vpn", "network"]);
        after.Events.Single(e => e.Detail == "Looking into the VPN.").CustomerVisible.Should().BeFalse();
    }

    /// <summary>All or nothing: a macro that closes a ticket it does not resolve changes nothing on an open one.</summary>
    [Fact]
    public async Task A_macro_that_cannot_finish_changes_nothing()
    {
        Ticket t = await Raise();
        Macro m = await macros.SaveAsync(ours, new Macro { Name = "Tag and close", AddTags = "done", Close = true }, "admin");

        Func<Task> run = () => macros.RunAsync(ours, t.Id, m.Id, "nils", ("nils", null, null), Tue(10));

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Nothing was changed*");
        Fresh(t.Id).Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task A_macro_can_resolve_and_close_in_one_go()
    {
        Ticket t = await Raise();
        Macro m = await macros.SaveAsync(ours, new Macro
        {
            Name = "Close as spam", AddTags = "spam", Resolution = "Spam; no action needed.", Close = true,
        }, "admin");

        await macros.RunAsync(ours, t.Id, m.Id, "nils", ("nils", null, null), Tue(10));

        Fresh(t.Id).Status.Should().Be(TicketStatus.Closed);
    }

    [Fact]
    public async Task Another_tenants_macro_does_not_run_here_and_ours_does_not_run_there()
    {
        Ticket ourTicket = await Raise();
        Ticket theirTicket = await Raise("Theirs", theirs, theirCustomer);
        Macro ourMacro = await macros.SaveAsync(ours, new Macro { Name = "Tag", AddTags = "x" }, "admin");

        (await macros.RunAsync(theirs, theirTicket.Id, ourMacro.Id, "x", ("x", null, null), Tue(10))).Should().BeNull();
        (await macros.RunAsync(ours, theirTicket.Id, ourMacro.Id, "x", ("x", null, null), Tue(10))).Should().BeNull();
        Fresh(theirTicket.Id).Tags.Should().BeEmpty();
        _ = ourTicket;
    }

    [Fact]
    public async Task A_macro_in_bulk_reports_the_tickets_it_could_not_finish()
    {
        Ticket open = await Raise("Open");
        Ticket done = await Raise("Done");
        await tickets.ResolveAsync(ours, done.Id, "Fixed.", "nils", Tue(9, 30));
        Macro m = await macros.SaveAsync(ours, new Macro { Name = "Close resolved", Close = true }, "admin");

        List<BulkOutcome> outcomes = await bulk.ApplyAsync(
            ours, [open.Id, done.Id], new BulkChange.RunMacro(m.Id, "nils", null, null), "nils", Tue(10));

        outcomes.Single(o => o.TicketId == done.Id).Skipped.Should().BeNull();
        outcomes.Single(o => o.TicketId == open.Id).Skipped.Should().Contain("not resolved");
    }

    // ───────────────────────────── Checklists ─────────────────────────────

    [Fact]
    public async Task A_checklist_starts_from_a_template_and_a_ticket_has_only_one()
    {
        Ticket t = await Raise();
        ChecklistTemplate tpl = await checklists.SaveTemplateAsync(ours, new ChecklistTemplate
        {
            Name = "New user", Items = "Create account\n\n  Send welcome mail  \n",
        }, "admin");

        await checklists.CreateAsync(ours, t.Id, tpl.Id, "nils");

        ChecklistView? view = await checklists.GetAsync(ours, t.Id);
        view!.Title.Should().Be("New user");
        view.Lines.Select(l => l.Text).Should().Equal("Create account", "Send welcome mail");
        view.Open.Should().Be(2);

        Func<Task> second = () => checklists.CreateAsync(ours, t.Id, null, "nils");
        await second.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>An item naming another ticket is done when that ticket is, and cannot be ticked by hand.</summary>
    [Fact]
    public async Task An_item_naming_a_ticket_follows_that_ticket()
    {
        Ticket main = await Raise("Main");
        Ticket sub = await Raise("Sub");
        await checklists.CreateAsync(ours, main.Id, null, "nils");
        await checklists.AddItemAsync(ours, main.Id, $"Wait for #{sub.Number}");

        ChecklistLine line = (await checklists.GetAsync(ours, main.Id))!.Lines.Single();
        line.Done.Should().BeFalse();

        Func<Task> tick = () => checklists.SetCheckedAsync(ours, main.Id, line.Id, true, "nils");
        await tick.Should().ThrowAsync<InvalidOperationException>();

        await tickets.CloseAsync(ours, sub.Id, "nils", Tue(11));
        (await checklists.GetAsync(ours, main.Id))!.Lines.Single().Done.Should().BeTrue();
    }

    [Fact]
    public async Task An_item_naming_another_tenants_ticket_reveals_nothing_about_it()
    {
        Ticket main = await Raise("Main");
        Ticket theirTicket = await Raise("Theirs", theirs, theirCustomer);
        await tickets.CloseAsync(theirs, theirTicket.Id, "x", Tue(10));

        await checklists.CreateAsync(ours, main.Id, null, "nils");
        await checklists.AddItemAsync(ours, main.Id, $"Wait for #{theirTicket.Number}");

        ChecklistLine line = (await checklists.GetAsync(ours, main.Id))!.Lines.Single();
        line.Done.Should().BeFalse("whether another tenant's ticket is closed is not ours to learn");
        line.ReferenceFound.Should().BeFalse();
    }

    [Fact]
    public async Task Items_tick_reorder_and_go_and_another_tenant_cannot_touch_them()
    {
        Ticket t = await Raise();
        await checklists.CreateAsync(ours, t.Id, null, "nils");
        await checklists.AddItemAsync(ours, t.Id, "First");
        await checklists.AddItemAsync(ours, t.Id, "Second");

        List<ChecklistLine> lines = [.. (await checklists.GetAsync(ours, t.Id))!.Lines];

        (await checklists.SetCheckedAsync(theirs, t.Id, lines[0].Id, true, "x")).Should().BeFalse();
        (await checklists.SetCheckedAsync(ours, t.Id, lines[0].Id, true, "nils")).Should().BeTrue();
        await checklists.MoveItemAsync(ours, t.Id, lines[1].Id, -1);

        ChecklistView after = (await checklists.GetAsync(ours, t.Id))!;
        after.Lines.Select(l => l.Text).Should().Equal("Second", "First");
        after.Lines.Single(l => l.Text == "First").CheckedBy.Should().Be("nils");
        after.Open.Should().Be(1);

        (await checklists.RemoveItemAsync(theirs, t.Id, lines[0].Id)).Should().BeFalse();
        (await checklists.DeleteAsync(ours, t.Id)).Should().BeTrue();
        (await checklists.GetAsync(ours, t.Id)).Should().BeNull();
    }
}
