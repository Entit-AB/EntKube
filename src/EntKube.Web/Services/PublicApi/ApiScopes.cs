namespace EntKube.Web.Services.PublicApi;

/// <summary>
/// One resource a scope lets a caller address, and the exact operations that reach it.
/// </summary>
/// <param name="Label">
/// How the resource is described to the operator deciding whether to grant the scope. Free prose,
/// because a person reads it.
///
/// <para>⚠️ The prose is NOT machine-checked and cannot be. What is checked is the list beside
/// it: <c>ApiScopeAdvertisementTests</c> asserts that the operations declared across a scope are
/// EXACTLY the operations the running app guards with it. So a label can still be badly worded,
/// but it cannot sit next to a set of operations that is wrong — and a reviewer reading a diff
/// that adds <c>DELETE</c> to this list is looking straight at the sentence that has to cover it.</para>
/// </param>
/// <param name="Operations">
/// Method and path, with the route-group prefix removed and every route parameter written
/// <c>{}</c>: <c>GET /clusters</c>, <c>POST /apps/{}/secrets/sync</c>.
///
/// <para>⚠️ Exact operations rather than a path segment, and the difference is the whole
/// assertion. Matching on a segment made the check method-blind and prefix-happy: adding
/// <c>DELETE /advisor/findings/{}</c> under <c>ops:write</c> was satisfied by the already-declared
/// segment <c>advisor</c>, so a token advertised as "Acknowledge advisor findings" could delete
/// them with every test green. Set equality on (method, path) has no such slack.</para>
/// </param>
public sealed record ApiScopeResource(string Label, IReadOnlyList<string> Operations);

/// <summary>What a scope lets a caller do, and to what.</summary>
/// <param name="Action">
/// The verb shown to the operator. <see cref="ReadOnlyAction"/> exactly when the scope grants
/// nothing but <c>GET</c>s, and something else when it can change state.
///
/// <para>⚠️ This is the one piece of the sentence that IS checked, because it is the one prose
/// error a machine can see: a scope granting <c>PUT</c> and <c>DELETE</c> whose sentence begins
/// "Read" is wrong no matter what follows. <c>config:read</c> and <c>config:write</c> declare the
/// same resource and the same label — the verb is the only thing that distinguishes their two
/// sentences, and it had no assertion at all. Which particular verb a mutating scope uses
/// ("Trigger", "Change", "Acknowledge") is prose and is not checked.</para>
/// </param>
public sealed record ApiScopeGrant(string Action, IReadOnlyList<ApiScopeResource> Resources)
{
    /// <summary>The verb a scope must use when it grants reads and nothing else.</summary>
    public const string ReadOnlyAction = "Read";

    /// <summary>Every operation this scope grants, across all its resources.</summary>
    public IEnumerable<string> Operations => Resources.SelectMany(r => r.Operations);

    /// <summary>
    /// True when any declared operation is not a <c>GET</c>. Derived from the operations rather
    /// than declared beside them, so it cannot disagree with them.
    /// </summary>
    public bool Mutates =>
        Operations.Any(o => !o.StartsWith("GET ", StringComparison.Ordinal));

    /// <summary>The sentence the token-creation UI shows beside the scope's checkbox.</summary>
    ///
    /// <para>⚠️ Semicolons once any label carries a comma of its own, and that is not styling.
    /// "A, B and C" attaches its final "and" to whatever immediately precedes it, so a label like
    /// "every user account in this installation, including users of other tenants" followed by
    /// "and SCIM's discovery document" reads as though the tenants and the document are one list.
    /// On consent text a mis-parse is a misstatement.</para>
    public string Describe()
    {
        string[] labels = [.. Resources.Select(r => r.Label)];

        if (labels.Any(l => l.Contains(',', StringComparison.Ordinal)))
        {
            return $"{Action} {string.Join("; ", labels)}";
        }

        string joined = labels.Length switch
        {
            0 => "",
            1 => labels[0],
            _ => string.Join(", ", labels[..^1]) + " and " + labels[^1],
        };

        return $"{Action} {joined}";
    }
}

