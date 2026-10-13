using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Which store a cluster's telemetry is read from is memoized, and the memo is keyed by
/// <b>(tenant, cluster)</b> rather than by cluster. These are the tests that fail if it is not.
///
/// <para><b>The hazard.</b> Part of the decision is "is this cluster's collector still exporting to
/// the management plane", and that is a Catalog question, so it is asked as a tenant. A tenant who
/// may not see the cluster is told nothing — which, by
/// <see cref="TelemetryRoute"/>'s precedence, routes to the in-cluster node. Cached under the cluster
/// id alone, that answer would then be handed to the <em>owner</em>, whose collector is still
/// exporting here and whose node is therefore empty. An empty node answers every query
/// <em>successfully</em>, so the owner's logs, traces and dashboard panels would go blank with no
/// error anywhere — the exact failure the data-driven routing rule exists to prevent, reintroduced
/// through a cache key.</para>
///
/// <para>The cache takes its three questions as delegates, so none of this needs a cluster, a node
/// or a database: what is under test is the keying, not the answers.</para>
/// </summary>
public class TelemetryRoutingTenancyTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();
    private static readonly Guid Cluster = Guid.NewGuid();

    private static Task<bool> Yes() => Task.FromResult(true);

    private static Task<bool> No() => Task.FromResult(false);

    /// <summary>The owner's view: a node is installed, it is empty, and their collector still ships here.</summary>
    private static Task<bool?> StillHere() => Task.FromResult<bool?>(true);

    /// <summary>What a tenant who cannot see the cluster is told: nothing.</summary>
    private static Task<bool?> Unknown() => Task.FromResult<bool?>(null);

    private static Task<bool> Decide(RouteCache routes, Guid tenantId, Func<Task<bool?>> stillReceives)
        => routes.UseInClusterAsync(
            tenantId, Cluster,
            nodePresent: Yes,
            nodeHasData: No,            // the node exists and is empty — the state the cutover passes through
            stillReceives: stillReceives,
            logger: NullLogger.Instance);

    [Fact]
    public async Task The_owner_reads_the_management_plane_and_a_stranger_is_routed_elsewhere()
    {
        RouteCache routes = new();

        (await Decide(routes, Owner, StillHere)).Should().BeFalse(
            "their collector still exports here, so the data is here and not in the empty node");
        (await Decide(routes, Stranger, Unknown)).Should().BeTrue(
            "told nothing, the decision falls back to the node — which is the answer that must not be shared");
    }

    /// <summary>
    /// The same two questions in the other order — the one that fails against a cluster-only key,
    /// because the stranger's answer is cached first and the owner then reads it.
    /// </summary>
    [Fact]
    public async Task A_strangers_route_is_not_cached_for_the_owner()
    {
        RouteCache routes = new();

        (await Decide(routes, Stranger, Unknown)).Should().BeTrue();
        (await Decide(routes, Owner, StillHere)).Should().BeFalse(
            "asked second, the owner must still be sent to the store that holds their data");
    }

    /// <summary>
    /// And it still memoizes: within the window the answer stands even though the question would now
    /// answer differently. Without this, the test above would pass just as well against a cache that
    /// had been deleted — which is how a ratchet on a cache key quietly becomes a ratchet on nothing.
    /// </summary>
    [Fact]
    public async Task The_route_is_memoized_within_its_window()
    {
        RouteCache routes = new();

        (await Decide(routes, Owner, StillHere)).Should().BeFalse();
        (await Decide(routes, Owner, Unknown)).Should().BeFalse(
            "the decision is memoized for a few seconds, so this is the first answer, not a second one");
    }
}
