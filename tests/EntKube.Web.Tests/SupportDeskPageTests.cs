using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Pages.Support;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The support desk page itself: what it shows somebody, and what it refuses them.
///
/// <para>Only the refusing paths are rendered here. The sections themselves are the existing
/// support components, each with render tests of its own; what is new is the page deciding
/// whether to show one at all.</para>
/// </summary>
public class SupportDeskPageTests : BunitContext, IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly string slug;

    public SupportDeskPageTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        slug = $"entit-{tenantId:N}";

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = slug });
        db.SaveChanges();

        Services.AddSingleton(new TenantService(
            testDb.Factory, testDb.CreateVaultService(), new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory)));
        Services.AddSingleton(new TenantRoleService(testDb.Factory));
    }

    public new void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SignedInAsMemberHolding(params (TenantFeature Feature, AccessLevel Level)[] grants)
    {
        string userId = $"user-{Guid.NewGuid():N}";
        Guid role = Guid.NewGuid();

        db.Users.Add(new ApplicationUser { Id = userId, UserName = "anna" });
        db.TenantRoles.Add(new TenantRole
        {
            Id = role, TenantId = tenantId, Name = "Agent",
            PermissionsJson = TenantRoleService.EncodePermissions(grants.ToDictionary(g => g.Feature, g => g.Level)),
        });
        db.TenantMemberships.Add(new TenantMembership { UserId = userId, TenantId = tenantId, RoleId = role });
        db.SaveChanges();

        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs(userId));
    }

    private sealed class SignedInAs(string userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "anna"), new Claim(ClaimTypes.NameIdentifier, userId)], "test"))));
    }

    private IRenderedComponent<SupportDesk> Open(string? section) =>
        Render<SupportDesk>(p => p.Add(c => c.Slug, slug).Add(c => c.Section, section));

    [Fact]
    public void A_section_opened_by_its_address_without_the_permission_is_refused()
    {
        SignedInAsMemberHolding((TenantFeature.SupportTickets, AccessLevel.View));

        IRenderedComponent<SupportDesk> desk = Open("macros");

        desk.WaitForAssertion(() => desk.Find("[data-test=support-refused]").TextContent
            .Should().Contain("Ticket tools: Edit"));
    }

    [Fact]
    public void The_menu_offers_only_what_the_person_may_open()
    {
        SignedInAsMemberHolding((TenantFeature.SupportTickets, AccessLevel.View));

        IRenderedComponent<SupportDesk> desk = Open("macros");

        desk.WaitForAssertion(() => desk.FindAll("[data-section]").Should().NotBeEmpty());
        List<string?> offered = [.. desk.FindAll("[data-section]").Select(a => a.GetAttribute("data-section"))];

        offered.Should().Contain(["tickets", "feedback", "support-personal"]);
        offered.Should().NotContain(["macros", "support-mailbox", "sla"]);
        desk.Find("[data-section=tickets]").GetAttribute("href").Should().Be($"/support/{slug}/tickets");
    }

    [Fact]
    public void A_member_whose_role_includes_no_support_has_no_desk()
    {
        SignedInAsMemberHolding((TenantFeature.Clusters, AccessLevel.Manage));

        IRenderedComponent<SupportDesk> desk = Open(null);

        desk.WaitForAssertion(() => desk.Find("[data-test=support-no-access]"));
        desk.FindAll("[data-section]").Should().BeEmpty("not even the sections' names are shown");
    }

    /// <summary>
    /// Somebody signed in but not a member must not be able to tell a real tenant from a missing
    /// one, nor learn a real one's name, by trying addresses.
    /// </summary>
    [Fact]
    public void A_stranger_cannot_tell_a_real_tenant_from_a_missing_one()
    {
        db.Users.Add(new ApplicationUser { Id = "user-eve", UserName = "eve" });
        db.SaveChanges();
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAs("user-eve"));

        IRenderedComponent<SupportDesk> real = Open(null);
        IRenderedComponent<SupportDesk> missing = Render<SupportDesk>(p => p.Add(c => c.Slug, "no-such-tenant"));

        real.WaitForAssertion(() => real.Find("[data-test=support-no-access]"));
        missing.WaitForAssertion(() => missing.Find("[data-test=support-no-access]"));
        real.Find("[data-test=support-no-access]").TextContent.Should().Be(missing.Find("[data-test=support-no-access]").TextContent);
        real.Markup.Should().NotContain("ENTIT");
    }

    [Fact]
    public void A_section_the_desk_does_not_have_says_so()
    {
        SignedInAsMemberHolding((TenantFeature.SupportTickets, AccessLevel.View));

        IRenderedComponent<SupportDesk> desk = Open("clusters");

        desk.WaitForAssertion(() => desk.Find("[data-test=support-unknown-section]"));
    }
}
