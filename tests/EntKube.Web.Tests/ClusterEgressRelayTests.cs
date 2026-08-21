using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the in-cluster egress relay — the route that lets EntKube reach a
/// cloud whose API allowlists the provider's own network rather than wherever
/// EntKube happens to run.
///
/// The generated nginx config is the part that carries real risk: it decides what
/// the relay will and will not forward, and a mistake there either breaks every
/// call or turns the relay into an open proxy inside a customer's cluster.
/// </summary>
public class ClusterEgressRelayTests
{
    private static ClusterEgressRelay CreateRelay(Mock<IKubernetesClientFactory>? k8s = null) =>
        new((k8s ?? new Mock<IKubernetesClientFactory>()).Object, NullLogger<ClusterEgressRelay>.Instance);

    // ──────── Host normalization ────────

    [Fact]
    public void NormalizeHosts_reduces_urls_to_bare_hostnames()
    {
        // SNI carries a hostname only, so that is what the map has to key on.
        List<string> result = ClusterEgressRelay.NormalizeHosts(
            ["https://identity.example.com:5000/v3", "https://s3-kna1.citycloud.com"]);

        result.Should().BeEquivalentTo(["identity.example.com", "s3-kna1.citycloud.com"]);
    }

    [Fact]
    public void NormalizeHosts_accepts_a_bare_host_with_a_port()
    {
        ClusterEgressRelay.NormalizeHosts(["identity.example.com:5000"])
            .Should().BeEquivalentTo(["identity.example.com"]);
    }

    [Fact]
    public void NormalizeHosts_deduplicates_case_insensitively()
    {
        // The same host reached as an auth URL and as a catalog endpoint must not
        // produce two map entries — nginx rejects a duplicate key outright.
        ClusterEgressRelay.NormalizeHosts(
            ["https://Identity.Example.com:5000/v3", "identity.example.com", "IDENTITY.EXAMPLE.COM:443"])
            .Should().ContainSingle().Which.Should().Be("identity.example.com");
    }

    [Fact]
    public void NormalizeHosts_drops_blank_entries()
    {
        ClusterEgressRelay.NormalizeHosts(["", "   ", "identity.example.com"])
            .Should().ContainSingle().Which.Should().Be("identity.example.com");
    }

    // ──────── Generated nginx config ────────

    [Fact]
    public void Config_routes_each_allowed_host_to_itself_on_443()
    {
        string config = ClusterEgressRelay.BuildNginxConfig(
            ["identity.example.com", "s3-kna1.citycloud.com"], "10.96.0.10");

        config.Should().Contain("\"identity.example.com\" \"identity.example.com:443\";");
        config.Should().Contain("\"s3-kna1.citycloud.com\" \"s3-kna1.citycloud.com:443\";");
    }

    [Fact]
    public void Config_defaults_to_an_empty_upstream_so_it_is_not_an_open_relay()
    {
        // Anything not on the allowlist must map to "" — nginx then refuses to
        // connect rather than forwarding wherever the caller asked.
        string config = ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10");

        config.Should().Contain("default \"\";");
        config.Should().NotContain("evil.example.com");
    }

    [Fact]
    public void Config_passes_tls_through_rather_than_terminating_it()
    {
        // ssl_preread is the whole mechanism: the relay reads the ClientHello for
        // routing and never decrypts, so the caller's certificate validation and
        // S3 request signing keep seeing the real endpoint.
        string config = ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10");

        config.Should().Contain("ssl_preread on;");
        config.Should().Contain("proxy_pass $entkube_upstream;");
        config.Should().NotContain("ssl_certificate");
    }

    [Fact]
    public void Config_uses_the_discovered_cluster_dns_for_runtime_lookups()
    {
        string config = ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "172.20.0.10");

