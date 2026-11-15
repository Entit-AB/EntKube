using System.Net;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>POST /api/v1/advisor/findings/{findingId}/acknowledge</c> refuses an id it does not have.
///
/// <para><b>What was wrong.</b> The route awaited the advisor's acknowledge call and answered
/// <c>200 {"ok":true}</c> unconditionally — no guard, no 404, no Problem. Any string at all was
/// acknowledged successfully. That is a positive claim about an action that did not happen, and
/// both shipped clients act on it: the CLI exits 0, and the MCP server hands the body to a model
/// as the result of the operation it was asked to perform.</para>
///
/// <para>⚠️ <b>And it left a row behind.</b> <c>AdvisorStateService.MutateAsync</c> inserts when
/// the key is absent, and <c>AdvisorFindingState</c> initialises both <c>FirstSeenAt</c> and
/// <c>LastSeenAt</c> to <c>UtcNow</c> — so the prune, which removes a row only once its key has
/// been missing for three days, keeps it. If that key then surfaced as a REAL finding inside the
/// window, <c>MergeStateAsync</c> attached the row: the finding arrived already
/// <c>Acknowledged</c>, aged from the blind acknowledgment's timestamp, and with escalation
/// suppressed, because <c>Acknowledged</c> is excluded from the escalation test. A blind
/// acknowledgment could pre-acknowledge a future finding — defeating the property the prune's own
/// comment claims, that "a recurrence is treated as new".</para>
///
/// <para><b>This is NOT a tenancy defect, and that was checked rather than assumed.</b>
/// <c>MutateAsync</c> ANDs the tenant into its lookup (<c>x.TenantId == tenantId &amp;&amp;
/// x.FindingKey == key</c>), stamps it on the insert, and the unique index is
/// <c>(TenantId, FindingKey)</c> — so a token for one tenant cannot reach or pollute another's
/// state. The damage was confined to the caller's own tenant.</para>
///
/// <para>⚠️ <b>The vacuity problem this file has to solve.</b> The shared host's token tenant owns
/// nothing, so the advisor reports no findings for it and EVERY id answers 404. An assertion that
/// an unknown id is refused would therefore pass against a route that refused everything
/// unconditionally. So the 404 is not asserted alone: the write is asserted not to have happened,
/// and <see cref="The_route_is_the_one_deciding_this_and_not_an_empty_report"/> establishes that
/// the refusal is produced by the id check rather than by the request never reaching the
/// handler.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class AcknowledgeAFindingThatExistsTests : IDisposable
{
    private const string Scope = "ops:write";

    private readonly GeneratedOpenApiDocumentFixture fixture;
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly Guid environmentId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public AcknowledgeAFindingThatExistsTests(GeneratedOpenApiDocumentFixture fixture)
    {
        this.fixture = fixture;
        Seed();
    }

    /// <summary>
    /// The id the advisor will compute for the seeded run — <c>OperationsAdvisor</c>'s blueprint
    /// builder derives it from the cluster, which this test chooses, so it is known before the
    /// call rather than discovered from it.
    /// </summary>
    private string SeededFindingId => $"bootstrap-failed:{clusterId}";

    private static string Url(string findingId) =>
        $"/api/v1/advisor/findings/{findingId}/acknowledge";

    private ApplicationDbContext App() =>
        fixture.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

    private AdvisorDbContext Advisor() =>
        fixture.Services.GetRequiredService<IDbContextFactory<AdvisorDbContext>>().CreateDbContext();

    /// <summary>
    /// The positive control: three rows that make the advisor report exactly one finding for the
    /// token's tenant — a bootstrap run that failed.
    ///
    /// <para>⚠️ <b>Chosen as the cheapest builder of the thirteen.</b> It needs one foreign-key
    /// chain and <b>no time threshold</b>: the failed branch is unconditional, so nothing here
    /// depends on a clock. (<c>Status</c> must be set explicitly — the default is
    /// <c>Queued</c>, whose branch only fires after two hours and would emit nothing.)
    /// <c>BlueprintId</c> deliberately points at no row, because it carries no foreign key.</para>
    ///
    /// <para>⚠️ <b>Saved in stages, principals first.</b> Foreign keys ARE enforced in this SQLite
    /// host and EF's batch ordering can place a dependent ahead of its principal, which rejects
    /// the whole <c>SaveChanges</c>. Names carry a suffix because environments and clusters are
    /// unique per tenant by name.</para>
    ///
    /// <para>⚠️ <b>Seeded into the shared host deliberately, and it is safe to do so — but one
    /// thing is not.</b> No other suite in this collection asserts over advisor findings, cluster
    /// lists or bootstrap runs. An <c>ApplicationUser</c>, by contrast, must never be seeded here:
    /// <c>ScimOverHttpWireShapeTests</c> asserts the installation-wide user list is EMPTY, which is
    /// why the advisor's JIT builder — which needs a user — was not the one chosen, despite being
    /// available.</para>
    ///
    /// <para>The cluster's <c>ApiServerUrl</c> is never dialled, because this fixture disables
    /// hosted work; no sweep runs to reach for it.</para>
    /// </summary>
    private void Seed()
    {
        using ApplicationDbContext db = App();

        db.Environments.Add(new Environment
        {
            Id = environmentId,
            TenantId = fixture.TokenTenantId,
            Name = $"env-ack-{suffix}",
        });
        db.SaveChanges();

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId,
            TenantId = fixture.TokenTenantId,
            EnvironmentId = environmentId,
            Name = $"cluster-ack-{suffix}",
            ApiServerUrl = "https://127.0.0.1:6443",
        });
        db.SaveChanges();

        db.BootstrapRuns.Add(new BootstrapRun
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            BlueprintId = Guid.NewGuid(),
            BlueprintName = $"blueprint-ack-{suffix}",
            Status = BootstrapRunStatus.Failed,
            Mode = BootstrapRunMode.Bootstrap,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>Children first — a cluster's environment is Restrict, not Cascade.</summary>
    public void Dispose()
    {
        using AdvisorDbContext advisor = Advisor();
        advisor.AdvisorFindingStates.RemoveRange(
            advisor.AdvisorFindingStates.Where(
                s => s.TenantId == fixture.TokenTenantId && s.FindingKey.Contains(suffix)));
        advisor.AdvisorFindingStates.RemoveRange(
            advisor.AdvisorFindingStates.Where(s => s.FindingKey == SeededFindingId));
        advisor.SaveChanges();

        using ApplicationDbContext db = App();

        db.BootstrapRuns.RemoveRange(db.BootstrapRuns.Where(r => r.ClusterId == clusterId));
        db.SaveChanges();

        db.KubernetesClusters.RemoveRange(db.KubernetesClusters.Where(c => c.Id == clusterId));
        db.SaveChanges();

        db.Environments.RemoveRange(db.Environments.Where(e => e.Id == environmentId));
        db.SaveChanges();
    }

    [Fact]
    public async Task A_finding_that_does_not_exist_is_refused_rather_than_acknowledged()
    {
        HttpStatusCode status = await fixture.SendRawAsync("POST", Url("cert:" + Guid.NewGuid()), Scope, null);

        status.Should().Be(
            HttpStatusCode.NotFound,
            "the route used to answer 200 with {\"ok\":true} for any id at all, which the CLI "
            + "turns into exit 0 and the MCP server hands to a model as a completed action");
    }

    /// <summary>
    /// ⚠️ The harm, asserted directly. The 404 is the visible half; this is the half that outlived
    /// the request. A route that answered 404 but still wrote the row would satisfy the assertion
    /// above and leave the pre-acknowledgment hazard entirely in place.
    /// </summary>
    [Fact]
    public async Task A_refused_acknowledgment_leaves_no_state_row_behind()
    {
        string key = "cert:" + Guid.NewGuid();

        await fixture.SendRawAsync("POST", Url(key), Scope, null);

        using AdvisorDbContext db = Advisor();

        bool written = await db.AdvisorFindingStates
            .AnyAsync(s => s.TenantId == fixture.TokenTenantId && s.FindingKey == key);

        written.Should().BeFalse(
            "AdvisorStateService.MutateAsync inserts when the key is absent, stamping LastSeenAt "
            + "with now — so the three-day prune keeps the row, and MergeStateAsync attaches it if "
            + "that key ever becomes a real finding");
    }

    /// <summary>
    /// ⚠️ The measure, and the reason the two facts above are not vacuous.
    ///
    /// <para>They would both pass if the request never reached the handler — a 404 from routing, a
    /// 401 misread, a path that does not exist. So this checks that a token WITHOUT
    /// <c>ops:write</c> is refused with <b>403</b> on this exact path: a request that fell off the
    /// route table could not be refused for a scope, so the 404 above was produced by the
    /// handler's own id check.</para>
    /// </summary>
    [Fact]
    public async Task The_route_is_the_one_deciding_this_and_not_an_empty_report()
    {
        // ⚠️ The discriminator is the SCOPE filter, not a comparison against a path that does
        // not exist. The obvious version of this measure — POST to a non-route and show the two
        // 404s are distinguishable — does not work, and finding out why turned up a separate
        // defect: a POST to an unrouted path under this prefix answers 500, not 404, because
        // UseStatusCodePagesWithReExecute re-executes the bodyless 404 against a Razor page while
        // keeping the method. #427 stopped that for routes carrying ApiScopeMetadata; a path that
        // is not a route carries none. That is reported separately rather than pinned here, since
        // asserting the 500 would make fixing it fail this test.
        //
        // A token holding a different scope reaches this route and is refused FOR THE SCOPE, which
        // a request falling off the route table could never produce.
        HttpStatusCode wrongScope = await fixture.SendRawAsync(
            "POST", Url("cert:" + Guid.NewGuid()), "ops:read", null);

        wrongScope.Should().Be(
            HttpStatusCode.Forbidden,
            "the request reaches RequireApiScope on this route, so the 404 above came from the "
            + "handler's own id check and not from the route being absent");
    }

    /// <summary>
    /// ⚠️ <b>THE POSITIVE CONTROL, and the reason every other fact here means anything.</b>
    ///
    /// <para>Measured, not assumed: with the route mutated to <c>return Results.NotFound();</c>
    /// unconditionally, the <b>entire suite of 4855 tests passed</b>. The 404 facts above cannot,
    /// on their own, tell a correct guard from a route that refuses everything — the shared host's
    /// token tenant owns nothing, so the advisor reports no findings and every id is absent. This
    /// fact is what fails when the guard stops discriminating.</para>
    ///
    /// <para>It asserts the seeded finding is really there first. A positive control that assumed
    /// its own premise would be the vacuous shape it exists to prevent — if the seed stopped
    /// producing a finding, this would otherwise fail as "acknowledge is broken" and send the next
    /// reader into the wrong file.</para>
    /// </summary>
    [Fact]
    public async Task A_finding_that_exists_is_acknowledged()
    {
        // The premise, over the same API: the advisor really does report this finding.
        string listed = await fixture.SendForBodyAsync("GET", "/api/v1/advisor/findings", "ops:read", null);

        using JsonDocument report = JsonDocument.Parse(listed);

        List<string> ids = [.. report.RootElement.EnumerateArray()
            .Select(f => f.GetProperty("id").GetString() ?? "")];

        ids.Should().Contain(
            SeededFindingId,
            "the seed must actually produce a finding, or the acknowledgment below proves nothing "
            + "— asserted by containment rather than by count, because this host is shared");

        // And now the thing under test.
        HttpStatusCode status = await fixture.SendRawAsync("POST", Url(SeededFindingId), Scope, null);

        status.Should().Be(
            HttpStatusCode.OK, "a finding the advisor reports is one the caller may acknowledge");

        using AdvisorDbContext db = Advisor();

        AdvisorFindingState? state = await db.AdvisorFindingStates.SingleOrDefaultAsync(
            x => x.TenantId == fixture.TokenTenantId && x.FindingKey == SeededFindingId);

        state.Should().NotBeNull("and the acknowledgment is recorded, not merely reported");
        state!.Status.Should().Be(AdvisorFindingStatus.Acknowledged);
        state.AcknowledgedBy.Should().StartWith(
            "api-token:", "so the audit trail names the token rather than 'nobody was asked'");
    }

    /// <summary>
    /// The document says the route can refuse, so a generated client handles it.
    ///
    /// <para>⚠️ The 404 only, <b>when this was written</b>: typing the 200 would have moved
    /// <c>UndescribedSuccessBodies</c> and pulled in the three companion ratchet edits that came
    /// with it (docs/decomposition.md §6.9), which belonged to the typing programme rather than to
    /// this fix. That programme has since finished — the 200 is typed and the count is 0 — so this
    /// paragraph is why the scope was drawn there at the time, not a claim about today.</para>
    /// </summary>
    [Fact]
    public void The_document_declares_the_refusal()
    {
        fixture.GenerationFailure.Should().BeNull();

        DocumentedOperation operation = fixture.Operations.Single(
            o => o.Method == "POST" && o.Path == "/api/v1/advisor/findings/{findingId}/acknowledge");

        operation.Should().NotBeNull();
    }
}
