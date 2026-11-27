using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using Microsoft.AspNetCore.Components;

namespace EntKube.Web.Components.Pages.Support;

/// <summary>What a support section is opened with: the tenant, and whatever the address asked to open.</summary>
public sealed record SupportContext(Guid TenantId, string TenantSlug, int? Ticket, string? Search, Guid? Answer, Guid? Incident);

/// <summary>
/// One page of the support desk.
/// </summary>
/// <param name="Feature">
/// The permission that opens it, at <paramref name="Level"/>. Null for the one page that belongs
/// to the person rather than the tenant — their own notifications and absence — which opens for
/// anybody who works support here at all.
/// </param>
public sealed record SupportSection(
    string Key, string Label, string Icon, TenantFeature? Feature, AccessLevel Level,
    Func<SupportContext, RenderFragment> Render)
{
    /// <summary>Whether this person may open the section.</summary>
    public bool OpensFor(EffectivePermissions permissions) =>
        Feature is TenantFeature feature
            ? permissions.Allows(feature, Level)
            : SupportSections.OpensFor(permissions);
}

/// <summary>A heading in the support desk's menu and the sections under it.</summary>
public sealed record SupportGroup(string Name, string Icon, IReadOnlyList<SupportSection> Sections);

/// <summary>
/// The support desk's menu: every section, the permission that opens it, and what it shows.
///
/// <para>These used to be the Support part of the tenant page's tree, where being a member of
/// the tenant was enough to open any of them. They are here, in code rather than in the page,
/// so that the permission each one asks for sits on the same line as the section, and so a test
/// can hold every one of them to it.</para>
/// </summary>
public static class SupportSections
{
    // Most sections are a component that needs nothing but the tenant.
    private static Func<SupportContext, RenderFragment> Tab<T>() where T : IComponent =>
        ctx => builder =>
        {
            builder.OpenComponent<T>(0);
            builder.AddComponentParameter(1, "TenantId", ctx.TenantId);
            builder.CloseComponent();
        };

    // Some also need the tenant's address, to link to tickets inside it.
    private static Func<SupportContext, RenderFragment> TabWithSlug<T>() where T : IComponent =>
        ctx => builder =>
        {
            builder.OpenComponent<T>(0);
            builder.AddComponentParameter(1, "TenantId", ctx.TenantId);
            builder.AddComponentParameter(2, "TenantSlug", ctx.TenantSlug);
            builder.CloseComponent();
        };

    private static SupportSection Section(
        string key, string label, string icon, TenantFeature? feature, AccessLevel level,
        Func<SupportContext, RenderFragment> render) => new(key, label, icon, feature, level, render);

