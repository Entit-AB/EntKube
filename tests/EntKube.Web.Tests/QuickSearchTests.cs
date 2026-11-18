using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 8 (docs/helpdesk.md): quick search beyond tickets — this tenant's customers and its
/// knowledge base, for plain words only, never another tenant's, never an archived answer.
/// </summary>
public class QuickSearchTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly KnowledgeBaseService kb;
    private readonly SupportQuickSearch quick;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid vard = Guid.NewGuid();
    private readonly Guid skola = Guid.NewGuid();
    private readonly Guid theirVard = Guid.NewGuid();

    public QuickSearchTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = vard, TenantId = ours, Name = "Vardbolaget Nord" });
        db.Customers.Add(new Customer { Id = skola, TenantId = ours, Name = "Skolbolaget" });
        db.Customers.Add(new Customer { Id = theirVard, TenantId = theirs, Name = "Vardbolaget Syd" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        tickets = new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory));
        kb = new KnowledgeBaseService(factory);
        quick = new SupportQuickSearch(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("vpn drops", true)]
    [InlineData("Vård & co", true)]
    [InlineData("tag:vpn", false)]
    [InlineData("\"exact phrase\"", false)]
    [InlineData("vpn OR printer", false)]
    [InlineData("reported:>now-7d", false)]
    [InlineData("print*", false)]
    [InlineData("", false)]
    public void Only_plain_words_go_beyond_tickets(string query, bool plain) =>
        SupportQuickSearch.IsPlain(query).Should().Be(plain);

    [Fact]
    public async Task Customers_are_found_by_every_word_with_their_open_tickets_and_only_in_this_tenant()
    {
        Ticket open = await tickets.CreateAsync(ours, vard, null, "A", "", TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow);
        Ticket closed = await tickets.CreateAsync(ours, vard, null, "B", "", TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow);
        await tickets.CloseAsync(ours, closed.Id, "nils", DateTime.UtcNow);
        _ = open;

        QuickResults found = await quick.SearchAsync(ours, "vardbolaget nord");

        found.Customers.Should().Equal(new CustomerHit(vard, "Vardbolaget Nord", 1));
        (await quick.SearchAsync(ours, "vardbolaget syd")).Customers.Should().BeEmpty("that customer is another tenant's");
        (await quick.SearchAsync(ours, "bolaget")).Customers.Select(c => c.Name).Should().Equal("Skolbolaget", "Vardbolaget Nord");
    }

    [Fact]
    public async Task Answers_are_found_in_title_keywords_or_text_but_never_archived_or_another_tenants()
    {
        KbCategory cat = await kb.SaveCategoryAsync(ours, null, "Allmänt", null);
        KbCategory theirCat = await kb.SaveCategoryAsync(theirs, null, "Allmänt", null);
        async Task Answer(Guid tenant, Guid category, string title, KbVisibility v, string keywords = "", string body = "<p>x</p>") =>
            await kb.SaveAnswerAsync(tenant, new KbAnswer
            {
                Title = title, CategoryId = category, Visibility = v, Language = "sv", Keywords = keywords, BodyHtml = body,
            }, "nils");

        await Answer(ours, cat.Id, "VPN setup", KbVisibility.Public);
        await Answer(ours, cat.Id, "Remote access", KbVisibility.Internal, keywords: "vpn");
        await Answer(ours, cat.Id, "Half written", KbVisibility.Draft, body: "<p>About the VPN client</p>");
        await Answer(ours, cat.Id, "Old VPN", KbVisibility.Archived);
        await Answer(theirs, theirCat.Id, "Their VPN", KbVisibility.Public);

        (await quick.SearchAsync(ours, "vpn")).Answers.Select(a => a.Title)
            .Should().Equal("Half written", "Remote access", "VPN setup");
    }

    [Fact]
    public async Task A_ticket_query_finds_nothing_beyond_tickets()
    {
        (await quick.SearchAsync(ours, "customer:vardbolaget")).Should().Be(QuickResults.None);
    }

    [Fact]
    public async Task Like_wildcards_typed_as_words_are_taken_literally()
    {
        db.Customers.Add(new Customer { Id = Guid.NewGuid(), TenantId = ours, Name = "100% Drift" });
        await db.SaveChangesAsync();

        (await quick.SearchAsync(ours, "100")).Customers.Should().ContainSingle();
        SupportQuickSearch.IsPlain("100%").Should().BeFalse("% is not a plain-word character, so it never reaches LIKE");
    }
}
