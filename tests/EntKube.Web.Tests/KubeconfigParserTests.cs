using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// A kubeconfig file contains clusters, users, and contexts. When a user pastes
/// or uploads a kubeconfig, we need to parse it and present the available contexts
/// so they can choose which cluster to register.
/// </summary>
public class KubeconfigParserTests
{
    private const string SingleContextKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
        - cluster:
            server: https://k8s.prod.example.com:6443
            certificate-authority-data: LS0tLS1C...
          name: prod-cluster
        contexts:
        - context:
            cluster: prod-cluster
            user: admin-user
            namespace: default
          name: prod-context
        current-context: prod-context
        users:
        - name: admin-user
          user:
            token: secret-token-123
        """;

    private const string MultiContextKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
        - cluster:
            server: https://k8s.dev.example.com:6443
          name: dev-cluster
        - cluster:
            server: https://k8s.staging.example.com:6443
          name: staging-cluster
        - cluster:
            server: https://k8s.prod.example.com:6443
          name: prod-cluster
        contexts:
        - context:
            cluster: dev-cluster
            user: dev-user
          name: dev
        - context:
            cluster: staging-cluster
            user: staging-user
          name: staging
        - context:
            cluster: prod-cluster
            user: prod-user
          name: production
        current-context: dev
        users:
        - name: dev-user
          user:
            token: dev-token
        - name: staging-user
          user:
            token: staging-token
        - name: prod-user
          user:
            token: prod-token
        """;

    [Fact]
    public void ParseContexts_SingleContext_ReturnsOneEntry()
    {
        // A kubeconfig with one context should return that context
        // with its name and the referenced cluster's server URL.

        List<KubeconfigContext> contexts = KubeconfigParser.ParseContexts(SingleContextKubeconfig);

        contexts.Should().HaveCount(1);
        contexts[0].Name.Should().Be("prod-context");
        contexts[0].ClusterServer.Should().Be("https://k8s.prod.example.com:6443");
    }

    [Fact]
    public void ParseContexts_MultipleContexts_ReturnsAll()
    {
        // When multiple contexts exist, the user must choose.

        List<KubeconfigContext> contexts = KubeconfigParser.ParseContexts(MultiContextKubeconfig);

        contexts.Should().HaveCount(3);
        contexts.Select(c => c.Name).Should().Contain(["dev", "staging", "production"]);
    }

    [Fact]
    public void ParseContexts_ResolvesServerUrlFromClusterReference()
    {
        // Each context references a cluster by name — we resolve that
        // to the actual server URL so the user sees something meaningful.

        List<KubeconfigContext> contexts = KubeconfigParser.ParseContexts(MultiContextKubeconfig);

        KubeconfigContext dev = contexts.First(c => c.Name == "dev");
        dev.ClusterServer.Should().Be("https://k8s.dev.example.com:6443");

        KubeconfigContext prod = contexts.First(c => c.Name == "production");
        prod.ClusterServer.Should().Be("https://k8s.prod.example.com:6443");
    }

    [Fact]
    public void ParseContexts_IdentifiesCurrentContext()
    {
        // The current-context field tells us which context is active by default.

        List<KubeconfigContext> contexts = KubeconfigParser.ParseContexts(MultiContextKubeconfig);

        contexts.First(c => c.Name == "dev").IsCurrent.Should().BeTrue();
        contexts.First(c => c.Name == "staging").IsCurrent.Should().BeFalse();
    }

    [Fact]
    public void ParseContexts_EmptyOrInvalidYaml_ReturnsEmptyList()
    {
        // Invalid input should not throw — just return empty.

        KubeconfigParser.ParseContexts("").Should().BeEmpty();
        KubeconfigParser.ParseContexts("not: valid: kubeconfig").Should().BeEmpty();
        KubeconfigParser.ParseContexts("random text").Should().BeEmpty();
    }

    [Fact]
    public void ParseContexts_MissingClusterReference_SkipsContext()
    {
        // If a context references a cluster that doesn't exist in the file, skip it.

        string brokenConfig = """
            apiVersion: v1
            kind: Config
            clusters: []
            contexts:
            - context:
                cluster: nonexistent
                user: someone
              name: orphan-context
            current-context: orphan-context
            users: []
            """;

        List<KubeconfigContext> contexts = KubeconfigParser.ParseContexts(brokenConfig);

        contexts.Should().BeEmpty();
    }
}
