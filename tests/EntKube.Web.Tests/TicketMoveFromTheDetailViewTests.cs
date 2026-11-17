using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The move panel on one ticket, driven the way a browser drives it.
///
/// <para>A move is the one action on this screen that can take the ticket out of the tenant
/// the view is scoped to, and that makes it the one action whose aftermath the detail view
/// cannot read. Two things went wrong there and neither is visible from the service tests,
/// which are handed a tenant and never render anything:</para>
///
/// <para><b>The panel went blank.</b> The page re-read the ticket through its own
/// <c>TenantId</c> parameter — the tenant the explorer has open, which a move does not
/// change — so after a successful cross-tenant move the read returned null and the whole
/// body, "Back to the queue" included, rendered as nothing behind a success toast.</para>
///
/// <para><b>⚠️ The pickers were treated as the boundary.</b> What a <c>select</c> posts is a
/// string the browser puts on the circuit, not one of the options we rendered, so the
/// destination tenant arrived unchecked and was used to list another tenant's customers.
/// These tests dispatch change events with values that were never offered, which is exactly
/// what that looks like from the server's side.</para>
/// </summary>
public class TicketMoveFromTheDetailViewTests : BunitContext, IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>Tuesday 22 September 2026, inside S1 — and comfortably in the past.</summary>
    private static DateTime Tue(int hour, int minute = 0) => Swedish(2026, 9, 22, hour, minute);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;

    /// <summary>The tenant the ticket is in, and the one the explorer has open.</summary>
    private readonly Guid tenantA = Guid.NewGuid();

    /// <summary>A second tenant this person is a member of: a legitimate destination.</summary>
    private readonly Guid tenantB = Guid.NewGuid();

    /// <summary>A tenant this person is not a member of. Never offered, never permitted.</summary>
    private readonly Guid tenantC = Guid.NewGuid();

    private readonly Guid customerA = Guid.NewGuid();
    private readonly Guid otherCustomerA = Guid.NewGuid();
    private readonly Guid customerB = Guid.NewGuid();
    private readonly Guid customerC = Guid.NewGuid();
    private readonly Guid appA = Guid.NewGuid();

    private const string Unreachable = "Hemliga Vårdbolaget AB";

    public TicketMoveFromTheDetailViewTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.AddRange(
            new Tenant { Id = tenantA, Name = "ENTIT", Slug = "entit" },
            new Tenant { Id = tenantB, Name = "Partner", Slug = "partner" },
            new Tenant { Id = tenantC, Name = "Hemlig", Slug = "hemlig" });

        db.Customers.AddRange(
            new Customer { Id = customerA, TenantId = tenantA, Name = "Entit AB" },
            new Customer { Id = otherCustomerA, TenantId = tenantA, Name = "Entit Service AB" },
            new Customer { Id = customerB, TenantId = tenantB, Name = "Partnerbolaget AB" },
            new Customer { Id = customerC, TenantId = tenantC, Name = Unreachable });

        db.Apps.Add(new App { Id = appA, CustomerId = customerA, Name = "Journalportalen" });

        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            AppId = appA,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = Swedish(2026, 1, 1, 0),
        };
        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = ManagementLevel.Standard,
            SupportWindow = SupportWindow.S1,
            EffectiveFrom = Swedish(2026, 1, 1, 0),
            Reason = "On-boarding",
        });
        db.ApplicationContracts.Add(contract);

        // The operator: a member of A and B, and of nothing else. This is what decides which
        // tenants the move panel offers — and, now, which ones it accepts.
        db.Users.Add(new ApplicationUser { Id = "user-nils", UserName = "nils", Email = "nils@entit.example" });

        foreach (Guid tenantId in new[] { tenantA, tenantB })
        {
            Guid roleId = Guid.NewGuid();
            db.TenantRoles.Add(new TenantRole { Id = roleId, TenantId = tenantId, Name = "Support" });
            db.TenantMemberships.Add(new TenantMembership
            {
                UserId = "user-nils", TenantId = tenantId, RoleId = roleId,
            });
        }

        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        tickets = new TicketService(factory, contracts, SilentTicketNotifier.For(factory));

        Services.AddEveryContext(factory);
        Services.AddSingleton(contracts);
        Services.AddSingleton(tickets);
        Services.AddSingleton(new TimeService(factory, contracts));
        SupportDutyService duty = new(factory);
        Services.AddSingleton(duty);

        // The ticket's side panel: tags, links, reminders, followers (docs/helpdesk.md).
        Services.AddSingleton(new TicketWorkService(factory, tickets, SilentTicketNotifier.For(factory), duty));

        // Phase 2a: text modules, macros and checklists on the ticket (docs/helpdesk.md).
        Services.AddSingleton(new TextModuleService(factory));
        Services.AddSingleton(new TicketChecklistService(factory));
        Services.AddSingleton(new TicketPresenceService(factory));
        Services.AddSingleton(new KnowledgeBaseService(factory));
        Services.AddSingleton(new TicketAttachmentService(factory, new UnconfiguredAttachmentStore()));
        Services.AddSingleton(new TicketDraftService(factory));
        Services.AddSingleton(TestTicketReference.Instance);
        Services.AddSingleton(new MacroService(factory, tickets, new TicketWorkService(factory, tickets, SilentTicketNotifier.For(factory), duty)));
        Services.AddSingleton(new ToastService());
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("nils", "user-nils"));
        Services.AddScoped<CurrentActor>();
        Services.AddSingleton(new UserAccessService(factory));
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// ⚠️ Carries the NameIdentifier as well as the name: the move panel asks
    /// <see cref="UserAccessService"/> which tenants this user id can reach, and a principal
    /// without one reaches none — which renders no Move button at all and would make every
    /// assertion below vacuous.
    /// </summary>
    private sealed class SignedInAs(string name, string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.NameIdentifier, userId)],
                    "test"))));
    }

    private Task<Ticket> Raise(TicketPriority priority = TicketPriority.P3) =>
        tickets.CreateAsync(
            tenantA, customerA, appA, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Email, priority, Tue(9), "Karin", "karin@entit.example");

    // ---- The harness -------------------------------------------------------------------------

    private IRenderedComponent<TicketDetail> RenderDetail(Guid ticketId) =>
        Render<TicketDetail>(p => p
            .Add(c => c.TenantId, tenantA)
            .Add(c => c.TicketId, ticketId)
            .Add(c => c.OnBack, () => backs++)
            .Add(c => c.OnChanged, () => changes++));

    private int backs;
    private int changes;

    private static IElement ButtonSaying(IRenderedComponent<TicketDetail> detail, string text) =>
        detail.FindAll("button")
            .First(b => b.TextContent.Trim().StartsWith(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>The "Move it" inside the panel, not the toolbar button that opens it.</summary>
    private static IElement MoveItInThePanel(IRenderedComponent<TicketDetail> detail) =>
        detail.FindAll("button")
            .Last(b => b.TextContent.Trim().StartsWith("Move it", StringComparison.OrdinalIgnoreCase));

    /// <summary>The select that offers <paramref name="optionValue"/> among its options.</summary>
    private static IElement SelectOffering(IRenderedComponent<TicketDetail> detail, Guid optionValue) =>
        detail.FindAll("select").First(s => s.QuerySelectorAll("option")
            .Any(o => o.GetAttribute("value") == optionValue.ToString()));

    private static IElement TenantPicker(IRenderedComponent<TicketDetail> detail, Guid offered) =>
        SelectOffering(detail, offered);

    /// <summary>The Customer select, found by its placeholder rather than its contents.</summary>
    private static IElement CustomerPicker(IRenderedComponent<TicketDetail> detail) =>
        detail.FindAll("select").First(s => s.QuerySelectorAll("option")
            .Any(o => o.TextContent.Contains("choose", StringComparison.OrdinalIgnoreCase)));

    private async Task<IRenderedComponent<TicketDetail>> OpenMovePanel(Guid ticketId)
    {
        IRenderedComponent<TicketDetail> detail = RenderDetail(ticketId);

        detail.Markup.Should().Contain(
            "Journalen svarar inte", "the ticket has to be on the screen before it can be moved");

        await detail.InvokeAsync(() => ButtonSaying(detail, "Move it").Click());

        detail.Markup.Should().Contain("stays", "the move panel is what explains that the number stays");

        return detail;
    }

    // ---- The blank panel ---------------------------------------------------------------------

    /// <summary>
    /// <b>The regression.</b> A move into another tenant succeeded and then the screen showed
    /// nothing at all: the page re-read the ticket through the tenant it was still scoped to,
    /// got null, and rendered an empty panel with no error and no way back — the "Back to the
    /// queue" button lives inside the same <c>@if</c>.
    ///
    /// <para>So the view asks to be left instead, which is what the other detail views do when
    /// an action takes the entity out of the page's scope.</para>
    /// </summary>
    [Fact]
    public async Task A_move_into_another_tenant_hands_the_view_back_to_the_queue()
    {
        Ticket ticket = await Raise();

        IRenderedComponent<TicketDetail> detail = await OpenMovePanel(ticket.Id);

        await detail.InvokeAsync(() => TenantPicker(detail, tenantB).Change(tenantB.ToString()));
        await detail.InvokeAsync(() => CustomerPicker(detail).Change(customerB.ToString()));
        await detail.InvokeAsync(() => MoveItInThePanel(detail).Click());

        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).TenantId.Should().Be(
            tenantB, "the move itself has to have happened, or nothing below means anything");

        // Why the view cannot stay: the page only ever reads through the tenant it was given.
        (await tickets.GetAsync(tenantA, ticket.Id)).Should().BeNull(
            "the ticket is no longer readable through this view's tenant");

        backs.Should().Be(1, "the only honest thing left is to go back to the queue");
        changes.Should().BeGreaterThan(0, "and the queue has to be told the row left it");
    }

    /// <summary>
    /// The control for the test above: a move that stays inside the tenant must NOT throw the
    /// operator out of the ticket they are working on. Without this, going back unconditionally
    /// would pass the test above and ruin the ordinary case.
    /// </summary>
    [Fact]
    public async Task A_move_inside_the_same_tenant_stays_on_the_ticket()
    {
        Ticket ticket = await Raise();

        IRenderedComponent<TicketDetail> detail = await OpenMovePanel(ticket.Id);

        await detail.InvokeAsync(() => CustomerPicker(detail).Change(otherCustomerA.ToString()));
        await detail.InvokeAsync(() => MoveItInThePanel(detail).Click());

        db.ChangeTracker.Clear();
        Ticket saved = db.Tickets.Single(t => t.Id == ticket.Id);
        saved.CustomerId.Should().Be(otherCustomerA, "the move happened");
        saved.TenantId.Should().Be(tenantA, "and it stayed in this tenant");

        backs.Should().Be(0, "nothing took the ticket out of this view's reach");
        detail.Markup.Should().Contain("Journalen svarar inte", "so the ticket is still on the screen");
    }

    // ---- The pickers are not the boundary ----------------------------------------------------

    /// <summary>
    /// A tenant that was never offered must not be answered with its customers' names.
    ///
    /// <para>The positive half comes first on purpose: a reachable tenant really does fill the
    /// Customer picker, so the absence asserted afterwards is the guard working rather than the
    /// event never arriving.</para>
    /// </summary>
    [Fact]
    public async Task A_tenant_that_was_never_offered_is_not_queried_for_its_customers()
    {
        Ticket ticket = await Raise();

        IRenderedComponent<TicketDetail> detail = await OpenMovePanel(ticket.Id);

        await detail.InvokeAsync(() => TenantPicker(detail, tenantB).Change(tenantB.ToString()));

        detail.Markup.Should().Contain(
            "Partnerbolaget AB", "a tenant this person is a member of does fill the picker");

        // The same event, with a tenant the markup never offered — which is all it takes,
        // because the payload comes from the browser.
        await detail.InvokeAsync(() => TenantPicker(detail, tenantB).Change(tenantC.ToString()));

        detail.Markup.Should().NotContain(
            Unreachable, "a tenant outside this person's memberships must not be listed at all");

        // Nothing at all, not merely a different name: the picker is back to its placeholder.
        // (The refusal is also toasted, but toasts render in the layout, not in this component.)
        CustomerPicker(detail).QuerySelectorAll("option").Should().HaveCount(
            1, "only the placeholder is left, because the tenant was never queried");
    }

    /// <summary>
    /// And the move itself: a customer id that was never offered is no destination, however it
    /// arrived. <c>TicketService.MoveAsync</c> deliberately does not scope its destination — a
    /// move across tenants is the point of it — so this page is the only place that can say no.
    /// </summary>
    [Fact]
    public async Task A_customer_that_was_never_offered_cannot_be_moved_into()
    {
        Ticket ticket = await Raise();

        IRenderedComponent<TicketDetail> detail = await OpenMovePanel(ticket.Id);

        await detail.InvokeAsync(() => CustomerPicker(detail).Change(customerC.ToString()));
        await detail.InvokeAsync(() => MoveItInThePanel(detail).Click());

        db.ChangeTracker.Clear();
        Ticket refused = db.Tickets.Single(t => t.Id == ticket.Id);
        refused.TenantId.Should().Be(tenantA, "the ticket did not go anywhere");
        refused.CustomerId.Should().Be(customerA, "and it is still the customer it belonged to");

        // The control: the same two clicks with a destination that WAS offered do move it, so
        // the refusal above is the guard and not a broken test path.
        await detail.InvokeAsync(() => TenantPicker(detail, tenantB).Change(tenantB.ToString()));
        await detail.InvokeAsync(() => CustomerPicker(detail).Change(customerB.ToString()));
        await detail.InvokeAsync(() => MoveItInThePanel(detail).Click());

        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).TenantId.Should().Be(
            tenantB, "a destination that was offered still works");
    }

    // ---- §14.4's silence, with the day on it -------------------------------------------------

    /// <summary>
    /// The overdue line said "the customer has heard nothing since 16:40" — a bare time of day
    /// for a value that can be days old, because §14.4's deadline walks over closed hours and
    /// pauses. Read on a Monday that is this afternoon, not Friday, and the most overdue
    /// obligation on the ticket looks like the least urgent.
    /// </summary>
    [Fact]
    public async Task The_overdue_update_line_says_which_day_the_customer_last_heard_anything()
    {
        Ticket ticket = await Raise(TicketPriority.P1);

        TicketSlaStatus status = TicketService.StatusOf(
            (await tickets.GetAsync(tenantA, ticket.Id))!, DateTime.UtcNow);

        status.UpdateOverdue(DateTime.UtcNow).Should().BeTrue(
            "a P1 raised weeks ago and never updated is overdue — this is the branch under test");

        string expected = status.LastUpdateAt.ToLocalTime().ToString("MMM d HH:mm");

        IElement line = RenderDetail(ticket.Id).FindAll("span")
            .First(s => s.TextContent.Contains("heard nothing since", StringComparison.Ordinal));

        line.TextContent.Should().Contain(expected,
            "the gap is the whole point of the line, so it has to carry the day as well as the time");
    }
}
