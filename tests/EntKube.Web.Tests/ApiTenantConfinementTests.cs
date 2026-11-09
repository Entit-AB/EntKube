using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.OpenApi;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>How far an operation's reads and writes reach.</summary>
public enum TenantReach
{
    /// <summary>
    /// Every row it reads or writes belongs to the tenant named by the bearer token, enforced by a
    /// predicate derived from <c>GetApiPrincipal()!.TenantId</c>.
    /// </summary>
    ConfinedToTheTokenTenant,

    /// <summary>
    /// It reaches rows belonging to other tenants. ⚠️ Not a category anything should be added to
    /// without the decision being made deliberately — see the reason each one carries.
    /// </summary>
    InstallationWide,

    /// <summary>
    /// It touches no tenant-owned data at all — a static document, or a statement about the token
    /// itself. Distinct from "confined" because there is no predicate to point at, and a reader
    /// who found "confined" here would go looking for one.
    /// </summary>
    NoTenantData,
}

/// <summary>
/// Whether the API keeps the promise it makes about itself.
///
/// <para><c>PublicApiEndpoints.cs:11-22</c> says: <i>"Every route is authenticated by a scoped
/// ApiToken and is implicitly scoped to that token's tenant: no route accepts a tenant id from the
/// caller, so a token cannot be pointed at another tenant's data by changing a parameter. That is
/// the single most important property of this file."</i> Nothing tested it.</para>
///
/// <para><b>Measured, operation by operation, with every failure independently attacked.</b> All
/// 24 operations under <c>/api/v1</c> hold it: each derives the tenant only from the principal and
/// passes it as a predicate, and each of the eight that take an entity id from the caller ANDs the
/// tenant clause with the id clause, so naming another tenant's row returns 404 rather than the
/// row. The six user routes under <c>/scim/v2</c> do not hold it at all.</para>
///
/// <para>⚠️ <b>There is no global tenant filter in this codebase</b> — no <c>HasQueryFilter</c>
/// anywhere in <c>src/</c>, and the only EF interceptor is the kubeconfig one. Confinement is
/// per-query, which is why it has to be established per operation and cannot be inferred from the
/// model. It is also why this file is a declaration table rather than a scan: there is no single
/// mechanism a scan could look for.</para>
///
/// <para>⭐ <b>So reach is declared, with no default.</b> Every operation the document describes
/// must appear in <see cref="Reaches"/> or
/// <see cref="Every_operation_declares_how_far_it_reaches"/> fails. That is the same shape as
/// <c>ChangeIntent</c> on the exec verbs and tenant declaration on the sweeps, and for the same
/// reason: the API is about to grow — 77 of the product's capabilities have no route yet — and a
/// defaulted answer lets each new route say nothing while looking considered.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ApiTenantConfinementTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Every operation, and how far it reaches. Verified by reading each handler into the service
    /// method it calls — not from the path, and not from the handler alone.
    ///
    /// <para>⚠️ Paths keep their route-group prefix here, unlike in
    /// <c>ApiScopeAdvertisementTests</c>. Stripping it would make <c>/api/v1/Users</c> and
    /// <c>/scim/v2/Users</c> the same key, and the whole point of this table is that those two
    /// groups do not behave alike.</para>
    /// </summary>
    private static readonly Dictionary<string, (TenantReach Reach, string Why)> Reaches =
        new(StringComparer.Ordinal)
        {
            ["GET /api/v1/whoami"] =
                (TenantReach.NoTenantData, "Reports the calling token's own tenant and scopes."),

            ["GET /api/v1/clusters"] =
                (TenantReach.ConfinedToTheTokenTenant, "FleetApi.Scoped: Where(c => c.TenantId == tenantId)."),
            ["GET /api/v1/clusters/{clusterId}/components"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "The tenant clause is ANDed with the id clause in one query, and the component "
                 + "read carries its own predicate through Cluster.TenantId, so deleting the 404 "
                 + "guard would yield an empty list rather than another tenant's components."),

            ["GET /api/v1/apps"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate in the delivery service."),
            ["GET /api/v1/deployments"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; the optional appId filter is ANDed with it."),
            ["POST /api/v1/deployments/{deploymentId}/sync"] =
                (TenantReach.ConfinedToTheTokenTenant, "The deployment is resolved tenant-first before the sync runs."),
            ["POST /api/v1/deployments/{deploymentId}/restart"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "The deployment is resolved through the tenant-scoped delivery query before the "
                 + "restart, and the workload name is passed as an SDK argument rather than "
                 + "interpolated into a command."),
            ["POST /api/v1/apps/{appId}/secrets/sync"] =
                (TenantReach.ConfinedToTheTokenTenant, "The app is resolved tenant-first before any secret is read or pushed."),

            ["GET /api/v1/advisor/findings"] =
                (TenantReach.ConfinedToTheTokenTenant, "Findings are computed for the token's tenant."),
            // The handler's own comment says why the predicate is indirect: an incident carries
            // no TenantId, it belongs to a cluster and the cluster belongs to the tenant.
            ["GET /api/v1/incidents"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "Filtered through the cluster, because an incident has no tenant column of its "
                 + "own; the contract applies that once rather than each route repeating it."),
            // ⚠️ The reason first written here was wrong, and the verdict was right for a
            // different reason than the one given. AdvisorStateService.MutateAsync looks up
            // (TenantId == tenantId && FindingKey == key) and INSERTS a row stamped with the
            // caller's tenant when nothing matches — so the id is never joined to the tenant's
            // computed finding set, and an arbitrary key creates a junk row rather than a 404.
            // Confined because the row carries the caller's own tenant either way.
            ["POST /api/v1/advisor/findings/{findingId}/acknowledge"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "The state row is written with the caller's own tenant id; an unknown finding "
                 + "key inserts a row for that tenant rather than reaching another's."),
            ["GET /api/v1/feedback"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "FeedbackService.ListAsync filters on the token's tenant; the status query "
                 + "parameter narrows within it and no id is accepted."),
            // ⚠️ Takes a clusterId, and the log layer does not refuse a foreign one, so the route's
            // own IFleetApi.GetClusterAsync(tenantId, clusterId) is what confines this.
            ["GET /api/v1/logs/query"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "The cluster is looked up under the token's tenant first, and a foreign id is a 404."),
            ["GET /api/v1/error-issues"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "ErrorTrackingService.ListAsync filters on the token's tenant; the status query "
                 + "parameter narrows within it and no id is accepted."),
            ["GET /api/v1/slos"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; no caller-supplied id."),
            ["GET /api/v1/synthetic-checks"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; no caller-supplied id."),
            ["GET /api/v1/error-budgets"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; no caller-supplied id."),
            ["GET /api/v1/upgrades"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; no route, query or body parameter at all."),
            ["GET /api/v1/rollouts"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "Rollout judgements are read under a tenant predicate; the route accepts no "
                 + "parameter a caller could redirect."),

            // ⚠️ These three read a process-wide *ScanCache singleton rather than the database,
            // which is the shape that would leak across tenants if the cache were keyed wrongly.
            // Checked specifically: the lookup is by the principal's tenant.
            ["GET /api/v1/drift"] =
                (TenantReach.ConfinedToTheTokenTenant, "DriftScanCache is read by the principal's tenant id."),
            ["GET /api/v1/supply-chain"] =
                (TenantReach.ConfinedToTheTokenTenant, "SupplyChainScanCache is read by the principal's tenant id."),
            ["GET /api/v1/disaster-recovery"] =
                (TenantReach.ConfinedToTheTokenTenant, "DrScanCache is read by the principal's tenant id."),

            ["GET /api/v1/cost"] =
                (TenantReach.ConfinedToTheTokenTenant, "CostScanCache is read by the principal's tenant id."),
            // ⚠️ Three caller-supplied ids, not one. The reason first written here named only
            // customerId; the route also takes appId and clusterId, and all three are ANDed onto
            // a query already carrying e.TenantId == tenantId. A reason that names one of three
            // filters is how the fourth gets added unchecked.
            ["GET /api/v1/cost/history"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "Tenant predicate in SQL; the optional customerId, appId and clusterId filters "
                 + "are each ANDed onto it, and the side queries clusterId reaches keep their own."),
            ["GET /api/v1/cost/months"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "Ledger rows are summed under e.TenantId == tenantId, and the route passes "
                 + "customerId: null rather than accepting one."),

            ["GET /api/v1/cost-rates"] =
                (TenantReach.ConfinedToTheTokenTenant, "Rates are selected through the tenant's own clusters."),
            ["GET /api/v1/cost-rates/{clusterId}"] =
                (TenantReach.ConfinedToTheTokenTenant, "Ownership of clusterId is established against the token's tenant first."),
            ["PUT /api/v1/cost-rates/{clusterId}"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "PatchAsync refuses on AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId) "
                 + "before it touches the row, so the write cannot reach a foreign cluster."),
            ["DELETE /api/v1/cost-rates/{clusterId}"] =
                (TenantReach.ConfinedToTheTokenTenant,
                 "The delete is scoped through Cluster.TenantId, so a foreign cluster id matches "
                 + "no row rather than deleting one."),

            ["GET /scim/v2/ServiceProviderConfig"] =
                (TenantReach.NoTenantData, "A static SCIM capability document; no data of any tenant's."),

            // ⚠️ The exception, and the whole reason this file exists. Six routes, all of them.
            // ScimUserService queries IdentityDbContext.ApplicationUsers with no tenant predicate
            // and no SCIM method accepts a tenant id — it cannot, because ApplicationUser has no
            // tenant column; tenancy lives in TenantMembership. ScimEndpoints never calls
            // GetApiPrincipal at all. (An earlier draft cited docs/api-scope-coverage.md here;
            // that file lands with a different branch, so the reason is stated in full above
            // rather than pointed at. A dangling pointer is how an exemption loses its reason.)
            ["GET /scim/v2/Users"] =
                (TenantReach.InstallationWide,
                 "Enumerates every account in the installation — sign-in name, email, enabled "
                 + "flag — 200 at a time with pagination. No tenant predicate."),
            ["GET /scim/v2/Users/{id}"] =
                (TenantReach.InstallationWide, "Fetches any account by id, with no tenant check on the row."),
            ["POST /scim/v2/Users"] =
                (TenantReach.InstallationWide, "Creates an installation-wide account with no membership in the calling tenant."),
            ["PUT /scim/v2/Users/{id}"] =
                (TenantReach.InstallationWide, "Replaces any account by id, with no tenant check on the row."),
            ["PATCH /scim/v2/Users/{id}"] =
                (TenantReach.InstallationWide,
                 "Deprovisions any account by id. The lockout is immediate and stamps the security "
                 + "stamp, so it signs the user out of every session and keeps them out."),
            ["DELETE /scim/v2/Users/{id}"] =
                (TenantReach.InstallationWide, "Deletes (locks out) any account by id, with no tenant check on the row."),
        };

    /// <summary>
    /// Names a caller-supplied parameter would use to point a request at a tenant. Matched as a
    /// substring, case-insensitively, so <c>tenantId</c>, <c>tenant_slug</c> and <c>TenantSlug</c>
    /// are all caught.
    /// </summary>
    private static readonly string[] TenantParameterNames = ["tenant"];

    /// <summary>
    /// How many times <c>PublicApiEndpoints.cs</c> reads the API principal: once per /api/v1
    /// operation. Measured, not bounded — see the assertion for why a bound was not enough.
    /// </summary>
    private const int ApiV1PrincipalReads = 29;

    private IReadOnlyList<string> DocumentedOperations()
    {
        fixture.GenerationFailure.Should().BeNull(
            "every assertion below reads the generated document, so a generation failure must be "
            + "reported as itself rather than as an empty document that silently passes");

        return
        [
            .. from operation in fixture.Operations
               select $"{operation.Method} {operation.Path}"
        ];
    }

    /// <summary>
    /// ⚠️ The measure, not the finding: a comparison against an empty document passes every
    /// assertion below.
    /// </summary>
    [Fact]
    public void Both_sides_of_the_comparison_were_actually_found()
    {
        DocumentedOperations().Should().HaveCountGreaterThan(20,
            "the API had 31 operations when this was written");

        Reaches.Values.Select(v => v.Reach).Distinct().Should().HaveCountGreaterThan(1,
            "a table where every operation carries the same verdict is not a measurement");

        // ⚠️ The parameter collection itself, not just the operations. If generation stopped
        // emitting parameters, No_operation_accepts_a_tenant_from_the_caller would iterate
        // nothing and pass on an API with eight route-parameterised operations.
        IReadOnlyList<(string Operation, string Source, string Name)> supplied = CallerSuppliedNames();

        supplied.Should().Contain(n => n.Source == "Path",
            "eight operations take a route parameter, so none being visible means the check is "
            + "passing because it inspected nothing");

        supplied.Should().Contain(n => n.Source == "Query", "several take a query parameter; same reason");
    }

    /// <summary>
    /// The composition of the table, pinned.
    ///
    /// <para>⚠️ <b>Review found this file's weakest point by replay, and it was here.</b> The
    /// assertions pinned the <see cref="TenantReach.InstallationWide"/> set and the key set and
    /// nothing else, so 25 of 31 verdicts could be changed to a wrong value with every test green.
    /// <see cref="TenantReach.NoTenantData"/> was the escape hatch: all 23 confined rows could be
    /// relabelled as touching no tenant data, and
    /// <see cref="Both_sides_of_the_comparison_were_actually_found"/> would still see two distinct
    /// verdicts and pass. Two is small enough to name, so it is named.</para>
    /// </summary>
    [Fact]
    public void The_two_operations_that_touch_no_tenant_data_are_named()
    {
        string[] noTenantData =
        [
            .. from entry in Reaches
               where entry.Value.Reach == TenantReach.NoTenantData
               orderby entry.Key
               select entry.Key
        ];

        noTenantData.Should().BeEquivalentTo([
            "GET /api/v1/whoami",
            "GET /scim/v2/ServiceProviderConfig",
        ], "relabelling a confined operation as touching no tenant data would otherwise hide it "
         + "from every assertion in this file");

        Reaches.Values.Count(v => v.Reach == TenantReach.ConfinedToTheTokenTenant)
            .Should().Be(28, "the three counts have to add up to the operation count, or a row "
                + "has gone missing from the arithmetic");
    }

    /// <summary>
    /// Every row says why — not only the ones that leave the tenant.
    ///
    /// <para>⚠️ The first version checked a reason's length only for
    /// <see cref="TenantReach.InstallationWide"/> rows and <c>continue</c>d past the other 25, so
    /// a confined row could carry the empty string. The reason is the only part of a confined row
    /// a reader can check against the code, which makes it the only part worth having.</para>
    /// </summary>
    [Fact]
    public void Every_operation_says_why_it_reaches_what_it_reaches()
    {
        foreach ((string operation, (TenantReach _, string why)) in Reaches)
        {
            why.Should().NotBeNullOrWhiteSpace("{0} has no stated reason", operation);
            why.Length.Should().BeGreaterThan(25,
                "{0}'s reason has to name the mechanism — a predicate, a cache key, a guard — "
                + "because a verdict with no mechanism cannot be checked against the code by the "
                + "next person to read it", operation);
        }
    }

    /// <summary>
    /// ⭐ Declare or refuse. A route added without an entry here fails, which is the point: the
    /// API is going to grow by many routes, and "did anyone check the tenancy" is not a question
    /// that survives being optional.
    /// </summary>
    [Fact]
    public void Every_operation_declares_how_far_it_reaches()
    {
        DocumentedOperations().Should().BeEquivalentTo(Reaches.Keys,
            "every operation must state whether it is confined to the token's tenant, and every "
            + "entry here must name an operation that still exists");
    }

    /// <summary>
    /// Every name by which a caller could supply a tenant, across every operation: path, query,
    /// header and cookie parameters, and the properties of a request body.
    ///
    /// <para>⚠️ The request body half is not decoration. An earlier version of this read only
    /// <c>operation.Parameters</c>, and a body property named <c>tenantId</c> is not a parameter —
    /// it lives under <c>requestBody.content[*].schema</c>. So the assertion's name claimed "from
    /// the caller" while checking only some of the ways a caller supplies things, which is the
    /// same overclaim this file exists to stop. <c>PUT /api/v1/cost-rates/{clusterId}</c> takes a
    /// body, so the gap was reachable rather than theoretical.</para>
    /// </summary>
    private IReadOnlyList<(string Operation, string Source, string Name)> CallerSuppliedNames()
    {
        fixture.GenerationFailure.Should().BeNull();

        OpenApiDocument document = fixture.Document!;
        List<(string, string, string)> names = [];

        foreach ((string path, IOpenApiPathItem item) in document.Paths ?? [])
        {
            foreach ((HttpMethod verb, OpenApiOperation operation) in item.Operations ?? [])
            {
                string where = $"{verb.Method.ToUpperInvariant()} {path}";

                // Operation-level AND path-item-level: a parameter declared on the path item
                // applies to every operation under it and is a legal OpenAPI location, so reading
                // only the operation's own list would leave a place for one to hide.
                foreach (IOpenApiParameter parameter in
                         (operation.Parameters ?? []).Concat(item.Parameters ?? []))
                {
                    names.Add((where, parameter.In?.ToString() ?? "parameter", parameter.Name ?? ""));
                }

                foreach ((string media, OpenApiMediaType content) in
                         operation.RequestBody?.Content ?? new Dictionary<string, OpenApiMediaType>())
                {
                    foreach (string property in PropertyNames(content.Schema))
                    {
                        names.Add((where, $"body({media})", property));
                    }
                }
            }
        }

        return names;
    }

    /// <summary>
    /// A schema's property names.
    ///
    /// <para>⚠️ <b>Measured, because the obvious guess was wrong.</b> The body schema for a record
    /// parameter IS an <c>OpenApiSchemaReference</c> rather than an inline object — probed, and it
    /// is — so this first resolved the reference through <c>Components.Schemas</c> by hand, with a
    /// comment saying that was what stopped the check inspecting air. Removing that branch changed
    /// nothing: in OpenAPI.NET v2 a reference proxies to its target, so <c>.Properties</c> on the
    /// reference returns the target's nine properties directly. The branch was dead code carrying
    /// a false rationale, which is worse than no branch.</para>
    ///
    /// <para>So the protection is <see cref="The_body_inspection_actually_reaches_a_body"/>, which
    /// names a property the body is known to carry. If a future version stops proxying, that test
    /// fails rather than this one quietly returning nothing.</para>
    /// </summary>
    private static IEnumerable<string> PropertyNames(IOpenApiSchema? schema) =>
        schema?.Properties?.Keys ?? (IEnumerable<string>)[];

    /// <summary>
    /// ⭐ The property the file claims about itself, asserted literally: no operation accepts a
    /// tenant from the caller — by parameter or by request body.
    /// </summary>
    [Fact]
    public void No_operation_accepts_a_tenant_from_the_caller()
    {
        foreach ((string operation, string source, string name) in CallerSuppliedNames())
        {
            TenantParameterNames.Should().NotContain(
                n => name.Contains(n, StringComparison.OrdinalIgnoreCase),
                "{0} accepts '{1}' as {2}, so a caller can point it at a tenant of their "
                + "choosing — which is the one thing the API promises cannot happen",
                operation, name, source);
        }
    }

    /// <summary>
    /// ⚠️ The measure for the body half. A <c>$ref</c> that failed to resolve, or a document that
    /// stopped describing request bodies, would make the check above inspect nothing and report a
    /// clean bill of health. This fails if no body property is found anywhere.
    /// </summary>
    [Fact]
    public void The_body_inspection_actually_reaches_a_body()
    {
        IReadOnlyList<(string Operation, string Source, string Name)> supplied = CallerSuppliedNames();

        supplied.Should().Contain(
            n => n.Source.StartsWith("body(", StringComparison.Ordinal),
            "PUT /api/v1/cost-rates/{clusterId} takes a CostRateInput body, so at least one body "
            + "property must be visible — otherwise the tenant check above is reading air");

        supplied.Should().Contain(
            n => n.Source.StartsWith("body(", StringComparison.Ordinal)
                 && n.Name.Equals("chargeIdleCapacity", StringComparison.OrdinalIgnoreCase),
            "naming one property the body is known to carry keeps this from passing on a body "
            + "schema that resolved to some other type's properties");
    }

    /// <summary>
    /// The exception is exactly the six SCIM user routes — not five, and not seven.
    /// </summary>
    [Fact]
    public void Only_the_scim_user_routes_reach_beyond_the_token_tenant()
    {
        string[] installationWide =
        [
            .. from entry in Reaches
               where entry.Value.Reach == TenantReach.InstallationWide
               orderby entry.Key
               select entry.Key
        ];

        installationWide.Should().BeEquivalentTo([
            "DELETE /scim/v2/Users/{id}",
            "GET /scim/v2/Users",
            "GET /scim/v2/Users/{id}",
            "PATCH /scim/v2/Users/{id}",
            "POST /scim/v2/Users",
            "PUT /scim/v2/Users/{id}",
        ], "an operation joining this set is a decision, not a refactor");
    }

    /// <summary>Every operation that leaves the tenant says what it reaches and how.</summary>
    [Fact]
    public void Every_operation_that_leaves_the_tenant_states_what_it_reaches()
    {
        foreach ((string operation, (TenantReach reach, string why)) in Reaches)
        {
            if (reach != TenantReach.InstallationWide)
            {
                continue;
            }

            why.Should().NotBeNullOrWhiteSpace();
            why.Length.Should().BeGreaterThan(40,
                "{0} reaches other tenants' data, and a one-word reason is how an exemption "
                + "outlives the thing that justified it", operation);
        }
    }

    /// <summary>
    /// ⚠️ The self-retiring half, and the one countable cross-check available: the file serving
    /// the unconfined routes does not read the token's tenant, and the file serving the confined
    /// ones does.
    ///
    /// <para>So when SCIM is given a tenant predicate this test FAILS, and whoever fixes it is
    /// forced to move those six rows out of <see cref="TenantReach.InstallationWide"/> rather than
    /// leaving a table that describes a defect no longer present. A reason nobody is obliged to
    /// revisit is how a wrong rationale stays green for a year.</para>
    ///
    /// <para>⚠️ Line comments are stripped before matching. <c>RazorGodContextRatchetTests</c>
    /// records a scan that counted a code COMMENT about its own subject and reported two
    /// components that inject nothing; the comment immediately above the SCIM rows in this very
    /// file names <c>GetApiPrincipal</c>, so without stripping, this assertion would be reading
    /// its own prose.</para>
    /// </summary>
    [Fact]
    public void The_file_serving_the_unconfined_routes_does_not_read_the_token_tenant()
    {
        static int PrincipalReads(string relativePath)
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
            // Block comments as well as line comments. With only `//` stripped, one
            // `/* ... */` naming .GetApiPrincipal() four times would buy four routes' worth of
            // headroom under the count below — review found exactly that.
            string stripped = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            stripped = Regex.Replace(stripped, @"//[^\n]*", "");

            return Regex.Matches(stripped, @"\.GetApiPrincipal\(\)").Count;
        }

        PrincipalReads(Path.Combine("src", "EntKube.Web", "Services", "Scim", "ScimEndpoints.cs"))
            .Should().Be(0,
                "the six SCIM user routes are banked as InstallationWide precisely because this "
                + "file never asks who is calling. If it now does, the bank above is describing a "
                + "defect that may have been fixed, and has to be re-measured rather than trusted");

        PrincipalReads(Path.Combine("src", "EntKube.Web", "Services", "PublicApi", "PublicApiEndpoints.cs"))
            .Should().Be(ApiV1PrincipalReads,
                "the 29 /api/v1 operations read the principal exactly once each — measured, not "
                + "assumed. This was BeGreaterThan(20), which left three routes free to stop "
                + "scoping themselves with nothing noticing, and the doc never stated the real "
                + "number so a reader could not see the slack was 12.5%. ⚠️ A legitimate "
                + "refactor — extracting a Tenant(ctx) funnel, or splitting this 987-line file — "
                + "fails here with confinement perfect. That is a prompt to re-measure, not a "
                + "defect to work around");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "charts")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
