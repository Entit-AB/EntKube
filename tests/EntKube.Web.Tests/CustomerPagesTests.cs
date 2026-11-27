using EntKube.Web.Components.Pages.Customers;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The customer pages' shape: which tabs a customer has, who may open each, and the addresses
/// that lead to them — including the tenant-page addresses that used to.
///
/// <para>Customers moved out of the tenant page into a menu of their own, as support did. The
/// same three things can go wrong in the move, and each test here is one of them: a tab asking
/// for a permission nobody can be given, an old address leading nowhere, or the tenant page
/// still showing a customer without the checks.</para>
/// </summary>
public class CustomerPagesTests
{
    private const string TenantPage =
        "../../../../../src/EntKube.Web/Components/Pages/Tenants/TenantExplorer.razor";

    [Fact]
    public void A_customer_has_the_tabs_it_had_on_the_tenant_page()
    {
        CustomerSections.All.Select(t => t.Key).Should().Equal("overview", "apps", "agreement", "hours", "report", "portal");
    }

    [Fact]
    public void Every_tab_asks_for_a_customer_permission_at_a_level_it_has()
    {
        foreach (CustomerTabSection tab in CustomerSections.All)
        {
            FeaturePermission permission = PermissionCatalog.Describe(tab.Feature);

            permission.Area.Should().Be(PermissionArea.Customers, tab.Key);
            permission.Levels.Should().Contain(tab.Level, tab.Key);
        }
    }

    [Fact]
    public void The_roles_page_no_longer_says_any_customer_permission_is_unchecked()
    {
        PermissionCatalog.Features.Where(f => f.Area == PermissionArea.Customers).Should().OnlyContain(f => f.Enforced);
    }

    // ───────────────────────────── Who sees what ─────────────────────────────

    [Fact]
    public void Somebody_who_may_see_customers_sees_them_but_not_their_agreements_or_portal_access()
    {
        EffectivePermissions accountViewer = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.Customers] = AccessLevel.View,
        });

        CustomerSections.VisibleTo(accountViewer).Select(t => t.Key).Should().Equal("overview", "apps");
    }

    [Fact]
    public void An_account_manager_sees_the_agreement_without_portal_access()
    {
        EffectivePermissions accountManager = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.Customers] = AccessLevel.Edit,
            [TenantFeature.CustomerAgreements] = AccessLevel.Edit,
        });

        CustomerSections.VisibleTo(accountManager).Select(t => t.Key).Should().Equal("overview", "apps", "agreement", "hours", "report");
    }

    [Fact]
    public void Portal_access_alone_is_not_enough_to_open_customers()
    {
        // The customer list is what every other tab hangs off. Somebody who may only run portal
        // access still needs to be able to see the customer they are granting it for.
        EffectivePermissions portalOnly = EffectivePermissions.Granting(new Dictionary<TenantFeature, AccessLevel>
        {
            [TenantFeature.CustomerPortalAccess] = AccessLevel.Manage,
        });

        CustomerSections.OpensFor(portalOnly).Should().BeFalse();
        CustomerSections.OpensFor(EffectivePermissions.Everything).Should().BeTrue();
    }

    // ───────────────────────────── Leaving the tenant page ─────────────────────────────

    [Fact]
    public void An_old_link_to_the_customer_list_or_a_customer_lands_on_the_customer_pages()
    {
        Guid customer = Guid.Parse("2f1d4c6a-8b3e-4d5f-9a7c-1e2b3c4d5e6f");

        CustomerLinks.FromTenantPage("entit", "customers", null).Should().Be("/customers/entit");
        CustomerLinks.FromTenantPage("entit", null, customer).Should().Be($"/customers/entit/{customer}");
        CustomerLinks.FromTenantPage("entit", "clusters", null).Should().BeNull();
        CustomerLinks.FromTenantPage("entit", null, null).Should().BeNull();
    }

    [Fact]
    public void Customer_addresses_are_built_in_one_place()
    {
        Guid customer = Guid.Parse("2f1d4c6a-8b3e-4d5f-9a7c-1e2b3c4d5e6f");

        CustomerLinks.List("a b").Should().Be("/customers/a%20b");
        CustomerLinks.Customer("entit", customer).Should().Be($"/customers/entit/{customer}");
        CustomerLinks.Tab("entit", customer, "portal").Should().Be($"/customers/entit/{customer}/portal");
    }

    [Fact]
    public void The_tenant_page_no_longer_shows_a_customer_itself()
    {
        string source = TenantPageSource();

        source.Should().NotContain("<CustomerTab", "a customer is shown on the customer pages, with their checks");
        source.Should().NotContain("case \"customers\":");
    }

    [Fact]
    public void The_tenant_page_sends_an_old_customer_address_on_before_loading_anything()
    {
        string source = TenantPageSource();
        int onParameters = source.IndexOf("protected override async Task OnParametersSetAsync()", StringComparison.Ordinal);
        int redirect = source.IndexOf("CustomerLinks.FromTenantPage(Slug, SectionParam, CustomerParam)", StringComparison.Ordinal);
        int load = source.IndexOf("await LoadTenantAsync()", StringComparison.Ordinal);

        onParameters.Should().BePositive();
        redirect.Should().BeGreaterThan(onParameters);
        redirect.Should().BeLessThan(load);
    }

    private static string TenantPageSource()
    {
        string path = Path.Combine(AppContext.BaseDirectory, TenantPage);
        File.Exists(path).Should().BeTrue("the tenant page lives in " + TenantPage);
        return File.ReadAllText(path);
    }
}
