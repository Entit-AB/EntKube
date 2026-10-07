using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Listing the Tailscale ingresses on a cluster.
///
/// <para><b>Why this exists.</b> Found while writing the first coverage for
/// <see cref="HeadscaleService"/>, which had the same defect: <c>?[0]</c> on a JSON array looks
/// null-safe and is not. The null-conditional operator guards against the array being
/// <em>absent</em>; indexing one that is present but <em>empty</em> throws
/// <see cref="ArgumentOutOfRangeException"/>. Three of these sat in one loop, with no try/catch
/// around it, over arrays that are all legitimately empty — and
/// <c>status.loadBalancer.ingress</c> is empty for every LoadBalancer that has not been assigned
/// an address yet. So one freshly created Ingress took the entire listing down with
/// "Index was out of range".</para>
/// </summary>
public class TailscaleIngressListingTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly TailscaleService sut;

    public TailscaleIngressListingTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        TestDbContextFactory factory = new(connection);
        using ApplicationDbContext db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        sut = new TailscaleService(factory, k8s.Object);
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ClusterReturns(string json) =>
        k8s.Setup(x => x.GetJsonAllNamespacesAsync("ingresses", It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);

    [Fact]
    public async Task A_pending_load_balancer_does_not_take_the_whole_listing_down()
    {
        // Exactly what the API returns in the seconds after an Ingress is created: the object is
        // there, status.loadBalancer.ingress is an empty array.
        ClusterReturns("""
            {"items":[{
              "metadata": {"name":"grafana","namespace":"monitoring"},
              "spec": {
                "ingressClassName":"tailscale",
                "rules":[{"host":"grafana","http":{"paths":[
                  {"backend":{"service":{"name":"grafana","port":{"number":3000}}}}]}}]
              },
              "status": {"loadBalancer":{"ingress":[]}}
            }]}
            """);

        List<TailscaleIngress> result = await sut.ListIngressesAsync("kubeconfig");

        result.Should().ContainSingle();
        result[0].Name.Should().Be("grafana");
        result[0].Namespace.Should().Be("monitoring");
        result[0].ServiceName.Should().Be("grafana");
        result[0].ServicePort.Should().Be(3000);

        // No address yet, reported as such rather than as a crash.
        result[0].LoadBalancerHostname.Should().BeNull();
    }

    [Fact]
    public async Task An_ingress_with_no_rules_is_listed_rather_than_throwing()
    {
        // A default-backend-only Ingress is valid and carries no rules KEY at all — which is a
        // different path from an empty rules array; see the test below for that one.
        ClusterReturns("""
            {"items":[{
              "metadata": {"name":"bare","namespace":"default"},
              "spec": {"ingressClassName":"tailscale"},
              "status": {"loadBalancer":{"ingress":[{"hostname":"bare.tailnet.ts.net"}]}}
            }]}
            """);

        List<TailscaleIngress> result = await sut.ListIngressesAsync("kubeconfig");

        result.Should().ContainSingle();
        result[0].TailscaleHostname.Should().BeNull();
        result[0].ServiceName.Should().BeEmpty();
        result[0].ServicePort.Should().Be(80, "the documented default when no backend port is given");
        result[0].LoadBalancerHostname.Should().Be("bare.tailnet.ts.net");
    }

    /// <summary>
    /// The case the first version of this file missed, and mutation testing caught: an array that
    /// is <em>present but empty</em>.
    ///
    /// <para>Omitting the key entirely — <c>"spec": {"ingressClassName":"tailscale"}</c> — makes
    /// <c>item["spec"]?["rules"]</c> null, and <c>?[0]</c> on null short-circuits harmlessly. So a
    /// test written that way passes against the bug. Only <c>"rules": []</c> reaches the index and
    /// throws. The distinction is the whole defect, so it needs its own case.</para>
    /// </summary>
    [Fact]
    public async Task An_ingress_whose_rules_array_is_present_but_empty_does_not_throw()
    {
        ClusterReturns("""
            {"items":[{
              "metadata": {"name":"empty-rules","namespace":"default"},
              "spec": {"ingressClassName":"tailscale","rules":[]},
              "status": {"loadBalancer":{"ingress":[]}}
            }]}
            """);

        List<TailscaleIngress> result = await sut.ListIngressesAsync("kubeconfig");

        result.Should().ContainSingle();
        result[0].Name.Should().Be("empty-rules");
        result[0].TailscaleHostname.Should().BeNull();
    }

    /// <summary>And the same one level down: a rule carrying an empty paths array.</summary>
    [Fact]
    public async Task A_rule_whose_paths_array_is_present_but_empty_does_not_throw()
    {
        ClusterReturns("""
            {"items":[{
              "metadata": {"name":"empty-paths","namespace":"default"},
              "spec": {"ingressClassName":"tailscale",
                "rules":[{"host":"somewhere","http":{"paths":[]}}]},
              "status": {"loadBalancer":{"ingress":[]}}
            }]}
            """);

        List<TailscaleIngress> result = await sut.ListIngressesAsync("kubeconfig");

        result.Should().ContainSingle();
        result[0].TailscaleHostname.Should().Be("somewhere");
        result[0].ServiceName.Should().BeEmpty();
        result[0].ServicePort.Should().Be(80);
    }

    [Fact]
    public async Task An_assigned_address_is_reported()
    {
        ClusterReturns("""
            {"items":[{
              "metadata": {"name":"api","namespace":"apps"},
              "spec": {"ingressClassName":"tailscale",
                "rules":[{"host":"api","http":{"paths":[
                  {"backend":{"service":{"name":"api","port":{"number":8080}}}}]}}]},
              "status": {"loadBalancer":{"ingress":[{"hostname":"api.tailnet.ts.net"}]}}
            }]}
            """);

        (await sut.ListIngressesAsync("kubeconfig")).Should().ContainSingle()
            .Which.LoadBalancerHostname.Should().Be("api.tailnet.ts.net");
    }

    [Fact]
    public async Task Ingresses_served_by_something_else_are_left_alone()
    {
        ClusterReturns("""
            {"items":[
              {"metadata":{"name":"nginx-one","namespace":"web"},
               "spec":{"ingressClassName":"nginx","rules":[]},"status":{}},
              {"metadata":{"name":"ts-one","namespace":"web"},
               "spec":{"ingressClassName":"tailscale"},"status":{}}
            ]}
            """);

        // The class filter is what keeps this view about the VPN; an empty rules array on the
        // foreign one must not throw on the way past it either.
        (await sut.ListIngressesAsync("kubeconfig")).Should().ContainSingle()
            .Which.Name.Should().Be("ts-one");
    }
}
