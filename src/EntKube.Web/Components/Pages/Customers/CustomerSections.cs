using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.AspNetCore.Components;

namespace EntKube.Web.Components.Pages.Customers;

/// <summary>What a customer tab is opened with: the tenant, and the customer — already known to be the tenant's.</summary>
public sealed record CustomerContext(Guid TenantId, string TenantSlug, Customer Customer);

/// <summary>One tab of a customer's page, and the permission that opens it.</summary>
public sealed record CustomerTabSection(
    string Key, string Label, string Icon, TenantFeature Feature, AccessLevel Level,
    Func<CustomerContext, RenderFragment> Render)
{
    /// <summary>Whether this person may open the tab — which needs the customer pages at all, too.</summary>
    public bool OpensFor(EffectivePermissions permissions) =>
        CustomerSections.OpensFor(permissions) && permissions.Allows(Feature, Level);
}

/// <summary>
/// A customer's tabs: who the customer is and what runs for them, what they have agreed to, and
/// who among their own people may open the portal.
///
/// <para>These were the buttons on the tenant page's customer view, where being a member of the
/// tenant was enough for all of them. Each now names the permission that opens it, so that the
/// commercial side and portal access can be given to the people who run them and nobody else.</para>
/// </summary>
public static class CustomerSections
{
    // Each tab is an existing component given a handful of parameters, named here once.
    private static Func<CustomerContext, RenderFragment> Panel<T>(Func<CustomerContext, Dictionary<string, object?>> parameters)
        where T : IComponent =>
        ctx => builder =>
        {
            builder.OpenComponent<T>(0);
            builder.AddMultipleAttributes(1, parameters(ctx));
            builder.CloseComponent();
        };

    /// <summary>The tabs, in the order they are shown.</summary>
    public static IReadOnlyList<CustomerTabSection> All { get; } =
    [
        new("overview", "Overview", "bi-person-badge", TenantFeature.Customers, AccessLevel.View,
            Panel<CustomerOverviewTab>(ctx => new() { ["TenantId"] = ctx.TenantId, ["TenantSlug"] = ctx.TenantSlug, ["Customer"] = ctx.Customer })),
        new("apps", "Apps", "bi-app-indicator", TenantFeature.Customers, AccessLevel.View,
            Panel<CustomerAppsTab>(ctx => new() { ["TenantId"] = ctx.TenantId, ["TenantSlug"] = ctx.TenantSlug, ["Customer"] = ctx.Customer })),

        // The commercial side: what was agreed, what it costs, and the people the agreement names.
        new("agreement", "Agreement", "bi-file-earmark-ruled", TenantFeature.CustomerAgreements, AccessLevel.View,
            Panel<PortfolioContractPanel>(ctx => new() { ["Customer"] = ctx.Customer, ["TenantId"] = ctx.TenantId })),
        new("hours", "Hours", "bi-stopwatch", TenantFeature.CustomerAgreements, AccessLevel.View,
            Panel<TimebankPanel>(ctx => new() { ["TenantId"] = ctx.TenantId, ["CustomerId"] = ctx.Customer.Id })),
        new("report", "Monthly report", "bi-file-earmark-bar-graph", TenantFeature.CustomerAgreements, AccessLevel.View,
            Panel<MonthlyReportPanel>(ctx => new() { ["TenantId"] = ctx.TenantId, ["CustomerId"] = ctx.Customer.Id })),

        // Who among the customer's own people may open the portal: a door to the outside.
        new("portal", "Portal access", "bi-door-open", TenantFeature.CustomerPortalAccess, AccessLevel.View,
            Panel<CustomerPortalAccessTab>(ctx => new() { ["TenantId"] = ctx.TenantId, ["Customer"] = ctx.Customer })),
    ];

    private static readonly Dictionary<string, CustomerTabSection> ByKey = All.ToDictionary(t => t.Key);

    /// <summary>The tab with this key, or null if a customer has none.</summary>
    public static CustomerTabSection? Find(string? key) => key is not null && ByKey.TryGetValue(key, out CustomerTabSection? t) ? t : null;

    /// <summary>
    /// Whether this person may open customers in the tenant at all. Every tab hangs off seeing the
    /// customer, so that is what it takes — even for somebody who only runs portal access.
    /// </summary>
    public static bool OpensFor(EffectivePermissions permissions) => permissions.Allows(TenantFeature.Customers, AccessLevel.View);

    /// <summary>The tabs as this person sees them.</summary>
    public static IReadOnlyList<CustomerTabSection> VisibleTo(EffectivePermissions permissions) =>
        [.. All.Where(t => t.OpensFor(permissions))];

    /// <summary>Where a customer's page opens when no tab is asked for.</summary>
    public static CustomerTabSection? LandingFor(EffectivePermissions permissions) => VisibleTo(permissions).FirstOrDefault();
}

/// <summary>The addresses of the customer pages, built in one place.</summary>
public static class CustomerLinks
{
    /// <summary>Choosing a tenant's customers, when more than one tenant has them.</summary>
    public const string Home = "/customers";

    /// <summary>A tenant's customer list.</summary>
    public static string List(string slug) => $"{Home}/{Uri.EscapeDataString(slug)}";

    /// <summary>A customer's page, on the tab it opens with.</summary>
    public static string Customer(string slug, Guid customerId) => $"{List(slug)}/{customerId}";

    /// <summary>One tab of a customer's page.</summary>
    public static string Tab(string slug, Guid customerId, string key) => $"{Customer(slug, customerId)}/{key}";

    /// <summary>
    /// Where an old tenant-page address for customers now lives: <c>?section=customers</c> is the
    /// list, <c>?customer=…</c> a customer. Null for any other address.
    /// </summary>
    public static string? FromTenantPage(string slug, string? section, Guid? customerId)
    {
        if (customerId is Guid id)
        {
            return Customer(slug, id);
        }

        return section == "customers" ? List(slug) : null;
    }
}
