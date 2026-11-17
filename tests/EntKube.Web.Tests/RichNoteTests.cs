using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 7 (docs/helpdesk.md): notes written in the rich editor, and deleting one's own internal
/// note within ten minutes (decision 5) — never a customer-visible entry, never somebody else's,
/// never late.
/// </summary>
public class RichNoteTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketWorkService work;
    private readonly TicketDraftService drafts;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    public RichNoteTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = customer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        Guid role = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole { Id = role, TenantId = ours, Name = "Admin" });
        db.Users.Add(new ApplicationUser { Id = "user-anna", UserName = "anna", Email = "anna@entit.example" });
        db.TenantMemberships.Add(new TenantMembership { UserId = "user-anna", TenantId = ours, RoleId = role });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        TicketNotifier notifier = SilentTicketNotifier.For(factory);
        tickets = new TicketService(factory, new ContractService(factory), notifier);
        work = new TicketWorkService(factory, tickets, notifier, new SupportDutyService(factory));
        drafts = new TicketDraftService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise(Guid? tenant = null) =>
        tickets.CreateAsync(tenant ?? ours, tenant == theirs ? theirCustomer : customer, null, "VPN drops", "",
            TicketChannel.Email, TicketPriority.P3, Now.AddHours(-1));

    /// <summary>A note as it is stored, read fresh — RecordedAt set to when it was "written".</summary>
    private async Task<TicketEvent> Note(Guid ticketId, string userId, bool customerVisible = false, DateTime? writtenAt = null, Guid? tenant = null)
    {
        Guid id = (await tickets.AddNoteAsync(tenant ?? ours, ticketId, "<p>Restarted the <strong>gateway</strong>.</p>",
            "Nils", userId, Now, customerVisible))!.Value;
        await db.TicketEvents.Where(e => e.Id == id).ExecuteUpdateAsync(u => u.SetProperty(e => e.RecordedAt, writtenAt ?? Now));
        db.ChangeTracker.Clear();
        return db.TicketEvents.AsNoTracking().Single(e => e.Id == id);
    }

    // ───────────────────────────── Rich notes ─────────────────────────────

    [Fact]
    public async Task A_note_from_the_editor_is_kept_sanitised_with_its_text_beside_it()
    {
        Ticket t = await Raise();

        Guid? id = await tickets.AddNoteAsync(ours, t.Id,
            "<p>Restarted the <strong>gateway</strong>.</p><script>steal()</script><img src=x onerror=alert(1)>",
            "Nils", "user-nils", Now, customerVisible: false);

        TicketEvent e = db.TicketEvents.AsNoTracking().Single(x => x.Id == id);
        e.DetailHtml.Should().Be("<p>Restarted the <strong>gateway</strong>.</p>");
        e.Detail.Should().Be("Restarted the gateway.");
        e.ActorUserId.Should().Be("user-nils");
        e.Kind.Should().Be(TicketEventKind.Note);
    }

    [Theory]
    [InlineData("<p></p>")]
    [InlineData("<p><br></p>")]
    [InlineData("<script>only()</script>")]
    public async Task An_editor_with_nothing_in_it_adds_no_note(string html)
    {
        Ticket t = await Raise();

        Func<Task> add = () => tickets.AddNoteAsync(ours, t.Id, html, "Nils", "user-nils", Now, false);

        await add.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_note_cannot_be_written_onto_another_tenants_ticket()
    {
        Ticket theirsTicket = await Raise(theirs);

        (await tickets.AddNoteAsync(ours, theirsTicket.Id, "<p>x</p>", "Nils", "user-nils", Now, false)).Should().BeNull();
        db.TicketEvents.Count(e => e.TicketId == theirsTicket.Id && e.Kind == TicketEventKind.Note).Should().Be(0);
    }

    [Fact]
    public async Task A_note_mentioning_colleagues_keeps_its_formatting_and_says_who_was_told_in_both_forms()
    {
        Ticket t = await Raise();

        List<string>? reached = await work.AddNoteMentioningAsync(ours, t.Id, "<p>Can you look at <em>this</em>?</p>", false,
            [new TicketWorkService.Mentioned("user-anna", "Anna", "anna@entit.example")], "Nils", "user-nils", Now);

        reached.Should().NotBeNull();
        TicketEvent e = db.TicketEvents.AsNoTracking().Single(x => x.TicketId == t.Id && x.Kind == TicketEventKind.Note);
        e.DetailHtml.Should().Be("<p>Can you look at <em>this</em>?</p><p>→ Anna</p>");
        e.Detail.Should().Be("Can you look at this?\n\n→ Anna");
        e.ActorUserId.Should().Be("user-nils");
        db.TicketSubscriptions.AsNoTracking().Should().Contain(s => s.TicketId == t.Id && s.UserId == "user-anna");
    }

    // ───────────────────────────── Deleting (decision 5) ─────────────────────────────

    [Fact]
    public async Task Its_author_deletes_an_internal_note_within_ten_minutes_and_its_highlights_go_with_it()
    {
        Ticket t = await Raise();
        TicketEvent note = await Note(t.Id, "user-nils");
        await new TicketHighlightService(new TestDbContextFactory(connection)).AddAsync(ours, note.Id, "gateway", HighlightColor.Yellow, "nils");

        await tickets.DeleteNoteAsync(ours, note.Id, "user-nils", Now.AddMinutes(10));

        db.TicketEvents.Any(e => e.Id == note.Id).Should().BeFalse();
        db.TicketHighlights.Any(h => h.EventId == note.Id).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_added_with_a_deleted_note_stays_on_the_ticket()
    {
        Ticket t = await Raise();
        TicketEvent note = await Note(t.Id, "user-nils");
        Guid file = Guid.NewGuid();
        db.TicketAttachments.Add(new TicketAttachment
        {
            Id = file, TenantId = ours, TicketId = t.Id, TicketEventId = note.Id, FileName = "log.txt", Source = AttachmentSource.Agent,
        });
        await db.SaveChangesAsync();

        await tickets.DeleteNoteAsync(ours, note.Id, "user-nils", Now);

        db.ChangeTracker.Clear();
        db.TicketAttachments.Single(a => a.Id == file).Should().Match<TicketAttachment>(a => a.TicketId == t.Id && a.TicketEventId == null);
    }

    [Fact]
    public async Task Somebody_elses_note_is_not_theirs_to_delete()
    {
        Ticket t = await Raise();
        TicketEvent note = await Note(t.Id, "user-anna");

        Func<Task> delete = () => tickets.DeleteNoteAsync(ours, note.Id, "user-nils", Now);

        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Only the person who wrote*");
        db.TicketEvents.Any(e => e.Id == note.Id).Should().BeTrue();
    }

    [Fact]
    public async Task What_the_customer_can_see_is_never_deleted()
    {
        Ticket t = await Raise();
        TicketEvent note = await Note(t.Id, "user-nils", customerVisible: true);

        Func<Task> delete = () => tickets.DeleteNoteAsync(ours, note.Id, "user-nils", Now);

        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Only internal notes*");
    }

    [Fact]
    public async Task After_ten_minutes_a_note_stays()
    {
        Ticket t = await Raise();
        TicketEvent note = await Note(t.Id, "user-nils");

        Func<Task> delete = () => tickets.DeleteNoteAsync(ours, note.Id, "user-nils", Now.AddMinutes(10).AddSeconds(1));

        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*first ten minutes*");
    }

    /// <summary>The window runs from when the note was WRITTEN, not from the time it is dated.</summary>
    [Fact]
    public async Task The_window_counts_from_when_the_note_was_written()
    {
        Ticket t = await Raise();
        TicketEvent note = await Note(t.Id, "user-nils", writtenAt: Now.AddMinutes(30));

        TicketService.CanDeleteNote(note, "user-nils", Now.AddMinutes(35)).Should().BeTrue("At is earlier, but it was written five minutes ago");
    }

    [Fact]
    public async Task Entries_that_are_not_notes_and_notes_from_before_authors_were_recorded_stay()
    {
        Ticket t = await Raise();
        TicketEvent created = db.TicketEvents.AsNoTracking().First(e => e.TicketId == t.Id);
        TicketEvent legacy = new()
        {
            Id = Guid.NewGuid(), TicketId = t.Id, Kind = TicketEventKind.Note, Detail = "old", CustomerVisible = false,
            Actor = "Nils", RecordedAt = Now,
        };
        db.TicketEvents.Add(legacy);
        await db.SaveChangesAsync();

        TicketService.CanDeleteNote(created, "user-nils", Now).Should().BeFalse();
        TicketService.CanDeleteNote(legacy, "user-nils", Now).Should().BeFalse("nobody can prove they wrote it");
        TicketService.CanDeleteNote(legacy, null, Now).Should().BeFalse();
    }

    [Fact]
    public async Task Another_tenants_note_is_out_of_reach()
    {
        Ticket theirsTicket = await Raise(theirs);
        TicketEvent note = await Note(theirsTicket.Id, "user-nils", tenant: theirs);

        Func<Task> delete = () => tickets.DeleteNoteAsync(ours, note.Id, "user-nils", Now);

        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no such entry*");
        db.TicketEvents.Any(e => e.Id == note.Id).Should().BeTrue();
    }

    // ───────────────────────────── Drafts ─────────────────────────────

    [Fact]
    public async Task A_note_draft_keeps_its_formatting_sanitised_and_an_empty_editor_clears_it()
    {
        Ticket t = await Raise();

        await drafts.SavePersonalNoteAsync(ours, t.Id, "user-nils", "<p>Half <strong>done</strong></p><script>x()</script>", false);
        (await drafts.GetPersonalNoteAsync(ours, t.Id, "user-nils"))!.Value.Html.Should().Be("<p>Half <strong>done</strong></p>");

        await drafts.SavePersonalNoteAsync(ours, t.Id, "user-nils", "<p><br></p>", false);
        (await drafts.GetPersonalNoteAsync(ours, t.Id, "user-nils")).Should().BeNull();
    }

    /// <summary>A draft saved before notes were rich is plain text, and must come back as what was typed.</summary>
    [Fact]
    public void A_plain_text_draft_from_before_comes_back_as_its_text()
    {
        NoteDraft old = new("Line one\nLine <two> & three", false, "nils", Now);

        old.Html.Should().Be("Line one<br>Line &lt;two&gt; &amp; three");
    }

    [Fact]
    public async Task An_empty_editor_cannot_be_left_as_a_shared_draft()
    {
        Ticket t = await Raise();

        Func<Task> share = () => drafts.ShareNoteAsync(ours, t.Id, "<p></p>", false, "Nils", overwrite: false);

        await share.Should().ThrowAsync<ArgumentException>();
    }
}
