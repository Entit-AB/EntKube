using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Jit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What the seam hands over when a caller needs to <em>reach</em> a cluster rather than command it.
///
/// <para><c>GetConnectionAsync</c> exists for <c>JitProxyService</c>, which must talk to the API
/// server while deliberately <b>not</b> presenting EntKube's identity: presenting the stored client
/// certificate would make the API server authenticate the certificate and ignore the grant's bound
/// token, so the request would execute as cluster-admin instead of as the grant. Taking the whole
/// kubeconfig to use one third of it was how that service came to hold a credential it was careful
/// never to use.</para>
///
/// <para><b>Why this verb is safe where the proxy verb needed an argument.</b> A CA bundle and an
/// address are <em>strictly less</em> than a kubeconfig — you cannot authenticate with them. That is
/// the test for any future seam addition: does it reduce authority, or only move the credential?</para>
/// </summary>
public class ClusterConnectionSeamTests
{
    private static string KubeconfigWith(string server, string? caPem, bool skipTls)
    {
        string cluster = skipTls
            ? $"    server: {server}\n    insecure-skip-tls-verify: true"
            : caPem is null
                ? $"    server: {server}"
                : $"    server: {server}\n    certificate-authority-data: "
                  + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(caPem));

        return $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: c
              cluster:
            {cluster}
            contexts:
            - name: c
              context:
                cluster: c
                user: u
            current-context: c
            users:
            - name: u
              user:
                token: SUPER-SECRET-ADMIN-TOKEN
            """;
    }

    private static string SelfSignedPem()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return cert.ExportCertificatePem();
    }

    private static IKubernetesClientFactory Factory() =>
        new KubernetesClientFactory(
            new Mock<EntKube.Web.Services.ClusterChanges.IClusterChangeGate>().Object,
            new KubernetesProxyClientPool(NullLogger<KubernetesProxyClientPool>.Instance));

    [Fact]
    public void The_connection_carries_the_address_and_the_ca()
    {
        ClusterConnection connection =
            Factory().ReadConnection(KubeconfigWith("https://k8s.example.com:6443", SelfSignedPem(), skipTls: false));

        connection.Server.Should().Be(new Uri("https://k8s.example.com:6443/"));
        connection.CaCerts.Should().NotBeNullOrEmpty();
        connection.SkipTlsVerify.Should().BeFalse();
    }

    /// <summary>
    /// The point of the whole change: a <see cref="ClusterConnection"/> has no property that could
    /// authenticate to the API server. Asserted structurally, because "we only read three fields"
    /// is a claim about today's code and this is a claim about the type.
    /// </summary>
    [Fact]
    public void The_connection_carries_nothing_that_authenticates()
    {
        typeof(ClusterConnection).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(["Server", "CaCerts", "SkipTlsVerify"])
            .And.NotContain(n =>
                n.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Kubeconfig", StringComparison.OrdinalIgnoreCase)
                || n.Contains("ClientCert", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A kubeconfig carrying an admin token yields a connection that does not. The token is right
    /// there in the input, so this is the assertion that the omission is real rather than incidental.
    /// </summary>
    [Fact]
    public void An_admin_credential_in_the_kubeconfig_does_not_come_out_the_other_side()
    {
        ClusterConnection connection =
            Factory().ReadConnection(KubeconfigWith("https://k8s.example.com:6443", SelfSignedPem(), skipTls: false));

        string rendered = $"{connection.Server}|{connection.SkipTlsVerify}|"
            + string.Join(',', (connection.CaCerts ?? []).Cast<X509Certificate2>().Select(c => c.Subject));

        rendered.Should().NotContain("SUPER-SECRET-ADMIN-TOKEN");
    }

    /// <summary>
    /// <c>insecure-skip-tls-verify</c> is carried rather than resolved, because it is the operator's
    /// choice and a caller that silently ignored it would verify against a CA they declined to
    /// trust. The pool honours it and warns, which is the behaviour that was already there.
    /// </summary>
    [Fact]
    public void Skip_tls_verify_is_carried_through()
    {
        ClusterConnection connection =
            Factory().ReadConnection(KubeconfigWith("https://k8s.example.com:6443", caPem: null, skipTls: true));

        connection.SkipTlsVerify.Should().BeTrue();
    }

    /// <summary>
    /// The upstream pool keyed on a hash of the kubeconfig text, so any rotation rebuilt the client
    /// — including a client-certificate rotation it never used. Keying on the connection keeps the
    /// property that mattered: a different address or CA is a different upstream.
    /// </summary>
    [Fact]
    public void Two_connections_to_different_servers_are_different_upstreams()
    {
        using JitUpstreamClientPool pool = new(NullLogger<JitUpstreamClientPool>.Instance);

        JitUpstreamClientPool.Upstream a =
            pool.Get(new ClusterConnection(new Uri("https://a.example:6443"), null, false));
        JitUpstreamClientPool.Upstream b =
            pool.Get(new ClusterConnection(new Uri("https://b.example:6443"), null, false));
        JitUpstreamClientPool.Upstream again =
            pool.Get(new ClusterConnection(new Uri("https://a.example:6443"), null, false));

        a.BaseAddress.Should().NotBe(b.BaseAddress);
        again.Client.Should().BeSameAs(a.Client, "the same connection reuses the warm client");
    }
}
