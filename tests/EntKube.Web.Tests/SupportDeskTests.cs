using System.Text.RegularExpressions;
using EntKube.Web.Components.Pages.Support;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The support desk's own menu: which sections it has, who may open each, and the addresses that
/// lead to them — including every address that used to lead to the same place on the tenant page.
///
/// <para>Support moved out of the tenant page's tree into a menu of its own. The danger of a move
/// like that is the half that gets forgotten: a section that is in neither place, a link that
/// still points at the old place, or a section that is in both and so is guarded in one and not
/// the other. Each test below is one of those.</para>
/// </summary>
public class SupportDeskTests
{
    private const string TenantPage =
        "../../../../../src/EntKube.Web/Components/Pages/Tenants/TenantExplorer.razor";

    // ───────────────────────────── The menu's shape ─────────────────────────────

    [Fact]
    public void No_section_appears_twice()
    {
        SupportSections.All.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty("a section is opened by its key, so two with one key are one section shown twice");

        SupportSections.Groups.GroupBy(g => g.Name).Where(g => g.Count() > 1).Select(g => g.Key)
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_section_asks_for_a_level_its_permission_actually_has()
    {
        // A section that asked for "Reporting: Edit" could never be opened by anyone but an
        // administrator, because no role can be granted a level the roles page does not offer.
        foreach (SupportSection s in SupportSections.All.Where(s => s.Feature is not null))
        {
            FeaturePermission permission = PermissionCatalog.Describe(s.Feature!.Value);

            permission.Area.Should().Be(PermissionArea.Support, s.Key);
            permission.Levels.Should().Contain(s.Level, $"{s.Key} asks for {s.Feature} at {s.Level}");
        }
    }

    [Fact]
    public void The_whole_helpdesk_and_incidents_are_on_the_support_menu()
    {
        SupportSections.All.Select(s => s.Key).Should().BeEquivalentTo(
        [
            "tickets", "support-dashboard", "support-report", "support-personal", "support-roster", "feedback",
            "text-modules", "macros", "ticket-templates", "knowledge-base", "issue-trackers",
            "on-call", "incidents", "subconsultants",
            "support-inbox", "support-mailbox", "mail-triage", "support-attachments",
            "ticket-bridge",
            "sla",
        ]);
    }

    [Fact]
    public void The_roles_page_no_longer_says_any_support_permission_is_unchecked()
    {
        // The support desk checks every one of them, at the section and in each write.
        PermissionCatalog.Features.Where(f => f.Area == PermissionArea.Support)
            .Should().OnlyContain(f => f.Enforced);
    }

    // ───────────────────────────── Leaving the tenant page ─────────────────────────────

    [Fact]
    public void The_tenant_page_no_longer_opens_any_support_section_itself()
    {
        // A section the tenant page still routed would be reachable there without the support
        // desk's permission check.
        HashSet<string> routed = [.. Regex.Matches(TenantPageSource(), """case "([a-z0-9-]+)":""").Select(m => m.Groups[1].Value)];

        routed.Should().HaveCountGreaterThan(30, "the pattern must still fit the tenant page's router");
        routed.Intersect(SupportSections.All.Select(s => s.Key)).Should().BeEmpty();
    }

    [Fact]
    public void Alerting_stays_on_the_tenant_page()
    {
        HashSet<string> routed = [.. Regex.Matches(TenantPageSource(), """case "([a-z0-9-]+)":""").Select(m => m.Groups[1].Value)];

        routed.Should().Contain(["alerts", "alert-rules", "telemetry-alerts", "alert-routing", "notifications", "notification-providers"]);
    }

    /// <summary>
    /// The address mapping below is only half of the redirect; the other half is the tenant page
    /// asking for it before it draws anything. Rendering the tenant page needs a cluster's worth
    /// of services, so this checks the call is there — narrow, and enough to catch it being lost
    /// in the next reshuffle of that file.
    /// </summary>
    [Fact]
    public void The_tenant_page_sends_an_old_support_address_on_before_loading_anything()
    {
        string source = TenantPageSource();
        int onParameters = source.IndexOf("protected override async Task OnParametersSetAsync()", StringComparison.Ordinal);
        int redirect = source.IndexOf("SupportLinks.FromTenantPage(Slug, SectionParam, TicketParam, SearchParam, AnswerParam, IncidentParam)", StringComparison.Ordinal);
        int load = source.IndexOf("await LoadTenantAsync()", StringComparison.Ordinal);

        onParameters.Should().BePositive();
        redirect.Should().BeGreaterThan(onParameters, "the redirect is the first thing the page does with new parameters");
        redirect.Should().BeLessThan(load, "an old address must not wait for the tenant's whole tree to load");
    }

    [Fact]
    public void An_old_link_to_a_ticket_lands_on_the_ticket_in_the_support_desk()
    {
        // Links like this one sit in mail that was sent before the move, and in browser history.
        SupportLinks.FromTenantPage("entit", "tickets", ticket: 1042, search: null, answer: null, incident: null)
            .Should().Be("/support/entit/tickets?ticket=1042");
    }

    [Fact]
    public void An_old_link_carries_its_search_answer_and_incident_across()
    {
        Guid answer = Guid.Parse("6c5b2a8e-0d0f-4f0e-9c1a-3b2d1e0f9a87");
        Guid incident = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

        SupportLinks.FromTenantPage("entit", "tickets", null, "tag:vpn owner:me", null, null)
            .Should().Be("/support/entit/tickets?q=tag%3Avpn%20owner%3Ame");
        SupportLinks.FromTenantPage("entit", "knowledge-base", null, null, answer, null)
            .Should().Be($"/support/entit/knowledge-base?answer={answer}");
        SupportLinks.FromTenantPage("entit", "incidents", null, null, null, incident)
            .Should().Be($"/support/entit/incidents?incident={incident}");
    }

    [Fact]
    public void Every_moved_section_is_redirected_and_nothing_else_is()
    {
        foreach (SupportSection s in SupportSections.All)
        {
            SupportLinks.FromTenantPage("entit", s.Key, null, null, null, null).Should().Be($"/support/entit/{s.Key}");
        }

        SupportLinks.FromTenantPage("entit", "alerts", null, null, null, null).Should().BeNull();
        SupportLinks.FromTenantPage("entit", "clusters", null, null, null, null).Should().BeNull();
        SupportLinks.FromTenantPage("entit", null, 1042, null, null, null).Should().BeNull();
    }

    [Fact]
    public void A_tenant_slug_is_escaped_into_the_address()
    {
        SupportLinks.Ticket("a b", 7).Should().Be("/support/a%20b/tickets?ticket=7");
    }

    // ───────────────────────────── Who sees what ─────────────────────────────

    [Fact]
    public void A_ticket_viewer_sees_the_queue_but_not_the_tools_or_the_mailbox()
    {
        EffectivePermissions viewer = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.SupportTickets] = AccessLevel.View,
        });

        List<string> visible = [.. SupportSections.VisibleTo(viewer).SelectMany(g => g.Sections).Select(s => s.Key)];

        visible.Should().Contain(["tickets", "feedback", "support-personal"]);
        visible.Should().NotContain(["macros", "support-mailbox", "sla", "support-report"]);
        SupportSections.VisibleTo(viewer).Should().OnlyContain(g => g.Sections.Count > 0, "an empty group is not shown");
    }

    [Fact]
    public void Somebody_with_no_support_permission_has_no_support_desk_at_all()
    {
        // Not even "my notifications & absence": that page is for people who work support.
        EffectivePermissions platformOnly = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.Clusters] = AccessLevel.Manage,
            [TenantFeature.Customers] = AccessLevel.Manage,
        });

        SupportSections.OpensFor(platformOnly).Should().BeFalse();
        SupportSections.VisibleTo(platformOnly).Should().BeEmpty();
        SupportSections.OpensFor(EffectivePermissions.Nothing).Should().BeFalse();
    }

    [Fact]
    public void A_global_admin_sees_every_section()
    {
        SupportSections.VisibleTo(EffectivePermissions.Everything).SelectMany(g => g.Sections)
            .Should().HaveCount(SupportSections.All.Count);
    }

    [Fact]
    public void The_desk_opens_on_the_first_section_the_person_may_see()
    {
        EffectivePermissions librarian = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.KnowledgeBase] = AccessLevel.Edit,
        });

        // Their own notification settings are open to them too, but are not what they came for.
        SupportSections.LandingFor(librarian)!.Key.Should().Be("knowledge-base");
        SupportSections.LandingFor(EffectivePermissions.Everything)!.Key.Should().Be("tickets");
        SupportSections.LandingFor(EffectivePermissions.Nothing).Should().BeNull();
    }

    private static string TenantPageSource()
    {
        string path = Path.Combine(AppContext.BaseDirectory, TenantPage);
        File.Exists(path).Should().BeTrue("the tenant page lives in " + TenantPage);
        return File.ReadAllText(path);
    }
}
