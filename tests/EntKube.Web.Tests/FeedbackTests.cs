using System.Net;
using System.Text;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The parse is the only thing between an anonymous document and the operational database, so each
/// test here is a thing a caller who is not the widget could send.
/// </summary>
public class FeedbackParseTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static FeedbackSubmission? Parse(object body)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return FeedbackIngest.Parse(doc.RootElement);
    }

    private static string DataUrl(string type, byte[] bytes) => $"data:{type};base64,{Convert.ToBase64String(bytes)}";

    [Fact]
    public void A_submission_with_neither_words_nor_a_rating_says_nothing_and_is_refused()
    {
        Parse(new { kind = "Bug", message = "   " }).Should().BeNull();
    }

    [Fact]
    public void A_rating_alone_is_a_submission()
    {
        FeedbackSubmission? parsed = Parse(new { kind = "Praise", rating = 4 });

        parsed.Should().NotBeNull();
        parsed!.Rating.Should().Be(4);
        parsed.Message.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void A_rating_outside_one_to_five_is_not_a_rating(int rating)
    {
        Parse(new { kind = "Bug", message = "x", rating })!.Rating.Should().BeNull();
    }

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("2")]
    [InlineData("")]
    public void A_kind_the_enum_does_not_name_is_refused_including_a_number_tryparse_would_accept(string kind)
    {
        Parse(new { kind, message = "x" }).Should().BeNull();
    }

    [Theory]
    [InlineData("/reset?token=secret", "/reset")]
    [InlineData("/page#access_token=secret", "/page")]
    [InlineData("https://app.example.com/orders/1?session=abc", "/orders/1")]
    [InlineData("", "/")]
    public void Only_the_path_survives_because_queries_carry_tokens_and_this_field_reaches_tickets(
        string sent, string stored)
    {
        Parse(new { kind = "Bug", message = "x", path = sent })!.PagePath.Should().Be(stored);
    }

    [Theory]
    [InlineData("person@example.com", "person@example.com")]
    [InlineData("Mallory <mallory@example.com>", null)]
    [InlineData("a@b.c\r\nBcc: victim@example.com", null)]
    [InlineData("not an address", null)]
    public void Only_a_bare_address_is_kept(string sent, string? stored)
    {
        Parse(new { kind = "Bug", message = "x", email = sent })!.ContactEmail.Should().Be(stored);
    }

    [Fact]
    public void A_bad_address_loses_the_address_and_not_the_report()
    {
        Parse(new { kind = "Bug", message = "kept", email = "nope" })!.Message.Should().Be("kept");
    }

    [Fact]
    public void The_image_type_comes_from_the_bytes_and_the_claim_is_ignored()
    {
        (byte[]? bytes, string? type) = FeedbackIngest.DecodeScreenshot(DataUrl("image/gif", Jpeg));

        type.Should().Be("image/jpeg");
        bytes.Should().Equal(Jpeg);

        FeedbackIngest.DecodeScreenshot(DataUrl("image/jpeg", Png)).ContentType.Should().Be("image/png");
    }

    /// <summary>SVG renders script in some contexts; this is why the list is two formats long.</summary>
    [Fact]
    public void An_svg_is_not_an_image_here_even_when_it_claims_to_be_a_png()
    {
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>");

        FeedbackIngest.DecodeScreenshot(DataUrl("image/png", svg)).Bytes.Should().BeNull();
    }

    [Fact]
    public void An_unusable_screenshot_is_dropped_and_the_words_still_arrive()
    {
        FeedbackSubmission parsed = Parse(new { kind = "Bug", message = "kept", screenshot = "data:image/png;base64,@@@" })!;

        parsed.Message.Should().Be("kept");
        parsed.Screenshot.Should().BeNull();
        parsed.ScreenshotContentType.Should().BeNull();
    }

    [Fact]
    public void A_screenshot_over_the_limit_is_dropped()
    {
        byte[] huge = new byte[FeedbackLimits.MaxScreenshotBytes + 1];
        Png.CopyTo(huge, 0);

        FeedbackIngest.DecodeScreenshot(DataUrl("image/png", huge)).Bytes.Should().BeNull();
    }

    [Fact]
    public void A_screenshot_at_the_limit_is_kept_whole()
    {
        byte[] exact = new byte[FeedbackLimits.MaxScreenshotBytes];
        Png.CopyTo(exact, 0);

        FeedbackIngest.DecodeScreenshot(DataUrl("image/png", exact)).Bytes.Should().HaveCount(exact.Length);
    }

    [Fact]
    public void The_message_is_cut_to_the_column_rather_than_failing_the_insert()
    {
        Parse(new { kind = "Bug", message = new string('x', FeedbackLimits.MaxMessageLength + 50) })!
            .Message.Should().HaveLength(FeedbackLimits.MaxMessageLength);
    }

    [Fact]
    public void Errors_are_capped_cut_and_kept_on_one_line_each()
    {
        string[] sent = [.. Enumerable.Range(0, 9).Select(i => $"e{i}\nsecond line")];
        sent[0] = new string('x', 1000);

        FeedbackSubmission parsed = Parse(new { kind = "Bug", message = "x", errors = sent })!;

        string[] stored = parsed.RecentErrors!.Split('\n');
        stored.Should().HaveCount(FeedbackLimits.MaxErrors);
        stored[0].Should().HaveLength(FeedbackLimits.MaxErrorLength);
        stored[1].Should().Be("e1 second line");
        parsed.RecentErrors.Length.Should().BeLessThanOrEqualTo(FeedbackLimits.MaxErrorsLength);
    }

    [Fact]
    public void A_session_id_that_is_not_the_snippet_s_shape_is_dropped()
    {
        Parse(new { kind = "Bug", message = "x", sessionId = "abc123XYZ" })!.RumSessionId.Should().Be("abc123XYZ");
        Parse(new { kind = "Bug", message = "x", sessionId = "<script>" })!.RumSessionId.Should().BeNull();
    }
}