    /// <summary>The menu, heading by heading, in the order it is shown.</summary>
    public static IReadOnlyList<SupportGroup> Groups { get; } =
    [
        // The desk's own subject first: people waiting for an answer, and what is known about how
        // that is going.
        new("Tickets", "bi-life-preserver",
        [
            Section("tickets", "Ticket queue", "bi-life-preserver", TenantFeature.SupportTickets, AccessLevel.View,
                ctx => builder =>
                {
                    builder.OpenComponent<TicketQueue>(0);
                    builder.AddComponentParameter(1, "TenantId", (Guid?)ctx.TenantId);
                    builder.AddComponentParameter(2, "TenantSlug", ctx.TenantSlug);
                    builder.AddComponentParameter(3, "FocusTicketNumber", ctx.Ticket);
                    builder.AddComponentParameter(4, "Search", ctx.Search);
                    builder.CloseComponent();
                }),
            Section("support-dashboard", "Dashboard", "bi-speedometer2", TenantFeature.SupportReporting, AccessLevel.View, TabWithSlug<SupportDashboardTab>()),
            Section("support-report", "Reporting", "bi-bar-chart", TenantFeature.SupportReporting, AccessLevel.View, TabWithSlug<SupportReportTab>()),
            Section("support-personal", "My notifications & absence", "bi-bell", null, AccessLevel.View, Tab<SupportPersonalTab>()),
            // Beside the queue because it answers the question the queue raises: whose is this one.
            Section("support-roster", "Support roster", "bi-person-check", TenantFeature.SupportRoster, AccessLevel.View, Tab<SupportRosterTab>()),
            // What people said through the widget, waiting for somebody to decide which of it is a ticket.
            Section("feedback", "Customer feedback", "bi-chat-square-text", TenantFeature.SupportTickets, AccessLevel.View, Tab<FeedbackInbox>()),
        ]),

        // What agents reach for while working the queue. Using one is part of working a ticket;
        // these pages are where they are written, so they ask for the tools permission itself.
        new("Ticket tools", "bi-tools",
        [
            Section("text-modules", "Text modules", "bi-card-text", TenantFeature.SupportTools, AccessLevel.Edit, Tab<TextModulesTab>()),
            Section("macros", "Macros", "bi-lightning", TenantFeature.SupportTools, AccessLevel.Edit, Tab<MacrosTab>()),
            Section("ticket-templates", "Templates", "bi-files", TenantFeature.SupportTools, AccessLevel.Edit, Tab<TicketTemplatesTab>()),
            Section("knowledge-base", "Knowledge base", "bi-book", TenantFeature.KnowledgeBase, AccessLevel.View,
                ctx => builder =>
                {
                    builder.OpenComponent<KnowledgeBaseTab>(0);
                    builder.AddComponentParameter(1, "TenantId", ctx.TenantId);
                    builder.AddComponentParameter(2, "OpenAnswerId", ctx.Answer);
                    builder.AddComponentParameter(3, "TenantSlug", ctx.TenantSlug);
                    builder.CloseComponent();
                }),
            // Where the developers' issues are, so a ticket can show the one being fixed for it.
            Section("issue-trackers", "Issue trackers", "bi-github", TenantFeature.SupportChannels, AccessLevel.View, Tab<IssueTrackersTab>()),
        ]),

        // Somebody is on call for what fires, and works it as an incident. The alerts themselves
        // stay with the tenant: they are about the platform, not about a customer waiting.
        new("Incidents", "bi-exclamation-octagon",
        [
            Section("on-call", "On-call", "bi-person-workspace", TenantFeature.Incidents, AccessLevel.View, Tab<OnCallTab>()),
            Section("incidents", "Incidents", "bi-exclamation-octagon", TenantFeature.Incidents, AccessLevel.View,
                ctx => builder =>
                {
                    builder.OpenComponent<IncidentManagement>(0);
                    builder.AddComponentParameter(1, "TenantId", ctx.TenantId);
                    builder.AddComponentParameter(2, "FocusIncidentId", ctx.Incident);
                    builder.CloseComponent();
                }),
            Section("subconsultants", "Subconsultants", "bi-people", TenantFeature.Incidents, AccessLevel.View, Tab<SubconsultantRegister>()),
        ]),

        // The ways tickets arrive, and how each is set up.
        new("Mail", "bi-envelope",
        [
            Section("support-inbox", "Support inbox", "bi-envelope", TenantFeature.SupportTickets, AccessLevel.View, Tab<SupportInbox>()),
            Section("support-mailbox", "Mailbox connection", "bi-envelope-at", TenantFeature.SupportChannels, AccessLevel.View, Tab<SupportMailboxTab>()),
            Section("mail-triage", "Mail triage phrases", "bi-funnel", TenantFeature.SupportChannels, AccessLevel.View, Tab<MailTriageRulesTab>()),
            Section("support-attachments", "Attachments", "bi-paperclip", TenantFeature.SupportChannels, AccessLevel.View, Tab<SupportAttachmentsTab>()),
        ]),
        // A customer's own service desk reaching us directly: a channel, like the mailbox.
        new("Ticket bridge", "bi-arrow-left-right",
        [
            Section("ticket-bridge", "Customer systems", "bi-plug", TenantFeature.SupportChannels, AccessLevel.View, Tab<TicketBridgeTab>()),
        ]),

        // The agreements' own promises about how all of the above went.
        new("Service levels", "bi-check2-square",
        [
            Section("sla", "SLA", "bi-check2-square", TenantFeature.ServiceLevels, AccessLevel.View, Tab<SlaTargetManager>()),
        ]),
    ];