        config.Should().Contain("resolver 172.20.0.10 ipv6=off");
    }

    [Fact]
    public void Config_listens_on_the_port_the_tunnel_forwards_to()
    {
        // The tunnel hardcodes this port; a drift between the two would forward to
        // nothing and fail only at runtime.
        ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10")
            .Should().Contain($"listen {ClusterEgressRelay.Port};");
    }

    [Fact]
    public void Config_survives_a_hostname_that_looks_like_a_directive()
    {
        // Map keys are quoted so a leading digit or hyphen cannot be parsed as
        // nginx syntax.
        string config = ClusterEgressRelay.BuildNginxConfig(["3-api.example.com"], "10.96.0.10");

        config.Should().Contain("\"3-api.example.com\" \"3-api.example.com:443\";");
    }

    // ──────── Manifest ────────

    [Fact]
    public void Manifest_rolls_the_pods_when_the_allowlist_changes()
    {
        // A ConfigMap update alone leaves nginx running the old config, so the
        // pod template must carry a hash of it.
        ClusterEgressRelay relay = CreateRelay();

        string first = relay.BuildManifest(
            ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10"));
        string second = relay.BuildManifest(
            ClusterEgressRelay.BuildNginxConfig(["identity.example.com", "nova.example.com"], "10.96.0.10"));

        string FirstHash(string manifest) =>
            manifest.Split('\n').First(l => l.Contains("entkube.io/config-hash")).Trim();

        FirstHash(first).Should().NotBe(FirstHash(second));
    }

    [Fact]
    public void Manifest_is_stable_for_unchanged_input()
    {
        ClusterEgressRelay relay = CreateRelay();
        string config = ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10");

        relay.BuildManifest(config).Should().Be(relay.BuildManifest(config));
    }

    [Fact]
    public void Manifest_embeds_the_config_as_valid_block_scalar_yaml()
    {
        // The config is injected into a YAML literal block, so every line needs the
        // block's indentation or kubectl rejects the whole manifest.
        ClusterEgressRelay relay = CreateRelay();
        string config = ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10");

        string manifest = relay.BuildManifest(config);
        string[] lines = manifest.Split('\n');

        int start = Array.FindIndex(lines, l => l.Contains("nginx.conf: |"));
        start.Should().BeGreaterThan(0);

        // Every non-blank line until the next document break must be indented past
        // the "  nginx.conf:" key.
        for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("---"); i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            lines[i].Should().StartWith("    ", because: $"line {i} is inside the block scalar: '{lines[i]}'");
        }
    }

    [Fact]
    public void Manifest_satisfies_the_restricted_pod_security_standards()
    {
        // Clusters commonly enforce these via Kyverno, and admission rejects the
        // pod outright if the seccomp profile is missing — the relay is useless if
        // it cannot be admitted.
        ClusterEgressRelay relay = CreateRelay();
        string manifest = relay.BuildManifest(
            ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10"));

        manifest.Should().Contain("seccompProfile:");
        manifest.Should().Contain("type: RuntimeDefault");
        manifest.Should().Contain("runAsNonRoot: true");
        manifest.Should().Contain("allowPrivilegeEscalation: false");
        manifest.Should().Contain("readOnlyRootFilesystem: true");
        manifest.Should().Contain("drop: [\"ALL\"]");
    }

    [Fact]
    public void Manifest_pins_the_image_rather_than_tracking_latest()
    {
        // A floating tag both breaks reproducibility and trips the common
        // disallow-latest-tag admission policy.
        ClusterEgressRelay relay = CreateRelay();
        string manifest = relay.BuildManifest(
            ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10"));

        manifest.Should().NotContain(":latest");
        manifest.Should().MatchRegex(@"image: \S+:\d+\.\d+");
    }

    [Fact]
    public void Manifest_does_not_mount_a_service_account_token()
    {
        // The relay forwards TCP and never calls the API server.
        ClusterEgressRelay relay = CreateRelay();

        relay.BuildManifest(ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10"))
            .Should().Contain("automountServiceAccountToken: false");
    }

    [Fact]
    public void Manifest_names_the_service_and_port_the_tunnel_forwards_to()
    {
        ClusterEgressRelay relay = CreateRelay();
        string manifest = relay.BuildManifest(
            ClusterEgressRelay.BuildNginxConfig(["identity.example.com"], "10.96.0.10"));

        manifest.Should().Contain($"name: {ClusterEgressRelay.Name}");
        manifest.Should().Contain($"namespace: {ClusterEgressRelay.Namespace}");
        manifest.Should().Contain($"port: {ClusterEgressRelay.Port}");
    }

    // ──────── Guard rails ────────

    [Fact]
    public async Task Ensure_refuses_an_empty_allowlist()
    {
        // A relay that forwards nothing looks deployed but drops every call, which
        // is a worse failure than refusing up front.
        ClusterEgressRelay relay = CreateRelay();

        Func<Task> act = async () => await relay.EnsureAsync("kubeconfig", []);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty allowlist*");
    }

    [Fact]
    public async Task Ensure_applies_the_manifest_to_the_target_cluster()
    {
        Mock<IKubernetesClientFactory> k8s = new();
        k8s.Setup(x => x.GetJsonAsync("svc", "kube-system", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync("""{"items":[{"metadata":{"name":"kube-dns"},"spec":{"clusterIP":"10.96.0.10"}}]}""");

        string? applied = null;
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, CancellationToken>((m, _, _) => applied = m)
           .Returns(Task.CompletedTask);

        await CreateRelay(k8s).EnsureAsync("kubeconfig", ["https://identity.example.com:5000/v3"]);

        applied.Should().NotBeNull();
        applied.Should().Contain("kind: Deployment");
        applied.Should().Contain("\"identity.example.com\" \"identity.example.com:443\";");
        applied.Should().Contain("resolver 10.96.0.10");
    }

    [Fact]
    public async Task Ensure_falls_back_to_the_default_dns_ip_when_the_service_cannot_be_read()
    {
        // A wrong resolver breaks every lookup, so the fallback needs to be a real
        // address rather than a placeholder.
        Mock<IKubernetesClientFactory> k8s = new();
        k8s.Setup(x => x.GetJsonAsync("svc", "kube-system", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("forbidden"));

        string? applied = null;
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .Callback<string, string, CancellationToken>((m, _, _) => applied = m)
           .Returns(Task.CompletedTask);

        await CreateRelay(k8s).EnsureAsync("kubeconfig", ["identity.example.com"]);

        applied.Should().Contain("resolver 10.96.0.10");
    }

    // ──────── Egress transports ────────

    [Fact]
    public void A_relay_egress_and_a_proxy_egress_are_distinct_transports()
    {
        using OpenStackHttpFactory factory = new(new Mock<IHttpClientFactory>().Object, null!);
        Amazon.S3.AmazonS3Config config = new();

        string relay = factory.CreateAwsHttpClientFactory(new ResolvedEgress(RelayLocalPort: 5000))!
            .GetConfigUniqueString(config);
        string proxy = factory.CreateAwsHttpClientFactory(
            new ResolvedEgress(new OpenStackProxy("socks5://127.0.0.1:5000")))!.GetConfigUniqueString(config);

        relay.Should().NotBe(proxy);
    }

    [Fact]
    public void Relay_tunnels_on_different_ports_do_not_share_a_pool()
    {
        // A restarted tunnel gets a new local port; reusing the old handler would
        // keep dialling a port nothing listens on.
        using OpenStackHttpFactory factory = new(new Mock<IHttpClientFactory>().Object, null!);
        Amazon.S3.AmazonS3Config config = new();

        factory.CreateAwsHttpClientFactory(new ResolvedEgress(RelayLocalPort: 5000))!.GetConfigUniqueString(config)
            .Should().NotBe(
                factory.CreateAwsHttpClientFactory(new ResolvedEgress(RelayLocalPort: 5001))!.GetConfigUniqueString(config));
    }

    [Fact]
    public void A_direct_egress_produces_no_aws_override()
    {
        using OpenStackHttpFactory factory = new(new Mock<IHttpClientFactory>().Object, null!);

        factory.CreateAwsHttpClientFactory(new ResolvedEgress()).Should().BeNull();
    }
}
