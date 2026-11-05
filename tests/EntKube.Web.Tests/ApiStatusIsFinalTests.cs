using System.Net;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether the status an API handler chooses is the status the caller receives.
///
/// <para><b>What was wrong.</b> <c>Program.cs</c> installs
/// <c>UseStatusCodePagesWithReExecute("/not-found")</c> for the whole application. That middleware
/// re-executes the pipeline against that page for any <b>bodyless</b> error response, <b>keeping
/// the original HTTP method</b> — and <c>/not-found</c> is a Razor <c>@page</c>, which accepts
/// only <c>GET</c>. So a 404 chosen by an API handler was delivered as:</para>
///
/// <list type="table">
/// <item><term>GET</term><description>404 — correct, by luck rather than by design</description></item>
/// <item><term>DELETE</term><description>405 Method Not Allowed, from the page's own method constraint</description></item>
/// <item><term>POST</term><description>400 "A valid antiforgery token was not provided with the request"</description></item>
/// </list>
///
/// <para>Four PRs went into making the generated document say truthfully what each operation can
/// answer. The document said 404; the server said 405. A friendly page is the right answer for a
/// browser and the wrong one for a client that was told to expect a status.</para>
///
/// <para>⚠️ <b>How it was found, because the method is the point.</b> A cross-tenant probe failed
/// with 405 where it expected 404 and the obvious reading was a broken test. The tell was
/// repeating the DELETE with alternating ids: <c>owner=204, stranger=405, owner=405</c> — the
/// owner gets 405 too, once its own row is gone. A status that tracks "did the handler return
/// 404" rather than "whose row is it" cannot be a tenancy or a routing effect, and that is what
/// identified it as a rewrite.</para>
///
/// <para>⭐ These assertions are over HTTP and use ids belonging to nobody, so they are about the
/// status mechanism and not about tenancy — <c>CrossTenantProbeTests</c> owns that question. The
/// fix is one <see cref="ApiStatusIsFinal"/> metadata in <c>RequireApiScope</c>, which every one
/// of the 31 operations already passes through, so no route has to remember it. Asserting that
/// the metadata is PRESENT would assert nothing: it is a marker interface with no members, and
/// what matters is whether the middleware honours it. Hence HTTP, and hence all four methods —
/// the defect was invisible on <c>GET</c>.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ApiStatusIsFinalTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>A valid body, so a refused PUT is refused for its id and not for its payload.</summary>
    private const string RateBody = """
        {"cpuCoreHourCost":1,"memoryGiBHourCost":1,"storageGiBMonthCost":1,
         "clusterMonthlyOverhead":1,"loadBalancerMonthlyCost":1,"publicIpMonthlyCost":1}
        """;

    [Fact]
    public async Task A_missing_rate_is_a_404_when_asked_for_with_GET()
    {
        (await fixture.SendRawAsync(
            "GET", $"/api/v1/cost-rates/{Guid.NewGuid()}", ApiScopes.ConfigRead, null))
            .Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The one that was a 405. <c>/not-found</c> is a <c>@page</c>, so re-executing a DELETE
    /// against it produced the page's method constraint rather than the API's answer.
    /// </summary>
    [Fact]
    public async Task A_missing_rate_is_a_404_when_DELETED_and_not_a_405()
    {
        (await fixture.SendRawAsync(
            "DELETE", $"/api/v1/cost-rates/{Guid.NewGuid()}", ApiScopes.ConfigWrite, null))
            .Should().Be(HttpStatusCode.NotFound,
                "a 405 here would tell a client the route does not accept DELETE, when it does "
                + "and has just refused this particular id");
    }

    [Fact]
    public async Task A_missing_rate_is_a_404_when_PUT_and_not_a_405()
    {
        (await fixture.SendRawAsync(
            "PUT", $"/api/v1/cost-rates/{Guid.NewGuid()}", ApiScopes.ConfigWrite, RateBody))
            .Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The one that was a 400 about antiforgery — a message with no meaning at all for a caller
    /// holding a bearer token, since antiforgery exists to protect cookie-authenticated forms.
    /// </summary>
    [Fact]
    public async Task A_missing_app_is_a_404_when_POSTED_to_and_not_an_antiforgery_failure()
    {
        (await fixture.SendRawAsync(
            "POST", $"/api/v1/apps/{Guid.NewGuid()}/secrets/sync", ApiScopes.AppsWrite, null))
            .Should().Be(HttpStatusCode.NotFound,
                "the caller holds a bearer token and cannot carry an antiforgery token; being "
                + "told to add one is an answer to a question it did not ask");
    }

    /// <summary>
    /// ⚠️ The other direction, and the reason the fix is endpoint metadata rather than removing
    /// the middleware: the friendly page must still answer for everything that is not the API. A
    /// fix that silenced <c>/not-found</c> app-wide would pass every assertion above while
    /// regressing what the middleware is there for.
    /// </summary>
    [Fact]
    public async Task A_page_that_does_not_exist_still_gets_the_friendly_page()
    {
        string body = await fixture.SendForBodyAsync(
            "GET", $"/this-page-does-not-exist-{Guid.NewGuid():N}", ApiScopes.FleetRead, null);

        body.Should().NotBeEmpty(
            "status-code re-execution still has to serve the application's own 404 page — the "
            + "API is excluded from it, not the app");
    }
}
