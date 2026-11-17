using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 2b (docs/helpdesk.md): the note a person is typing survives a reload and is theirs
/// alone; a shared draft is one per ticket, overwritten only when asked, and gone once a note is
/// made from it; new-ticket drafts belong to the tenant.
/// </summary>
public class TicketDraftTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketDraftService drafts;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    public TicketDraftTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = customer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        tickets = new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory));
        drafts = new TicketDraftService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise() => tickets.CreateAsync(
        ours, customer, null, "Login fails", "", TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow.AddHours(-1));

    [Fact]
    public async Task What_one_person_types_comes_back_to_them_and_to_nobody_else()
    {
        Ticket t = await Raise();

        await drafts.SavePersonalNoteAsync(ours, t.Id, "anna", "Half a thought", customerVisible: true);

        (await drafts.GetPersonalNoteAsync(ours, t.Id, "anna")).Should().BeEquivalentTo(new { Text = "Half a thought", CustomerVisible = true });
        (await drafts.GetPersonalNoteAsync(ours, t.Id, "nils")).Should().BeNull();
        (await drafts.GetPersonalNoteAsync(theirs, t.Id, "anna")).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_the_box_clears_what_would_be_restored()
    {
        Ticket t = await Raise();
        await drafts.SavePersonalNoteAsync(ours, t.Id, "anna", "Something", false);
        await drafts.SavePersonalNoteAsync(ours, t.Id, "anna", "   ", false);

        (await drafts.GetPersonalNoteAsync(ours, t.Id, "anna")).Should().BeNull();
    }

    [Fact]
    public async Task Another_tenant_cannot_leave_a_draft_on_our_ticket()
    {
        Ticket t = await Raise();

        await drafts.SavePersonalNoteAsync(theirs, t.Id, "mallory", "x", false);
        (await drafts.ShareNoteAsync(theirs, t.Id, "x", false, "mallory", overwrite: true)).Should().BeFalse();

        db.TicketDrafts.AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public async Task A_shared_draft_is_replaced_only_when_asked()
    {
        Ticket t = await Raise();
        await drafts.ShareNoteAsync(ours, t.Id, "Anna's version", false, "anna", overwrite: false);

        Func<Task> again = () => drafts.ShareNoteAsync(ours, t.Id, "Nils's version", false, "nils", overwrite: false);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("anna already left a draft*");
        (await drafts.GetSharedNoteAsync(ours, t.Id))!.Value.Text.Should().Be("Anna's version");

        await drafts.ShareNoteAsync(ours, t.Id, "Nils's version", false, "nils", overwrite: true);
        (await drafts.GetSharedNoteAsync(ours, t.Id))!.Value.By.Should().Be("nils");
    }

    [Fact]
    public async Task Adding_a_note_made_from_the_shared_draft_removes_it_and_the_writers_own()
    {
        Ticket t = await Raise();
        await drafts.ShareNoteAsync(ours, t.Id, "Shared", false, "anna", false);
        await drafts.SavePersonalNoteAsync(ours, t.Id, "nils", "Shared", false);
        await drafts.SavePersonalNoteAsync(ours, t.Id, "anna", "Anna's own", false);

        await drafts.NoteAddedAsync(ours, t.Id, "nils", fromSharedDraft: true);

        (await drafts.GetSharedNoteAsync(ours, t.Id)).Should().BeNull();
        (await drafts.GetPersonalNoteAsync(ours, t.Id, "nils")).Should().BeNull();
        (await drafts.GetPersonalNoteAsync(ours, t.Id, "anna")).Should().NotBeNull("only the writer's own draft goes");
    }

    [Fact]
    public async Task A_note_not_made_from_the_shared_draft_leaves_it_alone()
    {
        Ticket t = await Raise();
        await drafts.ShareNoteAsync(ours, t.Id, "Shared", false, "anna", false);

        await drafts.NoteAddedAsync(ours, t.Id, "nils", fromSharedDraft: false);

        (await drafts.GetSharedNoteAsync(ours, t.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task New_ticket_drafts_are_the_tenants_and_saving_under_no_id_makes_a_copy()
    {
        NewTicketDraft form = new() { Title = "Printer", Description = "Offline", CustomerId = customer, Tags = "printer" };

        TicketDraft first = await drafts.SaveNewTicketDraftAsync(ours, null, "Printer call", form, "anna");
        await drafts.SaveNewTicketDraftAsync(ours, first.Id, "Printer call (edited)", form, "nils");
        await drafts.SaveNewTicketDraftAsync(ours, null, "Copy", form, "nils");

        (await drafts.ListNewTicketDraftsAsync(ours)).Select(d => d.Name).Should().BeEquivalentTo(["Printer call (edited)", "Copy"]);
        (await drafts.ListNewTicketDraftsAsync(theirs)).Should().BeEmpty();

        NewTicketDraft loaded = new();
        TicketDraftService.Load(first, loaded);
        loaded.Title.Should().Be("Printer");

        (await drafts.DeleteNewTicketDraftAsync(theirs, first.Id)).Should().BeFalse();
        (await drafts.DeleteNewTicketDraftAsync(ours, first.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_new_ticket_draft_cannot_name_another_tenants_customer()
    {
        Func<Task> save = () => drafts.SaveNewTicketDraftAsync(
            ours, null, "x", new NewTicketDraft { CustomerId = theirCustomer }, "nils");

        await save.Should().ThrowAsync<InvalidOperationException>();
    }
}
