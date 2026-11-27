using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// What an operator, and a customer in the portal, see of the feedback inbox. Everything in a row
/// was written by an anonymous browser, so what matters here is that it stays text and that the
/// portal's view stays read-only.
/// </summary>
public class FeedbackInboxRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly FeedbackService service;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid siteId = Guid.NewGuid();

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];

    private EffectivePermissions permissions = EffectivePermissions.Everything;

    public FeedbackInboxRenderTests()
    {
        // What the person may do, as the support desk hands it down: everything, unless a test
        // says otherwise.
        Services.AddCascadingValue(_ => permissions);

        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options))
        {
            db.Database.EnsureCreated();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Mine", Slug = "mine" });
            db.SaveChanges();
        }

        TestDbContextFactory factory = new(connection);
        service = new FeedbackService(factory, new ConfigurationBuilder().Build());

        Services.AddSingleton(service);
        Services.AddSingleton(new FeedbackTicketBridge(
            factory, service,
            new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory))));
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("operator"));
    }

    public new void Dispose()
    {
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

    private Task<Guid> SubmitAsync(
        string message, int? rating = null, byte[]? image = null, Guid? site = null, string? email = null) =>
        service.SubmitAsync(
            new RumSiteInfo(site ?? siteId, tenantId, null, [], 1, true, "Storefront", null, true),
            new FeedbackSubmission(
                FeedbackKind.Bug, rating, message, email, "/checkout", "Chrome", "macOS", "desktop", 1280, 720,
                "sess1", "TypeError: x", image, image is null ? null : "image/png"));

    private IRenderedComponent<FeedbackInbox> Render(bool readOnly = false, Guid? site = null) =>
        Render<FeedbackInbox>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.SiteId, site)
            .Add(c => c.ReadOnly, readOnly));

    private static void OpenFirstRow(IRenderedComponent<FeedbackInbox> cut)
    {
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotBeEmpty());
        cut.Find("tbody tr").Click();
    }

    [Fact]
    public async Task Markup_in_a_message_is_shown_as_text_in_the_row_and_the_detail()
    {
        await SubmitAsync("<script>alert(1)</script>");

        IRenderedComponent<FeedbackInbox> cut = Render();
        OpenFirstRow(cut);

        cut.WaitForAssertion(() => cut.Find(".card-body p").TextContent.Should().Be("<script>alert(1)</script>"));
        cut.FindAll("script").Should().BeEmpty("it must never become an element");
        cut.Markup.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task The_operator_can_act_and_the_portal_view_cannot()
    {
        await SubmitAsync("Hello");

        IRenderedComponent<FeedbackInbox> operatorView = Render();
        OpenFirstRow(operatorView);
        operatorView.WaitForAssertion(() => operatorView.Markup.Should().Contain("Create ticket").And.Contain("Dismiss"));

        IRenderedComponent<FeedbackInbox> portalView = Render(readOnly: true);
        OpenFirstRow(portalView);

        // Positive control: the detail did open, so the buttons' absence means something.
        portalView.WaitForAssertion(() => portalView.Markup.Should().Contain("Client"));

        // Buttons, because the status filter's options legitimately say "Triaged" and "Dismissed".
        portalView.FindAll("button").Select(b => b.TextContent.Trim()).Should().BeEquivalentTo(["Refresh"]);
        portalView.Markup.Should().NotContain("RUM › Sites", "a customer cannot reach the operator's settings");
    }

    [Fact]
    public async Task Narrowing_to_a_site_shows_that_site_alone()
    {
        await SubmitAsync("from the chosen site");
        await SubmitAsync("from somebody else's site", site: Guid.NewGuid());

        IRenderedComponent<FeedbackInbox> cut = Render(readOnly: true, site: siteId);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("from the chosen site"));
        cut.Markup.Should().NotContain("somebody else");
    }

    [Fact]
    public async Task The_screenshot_is_loaded_when_the_item_is_opened_and_not_before()
    {
        await SubmitAsync("Look at this", image: Png);

        IRenderedComponent<FeedbackInbox> cut = Render();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(1));
        cut.Markup.Should().NotContain("data:image/png", "the list never reads an image");

        cut.Find("tbody tr").Click();

        cut.WaitForAssertion(() => cut.Find("img").GetAttribute("src")
            .Should().Be("data:image/png;base64," + Convert.ToBase64String(Png)));
    }

    [Fact]
    public async Task A_typed_address_is_labelled_as_unverified()
    {
        await SubmitAsync("Hello", email: "someone@example.com");

        IRenderedComponent<FeedbackInbox> cut = Render();
        OpenFirstRow(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("someone@example.com").And.Contain("not verified"));
    }

    [Fact]
    public async Task A_refused_ticket_says_why_rather_than_doing_nothing()
    {
        await SubmitAsync("No customer behind this site");

        IRenderedComponent<FeedbackInbox> cut = Render();
        OpenFirstRow(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Create ticket"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create ticket")).Click();

        cut.WaitForAssertion(() => cut.Find(".alert").TextContent.Should().Contain("no app owner"));
    }

    [Fact]
    public async Task Marking_an_item_records_who_and_moves_it()
    {
        Guid id = await SubmitAsync("Noise");

        IRenderedComponent<FeedbackInbox> cut = Render();
        OpenFirstRow(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Dismiss"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Dismiss")).Click();

        cut.WaitForAssertion(() => cut.Find("tbody tr").TextContent.Should().Contain("Dismissed"));

        FeedbackItem item = (await service.GetAsync(tenantId, id))!;
        item.Status.Should().Be(FeedbackStatus.Dismissed);
        item.TriagedBy.Should().Be("operator");
    }

    [Fact]
    public async Task No_ratings_reads_as_no_ratings_and_not_as_a_score_of_zero()
    {
        await SubmitAsync("Hello");

        IRenderedComponent<FeedbackInbox> cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("no ratings in the last 30 days"));
        cut.Markup.Should().NotContain("average rating");
    }

    [Fact]
    public async Task The_average_is_shown_with_how_many_ratings_it_rests_on()
    {
        await SubmitAsync("One", rating: 2);
        await SubmitAsync("Two", rating: 5);

        IRenderedComponent<FeedbackInbox> cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("average rating 3.5 of 5 from 2"));
    }
}
