using EntKube.Web.Services;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Covers the pooling contract the telemetry read paths depend on: one client per cluster, reused across
/// calls. The point of the pool is that a dashboard's dozen PromQL queries share one TLS connection to the
/// API server instead of handshaking per panel, so "same kubeconfig gives back the same instance" is the
/// property that actually matters — if it regresses, the win silently disappears with nothing failing.
/// </summary>
public class KubernetesProxyClientPoolTests
{
    private static string Kubeconfig(string server) =>
        $"""
         apiVersion: v1
         kind: Config
         clusters:
         - cluster:
             server: {server}
           name: test
         contexts:
         - context:
             cluster: test
             user: test
           name: test
         current-context: test
         users:
         - name: test
           user:
             token: fake-token
         """;

    private static KubernetesProxyClientPool NewPool() =>
        new(NullLogger<KubernetesProxyClientPool>.Instance);

    [Fact]
    public void Get_ReturnsTheSameClientForTheSameKubeconfig()
    {
        using KubernetesProxyClientPool pool = NewPool();
        string config = Kubeconfig("https://k8s.example.com");

        Kubernetes first = pool.Get(config);
        Kubernetes second = pool.Get(config);

        Assert.Same(first, second);
    }

    [Fact]
    public void Get_ReturnsDistinctClientsForDifferentClusters()
    {
        using KubernetesProxyClientPool pool = NewPool();

        Kubernetes a = pool.Get(Kubeconfig("https://a.example.com"));
        Kubernetes b = pool.Get(Kubeconfig("https://b.example.com"));

        Assert.NotSame(a, b);
        Assert.Equal("https://a.example.com/", a.BaseUri.ToString());
        Assert.Equal("https://b.example.com/", b.BaseUri.ToString());
    }

    [Fact]
    public void Get_TreatsARotatedKubeconfigAsANewEntry()
    {
        using KubernetesProxyClientPool pool = NewPool();
        string server = "https://k8s.example.com";

        Kubernetes before = pool.Get(Kubeconfig(server));
        Kubernetes after = pool.Get(Kubeconfig(server).Replace("fake-token", "rotated-token"));

        // Keying on the kubeconfig's content means new credentials can never be served from the old client.
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Get_IsStableUnderConcurrentFirstUse()
    {
        using KubernetesProxyClientPool pool = NewPool();
        string config = Kubeconfig("https://k8s.example.com");

        // Every thread races to create the first entry; the compare-and-swap must leave exactly one winner
        // rather than handing different callers their own client (which would defeat connection reuse).
        Kubernetes[] clients = new Kubernetes[32];
        Parallel.For(0, clients.Length, i => clients[i] = pool.Get(config));

        Assert.Single(clients.Distinct());
    }

    [Fact]
    public void Get_RejectsAnEmptyKubeconfig()
    {
        using KubernetesProxyClientPool pool = NewPool();

        Assert.Throws<ArgumentException>(() => pool.Get(""));
        Assert.Throws<ArgumentException>(() => pool.Get("   "));
    }

    [Fact]
    public void Get_ThrowsOnceThePoolIsDisposed()
    {
        KubernetesProxyClientPool pool = NewPool();
        pool.Get(Kubeconfig("https://k8s.example.com"));
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Get(Kubeconfig("https://k8s.example.com")));
    }
}
