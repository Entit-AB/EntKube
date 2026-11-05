using System.Net;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// A POST to a path that matches no route is not a 500.
///
/// <para><b>What was wrong.</b> <c>UseStatusCodePagesWithReExecute("/not-found")</c> re-runs the
/// pipeline against a Razor <c>@page</c> while <b>keeping the original method</b>. Blazor's
/// catch-all matches any path for GET, so routing answers 405 for every other verb — and that 405
/// was then re-executed as a POST, reached antiforgery, and surfaced as a <b>500</b>.</para>
///
/// <para>⚠️ <b>A 500 is a lie with consequences.</b> It tells a client that mistyped a URL that
/// this server is broken — retry, escalate, page someone — when the honest answer is the 405
/// routing already determined. And every scanner POSTing at <c>/wp-login.php</c> put one in the
/// error rate, which is how a real one gets missed.</para>
///
/// <para><b>Measured, before and after, across four paths:</b></para>
///
/// <list type="table">
/// <item><term>GET</term><description>404 with a 22 kB page — before and after. That is the
/// point.</description></item>
/// <item><term>POST</term><description>500 with a 1.5 kB body → <b>405, bodyless</b>.</description></item>
/// <item><term>PUT, DELETE</term><description>405 → 405, unchanged.</description></item>
/// </list>
///
/// <para>⚠️ <b>405, not 404, and that is deliberate.</b> Routing matched a real route template —
/// Blazor's catch-all — and refused the method, so 405 is what the framework actually determined.
/// Turning it into a 404 would mean changing the catch-all that serves the entire application
/// shell. The defect was the 500, and only the 500.</para>
///
/// <para>⚠️ <b>The obvious fix does not work, and this file exists partly to record that.</b>
/// Wrapping the middleware in <c>UseWhen(ctx => IsGet…)</c> removes the 500 and <b>also removes
/// the friendly page</b> — measured, GET's body went from 22794 bytes to 0 — because the
/// re-execute middleware inside a branch does not wrap the main pipeline. The fix is the
/// documented per-request opt-out instead.</para>
///
/// <para>⚠️ <b>Same root cause as the API's 404-arriving-as-405, and NOT fixable by that
/// change.</b> That one put <c>ISkipStatusCodePagesMetadata</c> on everything going through
/// <c>RequireApiScope</c>. Metadata cannot reach this case by construction: a request that matched
/// no endpoint carries none. The two are complementary.</para>
///
/// <para><b>Found while building a measure, not while looking for it.</b> A test for another route
/// wanted to show that a handler's 404 and a routing 404 are distinguishable, and POSTing at a
/// non-route answered 500 instead of the 404 it expected.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class UnroutedRequestTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Four paths, deliberately spread: under the API prefix, under SCIM, and under no prefix this
    /// application owns at all. The defect was in app-wide middleware, so a single path could not
    /// tell an API-specific quirk from what it actually was.
    /// </summary>
    public static TheoryData<string> UnroutedPaths() =>
    [
        "/api/v1/does-not-exist",
        "/api/v1/advisor/findings/cert:x/acknowledge-not-a-route",
        "/scim/v2/NotAThing",
        "/totally/unrelated",
    ];

    [Theory]
    [MemberData(nameof(UnroutedPaths))]
    public async Task A_post_to_a_path_that_is_not_a_route_is_not_a_500(string path)
    {
        HttpStatusCode status = await fixture.SendRawAsync("POST", path, "ops:read", null);

        status.Should().NotBe(
            HttpStatusCode.InternalServerError,
            "a mistyped URL is not this server being broken, and this answered 500 — the "
            + "re-executed POST reached antiforgery, which refused it, and nothing handled that");

        status.Should().Be(
            HttpStatusCode.MethodNotAllowed,
            "which is what routing determined: Blazor's catch-all matches the path for GET and "
            + "refuses the method. 404 would need that catch-all changed, and it serves the "
            + "whole application shell");
    }

    /// <summary>
    /// The verbs that were already right, pinned so the fix cannot be "simplified" into changing
    /// them. These never reached the 500 — the re-execution could not render for them either, so
    /// routing's answer survived.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnroutedPaths))]
    public async Task A_put_or_delete_to_a_path_that_is_not_a_route_is_unchanged(string path)
    {
        HttpStatusCode put = await fixture.SendRawAsync("PUT", path, "ops:read", null);
        HttpStatusCode delete = await fixture.SendRawAsync("DELETE", path, "ops:read", null);

        put.Should().Be(HttpStatusCode.MethodNotAllowed);
        delete.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>
    /// ⚠️ The other direction, and the reason the fix is a method filter rather than deleting the
    /// middleware. A browser following a dead link must still get the friendly page, so the GET
    /// path has to keep re-executing — a fix that turned every 404 into a bare status would pass
    /// every assertion above and quietly remove a user-facing page.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnroutedPaths))]
    public async Task A_get_to_the_same_path_still_gets_the_friendly_page(string path)
    {
        HttpStatusCode status = await fixture.SendRawAsync("GET", path, "ops:read", null);

        status.Should().Be(HttpStatusCode.NotFound);

        // ⚠️ The status alone cannot tell the friendly page from a bare 404 — both are 404 — so
        // this asserts the page's own CONTENT. A length threshold would pass against the 1.5 kB
        // error body the broken POST used to return, and the first attempt at this fix produced a
        // body of exactly 0 bytes here while every status assertion above still passed.
        string body = await fixture.SendForBodyAsync("GET", path, "ops:read", null);

        body.Should().Contain(
            "Page not found",
            "NotFound.razor renders this, and the re-execution is the only thing that reaches it "
            + "— restricting the re-execution to GET must not stop it happening FOR a GET");
    }
}
