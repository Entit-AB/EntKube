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
/// <para><b>What was wrong.</b> Every one of the API's operations was documented as answering
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
    /// <param name="StatedScope">
    /// Null when the document says any live token suffices — the case where the filter has
    /// nothing to refuse the caller for, so 403 is unreachable.
    /// </param>
    private sealed record Described(
        string Method,
        string Path,
        string? StatedScope,
        IReadOnlyList<string> Statuses,
        bool HasSuccessBodySchema)
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

                described.Add(new Described(
                    method,
                    path,
                    scopeByOperation.TryGetValue((method, path), out string? scope) ? scope : null,
                    operation.Responses is null ? [] : [.. operation.Responses.Keys],
                    body));
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
    /// ⚠️ <b>This is a PIN on a known gap, not a ratchet — and saying so is the point.</b>
    ///
    /// <para>No operation describes the shape of its success body. Every handler returns
    /// <c>Results.Ok(new { … })</c>, which produces a bare <c>IResult</c> carrying no type, so
    /// ASP.NET Core has nothing to infer a schema from and emits a 200 with no content.</para>
    ///
    /// <para><b>This is what blocks generating the clients</b> (<c>docs/decomposition.md</c> item
    /// 8): a generator fed this document emits a method per route whose return type is
    /// <c>object</c>, which is strictly worse than the hand-written clients it would replace.
    /// Item 8 is not blocked on choosing a generator; it is blocked on this.</para>
    ///
    /// <para><b>What this test does and does not do.</b> It fails the day the first response type
    /// is declared — deliberately, because that is the moment this pin should become a count that
    /// only goes up. It does NOT stop a new route being added without a response type; expressing
    /// that needs an absolute number, and banking one here while another branch adds a route would
    /// put two changes on one constant and lose a decrement.</para>
    /// </summary>
    [Fact]
    public void No_operation_yet_describes_its_success_body()
    {
        List<Described> typed = [.. Describe().Where(d => d.HasSuccessBodySchema)];

        typed.Should().BeEmpty(
            "a response type has been declared, which is good and means this test has done its "
            + "job: replace it with a count of operations that DO describe a body, asserted to be "
            + "at least as high as today's, so the number can only rise. Newly typed: "
            + string.Join("; ", typed.Select(d => d.ToString())));
    }
}