/// <summary>
/// The scope vocabulary for public API tokens.
///
/// Deliberately small and resource-shaped rather than one scope per endpoint: a
/// vocabulary an operator cannot hold in their head gets granted wholesale, which
/// defeats the point. Read and write are always separate — the common case (a
/// dashboard, a monitoring integration) needs no write access at all.
///
/// <para><b>Updated 2026-11-04. What was wrong.</b> Every scope's description was hand-written
/// prose, and <see cref="Describe"/> is rendered as the label of the checkbox an operator ticks in
/// <c>ApiTokensTab.razor</c> — so it is the consent text, not documentation. Measured against the
/// routes, every one of the eight was wrong, in both directions:</para>
///
/// <list type="bullet">
/// <item><c>apps:write</c> said "Trigger deployment syncs and restarts" and also granted
/// <c>POST /apps/{appId}/secrets/sync</c>, which pushes an app's vaulted secrets into the cluster.
/// An operator issuing a token so CI can restart a pod was also handing it secret delivery, and
/// the label never said so.</item>
/// <item><c>ops:read</c> said "advisor findings, incidents, drift and vulnerability reports" and
/// granted eleven routes — among them <c>/cost</c>, <c>/cost/history</c> and <c>/cost/months</c>,
/// which carry the whole cloud bill with per-customer attribution.</item>
/// <item>In the other direction <c>fleet:read</c> advertised nodes and workloads,
/// <c>apps:read</c> advertised environments, <c>ops:write</c> advertised opening and resolving
/// incidents, and <c>config:read</c> advertised rollout policies. None of those four have a
/// route at all; see docs/api-scope-coverage.md.</item>
/// </list>
///
/// <para>⭐ So the description is no longer written. Each scope declares what it grants as
/// <see cref="ApiScopeResource"/> data, keyed by the route segment that serves it, and
/// <see cref="Describe"/> renders the sentence from that list. The prose and the assertion now
/// read the same field, which is the only arrangement where a wrong description cannot stay
/// green: a ratchet asserts numbers, never explanations, so the explanation has to be built out
/// of something countable.</para>
/// </summary>
public static class ApiScopes
{
    /// <summary>Clusters and the components installed on them.</summary>
    public const string FleetRead = "fleet:read";

    /// <summary>Apps and deployments.</summary>
    public const string AppsRead = "apps:read";

    /// <summary>Deployment syncs and restarts — and secret delivery.</summary>
    public const string AppsWrite = "apps:write";

    /// <summary>
    /// Power actions on KubeVirt virtual machines. Its own scope: stopping a customer's VM is not what a
    /// token minted to sync a deployment was asked to do, and apps:write is advertised as exactly that.
    /// </summary>
    public const string VmsWrite = "vms:write";

    /// <summary>The operational reports, cost among them.</summary>
    public const string OpsRead = "ops:read";

    /// <summary>
    /// Read the contents of application logs. Separate from ops:read, whose reports are summaries:
    /// log lines carry whatever the application printed, and a token issued to read the cost report
    /// must not gain that when a route is added.
    /// </summary>
    public const string LogsRead = "logs:read";

    /// <summary>Acknowledge advisor findings.</summary>
    public const string OpsWrite = "ops:write";

    /// <summary>Read EntKube's own configuration — the cluster price sheets.</summary>
    public const string ConfigRead = "config:read";

    /// <summary>
    /// Change EntKube's own configuration. Separate from ops:write because that scope is
    /// about reacting to incidents, while this one reshapes how the platform behaves —
    /// a Terraform run holds it, an on-call responder does not need it.
    /// </summary>
    public const string ConfigWrite = "config:write";

    /// <summary>
    /// Provision and deprovision users over SCIM. Its own scope rather than folded into
    /// a broader one: a token that can disable people's accounts is handed to a directory
    /// connector, and that connector has no business reading clusters or syncing apps.
    /// </summary>
    public const string ScimProvision = "scim:provision";

    public const string TicketsRead = "tickets:read";

    public const string TicketsWrite = "tickets:write";

