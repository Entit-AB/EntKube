using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 6 (docs/helpdesk.md): the highlighter. A highlight sits beside an entry — the entry is
/// never changed — and only words that are in the entry can be marked.
/// </summary>
public class TicketHighlightTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketHighlightService highlights;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    public TicketHighlightTests()
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
        highlights = new TicketHighlightService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<TicketEvent> Entry(Guid tenant, string detail, string? html = null)
    {
        Ticket ticket = await tickets.CreateAsync(tenant, tenant == theirs ? theirCustomer : customer, null, "T", "",
            TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow);

        TicketEvent entry = new()
        {
            Id = Guid.NewGuid(), TicketId = ticket.Id, Kind = TicketEventKind.Note, Detail = detail, DetailHtml = html,
        };
        db.TicketEvents.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    [Fact]
    public async Task A_passage_in_the_entry_is_highlighted_and_the_entry_is_left_as_it_was()
    {
        TicketEvent entry = await Entry(ours, "The printer on floor 3 stops after the first page.");

        TicketHighlight h = await highlights.AddAsync(ours, entry.Id, " stops after the first page ", HighlightColor.Green, "nils");

        h.Text.Should().Be("stops after the first page");
        (await highlights.ForEntriesAsync(ours, [entry.Id])).Should().ContainSingle().Which.Color.Should().Be(HighlightColor.Green);
        db.ChangeTracker.Clear();
        db.TicketEvents.Single(e => e.Id == entry.Id).Detail.Should().Be("The printer on floor 3 stops after the first page.");
    }

    /// <summary>A selection across two paragraphs carries line breaks the stored text spells differently.</summary>
    [Fact]
    public async Task A_selection_across_paragraphs_of_a_rich_entry_matches_ignoring_line_breaks()
    {
        TicketEvent entry = await Entry(ours, "", "<p>First we restarted &amp; waited.</p><p>Then it worked.</p>");

        // The stored text breaks the paragraphs with a line break; this selection, as a browser may
        // give it, with a space. Only a comparison that ignores whitespace sees the same words.
        Func<Task> add = () => highlights.AddAsync(ours, entry.Id, "restarted & waited. Then it", HighlightColor.Yellow, "nils");

        await add.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Words_that_are_not_in_the_entry_cannot_be_highlighted()
    {
        TicketEvent entry = await Entry(ours, "The printer stops.");

        Func<Task> add = () => highlights.AddAsync(ours, entry.Id, "the customer admitted fault", HighlightColor.Yellow, "nils");

        await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not in this entry*");
    }

    [Fact]
    public async Task Another_tenants_entries_and_highlights_are_out_of_reach()
    {
        TicketEvent theirEntry = await Entry(theirs, "Their secret words.");
        TicketHighlight theirs1 = await highlights.AddAsync(theirs, theirEntry.Id, "secret", HighlightColor.Pink, "eve");

        Func<Task> add = () => highlights.AddAsync(ours, theirEntry.Id, "secret", HighlightColor.Yellow, "nils");
        await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no such entry*");

        (await highlights.ForEntriesAsync(ours, [theirEntry.Id])).Should().BeEmpty();
        (await highlights.RemoveAsync(ours, theirs1.Id)).Should().BeFalse();
        (await highlights.ForEntriesAsync(theirs, [theirEntry.Id])).Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_selection_is_refused(string text)
    {
        TicketEvent entry = await Entry(ours, "Anything.");

        Func<Task> add = () => highlights.AddAsync(ours, entry.Id, text, HighlightColor.Yellow, "nils");

        await add.Should().ThrowAsync<ArgumentException>();
    }
}
