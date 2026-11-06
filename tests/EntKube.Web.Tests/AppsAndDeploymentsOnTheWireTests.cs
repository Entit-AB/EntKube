using System.Net;
using System.Text.Json;
using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>
/// What <c>GET /apps</c> and <c>GET /deployments</c> do that only an HTTP call can see: the
/// deployment count, the <c>appId</c> filter, the enum spellings and the row order.
///
/// <para>⚠️ <b>All of it is invisible to a record-level wire-shape test.</b>
/// <c>AppsAndDeploymentsWireShapeTests</c> hands <c>AppResponse.From</c> a count and therefore
/// cannot tell what the handler computed — a <c>deploymentCount</c> stuck at zero, a grouping
/// keyed on the wrong id, a dropped filter and a deleted sort all serialise perfectly.</para>
///
/// <para><b>Why the count is worth a request.</b> It crosses two ids: the handler groups
/// deployments by <c>AppId</c> and then looks each app up by its <c>Id</c>. Those are different
/// properties of different objects that happen to hold the same value, which is the shape a
/// plausible edit gets wrong — and <c>GetValueOrDefault</c> answers 0 rather than throwing, so
/// getting it wrong reads as "this tenant has no deployments" rather than as a defect.</para>
///
/// <para>⚠️ <b>Why the fixture has two customers and three apps, which looks like more than the
/// assertions need.</b> It is what makes the ORDER verifiable, and getting there took two wrong
/// theories and six mutations. <c>DeliveryModel</c> declares unique indexes on
/// <c>(CustomerId, Name)</c> for <c>Apps</c> and <c>(AppId, Name)</c> for <c>AppDeployments</c>
/// (<c>DeliveryModel.cs:122,175</c> — not in <c>ApplicationDbContext</c>, which is why grepping
/// there for <c>HasIndex</c> finds nothing). With every row under ONE parent the index's leading
/// column is constant, so an index scan hands back rows already in <c>Name</c> order — SQLite
/// collates TEXT as BINARY, which is ordinal — and the host silently supplies the very ordering
/// the handler is responsible for. That is what defeats the fleet equivalent, and a first draft
/// of this fixture reproduced it exactly: mutations deleting either <c>OrderBy</c> both passed.
///
/// <para>Splitting the rows across two parents breaks that. The customer named
/// <c>aaa-…</c> owns the <c>apple-…</c> app and the <c>zzz-…</c> customer owns the
/// <c>Zebra-…</c> one, so the natural walk — customers by <c>(TenantId, Name)</c>, then each
/// one's apps by name — yields <c>apple</c> BEFORE <c>Zebra</c>, which is the opposite of ordinal
/// order. Insertion order is arranged to agree with that, so a plain table scan gives the wrong
/// order too: both plausible query plans now disagree with the answer, and the handler's sort is
/// the only thing that can produce it.</para></para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class AppsAndDeploymentsOnTheWireTests : IDisposable
{
    private readonly GeneratedOpenApiDocumentFixture fixture;
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly Guid lowCustomerId = Guid.NewGuid();
    private readonly Guid highCustomerId = Guid.NewGuid();
    private readonly Guid environmentId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid busyAppId = Guid.NewGuid();
    private readonly Guid oneAppId = Guid.NewGuid();
    private readonly Guid quietAppId = Guid.NewGuid();

    /// <summary>
    /// ⚠️ The discriminator between ORDINAL and culture-sensitive ordering. Ordinally every
    /// uppercase letter sorts before every lowercase one, so <c>Zebra…</c> precedes
    /// <c>apple…</c>; every linguistic comparison, invariant culture included, compares the
    /// letters alphabetically and puts <c>apple…</c> first. Two names that merely differ would
    /// order the same way under both and prove nothing.
    /// </summary>
    private string Upper => $"Zebra-{suffix}";

    private string Lower => $"apple-{suffix}";

    /// <summary>Third app, ordinally last of the three — it carries the zero count.</summary>
    private string Quiet => $"quiet-{suffix}";

    private string UpperDeployment => $"Zebra-dep-{suffix}";

    private string LowerDeployment => $"apple-dep-{suffix}";

    /// <summary>Ordinally FIRST of the three deployments, so the order has a middle.</summary>
    private string MiddleDeployment => $"Mango-dep-{suffix}";

    public AppsAndDeploymentsOnTheWireTests(GeneratedOpenApiDocumentFixture fixture)
    {
        this.fixture = fixture;
        Seed();
    }

    private ApplicationDbContext Db() =>
        fixture.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

    /// <summary>
    /// Two customers, three apps, three deployments.
    ///
    /// <para>⚠️ Staged, principals first: foreign keys ARE enforced in this host and EF's batch
    /// ordering can place a dependent ahead of its principal. An app reaches the token's tenant
    /// the long way round, through its customer, because that is how <c>DeliveryApi</c> scopes
    /// it — an app hung off a customer in another tenant is simply absent from the answer.</para>
    ///
    /// <para>⚠️ Every insertion is in the order the assertions do NOT expect: the <c>apple</c> app
    /// before the <c>Zebra</c> one, the <c>apple</c> deployment before both of the others. See
    /// the class summary for why that matters and why it is not sufficient on its own.</para>
    ///
    /// <para>The counts are 2, 1 and 0 rather than 2 and 0. A one distinguishes "count this app's
    /// deployments" from "count the tenant's and give everyone the same number", which two values
    /// could not.</para>
    /// </summary>
    private void Seed()
    {
        using ApplicationDbContext db = Db();

        db.Customers.AddRange(
            new Customer
            {
                Id = lowCustomerId,
                TenantId = fixture.TokenTenantId,
                Name = $"aaa-cust-{suffix}",
            },
            new Customer
            {
                Id = highCustomerId,
                TenantId = fixture.TokenTenantId,
                Name = $"zzz-cust-{suffix}",
            });
        db.Environments.Add(new Environment
        {
            Id = environmentId,
            TenantId = fixture.TokenTenantId,
            Name = $"env-apps-{suffix}",
        });
        db.SaveChanges();

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId,
            TenantId = fixture.TokenTenantId,
            EnvironmentId = environmentId,
            Name = $"cluster-apps-{suffix}",
            ApiServerUrl = "https://127.0.0.9:6443",
        });
        db.SaveChanges();

        db.Apps.AddRange(
            // Both of the aaa- customer's apps first, so neither insertion order nor the
            // (CustomerId, Name) index can produce the expected answer.
            new App { Id = oneAppId, CustomerId = lowCustomerId, Name = Lower },
            new App { Id = quietAppId, CustomerId = lowCustomerId, Name = Quiet },
            new App { Id = busyAppId, CustomerId = highCustomerId, Name = Upper });
        db.SaveChanges();

        db.AppDeployments.AddRange(
            Deployment(LowerDeployment, oneAppId, DeploymentType.HelmChart, SyncStatus.Syncing, HealthStatus.Progressing),
            Deployment(MiddleDeployment, busyAppId, DeploymentType.Yaml, SyncStatus.Synced, HealthStatus.Healthy),
            Deployment(UpperDeployment, busyAppId, DeploymentType.GitHelm, SyncStatus.OutOfSync, HealthStatus.Degraded));
        db.SaveChanges();
    }

    private AppDeployment Deployment(
        string name, Guid appId, DeploymentType type, SyncStatus sync, HealthStatus health) =>
        new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Name = name,
            EnvironmentId = environmentId,
            ClusterId = clusterId,
            Namespace = $"ns-{suffix}",
            Type = type,
            SyncStatus = sync,
            HealthStatus = health,
        };

    public void Dispose()
    {
        using ApplicationDbContext db = Db();

        db.AppDeployments.RemoveRange(db.AppDeployments.Where(
            d => d.AppId == busyAppId || d.AppId == oneAppId || d.AppId == quietAppId));
        db.SaveChanges();

        db.Apps.RemoveRange(db.Apps.Where(
            a => a.Id == busyAppId || a.Id == oneAppId || a.Id == quietAppId));
        db.SaveChanges();

        db.KubernetesClusters.RemoveRange(db.KubernetesClusters.Where(c => c.Id == clusterId));
        db.SaveChanges();

        db.Customers.RemoveRange(db.Customers.Where(
            c => c.Id == lowCustomerId || c.Id == highCustomerId));
        db.Environments.RemoveRange(db.Environments.Where(e => e.Id == environmentId));
        db.SaveChanges();
    }

    /// <summary>
    /// ⚠️ Asserts the STATUS as well as the body. Without it a 403 or a 500 surfaces only as
    /// <c>EnumerateArray()</c> throwing on a ProblemDetails object — a failure, but one that sends
    /// the next reader to the wrong file.
    /// </summary>
    private async Task<List<JsonElement>> GetAsync(string url)
    {
        (HttpStatusCode status, string? mediaType, string body) =
            await fixture.SendForResponseAsync("GET", url, "apps:read");

        status.Should().Be(HttpStatusCode.OK);
        mediaType.Should().Be("application/json");

        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray()];
    }

    private async Task<List<JsonElement>> AppsAsync() => await GetAsync("/api/v1/apps");

    /// <summary>
    /// ⚠️ Three counts — 2, 1 and 0 — and the zero is the point. An app with no deployments is
    /// ABSENT from the handler's grouping, so its count comes from <c>GetValueOrDefault</c> rather
    /// than from the dictionary. The three fail in different directions: a count stuck at 0 fails
    /// on the busy app, a grouping that matched everything fails on the quiet one, and a count of
    /// the TENANT's deployments rather than the app's fails on all three at once.
    ///
    /// <para>⚠️ The key must be PRESENT and 0, not absent. A client reading an absent key cannot
    /// tell "no deployments" from "this version of the API does not send a count".</para>
    /// </summary>
    [Fact]
    public async Task The_deployment_count_is_this_app_s_deployments_and_zero_is_published_not_omitted()
    {
        Dictionary<string, JsonElement> byName = (await AppsAsync())
            .ToDictionary(a => a.GetProperty("name").GetString() ?? "");

        byName.Should().ContainKey(
            Upper, "the seed must be in the answer, or the assertions below pass over nothing");
        byName.Should().ContainKeys(Lower, Quiet);

        byName[Upper].GetProperty("deploymentCount").GetInt32().Should().Be(
            2, "the busy app has exactly two deployments, and the count is over its OWN rows");

        byName[Lower].GetProperty("deploymentCount").GetInt32().Should().Be(
            1, "and this one has exactly one — three deployments exist, so a tenant-wide count "
            + "would read 3 here");

        byName[Quiet].TryGetProperty("deploymentCount", out JsonElement quiet).Should().BeTrue(
            "an app with no deployments still publishes the key");
        quiet.GetInt32().Should().Be(
            0, "GetValueOrDefault answers 0 for an app absent from the grouping");
    }

    /// <summary>
    /// ⚠️ <c>customer</c> is the customer's NAME, and the app's own name is beside it. Both are
    /// strings on adjacent factory parameters, so this is the transposition the record test also
    /// guards — asserted here too because here the values come out of the database rather than out
    /// of a fixture that could have been written to match a misreading.
    ///
    /// <para>Two apps under DIFFERENT customers, so a handler that published one customer for
    /// every app — or read the customer off the wrong row — fails rather than matching.</para>
    /// </summary>
    [Fact]
    public async Task An_app_publishes_its_own_customer_s_name_beside_its_own()
    {
        Dictionary<string, JsonElement> byName = (await AppsAsync())
            .ToDictionary(a => a.GetProperty("name").GetString() ?? "");

        byName[Upper].GetProperty("customer").GetString().Should().Be($"zzz-cust-{suffix}");
        byName[Lower].GetProperty("customer").GetString().Should().Be($"aaa-cust-{suffix}");
        byName[Upper].GetProperty("name").GetString().Should().Be(Upper, "and not the customer");
    }

    /// <summary>
    /// ⚠️ The <c>appId</c> filter, which nothing asserted. It is a query parameter the framework
    /// binds by NAME — a misspelling binds nothing and is silently ignored, which answers 200 with
    /// every deployment the tenant has. <c>CliQueryParameterParityTests</c> catches the spelling
    /// drifting apart; this catches the filter not filtering.
    ///
    /// <para>⚠️ Asserted in both directions: the matching app's two deployments are present AND
    /// the third app's is absent. A filter that returned everything would pass the first half.
    /// The second call uses the app with no deployments at all, so an <c>appId</c> that matches
    /// nothing must be an empty array rather than a 404.</para>
    /// </summary>
    [Fact]
    public async Task The_appId_filter_narrows_to_that_app_and_an_unknown_one_is_empty_not_absent()
    {
        List<JsonElement> mine = await GetAsync($"/api/v1/deployments?appId={busyAppId}");

        mine.Select(d => d.GetProperty("name").GetString())
            .Should().BeEquivalentTo(
                [UpperDeployment, MiddleDeployment], "both of the busy app's deployments");
        mine.Should().OnlyContain(
            d => d.GetProperty("app").GetString() == Upper,
            "and nothing belonging to another app — the third deployment is on a different one");

        List<JsonElement> none = await GetAsync($"/api/v1/deployments?appId={quietAppId}");

        none.Should().BeEmpty(
            "an appId that matches nothing is an empty array — the route has never 404ed for it");
    }

    /// <summary>
    /// ⚠️ The row order, for both routes, and it took real work to make this assertion mean
    /// anything. See the class summary: a first draft put every row under one parent, and
    /// mutations deleting either <c>OrderBy</c> both PASSED, because the <c>(CustomerId, Name)</c>
    /// and <c>(AppId, Name)</c> indexes supplied ordinal order for free. Splitting the rows across
    /// two customers and two apps inverts the store's natural order, and both mutations now fail.
    ///
    /// <para><b>What it catches, measured per route:</b> deleting either sort, and weakening
    /// either comparer from <c>StringComparer.Ordinal</c> to the default overload — the likelier
    /// regression, because it reads as a simplification.</para>
    ///
    /// <para>⚠️ <b>Relative positions, not an exact list.</b> This host is shared across the
    /// whole <c>api-surface</c> collection, so the tenant may own rows another class seeded.
    /// Asserting the full sequence would make this fail for reasons that are nothing to do with
    /// ordering; asserting that <c>Zebra-</c> precedes <c>apple-</c> is true of the correct answer
    /// whatever else is interleaved.</para>
    /// </summary>
    [Fact]
    public async Task Both_routes_come_back_in_ordinal_name_order()
    {
        List<string> apps =
            [.. (await AppsAsync()).Select(a => a.GetProperty("name").GetString() ?? "")];

        apps.Should().Contain(Upper).And.Contain(Lower);
        apps.IndexOf(Upper).Should().BeLessThan(
            apps.IndexOf(Lower),
            "ordinally every uppercase letter sorts before every lowercase one, so Zebra- precedes "
            + "apple-; any linguistic comparison would put apple- first, and the store's own order "
            + "— aaa-cust's apps before zzz-cust's — would too");

        List<string> deployments =
            [.. (await GetAsync("/api/v1/deployments"))
                .Select(d => d.GetProperty("name").GetString() ?? "")];

        deployments.Should().Contain(UpperDeployment).And.Contain(LowerDeployment);
        deployments.IndexOf(UpperDeployment).Should().BeLessThan(
            deployments.IndexOf(LowerDeployment),
            "the same discriminator on the unfiltered list — filtering to one app would put both "
            + "rows under one (AppId, Name) index and hand the ordering back for free");
    }

    /// <summary>
    /// ⚠️ The three enums over real HTTP, as the strings a client reads. The record test proves
    /// the factory calls <c>ToString()</c>; this proves the whole path — including
    /// <c>DeliveryApi</c>'s <c>(ContractDeploymentType)(int)</c> casts, which are sound only while
    /// the two enum declarations agree member for member and are the step a record fixture cannot
    /// reach.
    ///
    /// <para>Three rows with nine distinct members between them, none of them the zero member, so
    /// a cast landing on a neighbouring ordinal has to be wrong consistently in nine places to
    /// pass.</para>
    /// </summary>
    [Fact]
    public async Task The_enums_reach_the_wire_as_their_member_names()
    {
        Dictionary<string, JsonElement> byName = (await GetAsync("/api/v1/deployments"))
            .ToDictionary(d => d.GetProperty("name").GetString() ?? "");

        byName[UpperDeployment].GetProperty("type").GetString().Should().Be("GitHelm");
        byName[UpperDeployment].GetProperty("syncStatus").GetString().Should().Be("OutOfSync");
        byName[UpperDeployment].GetProperty("healthStatus").GetString().Should().Be("Degraded");

        byName[MiddleDeployment].GetProperty("type").GetString().Should().Be("Yaml");
        byName[MiddleDeployment].GetProperty("syncStatus").GetString().Should().Be("Synced");
        byName[MiddleDeployment].GetProperty("healthStatus").GetString().Should().Be("Healthy");

        byName[LowerDeployment].GetProperty("type").GetString().Should().Be("HelmChart");
        byName[LowerDeployment].GetProperty("syncStatus").GetString().Should().Be("Syncing");
        byName[LowerDeployment].GetProperty("healthStatus").GetString().Should().Be("Progressing");
    }
}
