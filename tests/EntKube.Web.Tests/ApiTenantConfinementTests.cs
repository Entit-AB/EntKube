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
                (TenantReach.ConfinedToTheTokenTenant, "As above."),
            ["POST /api/v1/apps/{appId}/secrets/sync"] =
                (TenantReach.ConfinedToTheTokenTenant, "The app is resolved tenant-first before any secret is read or pushed."),

            ["GET /api/v1/advisor/findings"] =
                (TenantReach.ConfinedToTheTokenTenant, "Findings are computed for the token's tenant."),
            ["GET /api/v1/incidents"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate."),
            ["POST /api/v1/advisor/findings/{findingId}/acknowledge"] =
                (TenantReach.ConfinedToTheTokenTenant, "The finding id is only ever used within the tenant's own computed set."),
            ["GET /api/v1/error-budgets"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; no caller-supplied id."),
            ["GET /api/v1/upgrades"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; no route, query or body parameter at all."),
            ["GET /api/v1/rollouts"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate."),

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
            ["GET /api/v1/cost/history"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate; the optional customerId filter is ANDed with it."),
            ["GET /api/v1/cost/months"] =
                (TenantReach.ConfinedToTheTokenTenant, "Tenant predicate."),

            ["GET /api/v1/cost-rates"] =
                (TenantReach.ConfinedToTheTokenTenant, "Rates are selected through the tenant's own clusters."),
            ["GET /api/v1/cost-rates/{clusterId}"] =
                (TenantReach.ConfinedToTheTokenTenant, "Ownership of clusterId is established against the token's tenant first."),
            ["PUT /api/v1/cost-rates/{clusterId}"] =
                (TenantReach.ConfinedToTheTokenTenant, "As above, before the write."),
            ["DELETE /api/v1/cost-rates/{clusterId}"] =
                (TenantReach.ConfinedToTheTokenTenant, "As above, before the delete."),

            ["GET /scim/v2/ServiceProviderConfig"] =
                (TenantReach.NoTenantData, "A static SCIM capability document; no data of any tenant's."),

            // ⚠️ The exception, and the whole reason this file exists. Six routes, all of them.
            // ScimUserService queries IdentityDbContext.ApplicationUsers with no tenant predicate
            // and no SCIM method accepts a tenant id — it cannot, because ApplicationUser has no
            // tenant column; tenancy lives in TenantMembership. ScimEndpoints never calls
            // GetApiPrincipal at all. See docs/api-scope-coverage.md.
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

                foreach (IOpenApiParameter parameter in operation.Parameters ?? [])
                {
                    names.Add((where, parameter.In?.ToString() ?? "parameter", parameter.Name ?? ""));
                }

                foreach ((string media, OpenApiMediaType content) in
                         operation.RequestBody?.Content ?? new Dictionary<string, OpenApiMediaType>())
                {
                    foreach (string property in PropertyNames(document, content.Schema))
                    {
                        names.Add((where, $"body({media})", property));
                    }
                }
            }
        }

        return names;
    }

    /// <summary>
    /// A schema's property names, following a component reference.
    ///
    /// <para>⚠️ A body schema generated for a record parameter is a <c>$ref</c> to a named
    /// component, not an inline object, so reading <c>.Properties</c> off the reference would
    /// return nothing and the body check would silently inspect air.
    /// <see cref="The_body_inspection_actually_reaches_a_body"/> is what stops that passing as
    /// a clean result.</para>
    /// </summary>
    private static IEnumerable<string> PropertyNames(OpenApiDocument document, IOpenApiSchema? schema)
    {
        if (schema is null)
        {
            return [];
        }

        if (schema is OpenApiSchemaReference reference
            && document.Components?.Schemas?.TryGetValue(reference.Reference.Id ?? "", out IOpenApiSchema? target) == true)
        {
            return target.Properties?.Keys ?? (IEnumerable<string>)[];
        }

        return schema.Properties?.Keys ?? (IEnumerable<string>)[];
    }

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
               orderby entry.Key, StringComparer.Ordinal
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
            string withoutLineComments = Regex.Replace(source, @"//[^\n]*", "");
            return Regex.Matches(withoutLineComments, @"\.GetApiPrincipal\(\)").Count;
        }

        PrincipalReads(Path.Combine("src", "EntKube.Web", "Services", "Scim", "ScimEndpoints.cs"))
            .Should().Be(0,
                "the six SCIM user routes are banked as InstallationWide precisely because this "
                + "file never asks who is calling. If it now does, the bank above is describing a "
                + "defect that may have been fixed, and has to be re-measured rather than trusted");

        PrincipalReads(Path.Combine("src", "EntKube.Web", "Services", "PublicApi", "PublicApiEndpoints.cs"))
            .Should().BeGreaterThan(20,
                "every /api/v1 route derives its tenant from the principal, so this file reads it "
                + "about once per route; a sharp drop means routes stopped scoping themselves");
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
