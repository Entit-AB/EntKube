using FluentAssertions;
using Microsoft.OpenApi;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// What the generated document says each operation can ANSWER — not which routes exist
/// (<see cref="GeneratedOpenApiDocumentTests"/>) and not whether a client can reach them
/// (<c>CliEndpointParityTests</c>, <c>McpEndpointParityTests</c>), but whether the described
/// response set matches the responses the server actually produces.
///
/// <para><b>What was wrong.</b> Every one of the API's 31 operations was documented as answering
/// exactly one status: <c>200</c>, with no body schema. Meanwhile <c>ApiTokenAuthFilter</c>
/// answers <c>401</c> for a token that is missing, malformed, unknown, expired or revoked, and
/// <c>403</c> for one short a scope. A client generated from that document would model both
/// refusals as success and try to read a <c>ProblemDetails</c> as whatever the 200 was meant to
/// be — and #415's behavioural test had already PROVEN the 403 happens, so the document was
/// contradicting a tested fact.</para>
///
/// <para>⚠️ <b>Why this is read out of the generated document.</b> A source scan for
/// <c>ProducesProblem</c> would pass on a call that never reaches the document — which is exactly
/// how the self-description shipped as a 500 in the first place, with a green source-scan test
/// beside it. These assertions boot the app, generate the real document, and read the operations
/// out of it.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ApiDocumentedResponsesTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Operations answering a 2xx with no schema for it.
    ///
    /// <para><b>This is 0, and it is the end of the programme it measured.</b> Every operation on
    /// the API now describes the body it answers with, so a generated client has a named type per
    /// response and <c>docs/decomposition.md</c> item 8 is unblocked. The assertion stays, and it
    /// is now a floor rather than a ceiling: the number may not RISE. A route that ships
    /// answering a shape nothing can describe puts the generated clients back to returning
    /// <c>object</c> for it, and that is the regression this now exists to catch.</para>
    /// </summary>
    private const int UndescribedSuccessBodies = 0;

    /// <summary>
    /// Operations whose success is a 204 and so has nothing to describe: the public API's
    /// deletion, and SCIM's.
    ///
    /// <para>⚠️ Pinned precisely because a 204 is the one excuse from describing a body. Left
    /// unpinned, the count above could be driven to 0 by declaring success bodyless rather than
    /// by describing it.</para>
    ///
    /// <para>2 → 4: removing a ticket link and clearing a ticket's reminder are deletions with
    /// nothing to return. Removing a <em>tag</em> is not among them: it answers the tags left, which
    /// is what a caller adjusting a set needs to see.</para>
    /// </summary>
    private const int BodylessSuccesses = 4;

    /// <param name="StatedScope">
    /// Null when the document says any live token suffices — the case where the filter has
    /// nothing to refuse the caller for, so 403 is unreachable.
    /// </param>
    private sealed record Described(
        string Method,
        string Path,
        string? StatedScope,
        IReadOnlyList<string> Statuses,
        bool HasSuccessBodySchema,
        bool SuccessIsBodylessByDesign)
    {
        public override string ToString() => $"{Method} {Path} -> [{string.Join(",", Statuses)}]";
    }

    /// <summary>
    /// Joins the raw document (which carries the responses) to
    /// <see cref="GeneratedOpenApiDocumentFixture.Operations"/> (which carries the stated scope,
    /// parsed out of the rendered description) on method and path.
    /// </summary>
    private IReadOnlyList<Described> Describe()
    {
        fixture.GenerationFailure.Should().BeNull(
            "every assertion below reads the generated document, so a generation failure must be "
            + "reported as itself rather than as an empty document that silently passes");

        OpenApiDocument document = fixture.Document!;

        Dictionary<(string, string), string?> scopeByOperation = fixture.Operations.ToDictionary(
            o => (o.Method, o.Path), o => o.StatedScope);

        List<Described> described = [];

        foreach ((string path, IOpenApiPathItem item) in document.Paths ?? [])
        {
            foreach ((HttpMethod verb, OpenApiOperation operation) in item.Operations ?? [])
            {
                string method = verb.Method.ToUpperInvariant();

                // A 2xx whose content carries a schema in any media type. An entry with no
                // content at all, or content with a null schema, is NOT a described body — that
                // distinction is the whole point of the last test in this class.
                bool body = operation.Responses?.Any(r =>
                    r.Key.StartsWith("2", StringComparison.Ordinal)
                    && r.Value.Content?.Any(c => c.Value.Schema is not null) == true) == true;

                // A 204 is not an undescribed body; it is the absence of one, declared. Before
                // the handlers returned typed results the document said these answered 200 —
                // because a bare IResult tells it nothing and 200 is the assumption — so this
                // distinction could not even be drawn.
                bool bodyless = operation.Responses?.Keys.Any(k => k == "204") == true
                    && operation.Responses.Keys.All(k => !k.StartsWith("2", StringComparison.Ordinal) || k == "204");

                described.Add(new Described(
                    method,
                    path,
                    scopeByOperation.TryGetValue((method, path), out string? scope) ? scope : null,
                    operation.Responses is null ? [] : [.. operation.Responses.Keys],
                    body,
                    bodyless));
            }
        }

        return described;
    }

    /// <summary>
    /// ⚠️ The measure. Two of the tests below are conditional — one applies to operations that
    /// require a scope, the other to operations that do not — so each would pass vacuously if its
    /// class were empty. A scan that found no operations at all, or that classified every
    /// operation into one bucket, must fail as a BROKEN MEASUREMENT rather than as success.
    /// </summary>
    [Fact]
    public void Both_classes_of_operation_were_actually_found()
    {
        IReadOnlyList<Described> all = Describe();

        all.Should().NotBeEmpty("a document with no operations cannot be checked against anything");

        all.Should().HaveCount(
            fixture.Operations.Count,
            "this class walks the raw document while the rest of the suite walks the fixture's "
            + "parsed operations; if the two disagree, one of them is reading a different "
            + "document and every count in this file is suspect");

        all.Where(d => d.StatedScope is not null).Should().NotBeEmpty(
            "otherwise the 403 rule below is vacuous");

        all.Where(d => d.StatedScope is null).Should().NotBeEmpty(
            "otherwise the 'stays silent' half of the 403 rule below is vacuous — there would be "
            + "no operation for which 403 is unreachable, and the precision it checks would be "
            + "untested");
    }

    /// <summary>
    /// Every operation on this API goes through <c>RequireApiScope</c> — that is enforced by
    /// <c>GeneratedOpenApiDocumentTests</c> — so every operation can answer 401, and all of them
    /// must say so.
    /// </summary>
    [Fact]
    public void Every_operation_documents_the_401_it_can_answer()
    {
        List<Described> silent = [.. Describe().Where(d => !d.Statuses.Contains("401"))];

        silent.Should().BeEmpty(
            "a caller cannot handle an authentication failure it was not told about, and these "
            + "operations answer 401 whenever the bearer token is missing, malformed, unknown, "
            + "expired or revoked: "
            + string.Join("; ", silent.Select(d => d.ToString())));
    }

    /// <summary>
    /// ⚠️ Both directions, because over-documenting is its own defect: an operation that
    /// advertises a status it cannot return sends a client to write error handling that will never
    /// run, and the dead branch then looks like coverage.
    /// </summary>
    [Fact]
    public void An_operation_documents_403_exactly_when_a_scope_can_be_missing()
    {
        IReadOnlyList<Described> all = Describe();

        List<Described> hidingIt =
            [.. all.Where(d => d.StatedScope is not null && !d.Statuses.Contains("403"))];

        hidingIt.Should().BeEmpty(
            "each of these requires a named scope, so a token without it is refused with 403 — "
            + "which the document does not mention: "
            + string.Join("; ", hidingIt.Select(d => d.ToString())));

        List<Described> claimingIt =
            [.. all.Where(d => d.StatedScope is null && d.Statuses.Contains("403"))];

        claimingIt.Should().BeEmpty(
            "these are readable by any live token, so there is no scope for the filter to refuse "
            + "them for and 403 is unreachable: "
            + string.Join("; ", claimingIt.Select(d => d.ToString())));
    }

    /// <summary>
    /// ⚠️ <b>The debt, banked.</b> This replaces the pin that said NO operation described its
    /// success body — a pin written to fail the day the first response type was declared, which
    /// is what has now happened. It is a counting ratchet from here: the number may only go DOWN,
    /// and it going UP means a route shipped without a response type.
    ///
    /// <para><b>Why the number matters.</b> A generator fed an operation with no response schema
    /// emits a method returning <c>object</c>. Until this reaches zero, generating the clients
    /// (<c>docs/decomposition.md</c> item 8) produces something strictly worse than the
    /// hand-written clients it would replace. This count IS the remaining distance to item 8.</para>
    ///
    /// <para>The fix is per-route and cannot be centralised: <c>Produces&lt;T&gt;</c> has no
    /// <c>IEndpointConventionBuilder</c> overload, unlike the <c>ProducesProblem</c> calls that
    /// gave every route its 401 and 403 from one place. Each handler must return
    /// <c>TypedResults.Ok(record)</c> so its own return type carries the schema.</para>
    /// </summary>
    [Fact]
    public void The_operations_with_no_described_success_body_are_the_ones_still_banked()
    {
        List<Described> undescribed =
            [.. Describe().Where(d => !d.HasSuccessBodySchema && !d.SuccessIsBodylessByDesign)];

        // ⚠️ And they document NO success status at all, which is a regression this ratchet
        // exists to retire. Declaring the 401 and 403 in RequireApiScope gave ASP.NET Core
        // explicit response metadata for these endpoints, and that SUPPRESSES the 200 it
        // otherwise infers — so an operation with no typed success now documents only its two
        // ways of failing. It does answer 200. The fix is not to re-declare a bodyless 200, which
        // would be the same uninformative entry the document carried before; it is to give the
        // handler a return type, which produces a 200 WITH a schema. Each route this is done for
        // leaves this set, so this assertion retires itself.
        undescribed.Should().OnlyContain(
            d => !d.Statuses.Any(s => s.StartsWith("2", StringComparison.Ordinal)),
            "an operation with no typed success currently documents no 2xx whatsoever — if one of "
            + "these has gained a success status without gaining a schema, say which and why");

        undescribed.Should().HaveCount(
            UndescribedSuccessBodies,
            "this number is the remaining distance to a usable generated client. If you have just "
            + "typed a handler's response, LOWER it. If it has risen, a route shipped answering a "
            + "shape nothing can describe — give its handler a named record and return it through "
            + "TypedResults.Ok. Still undescribed: {0}",
            string.Join("; ", undescribed.Select(d => d.ToString())));
    }

    /// <summary>
    /// ⚠️ A described body must be a NAMED schema, not an inline one.
    ///
    /// <para>There is a shortcut that would retire the ratchet above without delivering what it
    /// is for: <c>TypedResults.Ok(new { … })</c> also gives the handler a return type, so the
    /// document gains a schema — but the type is a compiler-generated anonymous one, and a
    /// generator emits it as an unnamed or mangled class. The point of the exercise is a client
    /// whose methods return <c>ClusterRateResponse</c>, not
    /// <c>&lt;&gt;f__AnonymousType3_12</c>.</para>
    ///
    /// <para>So each described body must be a <c>$ref</c> to a component the document actually
    /// defines. That also catches a schema referencing a component that was never emitted, which
    /// would make the document unusable rather than merely ugly.</para>
    /// </summary>
    [Fact]
    public void A_described_success_body_is_a_named_schema_the_document_defines()
    {
        OpenApiDocument document = fixture.Document!;
        IReadOnlyList<Described> described = [.. Describe().Where(d => d.HasSuccessBodySchema)];

        described.Should().NotBeEmpty("otherwise this says nothing");

        List<string> problems = [];

        foreach ((string path, IOpenApiPathItem item) in document.Paths ?? [])
        {
            foreach ((HttpMethod verb, OpenApiOperation operation) in item.Operations ?? [])
            {
                string method = verb.Method.ToUpperInvariant();
                if (!described.Any(d => d.Method == method && d.Path == path)) continue;

                foreach ((string code, IOpenApiResponse response) in operation.Responses!)
                {
                    if (!code.StartsWith("2", StringComparison.Ordinal)) continue;

                    if (response.Content is null) continue;

                    foreach ((string media, OpenApiMediaType content) in response.Content)
                    {
                        // An array response names its component one level in, on `items`.
                        //
                        // ⚠️ HasFlag, not ==. JsonSchemaType is a [Flags] enum: Array is 64, Null
                        // is 1, and OpenAPI 3.1 writes a nullable array as type ["array","null"]
                        // — which is 65 and does NOT equal Array. An exact comparison would skip
                        // the descent for a nullable collection response, find the array schema
                        // where it expected a $ref, and report a correct handler as "inline, not
                        // a $ref". A false failure, and the next reader would have loosened the
                        // assertion to clear it.
                        IOpenApiSchema? schema = content.Schema;
                        if (schema?.Type?.HasFlag(JsonSchemaType.Array) == true) schema = schema.Items;

                        if (schema is not OpenApiSchemaReference { Reference.Id: { Length: > 0 } id })
                        {
                            problems.Add($"{method} {path} {code} {media}: inline, not a $ref");
                            continue;
                        }

                        if (document.Components?.Schemas?.ContainsKey(id) != true)
                            problems.Add($"{method} {path} {code} {media}: $ref to undefined '{id}'");
                    }
                }
            }
        }

        problems.Should().BeEmpty(
            "a generated client needs a named type per response, and an anonymous object produces "
            + "a mangled one: " + string.Join("; ", problems));
    }

    /// <summary>
    /// The refusals only particular routes can answer with.
    ///
    /// <para>⚠️ <b>A <c>ProblemHttpResult</c> in a handler's <c>Results&lt;…&gt;</c> union
    /// contributes NOTHING to the document.</b> So eighteen statuses across sixteen routes were
    /// reachable and undescribed: the <c>503</c> six sweep-backed reports answer before their
    /// first sweep has completed, the <c>422</c> eight action routes answer when the cluster
    /// refuses, and two <c>400</c>s. Each of those routes was described as only ever answering
    /// 200, 401 or 403, so a client generated from the document would deserialise a
    /// <c>ProblemDetails</c> into whatever the 200 was meant to be — the same defect the central
    /// 401 and 403 declarations were added to fix, one level further in.</para>
    ///
    /// <para>Pinned as a table rather than derived, because the alternative is scanning the
    /// handler source for status codes and that is what produced the wrong answer while this was
    /// being measured: a shared helper's <c>422</c> was attributed to the route registered above
    /// it, and <c>SyncAppSecrets</c>'s <c>207</c> to the route before that.</para>
    ///
    /// <para>⚠️ A <c>503</c> here is not an error in the usual sense — it is "no sweep has
    /// completed yet", which a client should retry rather than report. It has to be in the
    /// document for a client to tell it apart from a failure.</para>
    /// </summary>
    [Fact]
    public void The_routes_that_can_refuse_for_their_own_reasons_say_so()
    {
        Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
        {
            ["GET /api/v1/cost"] = ["503"],
            ["GET /api/v1/cost/history"] = ["400", "503"],
            ["GET /api/v1/cost/months"] = ["503"],
            ["GET /api/v1/disaster-recovery"] = ["503"],
            ["GET /api/v1/drift"] = ["503"],
            ["GET /api/v1/supply-chain"] = ["503"],
            ["GET /api/v1/logs/query"] = ["503"],
            ["GET /api/v1/clusters/{clusterId}/virtual-machines"] = ["422"],
            ["POST /api/v1/clusters/{clusterId}/virtual-machines/{ns}/{name}/migrate"] = ["422"],
            ["POST /api/v1/clusters/{clusterId}/virtual-machines/{ns}/{name}/pause"] = ["422"],
            ["POST /api/v1/clusters/{clusterId}/virtual-machines/{ns}/{name}/restart"] = ["422"],
            ["POST /api/v1/clusters/{clusterId}/virtual-machines/{ns}/{name}/start"] = ["422"],
            ["POST /api/v1/clusters/{clusterId}/virtual-machines/{ns}/{name}/stop"] = ["422"],
            ["POST /api/v1/clusters/{clusterId}/virtual-machines/{ns}/{name}/unpause"] = ["422"],
            ["POST /api/v1/deployments/{deploymentId}/restart"] = ["400", "422"],
            ["POST /api/v1/deployments/{deploymentId}/sync"] = ["422"],
            ["POST /api/v1/apps/{appId}/secrets/sync"] = ["207"],
        };

        Dictionary<string, Described> byOperation = Describe()
            .ToDictionary(d => $"{d.Method} {d.Path}", d => d);

        List<string> problems = [];

        foreach ((string operation, string[] statuses) in expected)
        {
            if (!byOperation.TryGetValue(operation, out Described? described))
            {
                problems.Add($"{operation}: not in the document at all");
                continue;
            }

            foreach (string status in statuses)
            {
                if (!described.Statuses.Contains(status))
                {
                    problems.Add($"{operation}: can answer {status} and does not say so");
                }
            }
        }

        problems.Should().BeEmpty(string.Join("; ", problems));

        // ⚠️ And the other direction. An operation that advertises a refusal it cannot produce
        // sends a client writing dead error handling, which is as wrong as hiding one — the 403
        // rule above is derived for exactly this reason.
        List<string> overclaimed =
            [.. from described in Describe()
                let key = $"{described.Method} {described.Path}"
                from status in described.Statuses
                where status is "422" or "503" or "207"
                where !expected.TryGetValue(key, out string[]? allowed) || !allowed.Contains(status)
                select $"{key} claims {status}"];

        overclaimed.Should().BeEmpty(
            "a route advertising a refusal it cannot answer is as wrong as one hiding a refusal "
            + "it can: " + string.Join("; ", overclaimed));
    }

    /// <summary>
    /// ⚠️ The measure for the count above. A scan that classified an operation into none of the
    /// three buckets — or into two — would make the banked number mean nothing, and it would
    /// drift silently because only one of the three is asserted.
    /// </summary>
    [Fact]
    public void Every_operation_falls_into_exactly_one_of_the_three_buckets()
    {
        IReadOnlyList<Described> all = Describe();

        all.Where(d => d.HasSuccessBodySchema && d.SuccessIsBodylessByDesign).Should().BeEmpty(
            "an operation cannot both describe a body and have none by design");

        int described = all.Count(d => d.HasSuccessBodySchema);
        int bodyless = all.Count(d => d.SuccessIsBodylessByDesign);
        int undescribed = all.Count(d => !d.HasSuccessBodySchema && !d.SuccessIsBodylessByDesign);

        (described + bodyless + undescribed).Should().Be(
            all.Count, "every operation must land in exactly one bucket");

        bodyless.Should().Be(
            BodylessSuccesses,
            "a 204 route is the only kind excused from describing a body, so the number of them "
            + "is pinned too — otherwise the excuse could be used to retire the debt without "
            + "describing anything");
    }

}
