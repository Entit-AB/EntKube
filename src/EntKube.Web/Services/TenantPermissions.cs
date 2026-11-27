using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>Where a permission sits on the roles page, and in the product.</summary>
public enum PermissionArea
{
    Platform,
    Support,
    Customers,
}

/// <summary>
/// One row of the roles page: a feature, the levels it can be granted at, and in plain words what
/// each of those levels lets somebody do.
/// </summary>
/// <param name="Enforced">
/// Whether anything checks this permission yet. Most of the platform rows were stored long before
/// anything asked about them, and a role that says "Clusters: View" while its members can still
/// change clusters is worse than one that says nothing. The roles page says so beside the row.
/// </param>
public sealed record FeaturePermission(
    TenantFeature Feature,
    PermissionArea Area,
    string Label,
    string Icon,
    bool Enforced,
    IReadOnlyDictionary<AccessLevel, string> Meaning)
{
    /// <summary>The levels this feature can be granted at, starting with None.</summary>
    public IReadOnlyList<AccessLevel> Levels { get; } =
        [AccessLevel.None, .. Meaning.Keys.Where(l => l != AccessLevel.None).Order()];

    /// <summary>The most this feature can be granted — what "everything" means for it.</summary>
    public AccessLevel Highest => Levels[^1];

    /// <summary>What a level lets somebody do, in the words the roles page shows.</summary>
    public string Describe(AccessLevel level) =>
        level == AccessLevel.None ? "No access" : Meaning.GetValueOrDefault(level, "");
}

/// <summary>
/// What one person may do in one tenant. Asked by every page and every action that is guarded,
/// so that the answer is always reached the same way.
/// </summary>
public sealed class EffectivePermissions
{
    private readonly IReadOnlyDictionary<TenantFeature, AccessLevel> grants;
    private readonly bool everything;

    private EffectivePermissions(IReadOnlyDictionary<TenantFeature, AccessLevel> grants, bool everything)
    {
        this.grants = grants;
        this.everything = everything;
    }

    /// <summary>Somebody who is not a member of the tenant, or nobody signed in.</summary>
    public static EffectivePermissions Nothing { get; } = new(new Dictionary<TenantFeature, AccessLevel>(), false);

    /// <summary>A global administrator: everything, in every tenant, member or not.</summary>
    public static EffectivePermissions Everything { get; } = new(new Dictionary<TenantFeature, AccessLevel>(), true);

    /// <summary>A member, holding what their role grants.</summary>
    public static EffectivePermissions Granting(IReadOnlyDictionary<TenantFeature, AccessLevel> grants) => new(grants, false);

    /// <summary>The level held on a feature. A feature the role does not mention is not held.</summary>
    public AccessLevel LevelOf(TenantFeature feature) =>
        everything ? AccessLevel.Manage : grants.GetValueOrDefault(feature, AccessLevel.None);

    /// <summary>Whether the level held on <paramref name="feature"/> is at least <paramref name="atLeast"/>.</summary>
    public bool Allows(TenantFeature feature, AccessLevel atLeast) => LevelOf(feature) >= atLeast;

    /// <summary>
    /// Whether anything in an area is held at all — at least View on one of its permissions. This
    /// is what decides whether a whole menu, such as Support, is offered in a tenant.
    /// </summary>
    public bool HoldsAnyIn(PermissionArea area) =>
        PermissionCatalog.Features.Where(f => f.Area == area).Any(f => Allows(f.Feature, AccessLevel.View));
}

/// <summary>A tenant, and what one person may do in it.</summary>
public sealed record TenantPermissions(Tenant Tenant, EffectivePermissions Permissions);

/// <summary>How many roles in one tenant were carried across, for the startup log.</summary>
public sealed record RolePermissionUpgrade(Guid TenantId, string TenantName, int Roles, bool KnowledgeBaseEditorsKept);

