using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 3 (docs/helpdesk.md): Zammad's search syntax compiled to a database query. Run against
/// SQLite here, so every query below is actually translated and executed, not just parsed.
/// </summary>
public class TicketSearchTests : IDisposable
{
    private static DateTime Swedish(int day, int hour) => TimeZoneInfo.ConvertTimeToUtc(
        new DateTime(2026, 9, day, hour, 0, 0, DateTimeKind.Unspecified), BusinessCalendar.SwedishTime);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly TicketWorkService work;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid vard = Guid.NewGuid();
    private readonly Guid skola = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();
    private readonly Guid journal = Guid.NewGuid();

    private Ticket login = null!, printer = null!, percent = null!, closed = null!;

    public TicketSearchTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Other", Slug = "other" });
        db.Customers.Add(new Customer { Id = vard, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer { Id = skola, TenantId = ours, Name = "Skolbolaget" });
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = theirs, Name = "Regionen" });
        db.Apps.Add(new App { Id = journal, CustomerId = vard, Name = "Journalportalen" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        TicketNotifier notifier = SilentTicketNotifier.For(factory);
        tickets = new TicketService(factory, new ContractService(factory), notifier);
        work = new TicketWorkService(factory, tickets, notifier, new SupportDutyService(factory));

        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        login = await tickets.CreateAsync(ours, vard, journal, "Login fails for everyone", "Nobody can sign in.",
            TicketChannel.Email, TicketPriority.P1, Swedish(22, 9), "Karin Berg", "karin@vard.example");
        printer = await tickets.CreateAsync(ours, skola, null, "Printer offline", "Floor 3.",
            TicketChannel.Conversation, TicketPriority.P4, Swedish(21, 14), "Lars", "lars@skola.example");
        percent = await tickets.CreateAsync(ours, skola, null, "Disk at 100% on db01", "",
            TicketChannel.Monitoring, TicketPriority.P2, Swedish(20, 8));
        closed = await tickets.CreateAsync(ours, vard, null, "Old VPN question", "",
            TicketChannel.Email, TicketPriority.P3, Swedish(1, 10));
        await tickets.CreateAsync(theirs, theirCustomer, null, "Login fails for them too", "",
            TicketChannel.Email, TicketPriority.P1, Swedish(22, 9));

        await tickets.RecordResponseAsync(ours, login.Id, "nils", Swedish(22, 9));
        await tickets.AssignAsync(ours, login.Id, "nils", "nils", Swedish(22, 9));
        await tickets.AddEventAsync(ours, printer.Id, TicketEventKind.Note, "Toner replaced, still offline.", "lars", Swedish(21, 15));
        await work.AddTagAsync(ours, login.Id, "vpn", "nils", Swedish(22, 9));
        await work.AddTagAsync(ours, closed.Id, "vpn", "nils", Swedish(1, 10));
        await work.AddTagAsync(ours, printer.Id, "hardware", "nils", Swedish(21, 14));
        await tickets.CloseAsync(ours, closed.Id, "nils", Swedish(2, 10));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<List<int>> Find(string query, DateTime? now = null) =>
        [.. (await tickets.SearchAsync(ours, query, now ?? Swedish(22, 12))).Tickets.Select(s => s.Ticket.Number).Order()];

    private static List<int> Numbers(params Ticket[] ts) => [.. ts.Select(t => t.Number).Order()];

    [Fact]
    public async Task A_bare_word_looks_in_the_title_the_description_and_the_history()
    {
        (await Find("login")).Should().Equal(Numbers(login));
        (await Find("sign")).Should().Equal(Numbers(login));
        (await Find("toner")).Should().Equal(Numbers(printer));
        (await Find($"{printer.Number}")).Should().Equal(Numbers(printer));
    }

    [Fact]
    public async Task Another_tenants_ticket_is_never_found_whatever_the_query_says()
    {
        (await Find("login OR them")).Should().Equal(Numbers(login));
        (await Find("customer:regionen")).Should().BeEmpty();
    }

    [Fact]
    public async Task Fields_phrases_and_wildcards()
    {
        (await Find("title:\"fails for everyone\"")).Should().Equal(Numbers(login));
        (await Find("title:print*")).Should().Equal(Numbers(printer));
        (await Find("customer:vård")).Should().Equal(Numbers(login, closed));
        (await Find("app:journal")).Should().Equal(Numbers(login));
        (await Find("requester:karin")).Should().Equal(Numbers(login));
        (await Find("assignee:nils")).Should().Equal(Numbers(login));
        (await Find("assignee:nobody status:new")).Should().Equal(Numbers(printer, percent));
    }

    [Fact]
    public async Task Statuses_priorities_channels_and_tags_by_our_names_and_by_zammads()
    {
        (await Find("status:open")).Should().Equal(Numbers(login));
        (await Find("state.name:(closed OR open)")).Should().Equal(Numbers(login, closed));
        (await Find("!status:closed tag:vpn")).Should().Equal(Numbers(login));
        (await Find("priority:p1")).Should().Equal(Numbers(login));
        (await Find("channel:phone")).Should().Equal(Numbers(printer));
        (await Find("tags:vpn")).Should().Equal(Numbers(login, closed));
        (await Find("tag:hard*")).Should().Equal(Numbers(printer));
    }

    [Fact]
    public async Task Dates_are_swedish_days_ranges_and_comparisons()
    {
        (await Find("reported:2026-09-21")).Should().Equal(Numbers(printer));
        (await Find("created_at:[2026-09-20 TO 2026-09-21]")).Should().Equal(Numbers(printer, percent));
        (await Find("reported:[2026-09-21 TO *]")).Should().Equal(Numbers(login, printer));
        (await Find("reported:>now-4h", Swedish(22, 12))).Should().Equal(Numbers(login));
        (await Find("closed:<2026-09-03")).Should().Equal(Numbers(closed));
    }

    [Fact]
    public async Task Number_ranges_and_boolean_grouping()
    {
        (await Find($"number:[{printer.Number} TO {percent.Number}]")).Should().Equal(Numbers(printer, percent));
        (await Find("(login OR printer) AND priority:P4")).Should().Equal(Numbers(printer));
        (await Find("login OR printer priority:P4")).Should().Equal(Numbers(login, printer), "AND binds tighter than OR");
    }

    /// <summary>LIKE's own characters typed in a search are literal: "100%" is not "everything".</summary>
    [Fact]
    public async Task What_is_typed_is_matched_literally_even_when_it_is_a_like_wildcard()
    {
        (await Find("title:\"100%\"")).Should().Equal(Numbers(percent));
        (await Find("title:\"_\"")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("colour:red", "There is no field")]
    [InlineData("title:heat~", "Fuzzy")]
    [InlineData("title:/joh?n/", "Regular expressions")]
    [InlineData("title:\"open", "never closed")]
    [InlineData("(login", "never closed")]
    [InlineData("status:sleeping", "not a status")]
    [InlineData("reported:yesterday", "YYYY-MM-DD")]
    [InlineData("title:[a TO b]", "takes words")]
    public async Task A_query_that_cannot_be_read_says_what_was_wrong(string query, string says)
    {
        Func<Task> search = () => tickets.SearchAsync(ours, query, DateTime.UtcNow);
        await search.Should().ThrowAsync<TicketSearchException>().WithMessage($"*{says}*");
    }

    // ───────────────────────────── The customer card ─────────────────────────────

    [Fact]
    public async Task The_customer_card_counts_this_customers_tickets_and_no_one_elses()
    {
        TicketService.CustomerSummary? s = await tickets.CustomerSummaryAsync(ours, login.Id, Swedish(22, 12));

        s!.Customer.Should().Be("Vårdbolaget");
        s.Open.Should().Be(1, "the closed VPN question is not open, and Skolbolaget's tickets are not theirs");
        s.LastTwelveMonths.Should().Be(2);
        s.OtherOpen.Should().BeEmpty();
        s.ReporterTickets.Should().Be(1);
        s.OldestOpenAge.Should().Be(TimeSpan.FromHours(3));

        (await tickets.CustomerSummaryAsync(theirs, login.Id, Swedish(22, 12))).Should().BeNull();
    }
}