/// <summary>The inbox: tenancy, the figures above it, retention, and the road to a ticket.</summary>
public sealed class FeedbackServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly FeedbackService service;
    private readonly FeedbackTicketBridge bridge;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid siteId = Guid.NewGuid();

    public FeedbackServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            db.Database.EnsureCreated();
            db.Tenants.AddRange(
                new Tenant { Id = tenantId, Name = "Mine", Slug = "mine" },
                new Tenant { Id = strangerId, Name = "Theirs", Slug = "theirs" });
            db.SaveChanges();
            db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Customer" });
            db.SaveChanges();
            db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Shop" });
            db.SaveChanges();
        }

        factory = new TestDbContextFactory(connection);

        service = new FeedbackService(
            factory,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Feedback:MaxPerSitePerDay"] = "2",
                ["Feedback:ScreenshotRetentionDays"] = "90",
                ["Feedback:RetentionDays"] = "365",
            }).Build());

        bridge = new FeedbackTicketBridge(
            factory, service,
            new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory)));
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private RumSiteInfo Site(Guid? tenant = null, Guid? site = null, Guid? app = null) =>
        new(site ?? siteId, tenant ?? tenantId, null, [], 1, true, "Storefront", app, true);

    private static FeedbackSubmission Submission(
        string message = "It broke", int? rating = null, byte[]? image = null, string? email = null) =>
        new(FeedbackKind.Bug, rating, message, email, "/checkout", "Chrome", "macOS", "desktop", 1280, 720,
            "sess1", "TypeError: x\nReferenceError: y", image, image is null ? null : "image/png");

    private async Task AgeAsync(Guid id, int days)
    {
        await using ApplicationDbContext db = factory.CreateDbContext();
        DateTime when = DateTime.UtcNow.AddDays(-days);
        await db.FeedbackItems.Where(f => f.Id == id).ExecuteUpdateAsync(u => u.SetProperty(f => f.CreatedAt, when));
    }

    [Fact]
    public async Task The_tenant_app_and_site_name_come_from_the_key()
    {
        Guid id = await service.SubmitAsync(Site(app: appId), Submission());

        FeedbackItem item = (await service.GetAsync(tenantId, id))!;

        item.TenantId.Should().Be(tenantId);
        item.AppId.Should().Be(appId);
        item.SiteName.Should().Be("Storefront");
        item.Status.Should().Be(FeedbackStatus.New);
    }

    /// <summary>
    /// ⚠️ Each read and write is asked by the wrong tenant, and then by the right one. Without the
    /// second half a method broken for everyone would pass as "tenant-scoped".
    /// </summary>
    [Fact]
    public async Task Another_tenant_s_id_behaves_exactly_like_an_id_that_does_not_exist()
    {
        Guid id = await service.SubmitAsync(Site(), Submission(image: [1, 2, 3]));

        (await service.GetAsync(strangerId, id)).Should().BeNull();
        (await service.GetScreenshotAsync(strangerId, id)).Should().BeNull();
        (await service.ListAsync(strangerId)).Should().BeEmpty();
        (await service.SetStatusAsync(strangerId, id, FeedbackStatus.Dismissed, "x")).Should().BeFalse();
        (await service.SummarizeAsync(strangerId, null, DateTime.UtcNow.AddDays(-1))).Unread.Should().Be(0);
        await service.LinkTicketAsync(strangerId, id, Guid.NewGuid(), "x");

        (await service.GetScreenshotAsync(tenantId, id)).Should().NotBeNull();
        FeedbackItem item = (await service.GetAsync(tenantId, id))!;
        item.Status.Should().Be(FeedbackStatus.New, "the stranger's writes must have changed nothing");
        item.TicketId.Should().BeNull();
        (await service.SetStatusAsync(tenantId, id, FeedbackStatus.Dismissed, "me")).Should().BeTrue();
    }

    [Fact]
    public async Task The_list_says_whether_there_is_a_screenshot_without_reading_it()
    {
        Guid with = await service.SubmitAsync(Site(), Submission(image: [1, 2, 3]));
        Guid without = await service.SubmitAsync(Site(), Submission());

        Dictionary<Guid, bool> rows = (await service.ListAsync(tenantId)).ToDictionary(r => r.Item.Id, r => r.HasScreenshot);

        rows[with].Should().BeTrue();
        rows[without].Should().BeFalse();
    }

    [Fact]
    public async Task The_list_narrows_by_site_and_status_and_puts_the_newest_first()
    {
        Guid otherSite = Guid.NewGuid();
        Guid old = await service.SubmitAsync(Site(), Submission("old"));
        await AgeAsync(old, 3);
        Guid fresh = await service.SubmitAsync(Site(), Submission("fresh"));
        Guid elsewhere = await service.SubmitAsync(Site(site: otherSite), Submission("elsewhere"));
        await AgeAsync(elsewhere, 1);
        await service.SetStatusAsync(tenantId, old, FeedbackStatus.Resolved, "me");

        (await service.ListAsync(tenantId)).Select(r => r.Item.Id).Should().Equal(fresh, elsewhere, old);
        (await service.ListAsync(tenantId, siteId)).Select(r => r.Item.Id).Should().Equal(fresh, old);
        (await service.ListAsync(tenantId, null, FeedbackStatus.Resolved)).Select(r => r.Item.Id).Should().Equal(old);
    }

    [Fact]
    public async Task The_daily_quota_counts_this_site_for_the_last_day_and_nothing_else()
    {
        Guid first = await service.SubmitAsync(Site(), Submission());
        (await service.WithinDailyQuotaAsync(siteId)).Should().BeTrue("one of two is used");

        Guid second = await service.SubmitAsync(Site(), Submission());
        (await service.WithinDailyQuotaAsync(siteId)).Should().BeFalse("two of two is used");

        (await service.WithinDailyQuotaAsync(Guid.NewGuid())).Should().BeTrue("another site has its own");

        await AgeAsync(first, 2);
        await AgeAsync(second, 2);
        (await service.WithinDailyQuotaAsync(siteId)).Should().BeTrue("and it is a rolling day");
    }

    /// <summary>The figures use 2 and 5 so a sum, a count or a stuck value cannot pass for the mean.</summary>
    [Fact]
    public async Task The_average_is_over_rated_items_in_the_window_and_unread_is_not_windowed()
    {
        await service.SubmitAsync(Site(), Submission(rating: 2));
        await service.SubmitAsync(Site(), Submission(rating: 5));
        await service.SubmitAsync(Site(), Submission(rating: null));

        Guid outside = await service.SubmitAsync(Site(), Submission(rating: 1));
        await AgeAsync(outside, 40);

        FeedbackSummary summary = await service.SummarizeAsync(tenantId, null, DateTime.UtcNow.AddDays(-30));

        summary.Rated.Should().Be(2);
        summary.AverageRating.Should().Be(3.5);
        summary.Unread.Should().Be(4, "old unread feedback is still unread");
    }

    [Fact]
    public async Task No_ratings_is_no_average_rather_than_an_average_of_zero()
    {
        await service.SubmitAsync(Site(), Submission());

        FeedbackSummary summary = await service.SummarizeAsync(tenantId, null, DateTime.UtcNow.AddDays(-30));

        summary.Rated.Should().Be(0);
        summary.AverageRating.Should().BeNull();
    }

    [Fact]
    public async Task Retention_takes_the_screenshot_first_and_the_item_much_later()
    {
        Guid young = await service.SubmitAsync(Site(), Submission(image: [1]));
        Guid middle = await service.SubmitAsync(Site(), Submission(image: [2]));
        Guid ancient = await service.SubmitAsync(Site(), Submission(image: [3]));
        await AgeAsync(young, 89);
        await AgeAsync(middle, 91);
        await AgeAsync(ancient, 366);

        (int screenshots, int items) = await service.PruneAsync(DateTime.UtcNow);

        screenshots.Should().Be(2);
        items.Should().Be(1);
        (await service.GetScreenshotAsync(tenantId, young)).Should().NotBeNull();
        (await service.GetAsync(tenantId, middle)).Should().NotBeNull();
        (await service.GetScreenshotAsync(tenantId, middle)).Should().BeNull();
        (await service.GetAsync(tenantId, ancient)).Should().BeNull();
    }

    [Fact]
    public async Task Retention_never_keeps_an_item_for_less_time_than_its_screenshot()
    {
        FeedbackService shortItems = new(
            factory,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Feedback:ScreenshotRetentionDays"] = "90",
                ["Feedback:RetentionDays"] = "10",
            }).Build());

        Guid id = await shortItems.SubmitAsync(Site(), Submission());
        await AgeAsync(id, 50);

        (await shortItems.PruneAsync(DateTime.UtcNow)).Items.Should().Be(0);
    }

    [Fact]
    public async Task A_ticket_carries_the_report_and_marks_the_item_triaged()
    {
        Guid id = await service.SubmitAsync(Site(app: appId), Submission("The checkout button does nothing\nSecond line"));

        FeedbackTicketResult result = await bridge.OpenAsync(tenantId, id, "operator");

        result.Refusal.Should().BeNull();

        await using ApplicationDbContext db = factory.CreateDbContext();
        Ticket ticket = await db.Tickets.SingleAsync(t => t.Id == result.TicketId);

        ticket.TenantId.Should().Be(tenantId);
        ticket.CustomerId.Should().Be(customerId);
        ticket.AppId.Should().Be(appId);
        ticket.Channel.Should().Be(TicketChannel.Feedback);
        ticket.Title.Should().Be("Bug: The checkout button does nothing");
        ticket.Description.Should().Contain("Second line").And.Contain("/checkout").And.Contain("TypeError: x");

        FeedbackItem item = (await service.GetAsync(tenantId, id))!;
        item.TicketId.Should().Be(ticket.Id);
        item.Status.Should().Be(FeedbackStatus.Triaged);
        item.TriagedBy.Should().Be("operator");
    }

    /// <summary>
    /// ⚠️ TicketNotifier mails RequestedByEmail. This address was typed into a public form, so
    /// passing it through would let anybody aim our ticket mail at a stranger.
    /// </summary>
    [Fact]
    public async Task The_reporter_s_address_is_never_where_ticket_mail_would_go()
    {
        Guid id = await service.SubmitAsync(Site(app: appId), Submission(email: "victim@example.com"));

        FeedbackTicketResult result = await bridge.OpenAsync(tenantId, id, "operator");

        await using ApplicationDbContext db = factory.CreateDbContext();
        Ticket ticket = await db.Tickets.SingleAsync(t => t.Id == result.TicketId);

        ticket.RequestedByEmail.Should().BeNull();
        ticket.Description.Should().Contain("victim@example.com").And.Contain("unverified");
    }

    [Fact]
    public async Task Asking_twice_returns_the_same_ticket()
    {
        Guid id = await service.SubmitAsync(Site(app: appId), Submission());

        FeedbackTicketResult first = await bridge.OpenAsync(tenantId, id, "operator");
        FeedbackTicketResult second = await bridge.OpenAsync(tenantId, id, "operator");

        second.TicketId.Should().Be(first.TicketId!.Value);

        await using ApplicationDbContext db = factory.CreateDbContext();
        (await db.Tickets.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_site_with_no_app_owner_has_no_customer_to_raise_a_ticket_for()
    {
        Guid id = await service.SubmitAsync(Site(app: null), Submission());

        FeedbackTicketResult result = await bridge.OpenAsync(tenantId, id, "operator");

        result.TicketId.Should().BeNull();
        result.Refusal.Should().Contain("no app owner");
    }

    [Fact]
    public async Task An_app_id_outside_the_tenant_cannot_open_a_ticket_on_that_customer()
    {
        // The app exists, under this test's tenant; the item claims it from another tenant.
        Guid id = await service.SubmitAsync(Site(tenant: strangerId, app: appId), Submission());

        FeedbackTicketResult result = await bridge.OpenAsync(strangerId, id, "operator");

        result.TicketId.Should().BeNull();

        await using ApplicationDbContext db = factory.CreateDbContext();
        (await db.Tickets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void An_item_with_no_words_still_gets_a_title()
    {
        FeedbackTicketBridge.Title(new FeedbackItem { Kind = FeedbackKind.Praise, SiteName = "Shop" })
            .Should().Be("Praise feedback from Shop");
    }

    [Fact]
    public void A_long_first_line_is_shortened_at_the_title_s_limit()
    {
        string title = FeedbackTicketBridge.Title(new FeedbackItem { Kind = FeedbackKind.Bug, Message = new string('x', 200) });

        title.Should().Be("Bug: " + new string('x', 77) + "...");
    }

    [Fact]
    public async Task Deleting_a_site_leaves_what_people_said()
    {
        await using (ApplicationDbContext db = factory.CreateDbContext())
        {
            db.RumSites.Add(new RumSite { Id = siteId, TenantId = tenantId, Name = "Storefront", PublicKey = "k" });
            await db.SaveChangesAsync();
        }

        Guid id = await service.SubmitAsync(Site(), Submission(image: [1]));

        await new RumSiteService(factory, new ConfigurationBuilder().Build()).DeleteAsync(tenantId, siteId);

        (await service.GetAsync(tenantId, id)).Should().NotBeNull();
        (await service.GetScreenshotAsync(tenantId, id)).Should().NotBeNull();
    }
}

/// <summary>Boots the real application once: real routing, real migrations, real guards.</summary>
public sealed class FeedbackHostFixture : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "feedback-http-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; }

    public FeedbackHostFixture()
    {
        Directory.CreateDirectory(tempDir);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("DatabaseProvider", "Sqlite");
            b.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(tempDir, "app.db")}");
            b.UseSetting("Telemetry:DataPath", Path.Combine(tempDir, "telemetry"));
            b.UseSetting("Telemetry:ObjectStorage:Bucket", "");
            b.UseSetting("Vault:RootKey", "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
            b.UseSetting("DataProtection:KeyPath", Path.Combine(tempDir, "keys"));
            b.UseSetting("Seed:AdminEmail", "");
            b.UseSetting("Rum:SiteCacheTtlSeconds", "1");
            b.UseSetting("Feedback:MaxPerSitePerDay", "2");
        });
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
    }
}

