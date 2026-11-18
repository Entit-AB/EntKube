using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 10 (docs/helpdesk.md): related answers, answers given on tickets, and the opt-in
/// knowledge-base editor permission.
/// </summary>
public class KnowledgeBaseLinkTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly KnowledgeBaseService kb;
    private readonly TicketService tickets;
    private readonly TenantRoleService roles;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid vard = Guid.NewGuid();
    private readonly Guid skola = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    public KnowledgeBaseLinkTests()
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
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        kb = new KnowledgeBaseService(factory);
        tickets = new TicketService(factory, new ContractService(factory), SilentTicketNotifier.For(factory));
        roles = new TenantRoleService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<KbAnswer> Answer(string title, KbVisibility v = KbVisibility.Public, Guid? customer = null, Guid? tenant = null)
    {
        Guid t = tenant ?? ours;
        KbCategory cat = (await kb.CategoriesAsync(t)).FirstOrDefault() ?? await kb.SaveCategoryAsync(t, null, "Allmänt", null);
        return await kb.SaveAnswerAsync(t, new KbAnswer
        {
            Title = title, CategoryId = cat.Id, Visibility = v, CustomerId = customer, Language = "sv", BodyHtml = "<p>x</p>",
        }, "nils");
    }

    // ───────────────────────────── Related answers ─────────────────────────────

    [Fact]
    public async Task Related_answers_show_on_both_and_replacing_the_set_replaces_it()
    {
        KbAnswer vpn = await Answer("VPN"), mfa = await Answer("MFA"), wifi = await Answer("Wi-Fi");

        await kb.SetRelatedAsync(ours, vpn.Id, [mfa.Id, wifi.Id, vpn.Id]);

        (await kb.RelatedAsync(ours, vpn.Id)).Select(a => a.Title).Should().Equal("MFA", "Wi-Fi");
        (await kb.RelatedAsync(ours, mfa.Id)).Select(a => a.Title).Should().Equal(["VPN"], "the link shows from the other side");

        await kb.SetRelatedAsync(ours, mfa.Id, []);

        (await kb.RelatedAsync(ours, vpn.Id)).Select(a => a.Title).Should().Equal("Wi-Fi");
        db.KbAnswerLinks.AsNoTracking().Should().ContainSingle("each pair is stored once");
    }

    [Fact]
    public async Task An_answer_cannot_be_related_to_another_tenants()
    {
        KbAnswer ourAnswer = await Answer("VPN");
        KbAnswer theirAnswer = await Answer("Their VPN", tenant: theirs);

        Func<Task> link = () => kb.SetRelatedAsync(ours, ourAnswer.Id, [theirAnswer.Id]);
        await link.Should().ThrowAsync<InvalidOperationException>();

        Func<Task> fromTheirs = () => kb.SetRelatedAsync(theirs, ourAnswer.Id, [theirAnswer.Id]);
        await fromTheirs.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>A customer is shown a related answer only if they could open it directly.</summary>
    [Fact]
    public async Task A_customer_sees_only_the_related_answers_they_may_open()
    {
        KbAnswer vpn = await Answer("VPN");
        KbAnswer forAll = await Answer("MFA");
        KbAnswer internalOne = await Answer("Admin trick", KbVisibility.Internal);
        KbAnswer forSkola = await Answer("Skola's setup", customer: skola);
        KbAnswer forVard = await Answer("Vård's setup", customer: vard);
        await kb.SetRelatedAsync(ours, vpn.Id, [forAll.Id, internalOne.Id, forSkola.Id, forVard.Id]);

        (await kb.RelatedForCustomerAsync(ours, vard, vpn.Id, Now)).Select(a => a.Title).Should().Equal("MFA", "Vård's setup");
        (await kb.RelatedForCustomerAsync(theirs, theirCustomer, vpn.Id, Now)).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_an_answer_removes_its_links_on_either_side()
    {
        KbAnswer a = await Answer("A"), b = await Answer("B"), c = await Answer("C");
        await kb.SetRelatedAsync(ours, b.Id, [a.Id, c.Id]);

        (await kb.DeleteAnswerAsync(ours, b.Id)).Should().BeTrue();

        db.KbAnswerLinks.AsNoTracking().Should().BeEmpty();
        (await kb.RelatedAsync(ours, a.Id)).Should().BeEmpty();
    }

    // ───────────────────────────── Answers given on tickets ─────────────────────────────

    [Fact]
    public async Task An_answer_given_on_a_ticket_is_recorded_once_and_shows_on_both()
    {
        KbAnswer vpn = await Answer("VPN");
        Ticket t = await tickets.CreateAsync(ours, vard, null, "VPN drops", "", TicketChannel.Email, TicketPriority.P3, Now);

        (await kb.LinkTicketAsync(ours, vpn.Id, t.Id, "nils")).Should().BeTrue();
        (await kb.LinkTicketAsync(ours, vpn.Id, t.Id, "anna")).Should().BeTrue();

        (await kb.TicketsUsingAsync(ours, vpn.Id)).Should().ContainSingle().Which.Number.Should().Be(t.Number);
        (await kb.AnswersOnTicketAsync(ours, t.Id)).Select(a => a.Title).Should().Equal("VPN");
    }

    [Fact]
    public async Task Nothing_links_across_tenants()
    {
        KbAnswer ourAnswer = await Answer("VPN");
        KbAnswer theirAnswer = await Answer("Their VPN", tenant: theirs);
        Ticket ourTicket = await tickets.CreateAsync(ours, vard, null, "x", "", TicketChannel.Email, TicketPriority.P3, Now);
        Ticket theirTicket = await tickets.CreateAsync(theirs, theirCustomer, null, "y", "", TicketChannel.Email, TicketPriority.P3, Now);

        (await kb.LinkTicketAsync(ours, ourAnswer.Id, theirTicket.Id, "nils")).Should().BeFalse();
        (await kb.LinkTicketAsync(ours, theirAnswer.Id, ourTicket.Id, "nils")).Should().BeFalse();
        (await kb.LinkTicketAsync(theirs, ourAnswer.Id, ourTicket.Id, "eve")).Should().BeFalse();
        db.KbAnswerTickets.AsNoTracking().Should().BeEmpty();

        await kb.LinkTicketAsync(ours, ourAnswer.Id, ourTicket.Id, "nils");
        (await kb.AnswersOnTicketAsync(theirs, ourTicket.Id)).Should().BeEmpty();
        (await kb.TicketsUsingAsync(theirs, ourAnswer.Id)).Should().BeEmpty();
    }

    // ───────────────────────────── The editor permission ─────────────────────────────

    private Guid Role(Guid tenant, string name, string? permissions)
    {
        Guid id = Guid.NewGuid();
        db.TenantRoles.Add(new TenantRole { Id = id, TenantId = tenant, Name = name, PermissionsJson = permissions });
        db.SaveChanges();
        return id;
    }

    private void Member(string userId, Guid tenant, Guid role)
    {
        if (!db.Users.Any(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = userId });
        }

        db.TenantMemberships.Add(new TenantMembership { UserId = userId, TenantId = tenant, RoleId = role });
        db.SaveChanges();
    }

    private static string Grant(AccessLevel level) =>
        $"{{\"{(int)TenantFeature.KnowledgeBase}\":{(int)level}}}";

    [Fact]
    public async Task Until_a_role_is_granted_editing_nobody_is_singled_out()
    {
        Member("user-nils", ours, Role(ours, "Agent", null));

        (await roles.FeatureGrantAsync(ours, "user-nils", TenantFeature.KnowledgeBase, AccessLevel.Edit))
            .Should().Be((false, false));
    }

    [Fact]
    public async Task Once_a_role_is_granted_editing_only_its_members_are()
    {
        Guid editors = Role(ours, "KB editors", Grant(AccessLevel.Edit));
        Guid agents = Role(ours, "Agents", Grant(AccessLevel.View));
        Member("user-anna", ours, editors);
        Member("user-nils", ours, agents);

        (await roles.FeatureGrantAsync(ours, "user-anna", TenantFeature.KnowledgeBase, AccessLevel.Edit)).Should().Be((true, true));
        (await roles.FeatureGrantAsync(ours, "user-nils", TenantFeature.KnowledgeBase, AccessLevel.Edit)).Should().Be((true, false));
        (await roles.FeatureGrantAsync(ours, null, TenantFeature.KnowledgeBase, AccessLevel.Edit)).Should().Be((true, false));
    }

    [Fact]
    public async Task Another_tenants_grant_neither_binds_nor_admits_here()
    {
        Guid theirEditors = Role(theirs, "KB editors", Grant(AccessLevel.Manage));
        Member("user-anna", theirs, theirEditors);
        Member("user-anna", ours, Role(ours, "Agents", null));

        (await roles.FeatureGrantAsync(ours, "user-anna", TenantFeature.KnowledgeBase, AccessLevel.Edit)).Should().Be((false, false));
    }

    /// <summary>The feature is appended last: its stored number must never move.</summary>
    [Fact]
    public void The_knowledge_base_feature_keeps_its_stored_number() =>
        ((int)TenantFeature.KnowledgeBase).Should().Be(17);
}
