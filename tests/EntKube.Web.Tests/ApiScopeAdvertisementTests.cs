using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether the sentence an operator reads before granting a scope matches what that scope
/// actually reaches.
///
/// <para><b>Why this is a security assertion and not a documentation one.</b>
/// <see cref="ApiScopes.Describe"/> is rendered as the label of the checkbox in
/// <c>ApiTokensTab.razor</c>'s token-creation form. It is not a doc comment an operator might
/// read; it is the only thing they read while deciding what to hand a third party. A label that
/// understates the grant produces consent nobody gave.</para>
///
/// <para><b>What was wrong.</b> All eight labels were hand-written prose, and measured against
/// the routes all eight were wrong. The two that mattered:</para>
///
/// <list type="bullet">
/// <item><c>apps:write</c> read "Trigger deployment syncs and restarts", and also granted
/// <c>POST /apps/{appId}/secrets/sync</c> — delivery of vaulted secret material into a live
/// cluster. A token minted so CI could restart a pod could push secrets, unadvertised.</item>
/// <item><c>ops:read</c> read "advisor findings, incidents, drift and vulnerability reports", and
/// granted eleven routes including <c>/cost</c>, <c>/cost/history</c> and <c>/cost/months</c> —
/// the cloud bill, attributed per customer.</item>
/// </list>
///
/// <para>⚠️ <b>Exactly what is and is not machine-checked here.</b> Claiming more than it does
/// would be the same defect one level up, so:</para>
///
/// <list type="bullet">
/// <item><b>Checked, exactly:</b> the set of operations each scope declares equals the set the
/// running app guards with that scope. Nothing hidden, nothing imagined, in either direction.</item>
/// <item><b>Checked:</b> a read-only scope's verb is "Read" and a mutating scope's verb is not.
/// That is the one prose error a machine can see.</item>
/// <item><b>NOT checked:</b> whether each label's wording fairly describes its operations. A
/// label reading "secret rotation status" beside <c>POST /apps/{}/secrets/sync</c> would pass.
/// Two named facts at the bottom pin the specific wording of the two findings above, and that is
/// a substring guard, not a general property. What the structure buys is that the wording sits
/// against an exact operation list, so a diff adding <c>DELETE</c> puts the sentence that must
/// cover it on the screen.</item>
/// </list>
///
/// <para>An earlier version of this file matched a declared path SEGMENT against the granted
/// routes. Review found three ways through it, all verified by replay: a new verb on an
/// already-declared segment was auto-advertised (<c>DELETE /advisor/findings/{}</c> under a label
/// saying "Acknowledge"); a parent prefix counted as served (<c>apps</c> advertised by
/// <c>apps:write</c> on the strength of <c>/apps/{}/secrets/sync</c>, when the API has no app
/// write route at all); and had the secrets route been mounted one segment over, at
/// <c>/deployments/{}/secrets/sync</c>, the general rule would have been green and only the
/// hardcoded substring would have fired. Set equality on (method, path) has none of that slack.</para>
///
/// <para>These read the booted app's generated document rather than scanning source, for the
/// reason <c>ApiDocumentedResponsesTests</c> records: a source scan passes on a call that never
/// reaches the surface it claims to describe.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ApiScopeAdvertisementTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Operations any live token can reach, which therefore belong to no scope and are advertised
    /// by none. Banked rather than derived so that making a route scopeless — widening who can
    /// reach it — cannot happen quietly.
    ///
    /// <para>⚠️ One, not two, and the difference is not a miscount. Source carries two
    /// <c>RequireApiScope()</c> calls with no scope argument: <c>GET /api/v1/whoami</c>, and
    /// <c>MapOpenApi</c>'s own <c>/api/v1/openapi.json</c> at <c>Program.cs:1102</c>. The second
    /// serves the document rather than being described by it, so it is absent from
    /// <see cref="GeneratedOpenApiDocumentFixture.Operations"/> by design — the fixture excludes
    /// it by exact path. This count is over the document, so it sees one.</para>
    /// </summary>
    private const int ScopelessOperations = 1;

    /// <summary>
    /// The route-group prefixes of the token-authenticated API, stripped so a declaration reads
    /// <c>GET /Users</c> rather than <c>GET /scim/v2/Users</c>.
    ///
    /// <para>⚠️ <see cref="Every_documented_path_is_under_a_prefix_this_test_knows"/> fails if a
    /// third group appears. Without that, a new prefix would leave its paths unstripped, they
    /// would match no declaration, and the failure would look like a missing label rather than
    /// like this list being out of date.</para>
    /// </summary>
    private static readonly string[] GroupPrefixes = ["/api/v1", "/scim/v2"];

    /// <summary>
    /// An operation as a scope declares it: method, then the path with its group prefix removed
    /// and every route parameter written <c>{}</c>. Parameter NAMES are dropped on purpose —
    /// renaming <c>{clusterId}</c> is not a change to what a token can reach.
    /// </summary>
    private static string Normalise(string method, string path)
    {
        foreach (string prefix in GroupPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                path = path[prefix.Length..];
                break;
            }
        }

        string[] segments =
        [
            .. from segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries)
               select segment.StartsWith('{') ? "{}" : segment
        ];

        return $"{method} /{string.Join('/', segments)}";
    }

    /// <summary>Scope to the operations the running app guards with it.</summary>
    private IReadOnlyDictionary<string, string[]> GrantedByScope()
    {
        fixture.GenerationFailure.Should().BeNull(
            "every assertion below reads the generated document, so a generation failure must be "
            + "reported as itself rather than as an empty document that silently passes");

        return fixture.Operations
            .Where(o => o.StatedScope is not null)
            .GroupBy(o => o.StatedScope!)
            .ToDictionary(g => g.Key, g => g.Select(o => Normalise(o.Method, o.Path)).ToArray());
    }

    /// <summary>
    /// ⚠️ The measure, not the finding. Every assertion below is a statement about a join between
    /// the scope table and the document; a join over nothing passes every one of them. This fails
    /// if either side of it goes empty, which is how a document that stopped being generated, or
    /// a scope table that stopped being read, would otherwise read as a clean bill of health.
    /// </summary>
    [Fact]
    public void Both_sides_of_the_comparison_were_actually_found()
    {
        IReadOnlyDictionary<string, string[]> granted = GrantedByScope();

        ApiScopes.All.Should().HaveCountGreaterThan(1);
        granted.Values.Sum(v => v.Length).Should().BeGreaterThan(20,
            "the API had 31 operations when this was written and all but the scopeless one state "
            + "a scope, so a handful would mean the document or the scope parse has gone blind");

        granted.Keys.Should().BeEquivalentTo(ApiScopes.All,
            "a scope the document never states is one no route enforces, and a scope the document "
            + "states that is not in the vocabulary cannot be granted by the token UI at all");
    }

    /// <summary>
    /// The prefix list this test strips with is complete. A third route group would otherwise
    /// make every one of its operations look merely unadvertised.
    /// </summary>
    [Fact]
    public void Every_documented_path_is_under_a_prefix_this_test_knows()
    {
        fixture.GenerationFailure.Should().BeNull();

        foreach (DocumentedOperation operation in fixture.Operations)
        {
            GroupPrefixes.Should().Contain(
                p => operation.Path.StartsWith(p, StringComparison.Ordinal),
                "{0} is in the document but under no route group this test knows how to strip",
                operation.Path);
        }
    }

    /// <summary>
    /// A scope offered in the token-creation UI that no route requires grants nothing. Ticking it
    /// looks like access and is not — and an operator who believed it would be the last to find
    /// out, because the failure mode is a 403 on a route they expected to reach.
    /// </summary>
    [Fact]
    public void Every_scope_offered_to_an_operator_is_required_by_some_operation()
    {
        IReadOnlyDictionary<string, string[]> granted = GrantedByScope();

        foreach (string scope in ApiScopes.All)
        {
            granted.Should().ContainKey(scope,
                "{0} is offered as a checkbox in the token-creation form, so some route must "
                + "require it", scope);
        }
    }

    /// <summary>
    /// ⭐ The assertion the rest of the file exists for, in both directions at once: the
    /// operations a scope declares are exactly the operations it grants.
    ///
    /// <para>Declaring too few is the hidden grant — a route reachable with a scope whose label
    /// never mentions it, which is how <c>POST /apps/{}/secrets/sync</c> came to be reachable
    /// with a token advertised as restarting pods, and how seven more routes including the cost
    /// reports were reachable with <c>ops:read</c>.</para>
    ///
    /// <para>Declaring too many is the over-promise — the direction that found <c>fleet:read</c>
    /// advertising nodes and workloads, <c>apps:read</c> advertising environments,
    /// <c>ops:write</c> advertising opening and resolving incidents, and <c>config:read</c>
    /// advertising rollout policies. Four resources with no route at all: not aspirational
    /// wording, a promise the server answers with 404.</para>
    /// </summary>
    [Fact]
    public void Each_scope_declares_exactly_the_operations_it_grants()
    {
        IReadOnlyDictionary<string, string[]> granted = GrantedByScope();

        foreach (string scope in ApiScopes.All)
        {
            ApiScopeGrant grant = ApiScopes.Grant(scope)!;

            grant.Operations.Should().BeEquivalentTo(granted[scope],
                "{0}'s label is built from the operations it declares, so that list has to be "
                + "exactly what the app guards with {0} — anything missing is a grant the "
                + "operator was not told about, anything extra is one the server does not honour",
                scope);
        }
    }

    /// <summary>
    /// ⭐ The verb, checked against the methods rather than against itself.
    ///
    /// <para>The previous version asserted <c>Describe(scope).StartsWith(grant.Action)</c>, which
    /// is tautological: <c>Describe</c> is <c>$"{Action} {labels}"</c>. So <c>config:write</c>
    /// could read "Read the cluster price sheets" while granting <c>PUT</c> and <c>DELETE</c>,
    /// with every assertion green. It matters most for exactly that pair: <c>config:read</c> and
    /// <c>config:write</c> declare the same resource and the same label, so the verb is the only
    /// thing that distinguishes their sentences.</para>
    /// </summary>
    [Fact]
    public void A_scope_that_can_only_read_says_Read_and_one_that_can_change_does_not()
    {
        IReadOnlyDictionary<string, string[]> granted = GrantedByScope();

        foreach (string scope in ApiScopes.All)
        {
            ApiScopeGrant grant = ApiScopes.Grant(scope)!;

            bool grantsAWrite = granted[scope]
                .Any(o => !o.StartsWith("GET ", StringComparison.Ordinal));

            grant.Mutates.Should().Be(grantsAWrite,
                "{0} derives Mutates from its declared operations, so it must agree with the "
                + "methods the app actually guards with {0}", scope);

            if (grantsAWrite)
            {
                grant.Action.Should().NotBe(ApiScopeGrant.ReadOnlyAction,
                    "{0} grants [{1}], so its sentence cannot begin \"{2}\"",
                    scope,
                    string.Join(", ", granted[scope].Where(o => !o.StartsWith("GET ", StringComparison.Ordinal))),
                    ApiScopeGrant.ReadOnlyAction);
            }
            else
            {
                grant.Action.Should().Be(ApiScopeGrant.ReadOnlyAction,
                    "{0} grants nothing but GETs, so a verb implying otherwise overstates it",
                    scope);
            }
        }
    }

    /// <summary>
    /// Every operation is either advertised by a scope or reachable with any live token — no
    /// third case. The scopeless count is banked because a route becoming scopeless widens who
    /// can reach it, and that is not a change to make by accident.
    ///
    /// <para>⚠️ "Any live token" and "no token at all" are distinguished rather than counted
    /// together. <c>PublicApiOpenApi</c> omits the scope line for both, so a bare count of
    /// operations stating no scope would let an UNAUTHENTICATED route in under the same number
    /// the moment this bank was legitimately raised to two.</para>
    /// </summary>
    [Fact]
    public void An_operation_states_a_scope_or_is_reachable_with_any_live_token()
    {
        fixture.GenerationFailure.Should().BeNull();

        DocumentedOperation[] scopeless =
            [.. fixture.Operations.Where(o => o.StatedScope is null)];

        scopeless.Should().OnlyContain(o => o.ClaimsAnyTokenWillDo,
            "an operation that states no scope must still say a token is needed; one that says "
            + "nothing at all is unauthenticated");

        scopeless.Should().HaveCount(ScopelessOperations,
            "a route that states no scope is readable by every token this installation has "
            + "ever issued, so the set of them is banked rather than derived");
    }

    /// <summary>
    /// The rendering carries every resource through to the sentence.
    ///
    /// <para>⚠️ Near-tautological and kept deliberately: <c>Describe</c> joins these very labels,
    /// so this cannot catch a wrong label. What it does catch is the join dropping one — which is
    /// separate code from the data, and the reason the semicolon branch below exists at all.</para>
    /// </summary>
    [Fact]
    public void The_sentence_an_operator_reads_names_every_resource_in_the_grant()
    {
        foreach (string scope in ApiScopes.All)
        {
            ApiScopeGrant grant = ApiScopes.Grant(scope)!;
            string sentence = ApiScopes.Describe(scope);

            foreach (ApiScopeResource resource in grant.Resources)
            {
                sentence.Should().Contain(resource.Label,
                    "{0}'s label is rendered from its resource list, so every resource in that "
                    + "list has to appear in the sentence the operator reads", scope);
            }
        }
    }

    /// <summary>
    /// The rendering switches to semicolons once a label carries a comma, because "A, B and C"
    /// would otherwise attach its final "and" to the inside of the preceding clause — reading as
    /// though users of other tenants and the discovery document were one list.
    /// </summary>
    [Fact]
    public void A_label_containing_a_clause_is_not_run_together_with_the_next_one()
    {
        ApiScopes.Describe(ApiScopes.ScimProvision)
            .Should().NotContain("other tenants and SCIM's discovery document");

        new ApiScopeGrant("Read", [
            new ApiScopeResource("one thing, with an aside", ["GET /a"]),
            new ApiScopeResource("another", ["GET /b"]),
        ]).Describe().Should().Be("Read one thing, with an aside; another");

        new ApiScopeGrant("Read", [
            new ApiScopeResource("one", ["GET /a"]),
            new ApiScopeResource("two", ["GET /b"]),
            new ApiScopeResource("three", ["GET /c"]),
        ]).Describe().Should().Be("Read one, two and three");
    }

    /// <summary>
    /// The defect, as its own named regression, and deliberately specific about the wording.
    ///
    /// <para>A bare <c>Contain("secret")</c> would survive a label rewritten to "secret rotation
    /// status" — which inverts the meaning while keeping the substring. Asserting the phrase that
    /// carries the consequence is the most a substring guard can do.</para>
    /// </summary>
    [Fact]
    public void A_token_that_can_restart_a_pod_is_told_it_can_also_deliver_secrets()
    {
        GrantedByScope()[ApiScopes.AppsWrite].Should().Contain("POST /apps/{}/secrets/sync",
            "the route whose advertisement was missing must still be the one apps:write grants, "
            + "or this regression test is asserting nothing");

        ApiScopes.Describe(ApiScopes.AppsWrite).Should().Contain("vaulted secrets into its cluster");
    }

    /// <summary>
    /// The second defect, likewise. An operator granting a monitoring integration ops:read is
    /// handing over the cloud bill with per-customer attribution, and the form has to say so —
    /// so this asserts the attribution, not merely the word "cost".
    /// </summary>
    [Fact]
    public void A_token_for_incident_dashboards_is_told_it_can_read_the_cloud_bill()
    {
        GrantedByScope()[ApiScopes.OpsRead].Should().Contain("GET /cost");

        ApiScopes.Describe(ApiScopes.OpsRead).Should().Contain("per-customer");
    }

    /// <summary>
    /// ⚠️ The one scope that leaves the tenant, named as such.
    ///
    /// <para><c>ScimUserService</c> queries <c>db.ApplicationUsers</c> with no tenant predicate
    /// and no SCIM method accepts a tenant id — <c>ApplicationUser</c> has no tenant column,
    /// because tenancy lives in <c>TenantMembership</c>. So a <c>scim:provision</c> token minted
    /// by one tenant can read, rename, disable and delete users of every other tenant. The
    /// token-creation form carried the sentence "it can never reach another tenant's data"
    /// directly above the checkbox that grants it.</para>
    ///
    /// <para>This asserts the wording only. Whether that reach should exist is an authorization
    /// question for the product owner, and the two changes that would settle it — a tenant
    /// predicate on the SCIM queries, or a restriction on who may grant the scope — are not
    /// things a label can stand in for. Written down here so that if the reach is later removed,
    /// this test fails and forces the label back.</para>
    /// </summary>
    [Fact]
    public void The_one_scope_that_is_not_confined_to_a_tenant_says_so()
    {
        string sentence = ApiScopes.Describe(ApiScopes.ScimProvision);

        sentence.Should().Contain("installation");
        sentence.Should().Contain("other tenants");
    }
}