public sealed class FeedbackHttpTests(FeedbackHostFixture host) : IClassFixture<FeedbackHostFixture>
{
    private const string Origin = "https://app.example.com";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private async Task<(Guid TenantId, Guid SiteId, string Key)> SiteAsync(bool feedbackEnabled = true, string origins = Origin)
    {
        Guid tenantId = Guid.NewGuid();
        string key = "key" + Guid.NewGuid().ToString("N");
        Guid siteId = Guid.NewGuid();

        await using ApplicationDbContext db = host.Factory.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = "t-" + tenantId.ToString("N")[..8] });
        await db.SaveChangesAsync();
        db.RumSites.Add(new RumSite
        {
            Id = siteId, TenantId = tenantId, Name = "Site", PublicKey = key,
            AllowedOrigins = origins, FeedbackEnabled = feedbackEnabled,
        });
        await db.SaveChangesAsync();

        return (tenantId, siteId, key);
    }

    private async Task<HttpResponseMessage> PostAsync(string key, string body, string? origin = Origin)
    {
        // A distinct client address per request is not available in the test server, so the shared
        // per-IP limit is left well above what this class sends.
        HttpClient client = host.Factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, $"/ingest/feedback/v1/{key}")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    private static string Body(string message = "It broke", string? tenantId = null) =>
        JsonSerializer.Serialize(new
        {
            kind = "Bug", message, tenantId, path = "/checkout?token=secret",
            screenshot = "data:image/png;base64," + Convert.ToBase64String(Png),
        });

    private async Task<List<FeedbackItem>> StoredAsync(Guid tenantId)
    {
        await using ApplicationDbContext db = host.Factory.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        return await db.FeedbackItems.Where(f => f.TenantId == tenantId).ToListAsync();
    }

    [Fact]
    public async Task A_report_from_an_allowed_origin_is_stored_under_the_key_s_tenant_with_its_screenshot()
    {
        (Guid tenantId, _, string key) = await SiteAsync();

        // The body names another tenant, which must not matter.
        using HttpResponseMessage response = await PostAsync(key, Body(tenantId: Guid.NewGuid().ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(Origin);

        FeedbackItem item = (await StoredAsync(tenantId)).Should().ContainSingle().Subject;
        item.PagePath.Should().Be("/checkout");

        FeedbackScreenshot? shot = await host.Factory.Services.GetRequiredService<FeedbackService>()
            .GetScreenshotAsync(tenantId, item.Id);
        shot!.ContentType.Should().Be("image/png");
        shot.Bytes.Should().Equal(Png);
    }

    [Fact]
    public async Task An_unknown_key_is_401()
    {
        using HttpResponseMessage response = await PostAsync("nobody", Body());
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_site_that_has_not_asked_for_feedback_stores_nothing()
    {
        (Guid tenantId, _, string key) = await SiteAsync(feedbackEnabled: false);

        using HttpResponseMessage response = await PostAsync(key, Body());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_origin_off_the_list_is_refused_and_gets_no_cors_answer()
    {
        (Guid tenantId, _, string key) = await SiteAsync();

        using HttpResponseMessage response = await PostAsync(key, Body(), "https://evil.example.com");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        (await StoredAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_daily_quota_refuses_the_next_report_and_stores_nothing_more()
    {
        (Guid tenantId, _, string key) = await SiteAsync();

        (await PostAsync(key, Body())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PostAsync(key, Body())).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage third = await PostAsync(key, Body());

        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        third.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(Origin, "the widget has to be able to read this");
        (await StoredAsync(tenantId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413_and_unparseable_json_is_400()
    {
        (_, _, string key) = await SiteAsync();

        using HttpResponseMessage bad = await PostAsync(key, "{not json");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using HttpResponseMessage huge = await PostAsync(key, new string('x', FeedbackLimits.MaxRequestBytes + 1));
        huge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task The_widget_script_is_served_anonymously_as_javascript()
    {
        using HttpResponseMessage response = await host.Factory.CreateClient().GetAsync("/feedback/v1/widget.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/javascript");
        (await response.Content.ReadAsStringAsync()).Should().Contain("/ingest/feedback/v1/");
    }
}

/// <summary><c>GET /feedback</c>, through a real token.</summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class FeedbackApiTests(GeneratedOpenApiDocumentFixture fixture)
{
    private const string Scope = "ops:read";

    [Fact]
    public async Task It_lists_this_tenant_s_feedback_and_never_another_s()
    {
        string marker = Guid.NewGuid().ToString("N");

        await using (ApplicationDbContext db = fixture.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext())
        {
            DateTime now = DateTime.UtcNow;
            db.FeedbackItems.AddRange(
                new FeedbackItem
                {
                    Id = Guid.NewGuid(), TenantId = fixture.TokenTenantId, RumSiteId = Guid.NewGuid(),
                    SiteName = "Mine", Kind = FeedbackKind.Idea, Message = marker, PagePath = "/p",
                    Rating = 4, CreatedAt = now, UpdatedAt = now,
                },
                new FeedbackItem
                {
                    Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), RumSiteId = Guid.NewGuid(),
                    SiteName = "Theirs", Kind = FeedbackKind.Bug, Message = "stranger-" + marker,
                    PagePath = "/p", CreatedAt = now, UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        (HttpStatusCode status, _, string body) =
            await fixture.SendForResponseAsync("GET", "/api/v1/feedback?status=new", Scope);

        status.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("stranger-" + marker);

        JsonElement mine = JsonDocument.Parse(body).RootElement.EnumerateArray()
            .Single(e => e.GetProperty("message").GetString() == marker);

        mine.GetProperty("kind").GetString().Should().Be("Idea");
        mine.GetProperty("status").GetString().Should().Be("New");
        mine.GetProperty("rating").GetInt32().Should().Be(4);
        mine.GetProperty("site").GetString().Should().Be("Mine");
        mine.GetProperty("pagePath").GetString().Should().Be("/p");
        mine.GetProperty("hasScreenshot").GetBoolean().Should().BeFalse();
        mine.GetProperty("ticketId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("2")]
    public async Task A_status_the_enum_does_not_name_is_400_and_never_a_500(string status)
    {
        (HttpStatusCode code, _, _) = await fixture.SendForResponseAsync("GET", $"/api/v1/feedback?status={status}", Scope);

        code.Should().Be(HttpStatusCode.BadRequest);
    }
}