    /// <summary>Every section, in menu order.</summary>
    public static IReadOnlyList<SupportSection> All { get; } = [.. Groups.SelectMany(g => g.Sections)];

    private static readonly Dictionary<string, SupportSection> ByKey = All.ToDictionary(s => s.Key);

    /// <summary>The section with this key, or null if the support desk has none.</summary>
    public static SupportSection? Find(string? key) => key is not null && ByKey.TryGetValue(key, out SupportSection? s) ? s : null;

    /// <summary>
    /// Whether this person works support in the tenant at all: some support permission at View or
    /// more. Without one there is no support desk for them here, not even their own settings.
    /// </summary>
    public static bool OpensFor(EffectivePermissions permissions) => permissions.HoldsAnyIn(PermissionArea.Support);

    /// <summary>The menu as this person sees it: only what they may open, and no empty headings.</summary>
    public static IReadOnlyList<SupportGroup> VisibleTo(EffectivePermissions permissions) =>
        [.. Groups
            .Select(g => g with { Sections = [.. g.Sections.Where(s => s.OpensFor(permissions))] })
            .Where(g => g.Sections.Count > 0)];

    /// <summary>
    /// Where the desk opens when no section is asked for: the first one this person may open,
    /// passing over their own settings, which are open to them but are not what they came for.
    /// </summary>
    public static SupportSection? LandingFor(EffectivePermissions permissions)
    {
        List<SupportSection> visible = [.. VisibleTo(permissions).SelectMany(g => g.Sections)];

        return visible.FirstOrDefault(s => s.Feature is not null) ?? visible.FirstOrDefault();
    }
}

/// <summary>
/// The addresses of things in the support desk. Every link to a ticket, a search, an answer or
/// an incident is built here, so that the next move of the desk is one edit and not a search.
/// </summary>
public static class SupportLinks
{
    /// <summary>The support desk's start page for a tenant.</summary>
    public static string Desk(string slug) => $"/support/{Uri.EscapeDataString(slug)}";

    /// <summary>A section of the support desk.</summary>
    public static string Section(string slug, string key) => $"{Desk(slug)}/{key}";

    /// <summary>A ticket, opened in the queue.</summary>
    public static string Ticket(string slug, int number) => $"{Section(slug, "tickets")}?ticket={number}";

    /// <summary>A search of the queue, which survives a reload and can be sent to somebody.</summary>
    public static string Search(string slug, string query) => $"{Section(slug, "tickets")}?q={Uri.EscapeDataString(query)}";

    /// <summary>A knowledge-base answer.</summary>
    public static string Answer(string slug, Guid answerId) => $"{Section(slug, "knowledge-base")}?answer={answerId}";

    /// <summary>An incident, or the incident list when there is no particular one to open.</summary>
    public static string Incident(string slug, Guid? incidentId) =>
        incidentId is Guid id ? $"{Section(slug, "incidents")}?incident={id}" : Section(slug, "incidents");

    /// <summary>
    /// Where an old tenant-page address for a support section now lives, or null if the address
    /// was never a support section's. Old addresses live on in mail already sent, in bookmarks and
    /// in browser history, so the tenant page sends them on rather than showing nothing.
    /// </summary>
    public static string? FromTenantPage(string slug, string? section, int? ticket, string? search, Guid? answer, Guid? incident)
    {
        if (SupportSections.Find(section) is not SupportSection moved)
        {
            return null;
        }

        // Whatever the old address asked to open inside the section comes along with it.
        List<string> query = [];

        if (ticket is int number)
        {
            query.Add($"ticket={number}");
        }

        if (!string.IsNullOrEmpty(search))
        {
            query.Add($"q={Uri.EscapeDataString(search)}");
        }

        if (answer is Guid answerId)
        {
            query.Add($"answer={answerId}");
        }

        if (incident is Guid incidentId)
        {
            query.Add($"incident={incidentId}");
        }

        string address = Section(slug, moved.Key);

        return query.Count == 0 ? address : $"{address}?{string.Join('&', query)}";
    }
}
