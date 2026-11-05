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
/// Three things <c>GET /clusters</c> does that only an HTTP call can see: the published
/// provisioning status, the row ORDER, and the component count.
///
/// <para>⚠️ <b>All three are invisible to a record-level wire-shape test</b>, which hands the
/// response type its values and therefore cannot tell what the handler computed. An adversarial
/// review of the typing change found that the first was unpinned anywhere and that the other two
/// were unpinned <em>in the whole suite</em> — a deleted <c>OrderBy</c> or a
/// <c>componentCount</c> stuck at zero passed everything, and both are columns the CLI renders.</para>
///
/// <para><b>The translation.</b> Fleet's entity calls the initial state <c>None</c>; the contract
/// calls the same state <c>Registered</c>. <c>PublicApiEndpoints.PublishedProvisioningStatus</c>
/// turns the contract's spelling back into the entity's for the wire, so a registered cluster
/// appears as <c>"None"</c> to the CLI's <c>clusters list</c>, to the MCP server, and to any
/// external client.</para>
///
/// <para>⚠️ <b>Nothing asserted this, and that was the whole hazard.</b> The helper is five lines
/// of apparent compatibility shim with no caller outside one projection, and the obvious reading of
/// it is that it translates a name nobody uses any more. Both declarations are live, and deleting
/// it would silently flip the published value from <c>"None"</c> to <c>"Registered"</c> for every
/// registered cluster — the overwhelming majority, since that is the state of any cluster EntKube
/// did not provision itself. Typing this route put that claim in a record's doc comment, and a doc
/// comment is exactly what no test reads.</para>
///
/// <para>⚠️ <b>Asserted over HTTP rather than against the helper</b>, which is <c>private</c> — and
/// the wire is the thing that matters anyway. A seeded cluster, a real token, and the bytes that
/// come back.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class ClusterProvisioningStatusOnTheWireTests : IDisposable
{
    private readonly GeneratedOpenApiDocumentFixture fixture;
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly Guid environmentId = Guid.NewGuid();
    private readonly Guid registeredId = Guid.NewGuid();
    private readonly Guid provisionedId = Guid.NewGuid();
    private readonly Guid failedId = Guid.NewGuid();
    private readonly Guid deletingId = Guid.NewGuid();

    /// <summary>
    /// ⚠️ The discriminator between ORDINAL and culture-sensitive ordering. Uppercase letters all
    /// sort before lowercase ordinally, so <c>Zebra…</c> precedes <c>apple…</c>; every linguistic
    /// comparison, invariant culture included, compares the letters alphabetically and puts
    /// <c>apple…</c> first. Two names that merely differ are not enough — they would order the same
    /// way under both.
    /// </summary>
    private string Upper => $"Zebra-{suffix}";

    private string Lower => $"apple-{suffix}";

    public ClusterProvisioningStatusOnTheWireTests(GeneratedOpenApiDocumentFixture fixture)
    {
        this.fixture = fixture;
        Seed();
    }

    private ApplicationDbContext Db() =>
        fixture.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

    /// <summary>
    /// Two clusters, because one would not distinguish "publishes None" from "publishes the entity
    /// name for everything" — the second is the control, and its entity name and published name
    /// agree.
    ///
    /// <para>Staged, principals first: foreign keys are enforced here and EF's batch ordering can
    /// place a dependent ahead of its principal. Names carry a suffix because clusters and
    /// environments are unique per tenant by name.</para>
    /// </summary>
    private void Seed()
    {
        using ApplicationDbContext db = Db();

        db.Environments.Add(new Environment
        {
            Id = environmentId,
            TenantId = fixture.TokenTenantId,
            Name = $"env-status-{suffix}",
        });
        db.SaveChanges();

        // The LOWER-case name is inserted first, so the seed order is the opposite of the
        // expected output. ⚠️ That was an attempt to make a DELETED sort fail too, and it does not
        // work — SQLite returns these rows in (TenantId, Name) index order under BINARY collation,
        // which is already ordinal, so the store masks the absence however they are inserted. The
        // inversion is kept because it costs nothing and removes insertion order as a reason the
        // assertion could pass; the limitation is recorded on the ordering test itself.
        db.KubernetesClusters.AddRange(
            new KubernetesCluster
            {
                Id = provisionedId,
                TenantId = fixture.TokenTenantId,
                EnvironmentId = environmentId,
                Name = Lower,
                ApiServerUrl = "https://127.0.0.2:6443",
                ProvisioningStatus = ClusterProvisioningStatus.Provisioned,
            },
            new KubernetesCluster
            {
                Id = registeredId,
                TenantId = fixture.TokenTenantId,
                EnvironmentId = environmentId,
                Name = Upper,
                ApiServerUrl = "https://127.0.0.1:6443",
                ProvisioningStatus = ClusterProvisioningStatus.None,
            },
            // ⚠️ The other two members, because two clusters could not tell the translation from
            // "publishes None for everything except Provisioned" — a mutation that passed both of
            // the original assertions.
            new KubernetesCluster
            {
                Id = failedId,
                TenantId = fixture.TokenTenantId,
                EnvironmentId = environmentId,
                Name = $"failed-{suffix}",
                ApiServerUrl = "https://127.0.0.3:6443",
                ProvisioningStatus = ClusterProvisioningStatus.Failed,
            },
            new KubernetesCluster
            {
                Id = deletingId,
                TenantId = fixture.TokenTenantId,
                EnvironmentId = environmentId,
                Name = $"deleting-{suffix}",
                ApiServerUrl = "https://127.0.0.4:6443",
                ProvisioningStatus = ClusterProvisioningStatus.Provisioning,
            });

        db.SaveChanges();

        // Two components on one cluster and none on another, so the grouping and the
        // GetValueOrDefault are both exercised rather than supplied as a literal.
        db.ClusterComponents.AddRange(
            new ClusterComponent
            {
                Id = Guid.NewGuid(),
                ClusterId = registeredId,
                Name = $"one-{suffix}",
                ComponentType = "HelmChart",
            },
            new ClusterComponent
            {
                Id = Guid.NewGuid(),
                ClusterId = registeredId,
                Name = $"two-{suffix}",
                ComponentType = "Manifest",
            });

        db.SaveChanges();
    }

    public void Dispose()
    {
        using ApplicationDbContext db = Db();

        db.ClusterComponents.RemoveRange(db.ClusterComponents.Where(c => c.ClusterId == registeredId));
        db.SaveChanges();

        db.KubernetesClusters.RemoveRange(db.KubernetesClusters.Where(
            c => c.Id == registeredId || c.Id == provisionedId
                 || c.Id == failedId || c.Id == deletingId));
        db.SaveChanges();

        db.Environments.RemoveRange(db.Environments.Where(e => e.Id == environmentId));
        db.SaveChanges();
    }

    /// <summary>
    /// ⚠️ Asserts the STATUS as well as the body. Without it a 403 or a 500 surfaces only as
    /// <c>EnumerateArray()</c> throwing on a ProblemDetails object — a failure, but one that sends
    /// the next reader to the wrong file.
    /// </summary>
    private async Task<List<JsonElement>> ClustersAsync()
    {
        (HttpStatusCode status, string? mediaType, string body) =
            await fixture.SendForResponseAsync("GET", "/api/v1/clusters", "fleet:read");

        status.Should().Be(HttpStatusCode.OK);
        mediaType.Should().Be("application/json");

        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray()];
    }

    [Fact]
    public async Task A_registered_cluster_is_published_as_None_and_every_other_state_as_itself()
    {
        Dictionary<string, string> statusByName = (await ClustersAsync())
            .ToDictionary(
                c => c.GetProperty("name").GetString() ?? "",
                c => c.GetProperty("provisioningStatus").GetString() ?? "");

        statusByName.Should().ContainKey(
            Upper, "the seed must be in the answer, or the assertions below pass over nothing");

        statusByName[Upper].Should().Be(
            "None",
            "the contract calls this state Registered; the wire has always called it None, and "
            + "PublishedProvisioningStatus is the only thing keeping it that way");

        // ⚠️ Three controls, not one. With only Provisioned beside it, a helper rewritten as
        // `status == Provisioned ? "Provisioned" : "None"` would publish None for Failed and
        // Provisioning too and still pass — so every state the seed can reach is named.
        statusByName[Lower].Should().Be("Provisioned");
        statusByName[$"failed-{suffix}"].Should().Be("Failed");
        statusByName[$"deleting-{suffix}"].Should().Be("Provisioning");
    }

    /// <summary>
    /// ⚠️ The row order, which nothing in the suite asserted for either fleet route. The CLI
    /// renders this as a table, so the order is the first thing an operator sees.
    ///
    /// <para>⚠️ <b>What this catches: weakening the comparer.</b> Replacing
    /// <c>StringComparer.Ordinal</c> with the default overload is the likely regression — it looks
    /// like a simplification — and it reorders every list, because ordinally all uppercase letters
    /// sort before all lowercase ones while any linguistic comparison, invariant culture included,
    /// compares the letters alphabetically. <c>Zebra-</c> against <c>apple-</c> is that
    /// discriminator; two names that merely differ would order the same way under both.</para>
    ///
    /// <para>⚠️ <b>What it CANNOT catch, measured rather than assumed: deleting the sort
    /// outright.</b> A mutation removing the <c>OrderBy</c> entirely still passes, and inverting
    /// the seed order did not change that. The reason is the store, not the test:
    /// <c>FleetApi.GetClustersAsync</c> does not order at all (no <c>OrderBy</c> anywhere in the
    /// module), so the rows arrive in whatever order SQLite yields — and with a unique index on
    /// <c>(TenantId, Name)</c> and TEXT columns collating BINARY, that is already ordinal name
    /// order. The test host silently supplies the behaviour the handler is responsible for.</para>
    ///
    /// <para>⚠️ <b>Which makes the handler's <c>OrderBy</c> more load-bearing than it looks, not
    /// less.</b> Production is Postgres, where a query with no <c>ORDER BY</c> has undefined row
    /// order — so the sort is doing real work there and is unverifiable here. That gap is stated
    /// rather than papered over: a reader who deletes the <c>OrderBy</c> will see a green suite,
    /// and this paragraph is the only warning they will get.</para>
    /// </summary>
    [Fact]
    public async Task Clusters_come_back_in_ordinal_name_order()
    {
        List<string> names = [.. (await ClustersAsync()).Select(c => c.GetProperty("name").GetString() ?? "")];

        int upper = names.IndexOf(Upper);
        int lower = names.IndexOf(Lower);

        upper.Should().BeGreaterThanOrEqualTo(0);
        lower.Should().BeGreaterThanOrEqualTo(0);

        upper.Should().BeLessThan(
            lower,
            "ordinally every uppercase letter sorts before every lowercase one, so Zebra- precedes "
            + "apple-; any linguistic comparison, invariant culture included, would put apple- "
            + "first — which is what makes this pair a discriminator rather than two names that "
            + "merely differ");
    }

    /// <summary>
    /// ⚠️ The component count's GROUPING and lookup. Every record-level test supplies this as a
    /// literal, so the second query, the group key and the <c>GetValueOrDefault</c> were all
    /// untested — and a count stuck at zero is indistinguishable from a cluster with nothing
    /// installed.
    /// </summary>
    [Fact]
    public async Task The_component_count_is_this_cluster_s_components_and_zero_is_published_not_omitted()
    {
        Dictionary<string, int> countByName = (await ClustersAsync())
            .ToDictionary(
                c => c.GetProperty("name").GetString() ?? "",
                c => c.GetProperty("componentCount").GetInt32());

        countByName[Upper].Should().Be(2, "two components are seeded on this cluster");

        countByName[Lower].Should().Be(
            0,
            "and a cluster with none publishes 0 rather than being absent from the list — the "
            + "group key must be the CLUSTER's id, so components do not leak across rows");
    }
}