/// <summary>
/// Every permission a tenant role can hold, what each level means, and the two moments when
/// grants are written without an administrator choosing them: a brand-new tenant's starting
/// roles, and the one-off carry-across of roles that existed before permissions were enforced.
/// </summary>
public static class PermissionCatalog
{
    // The platform rows were all stored with the same three levels long before anything read
    // them; they keep all three so that a level already saved still shows as itself.
    private static FeaturePermission Platform(TenantFeature feature, string label, string icon, string things) =>
        new(feature, PermissionArea.Platform, label, icon, Enforced: false, new Dictionary<AccessLevel, string>
        {
            [AccessLevel.View] = $"See {things}",
            [AccessLevel.Edit] = $"Change {things}",
            [AccessLevel.Manage] = $"Create and remove {things}",
        });

    private static FeaturePermission Row(
        TenantFeature feature, PermissionArea area, string label, string icon, bool enforced,
        params (AccessLevel Level, string Meaning)[] levels) =>
        new(feature, area, label, icon, enforced, levels.ToDictionary(l => l.Level, l => l.Meaning));

    /// <summary>The roles page, row by row, in the order it shows them.</summary>
    public static IReadOnlyList<FeaturePermission> Features { get; } =
    [
        Platform(TenantFeature.Clusters, "Clusters", "bi-hdd-network", "clusters"),
        Platform(TenantFeature.Environments, "Environments", "bi-layers", "environments"),
        Platform(TenantFeature.Apps, "Apps", "bi-box-seam", "apps"),
        Platform(TenantFeature.Deployments, "Deployments", "bi-rocket", "deployments"),
        Platform(TenantFeature.Groups, "Groups", "bi-diagram-3", "groups"),
        Platform(TenantFeature.Databases, "Databases", "bi-database", "databases"),
        Platform(TenantFeature.Storage, "Storage", "bi-cloud-arrow-up", "storage"),
        Platform(TenantFeature.GitRepositories, "Git Repositories", "bi-git", "git repositories"),
        Platform(TenantFeature.Keycloak, "Keycloak / Identity", "bi-shield-lock", "identity realms"),
        Platform(TenantFeature.ContainerRegistry, "Container Registry", "bi-box2-heart", "container registries"),
        Platform(TenantFeature.Messaging, "Messaging", "bi-send", "message brokers"),
        Platform(TenantFeature.Cache, "Cache", "bi-lightning", "caches"),
        Platform(TenantFeature.VPN, "VPN", "bi-shield-shaded", "VPN tunnels"),
        Platform(TenantFeature.Monitoring, "Monitoring", "bi-activity", "monitoring"),
        Platform(TenantFeature.Audit, "Audit Log", "bi-journal-text", "the audit log"),

        // Approving a customer's request for a shell into their namespace. Only Manage is asked
        // about; the lower levels are kept because roles already store them.
        Row(TenantFeature.JitAccess, PermissionArea.Platform, "JIT cluster access", "bi-terminal", enforced: true,
            (AccessLevel.View, "See access requests"),
            (AccessLevel.Edit, "See access requests"),
            (AccessLevel.Manage, "Approve, deny and revoke cluster access")),

        Row(TenantFeature.SupportTickets, PermissionArea.Support, "Tickets", "bi-life-preserver", enforced: true,
            (AccessLevel.View, "See the queue, tickets and customer feedback"),
            (AccessLevel.Edit, "Work tickets and the support inbox: reply, note, set state, priority and owner, log time, run macros"),
            (AccessLevel.Manage, "Also merge tickets, move them to another tenant, and remove other people's logged time")),
        Row(TenantFeature.SupportTools, PermissionArea.Support, "Ticket tools", "bi-tools", enforced: true,
            (AccessLevel.Edit, "Write text modules, macros and templates"),
            (AccessLevel.Manage, "Also delete them")),
        Row(TenantFeature.KnowledgeBase, PermissionArea.Support, "Knowledge base", "bi-book", enforced: true,
            (AccessLevel.View, "Read agents-only answers"),
            (AccessLevel.Edit, "Write and change answers"),
            (AccessLevel.Manage, "Also run the categories and delete answers")),
        Row(TenantFeature.SupportReporting, PermissionArea.Support, "Support reporting", "bi-bar-chart", enforced: true,
            (AccessLevel.View, "See the dashboard and reports, and export them")),
        Row(TenantFeature.SupportRoster, PermissionArea.Support, "Support roster", "bi-person-check", enforced: true,
            (AccessLevel.View, "See who is on the rota"),
            (AccessLevel.Manage, "Put other people on or off the rota (anybody working tickets may put themselves on it)")),
        Row(TenantFeature.SupportChannels, PermissionArea.Support, "Support channels", "bi-envelope-at", enforced: true,
            (AccessLevel.View, "See how tickets arrive"),
            (AccessLevel.Manage, "Change the mailbox, triage phrases, attachments, ticket bridge and issue trackers")),
        Row(TenantFeature.ServiceLevels, PermissionArea.Support, "Service levels", "bi-check2-square", enforced: true,
            (AccessLevel.View, "See the SLA targets"),
            (AccessLevel.Edit, "Change the SLA targets")),
        Row(TenantFeature.Incidents, PermissionArea.Support, "Incidents & on-call", "bi-exclamation-octagon", enforced: true,
            (AccessLevel.View, "See on-call and incidents"),
            (AccessLevel.Edit, "Acknowledge and work incidents, and write handover notes"),
            (AccessLevel.Manage, "Also change on-call schedules and the subconsultant register")),

        Row(TenantFeature.Customers, PermissionArea.Customers, "Customers", "bi-building", enforced: true,
            (AccessLevel.View, "See customers, their apps and what they use"),
            (AccessLevel.Edit, "Add apps, and change contacts, mail domains and support addresses (on the Agreement tab)"),
            (AccessLevel.Manage, "Also create and delete customers")),
        Row(TenantFeature.CustomerAgreements, PermissionArea.Customers, "Agreements", "bi-file-earmark-text", enforced: true,
            (AccessLevel.View, "See contracts, hours and monthly reports"),
            (AccessLevel.Edit, "Change contracts and price lists")),
        Row(TenantFeature.CustomerPortalAccess, PermissionArea.Customers, "Portal access", "bi-door-open", enforced: true,
            (AccessLevel.View, "See who can open the customer portal"),
            (AccessLevel.Manage, "Grant, revoke and change portal access")),
    ];

