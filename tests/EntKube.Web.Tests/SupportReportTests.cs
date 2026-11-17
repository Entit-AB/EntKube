using System.IO.Compression;
using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>Phase 6 (docs/helpdesk.md): reporting, and the .xlsx it downloads.</summary>
public class SupportReportTests : IDisposable
{
    private static DateTime Swedish(int day, int hour) => TimeZoneInfo.ConvertTimeToUtc(
        new DateTime(2026, 11, day, hour, 0, 0, DateTimeKind.Unspecified), BusinessCalendar.SwedishTime);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly SupportReportService reports;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid customer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    public SupportReportTests()
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
        reports = new SupportReportService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise(string title, DateTime at, TicketChannel channel = TicketChannel.Email, Guid? tenant = null) =>
        tickets.CreateAsync(tenant ?? ours, tenant == theirs ? theirCustomer : customer, null, title, "", channel,
            TicketPriority.P3, at);

    [Fact]
    public async Task The_report_counts_this_tenants_tickets_by_swedish_day_status_and_channel()
    {
        Ticket a = await Raise("A", Swedish(2, 0).AddMinutes(30));
        await Raise("B", Swedish(2, 10), TicketChannel.Conversation);
        await Raise("C", Swedish(3, 9));
        await Raise("Theirs", Swedish(2, 10), tenant: theirs);
        await tickets.CloseAsync(ours, a.Id, "nils", Swedish(3, 15));

        SupportReport r = await reports.ReportAsync(ours, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3));

        r.Created.Should().Be(3);
        r.Closed.Should().Be(1);
        r.Daily.Should().Equal((new DateOnly(2026, 11, 2), 2, 0), (new DateOnly(2026, 11, 3), 1, 1));
        r.ByChannel.Should().Contain(("Conversation", 1));
        r.ByStatus.Should().Contain(("Closed", 1));
    }

    [Fact]
    public async Task The_export_is_a_workbook_with_the_tickets_and_nothing_that_could_run_as_a_formula()
    {
        await Raise("=HYPERLINK(\"http://evil.example\",\"click\")", Swedish(2, 10));
        await Raise("Printer <offline> & cold", Swedish(2, 11));

        byte[] xlsx = await reports.ExportAsync(ours, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));

        using ZipArchive zip = new(new MemoryStream(xlsx));
        zip.Entries.Select(e => e.FullName).Should().Contain(["[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml"]);

        string sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        sheet.Should().Contain("Printer &lt;offline&gt; &amp; cold");
        // An inline string is never evaluated by Excel, but the same text re-saved as CSV would be;
        // the leading apostrophe (escaped as &apos; in the XML) keeps it text in both.
        sheet.Should().Contain("&apos;=HYPERLINK").And.NotContain("<t xml:space=\"preserve\">=HYPERLINK");
        sheet.Should().Contain("<v>").And.Contain("Vårdbolaget");
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+46 70", "'+46 70")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Normal text", "Normal text")]
    public void Text_that_would_start_a_formula_is_kept_as_text(string input, string written) =>
        XlsxWriter.Defuse(input).Should().Be(written);
}
