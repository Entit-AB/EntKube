namespace EntKube.Web.Data;

public enum TenantFeature
{
    Clusters,
    Environments,
    Customers,
    Apps,
    Deployments,
    Groups,
    Storage,
    Databases,
    GitRepositories,
    Keycloak,
    ContainerRegistry,
    Messaging,
    Cache,
    VPN,
    Monitoring,
    Audit,

    /// <summary>
    /// Approving just-in-time cluster access for customers.
    ///
    /// Its own feature rather than folded into Deployments, because the two are not the same
    /// authority: deploying an app changes what runs in a namespace, while this hands a person
    /// outside the organisation a shell into it. Appended last — these serialize as their numeric
    /// value, so inserting a member anywhere else silently re-points every permission already
    /// stored.
    /// </summary>
    JitAccess,

    /// <summary>
    /// The support knowledge base (helpdesk phase 10): reading its agents-only answers, writing
    /// answers, and running its categories. It used to be opt-in — nobody was singled out until
    /// some role was made its editor. Since role permissions became enforced it is an ordinary
    /// grant, and the roles that existed then were carried across so that nobody lost anything
    /// (see <see cref="Services.PermissionCatalog.CarryAcross"/>).
    /// </summary>
    KnowledgeBase,

    // ── Enforced from the start ──────────────────────────────────────────────────────────────
    // Everything below arrived when role permissions began to be checked rather than merely
    // stored. They are appended after KnowledgeBase for the reason given on JitAccess, and their
    // numbers are pinned by a test.

    /// <summary>The ticket queue and the tickets in it: reading, working, and merging or moving them.</summary>
    SupportTickets,

    /// <summary>What agents reach for while working: text modules, macros, ticket templates.</summary>
    SupportTools,

    /// <summary>The support dashboard and reports, including the spreadsheet export.</summary>
    SupportReporting,

    /// <summary>Who is on the support rota, and other people's absence.</summary>
    SupportRoster,

    /// <summary>The ways tickets arrive: the mailbox, its triage phrases, attachments, the ticket bridge, issue trackers.</summary>
    SupportChannels,

    /// <summary>The service-level targets the agreements promise.</summary>
    ServiceLevels,

    /// <summary>On-call, incidents and the subconsultant register.</summary>
    Incidents,

    /// <summary>A customer's contract, timebank and monthly report — the commercial side.</summary>
    CustomerAgreements,

    /// <summary>
    /// Who among the customer's own people may open the portal, and at what role. Its own
    /// permission because a grant here lets somebody outside the organisation read a customer's
    /// deployments and, at Operator, restart them.
    /// </summary>
    CustomerPortalAccess,
}