    private static readonly Dictionary<TenantFeature, FeaturePermission> ByFeature = Features.ToDictionary(f => f.Feature);

    /// <summary>The roles page's row for a feature.</summary>
    public static FeaturePermission Describe(TenantFeature feature) => ByFeature[feature];

    /// <summary>The features that did not exist before permissions were enforced.</summary>
    public static IReadOnlyList<TenantFeature> IntroducedByEnforcement { get; } =
    [
        TenantFeature.SupportTickets, TenantFeature.SupportTools, TenantFeature.SupportReporting,
        TenantFeature.SupportRoster, TenantFeature.SupportChannels, TenantFeature.ServiceLevels,
        TenantFeature.Incidents, TenantFeature.CustomerAgreements, TenantFeature.CustomerPortalAccess,
    ];

    /// <summary>
    /// The grants a role from before enforcement needs so that its members can still do what they
    /// already could.
    /// </summary>
    /// <param name="existing">What the role stored, which was never checked except where noted below.</param>
    /// <param name="knowledgeBaseEditorsChosen">
    /// Whether some role in the same tenant had been made the knowledge base's editor. That was
    /// the one permission that already bound, and only once somebody had set it.
    /// </param>
    public static Dictionary<TenantFeature, AccessLevel> CarryAcross(
        Dictionary<TenantFeature, AccessLevel> existing, bool knowledgeBaseEditorsChosen)
    {
        // Whatever the role already said is kept: the platform rows mean what they meant, and
        // JIT approval was always checked.
        Dictionary<TenantFeature, AccessLevel> carried = new(existing);

        // Every new feature guards something a member could already open, so each is granted at
        // the most it can be.
        foreach (TenantFeature feature in IntroducedByEnforcement)
        {
            carried[feature] = Describe(feature).Highest;
        }

        // Customers was stored but never checked. A level an administrator chose is taken at its
        // word now that it means something; a role that never mentioned it keeps what it had,
        // which was everything.
        if (!carried.ContainsKey(TenantFeature.Customers))
        {
            carried[TenantFeature.Customers] = Describe(TenantFeature.Customers).Highest;
        }

        // The knowledge base was opt-in. Where nobody had opted in, everybody could edit it, and
        // so everybody may. Where editors had been chosen, they keep all an editor could do —
        // write answers, run the categories, delete answers, which is Manage now — and everybody
        // else keeps reading: the agents-only answers were never hidden from them.
        AccessLevel knowledgeBase = existing.GetValueOrDefault(TenantFeature.KnowledgeBase, AccessLevel.None);

        carried[TenantFeature.KnowledgeBase] = knowledgeBaseEditorsChosen
            ? (knowledgeBase >= AccessLevel.Edit ? Describe(TenantFeature.KnowledgeBase).Highest : AccessLevel.View)
            : Describe(TenantFeature.KnowledgeBase).Highest;

        return carried;
    }

