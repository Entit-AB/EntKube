using System.Net;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The ticket routes over HTTP, as a token holder reaches them (docs/helpdesk.md, phase 1b).
/// The scopes, the document and the CLI/MCP parity are pinned by the API ratchets; these pin
/// what the routes DO: confinement to the token's tenant, the refusals, and that the history
/// says which token acted.
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class TicketApiTests : IDisposable
{
    private const string Read = "tickets:read";
    private const string Write = "tickets:write";

    private readonly GeneratedOpenApiDocumentFixture fixture;
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid otherCustomerId = Guid.NewGuid();
    private readonly Guid strangerTenantId = Guid.NewGuid();
    private readonly Guid strangerCustomerId = Guid.NewGuid();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public TicketApiTests(GeneratedOpenApiDocumentFixture fixture)
    {
        this.fixture = fixture;

        // Saved in stages: the shared host enforces foreign keys, and a customer's tenant must
        // exist before the customer does.
        using ApplicationDbContext db = App();
        db.Tenants.Add(new Tenant { Id = strangerTenantId, Name = $"stranger-{suffix}", Slug = $"stranger-{suffix}" });
        db.SaveChanges();

        db.Customers.Add(new Customer { Id = customerId, TenantId = fixture.TokenTenantId, Name = $"cust-{suffix}" });
        db.Customers.Add(new Customer { Id = otherCustomerId, TenantId = fixture.TokenTenantId, Name = $"other-{suffix}" });
        db.Customers.Add(new Customer { Id = strangerCustomerId, TenantId = strangerTenantId, Name = $"theirs-{suffix}" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        using ApplicationDbContext db = App();
        Guid[] customers = [customerId, otherCustomerId, strangerCustomerId];
        List<Guid> ticketIds = [.. db.Tickets.Where(t => customers.Contains(t.CustomerId)).Select(t => t.Id)];

        db.TicketLinks.RemoveRange(db.TicketLinks.Where(l => ticketIds.Contains(l.FromTicketId) || ticketIds.Contains(l.ToTicketId)));
        db.SaveChanges();
        db.Tickets.RemoveRange(db.Tickets.Where(t => ticketIds.Contains(t.Id)));
        db.SaveChanges();
        db.Customers.RemoveRange(db.Customers.Where(c => customers.Contains(c.Id)));
        db.SaveChanges();
        db.Tenants.RemoveRange(db.Tenants.Where(t => t.Id == strangerTenantId));
        db.SaveChanges();
    }

    private ApplicationDbContext App() =>
        fixture.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

    private async Task<Ticket> Raise(string title, Guid? customer = null, Guid? tenant = null)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TicketService>().CreateAsync(
            tenant ?? fixture.TokenTenantId, customer ?? customerId, null, $"{title} {suffix}", "Body.",
            TicketChannel.Email, TicketPriority.P3, DateTime.UtcNow.AddMinutes(-30));
    }

    private static string Json(object body) => JsonSerializer.Serialize(body, Web);

    [Fact]
    public async Task The_queue_lists_this_tenants_tickets_and_refuses_a_view_it_does_not_know()
    {
        Ticket ours = await Raise("Ours");
        Ticket theirs = await Raise("Theirs", strangerCustomerId, strangerTenantId);

        string body = await fixture.SendForBodyAsync("GET", "/api/v1/tickets", Read, null);

        body.Should().Contain($"\"number\":{ours.Number},").And.NotContain(theirs.Title);

        (await fixture.SendRawAsync("GET", "/api/v1/tickets?view=everything", Read, null))
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Another_tenants_ticket_number_is_no_ticket_here()
    {
        Ticket theirs = await Raise("Theirs", strangerCustomerId, strangerTenantId);

        (await fixture.SendRawAsync("GET", $"/api/v1/tickets/{theirs.Number}", Read, null))
            .Should().Be(HttpStatusCode.NotFound);

        (await fixture.SendRawAsync("POST", $"/api/v1/tickets/{theirs.Number}/tags", Write, Json(new { tag = "mine-now" })))
            .Should().Be(HttpStatusCode.NotFound);

        using ApplicationDbContext db = App();
        db.TicketTags.Count(t => t.TicketId == theirs.Id).Should().Be(0);
    }

    [Fact]
    public async Task A_tag_added_through_the_api_is_recorded_under_the_tokens_name()
    {
        Ticket ticket = await Raise("Tag me");

        string body = await fixture.SendForBodyAsync(
            "POST", $"/api/v1/tickets/{ticket.Number}/tags", Write, Json(new { tag = "  VPN " }));

        body.Should().Contain("\"tags\":[\"vpn\"]");

        using ApplicationDbContext db = App();
        db.TicketEvents.Single(e => e.TicketId == ticket.Id && e.Kind == TicketEventKind.Tagged)
            .Actor.Should().StartWith("api-token:");
    }

    [Fact]
    public async Task A_tag_that_cannot_be_one_is_refused_with_the_reason()
    {
        Ticket ticket = await Raise("Bad tag");

        (HttpStatusCode status, _, string body) = await fixture.SendForResponseAsync(
            "POST", $"/api/v1/tickets/{ticket.Number}/tags", Write, Json(new { tag = "a,b" }));

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.Should().Contain("without commas");
    }

    [Fact]
    public async Task A_merge_across_customers_is_refused_and_a_merge_within_one_says_what_it_did()
    {
        Ticket original = await Raise("Original");
        Ticket duplicate = await Raise("Duplicate");
        Ticket elsewhere = await Raise("Elsewhere", otherCustomerId);

        (HttpStatusCode refused, _, string why) = await fixture.SendForResponseAsync(
            "POST", $"/api/v1/tickets/{duplicate.Number}/merge", Write, Json(new { into = elsewhere.Number }));
        refused.Should().Be(HttpStatusCode.UnprocessableEntity);
        why.Should().Contain("another customer");

        string body = await fixture.SendForBodyAsync(
            "POST", $"/api/v1/tickets/{duplicate.Number}/merge", Write, Json(new { into = original.Number }));

        body.Should().Contain($"\"duplicate\":{duplicate.Number}").And.Contain($"\"original\":{original.Number}")
            .And.Contain("\"recordedAsResponse\":true");

        string merged = await fixture.SendForBodyAsync("GET", $"/api/v1/tickets/{duplicate.Number}", Read, null);
        merged.Should().Contain("\"status\":\"Merged\"").And.Contain($"\"mergedInto\":{original.Number}");
    }

    [Fact]
    public async Task A_reminder_can_be_set_and_cleared_and_never_on_a_closed_ticket()
    {
        Ticket ticket = await Raise("Remind me");
        string at = DateTime.UtcNow.AddDays(1).ToString("O");

        (await fixture.SendRawAsync("PUT", $"/api/v1/tickets/{ticket.Number}/reminder", Write,
            Json(new { at, note = "Call back" }))).Should().Be(HttpStatusCode.OK);

        string view = await fixture.SendForBodyAsync("GET", $"/api/v1/tickets/{ticket.Number}", Read, null);
        view.Should().Contain("\"reminderNote\":\"Call back\"");

        (await fixture.SendRawAsync("DELETE", $"/api/v1/tickets/{ticket.Number}/reminder", Write, null))
            .Should().Be(HttpStatusCode.NoContent);

        using (IServiceScope scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TicketService>()
                .CloseAsync(fixture.TokenTenantId, ticket.Id, "nils", DateTime.UtcNow);
        }

        (await fixture.SendRawAsync("PUT", $"/api/v1/tickets/{ticket.Number}/reminder", Write, Json(new { at })))
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// A note the customer reads in the portal must be asked for. A client that sends only the
    /// text writes an internal note.
    /// </summary>
    [Fact]
    public async Task A_note_is_internal_unless_the_caller_says_the_customer_sees_it()
    {
        Ticket ticket = await Raise("Notes");

        await fixture.SendForBodyAsync("POST", $"/api/v1/tickets/{ticket.Number}/notes", Write, Json(new { text = "Working on it." }));
        await fixture.SendForBodyAsync("POST", $"/api/v1/tickets/{ticket.Number}/notes", Write,
            Json(new { text = "We are on it.", customerVisible = true }));

        using ApplicationDbContext db = App();
        db.TicketEvents.Single(e => e.TicketId == ticket.Id && e.Detail == "Working on it.").CustomerVisible.Should().BeFalse();
        db.TicketEvents.Single(e => e.TicketId == ticket.Id && e.Detail == "We are on it.").CustomerVisible.Should().BeTrue();
    }

    [Fact]
    public async Task A_link_is_made_by_number_and_removed_by_its_id()
    {
        Ticket parent = await Raise("Parent");
        Ticket child = await Raise("Child");

        string link = await fixture.SendForBodyAsync(
            "POST", $"/api/v1/tickets/{child.Number}/links", Write, Json(new { number = parent.Number, role = "parent" }));
        link.Should().Contain("\"role\":\"parent\"");

        string fromParent = await fixture.SendForBodyAsync("GET", $"/api/v1/tickets/{parent.Number}", Read, null);
        fromParent.Should().Contain("\"role\":\"child\"");

        Guid linkId = JsonDocument.Parse(link).RootElement.GetProperty("linkId").GetGuid();

        (await fixture.SendRawAsync("DELETE", $"/api/v1/tickets/{parent.Number}/links/{linkId}", Write, null))
            .Should().Be(HttpStatusCode.NoContent);

        (await fixture.SendRawAsync("POST", $"/api/v1/tickets/{child.Number}/links", Write,
            Json(new { number = parent.Number, role = "sibling" }))).Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