    /// <summary>Every scope, in the order they should be presented.</summary>
    // Declared before All: static field initializers run in declaration order, so
    // assigning All from a field declared below it would leave All null at runtime.
    private static readonly string[] AllScopes =
        [FleetRead, AppsRead, AppsWrite, VmsWrite, OpsRead, LogsRead, OpsWrite, ConfigRead, ConfigWrite, TicketsRead, TicketsWrite, ScimProvision];

    public static readonly IReadOnlyList<string> All = AllScopes;

    /// <summary>
    /// What each scope grants, as the data both the operator's sentence and the route assertion
    /// are derived from.
    ///
    /// <para>⚠️ Adding a route under an existing scope means adding its resource here. That is
    /// the point, not an inconvenience: a route reachable with a scope whose label does not
    /// mention it is a grant nobody consented to, and that is how
    /// <c>/apps/{appId}/secrets/sync</c> came to be reachable with a token advertised as
    /// restarting pods.</para>
    /// </summary>
    private static readonly Dictionary<string, ApiScopeGrant> Grants = new(StringComparer.Ordinal)
    {
        [FleetRead] = new("Read", [
            new("clusters", ["GET /clusters"]),
            new("the components installed on a cluster", ["GET /clusters/{}/components"]),
            new("the virtual machines on a cluster and their state", ["GET /clusters/{}/virtual-machines"]),
        ]),

        // Deleting is not here and has no route: it destroys the VM's imported disks, and the page
        // refuses it while the VM has published ports.
        [VmsWrite] = new("Start, stop, restart, pause, unpause and live-migrate", [
            new("virtual machines", [
                "POST /clusters/{}/virtual-machines/{}/{}/start", "POST /clusters/{}/virtual-machines/{}/{}/stop",
                "POST /clusters/{}/virtual-machines/{}/{}/restart", "POST /clusters/{}/virtual-machines/{}/{}/pause",
                "POST /clusters/{}/virtual-machines/{}/{}/unpause", "POST /clusters/{}/virtual-machines/{}/{}/migrate",
            ]),
        ]),

        // `/apps` returns `customer = a.CustomerName` for every app, so this scope enumerates the
        // tenant's customer roster. ops:read was worded to admit per-customer attribution; this
        // discloses the same class of identity and had said only "apps".
        [AppsRead] = new("Read", [
            new("apps and the customer each one belongs to", ["GET /apps"]),
            new("deployments and their status", ["GET /deployments"]),
        ]),

        // ⚠️ The secret delivery is spelled out as its own resource, not folded into
        // "deployments", because it is a different privilege: the route pushes vaulted secret
        // material into a live cluster. An operator minting a token so CI could restart a pod was
        // granting that too, and the sentence did not say so.
        [AppsWrite] = new("Trigger", [
            new("a deployment sync or a restart",
                ["POST /deployments/{}/sync", "POST /deployments/{}/restart"]),
            new("delivery of an app's vaulted secrets into its cluster",
                ["POST /apps/{}/secrets/sync"]),
        ]),

        [LogsRead] = new("Read", [
            new("the contents of application logs, through log queries", ["GET /logs/query"]),
        ]),

        [OpsRead] = new("Read", [
            new("advisor findings", ["GET /advisor/findings"]),
            new("incidents", ["GET /incidents"]),
            // Carries what anonymous visitors wrote and any address they typed.
            new("in-app feedback, including messages and contact addresses", ["GET /feedback"]),
            new("synthetic checks and their uptime", ["GET /synthetic-checks"]),
            new("service level objectives and their error budgets", ["GET /slos"]),
            // Carries a sample of the log line, so this scope does reach log text for error lines.
            new("error issues, including a sample of each error's message", ["GET /error-issues"]),
            // customerId and customer per deployment, beside achieved availability and breach
            // state — the figure a support agreement is invoiced against.
            new("per-customer SLA attainment and error budgets", ["GET /error-budgets"]),
            new("available component upgrades", ["GET /upgrades"]),
            new("configuration drift", ["GET /drift"]),
            new("vulnerability and supply-chain reports", ["GET /supply-chain"]),
            // /cost and /cost/history carry customerId and customer names; /cost/history groups
            // BY CUSTOMER by default. This is the cloud bill, attributed.
            new("cost reports, including per-customer and per-environment attribution",
                ["GET /cost", "GET /cost/history", "GET /cost/months"]),
            new("rollout history", ["GET /rollouts"]),
            new("disaster-recovery readiness", ["GET /disaster-recovery"]),
        ]),

        [OpsWrite] = new("Acknowledge", [
            new("advisor findings", ["POST /advisor/findings/{}/acknowledge"]),
        ]),

        [ConfigRead] = new("Read", [
            new("the cluster price sheets", ["GET /cost-rates", "GET /cost-rates/{}"]),
        ]),

        // Same resource and same label as config:read — only the verb differs, which is why the
        // verb is asserted against the methods rather than left as prose.
        [ConfigWrite] = new("Change", [
            new("the cluster price sheets", ["PUT /cost-rates/{}", "DELETE /cost-rates/{}"]),
        ]),

        // ⚠️ Says what is in a ticket, because that is the privilege: customers' own words, which
        // for this agreement include patient data. Minting a token for "the ticket list" must not
        // read as minting one for titles only.
        [TicketsRead] = new("Read", [
            new("support tickets, including customers' correspondence and internal notes, which may contain personal and patient data",
                ["GET /tickets", "GET /tickets/{}"]),
        ]),

        [TicketsWrite] = new("Change", [
            new("support tickets' tags, links, reminders and notes, merge duplicates, and write notes the customer can see",
                [
                    "POST /tickets/{}/tags", "DELETE /tickets/{}/tags/{}",
                    "POST /tickets/{}/links", "DELETE /tickets/{}/links/{}",
                    "POST /tickets/{}/merge",
                    "PUT /tickets/{}/reminder", "DELETE /tickets/{}/reminder",
                    "POST /tickets/{}/notes",
                ]),
        ]),

        // ⚠️ This scope is NOT confined to the token's tenant, and it is the one grant on this
        // form which is not. ScimUserService queries db.ApplicationUsers with no tenant
        // predicate and no SCIM method accepts a tenant id, because ApplicationUser has no
        // tenant column at all — tenancy lives in TenantMembership. So a token minted here can
        // read, rename, disable and delete users of every other tenant, platform administrators
        // among them. See docs/api-scope-coverage.md; whether that reach is intended is an open
        // question for the product owner, but the label has to say so either way.
        [ScimProvision] = new("Read, create, rename, disable and delete", [
            new("every user account in this installation, including users of other tenants",
                [
                    "GET /Users", "GET /Users/{}", "POST /Users",
                    "PUT /Users/{}", "PATCH /Users/{}", "DELETE /Users/{}",
                ]),
            new("SCIM's discovery document", ["GET /ServiceProviderConfig"]),
        ]),
    };

    /// <summary>What the scope grants, or null for a scope this build does not know.</summary>
    public static ApiScopeGrant? Grant(string scope) =>
        Grants.TryGetValue(scope, out ApiScopeGrant? grant) ? grant : null;

    /// <summary>
    /// The sentence shown beside the scope's checkbox in the token-creation UI, rendered from
    /// <see cref="Grant"/> rather than written out.
    /// </summary>
    public static string Describe(string scope) => Grant(scope)?.Describe() ?? scope;

    /// <summary>True when the scope is one this build understands.</summary>
    public static bool IsKnown(string scope) => All.Contains(scope);

    /// <summary>
    /// Parses a stored scope string. Unknown scopes are dropped rather than kept:
    /// a scope this build does not understand cannot be enforced, so treating it as
    /// held would grant access no check governs.
    /// </summary>
    public static HashSet<string> Parse(string? scopes) =>
        scopes is null
            ? []
            : [.. scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Where(IsKnown)];

    public static string Serialize(IEnumerable<string> scopes) =>
        string.Join(' ', scopes.Where(IsKnown).Distinct().OrderBy(s => Array.IndexOf(AllScopes, s)));
}