    /// <summary>
    /// The three roles a new tenant starts with, granted what their names promise: the
    /// administrator everything, a member the daily work, a viewer a look at it.
    /// </summary>
    public static List<TenantRole> DefaultRolesFor(Guid tenantId) =>
    [
        DefaultRole(tenantId, "Administrator", f => f.Highest),
        DefaultRole(tenantId, "Member", MemberLevel),
        DefaultRole(tenantId, "Viewer", ViewerLevel),
    ];

    private static TenantRole DefaultRole(Guid tenantId, string name, Func<FeaturePermission, AccessLevel> level)
    {
        Dictionary<TenantFeature, AccessLevel> grants = Features
            .Select(f => (f.Feature, Level: level(f)))
            .Where(g => g.Level != AccessLevel.None)
            .ToDictionary(g => g.Feature, g => g.Level);

        return new TenantRole
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            PermissionsJson = TenantRoleService.EncodePermissions(grants),
            PermissionSchema = TenantRole.CurrentPermissionSchema,
        };
    }

    // A member works the queue and the platform — including adding apps for a customer, which is
    // Customers: Edit — and looks at, but does not run, how support is set up and what customers
    // have agreed to. Creating customers and letting outsiders into the portal are left to
    // administrators.
    private static AccessLevel MemberLevel(FeaturePermission f) => f.Feature switch
    {
        TenantFeature.SupportTickets or TenantFeature.SupportTools or TenantFeature.KnowledgeBase
            or TenantFeature.Incidents or TenantFeature.Customers => AccessLevel.Edit,
        TenantFeature.SupportReporting or TenantFeature.SupportRoster or TenantFeature.SupportChannels
            or TenantFeature.ServiceLevels or TenantFeature.CustomerAgreements => AccessLevel.View,
        TenantFeature.CustomerPortalAccess or TenantFeature.JitAccess => AccessLevel.None,
        _ => AccessLevel.Edit,
    };

    // A viewer sees tickets, answers, incidents and customers, and changes nothing. The ticket
    // tools are not something to look at — using one is working a ticket.
    private static AccessLevel ViewerLevel(FeaturePermission f) => f.Feature switch
    {
        TenantFeature.SupportTickets or TenantFeature.KnowledgeBase or TenantFeature.SupportReporting
            or TenantFeature.Incidents or TenantFeature.Customers => AccessLevel.View,
        TenantFeature.SupportTools or TenantFeature.SupportRoster or TenantFeature.SupportChannels
            or TenantFeature.ServiceLevels or TenantFeature.CustomerAgreements
            or TenantFeature.CustomerPortalAccess or TenantFeature.JitAccess => AccessLevel.None,
        _ => AccessLevel.View,
    };
}
