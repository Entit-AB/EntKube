using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for inbound mutual TLS on customer app routes.
///
/// The invariant most of these defend is that mTLS stays confined to its own listener port.
/// Client-certificate validation applies to every hostname on a port, so a mistake here does not
/// produce a broken mTLS route — it produces a gateway that demands a client certificate from
/// every other customer sharing it.
/// </summary>
public class MtlsServiceTests
{
    // ──────── Port isolation: the 443 blast radius ────────

    [Fact]
    public void BuildGatewayTlsBlock_WithNoMtlsPorts_EmitsNothing()
    {
        // A cluster with no mTLS must generate exactly the Gateway it always did.
        MtlsService.BuildGatewayTlsBlock([]).Should().BeEmpty();
    }

    [Fact]
    public void BuildGatewayTlsBlock_LeavesDefaultEmpty_SoPort443NeverRequiresAClientCertificate()
    {
        string yaml = MtlsService.BuildGatewayTlsBlock([8443]);

        // Istio falls back to `default` for any port with no perPort entry. A CA there would be
        // inherited by 443 and every hostname on it.
        yaml.Should().Contain("default: {}");
        yaml.Should().Contain("- port: 8443");
        yaml.Should().Contain("kind: ConfigMap");
        yaml.Should().Contain($"name: {MtlsService.CaConfigMapName(8443)}");
        yaml.Should().Contain("mode: AllowValidOnly");

        // The only port mentioned is the mTLS one.
        yaml.Should().NotContain("port: 443");
    }

    [Fact]
    public void BuildGatewayTlsBlock_WithSeveralPorts_EmitsOneEntryEach()
    {
        string yaml = MtlsService.BuildGatewayTlsBlock([9443, 8443, 8443]);

        yaml.Should().Contain("- port: 8443");
        yaml.Should().Contain("- port: 9443");
        yaml.Split("- port:").Length.Should().Be(3, "duplicates collapse to one entry per port");
    }

    [Theory]
    [InlineData(443)]   // would demand a client certificate from every co-hosted hostname
    [InlineData(80)]
    [InlineData(15021)] // Istio's own status port
    public void IsUsableListenerPort_RejectsPortsThatWouldBreakTheGateway(int port)
    {
        MtlsService.IsUsableListenerPort(port).Should().BeFalse();
    }

    [Theory]
    [InlineData(8443)]
    [InlineData(9443)]
    public void IsUsableListenerPort_AcceptsDedicatedPorts(int port)
    {
        MtlsService.IsUsableListenerPort(port).Should().BeTrue();
    }

    // ──────── Gateway generation ────────

    [Fact]
    public void GenerateGatewayYaml_WithoutMtls_HasNoFrontendTlsBlockOrTrustStore()
    {
        AppRoute route = Route("plain.example.com");

        string yaml = ExternalRouteService.GenerateGatewayYaml(
            "default-gateway", "istio-system", [], [route]);

        yaml.Should().NotContain("frontend:");
        yaml.Should().NotContain("ConfigMap");
        yaml.Should().Contain("hostname: plain.example.com");
    }

    [Fact]
    public void GenerateGatewayYaml_WithMtlsRoute_AddsMtlsListenerAndKeepsThePlainOne()
    {
        AppRoute route = MtlsRoute("api.acme.com", Bundle("Acme", 8443));

        string yaml = ExternalRouteService.GenerateGatewayYaml(
            "default-gateway", "istio-system", [], [route]);

        // Both listeners exist: existing clients keep using 443, mTLS clients move to 8443.
        yaml.Should().Contain("port: 443");
        yaml.Should().Contain("port: 8443");
        yaml.Should().Contain($"name: {MtlsService.MtlsListenerName("api.acme.com", 8443)}");

        // The mTLS listener terminates with the same server certificate as the plain one.
        yaml.Should().Contain($"name: {ExternalRouteService.ToCertSecretName("api.acme.com")}");

        // And the trust store ships alongside it.
        yaml.Should().Contain("kind: ConfigMap");
        yaml.Should().Contain($"name: {MtlsService.CaConfigMapName(8443)}");
        yaml.Should().Contain("ca.crt: |");
    }

    [Fact]
    public void GenerateGatewayYaml_WhenClientCertificateOnly_DropsThePlainListener()
    {
        AppRoute route = MtlsRoute("locked.acme.com", Bundle("Acme", 8443));
        route.ClientCertificateOnly = true;

        string yaml = ExternalRouteService.GenerateGatewayYaml(
            "default-gateway", "istio-system", [], [route]);

        yaml.Should().Contain("port: 8443");

        // The hostname must appear only under the mTLS listener — a lingering 443 listener would
        // leave the app reachable without a certificate, which is the whole point of this flag.
        string[] listenerBlocks = yaml.Split("    - name: ");
        listenerBlocks
            .Where(b => b.Contains("hostname: locked.acme.com"))
            .Should().OnlyContain(b => b.Contains("port: 8443"));
    }

    [Fact]
    public void GenerateGatewayYaml_WhenClientCertificateOnly_StillIssuesTheServerCertificate()
    {
        // The mTLS listener terminates TLS with the same cert-manager Secret as the plain listener
        // would have. Dropping the hostname from certificate generation because it no longer has a
        // 443 listener leaves the mTLS listener pointing at a Secret nothing creates.
        AppRoute route = MtlsRoute("locked.acme.com", Bundle("Acme", 8443));
        route.ClientCertificateOnly = true;

        string yaml = ExternalRouteService.GenerateGatewayYaml(
            "default-gateway", "istio-system", [], [route]);

        yaml.Should().Contain("kind: Certificate");
        yaml.Should().Contain($"secretName: {ExternalRouteService.ToCertSecretName("locked.acme.com")}");
        yaml.Should().Contain("- locked.acme.com");
    }

    [Fact]
    public void GenerateGatewayYaml_KeepsNonMtlsHostnamesOnPlain443Only()
    {
        AppRoute mtls = MtlsRoute("api.acme.com", Bundle("Acme", 8443));
        AppRoute plain = Route("www.globex.com");

        string yaml = ExternalRouteService.GenerateGatewayYaml(
            "default-gateway", "istio-system", [], [mtls, plain]);

        string[] blocks = yaml.Split("    - name: ");
        blocks
            .Where(b => b.Contains("hostname: www.globex.com"))
            .Should().OnlyContain(b => !b.Contains("port: 8443"),
                "a co-hosted hostname must never be pulled onto the mTLS port");
    }

    // ──────── Plan construction ────────

    [Fact]
    public void BuildPlan_WhenTrustAnchorNotLoaded_ThrowsRatherThanSilentlyDroppingMtls()
    {
        // An unloaded navigation renders identically to "no mTLS configured" — the failure mode
        // is a route that stops authenticating clients, so it must not be silent.
        AppRoute route = Route("api.acme.com");
        route.RequireClientCertificate = true;
        route.ClientCaBundleId = Guid.NewGuid();
        route.ClientCaBundle = null;

        Action act = () => MtlsService.BuildPlan([route], "istio-system");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*trust anchor was not loaded*");
    }

    [Fact]
    public void BuildPlan_IgnoresDisabledRoutes()
    {
        AppRoute route = MtlsRoute("api.acme.com", Bundle("Acme", 8443));
        route.IsEnabled = false;

        MtlsService.MtlsClusterPlan plan = MtlsService.BuildPlan([route], "istio-system");

        plan.IsEmpty.Should().BeTrue();
        plan.CaConfigMaps.Should().BeEmpty();
    }

    [Fact]
    public void BuildPlan_WhenTwoAnchorsShareAPort_WarnsAboutTheMergedTrustStore()
    {
        ClientCaBundle acme = Bundle("Acme", 8443);
        ClientCaBundle globex = Bundle("Globex", 8443);

        MtlsService.MtlsClusterPlan plan = MtlsService.BuildPlan(
            [MtlsRoute("api.acme.com", acme), MtlsRoute("api.globex.com", globex)],
            "istio-system");

        plan.BundlesByPort[8443].Should().HaveCount(2);
        plan.Warnings.Should().ContainSingle(w =>
            w.Contains("merges 2 trust anchors") && w.Contains("api.acme.com") && w.Contains("api.globex.com"));

        // Both CAs land in the one trust store the port is allowed to reference.
        plan.CaConfigMaps.Should().ContainSingle();
        plan.CaConfigMaps[0].Should().Contain("BEGIN CERTIFICATE");
    }

    [Fact]
    public void BuildPlan_WhenAnchorsHaveTheirOwnPorts_DoesNotWarnAndKeepsStoresSeparate()
    {
        MtlsService.MtlsClusterPlan plan = MtlsService.BuildPlan(
            [MtlsRoute("api.acme.com", Bundle("Acme", 8443)),
             MtlsRoute("api.globex.com", Bundle("Globex", 9443))],
            "istio-system");

        plan.Warnings.Should().BeEmpty();
        plan.CaConfigMaps.Should().HaveCount(2);
        plan.HostPorts["api.acme.com"].Should().Be(8443);
        plan.HostPorts["api.globex.com"].Should().Be(9443);
    }

    [Fact]
    public void BuildPlan_WithAnExpiredCa_Warns()
    {
        ClientCaBundle bundle = Bundle("Acme", 8443);
        bundle.Certificates[0].ExpiresAt = DateTime.UtcNow.AddDays(-1);

        MtlsService.MtlsClusterPlan plan = MtlsService.BuildPlan(
            [MtlsRoute("api.acme.com", bundle)], "istio-system");

        plan.Warnings.Should().ContainSingle(w => w.Contains("expired"));
    }

    [Fact]
    public void BuildPlan_WithAnEmptyAnchor_WarnsThatThePortWouldStopServing()
    {
        ClientCaBundle bundle = Bundle("Acme", 8443);
        bundle.Certificates.Clear();

        MtlsService.MtlsClusterPlan plan = MtlsService.BuildPlan(
            [MtlsRoute("api.acme.com", bundle)], "istio-system");

        plan.Warnings.Should().ContainSingle(w => w.Contains("no CA certificate"));
    }

    // ──────── Trust store rendering ────────

    [Fact]
    public void BuildCaConfigMapYaml_IndentsPemUnderTheCaCrtKey()
    {
        string yaml = MtlsService.BuildCaConfigMapYaml("istio-system", 8443, [Bundle("Acme", 8443)]);

        yaml.Should().Contain("kind: ConfigMap");
        yaml.Should().Contain("namespace: istio-system");
        yaml.Should().Contain("  ca.crt: |");
        yaml.Should().Contain("    -----BEGIN CERTIFICATE-----");

        // Every PEM line must sit inside the block scalar; a flush-left line would end the block
        // and produce a ConfigMap that parses but carries a truncated certificate.
        IEnumerable<string> pemLines = yaml.Split('\n')
            .SkipWhile(l => !l.Contains("ca.crt: |"))
            .Skip(1)
            .Where(l => l.Trim().Length > 0);
        pemLines.Should().OnlyContain(l => l.StartsWith("    "));
    }

    // ──────── Listener naming ────────

    [Fact]
    public void MtlsListenerName_DiffersFromThePlainListenerAndFitsTheDnsLabelLimit()
    {
        string host = new string('a', 70) + ".example.com";

        string plain = ExternalRouteService.ToListenerName(host);
        string mtls = MtlsService.MtlsListenerName(host, 8443);

        mtls.Should().NotBe(plain, "both listeners live in one Gateway and names must be unique");
        mtls.Length.Should().BeLessThanOrEqualTo(63);
        mtls.Should().EndWith("-mtls-8443");
    }

    // ──────── CA parsing ────────

    [Fact]
    public void ParseCa_AcceptsACaCertificateAndExtractsItsDetails()
    {
        using X509Certificate2 ca = CreateCa("CN=Acme Root CA");

        MtlsService.ParsedCa parsed = MtlsService.ParseCa(ca.ExportCertificatePem());

        parsed.IsCertificateAuthority.Should().BeTrue();
        parsed.Subject.Should().Contain("Acme Root CA");
        parsed.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        parsed.Fingerprint.Should().HaveLength(64);
        parsed.NormalizedPem.Should().Contain("BEGIN CERTIFICATE");
    }

    [Fact]
    public void ParseCa_RejectsALeafCertificate()
    {
        // Pasting the client certificate instead of its issuer yields a trust store that rejects
        // every client — a failure that only shows up against a live handshake.
        using X509Certificate2 leaf = CreateLeaf("CN=api-client");

        Action act = () => MtlsService.ParseCa(leaf.ExportCertificatePem());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a CA certificate*");
    }

    [Fact]
    public void ParseCa_RejectsGarbage()
    {
        Action act = () => MtlsService.ParseCa("not a certificate");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParseCa_RejectsEmptyInput()
    {
        Action act = () => MtlsService.ParseCa("   ");

        act.Should().Throw<InvalidOperationException>().WithMessage("*required*");
    }

    // ──────── Fixtures ────────

    private static AppRoute Route(string hostname) => new()
    {
        Id = Guid.NewGuid(),
        AppId = Guid.NewGuid(),
        Hostname = hostname,
        TlsMode = TlsMode.ClusterIssuer,
        ClusterIssuerName = "letsencrypt-prod",
        IsEnabled = true
    };

    private static AppRoute MtlsRoute(string hostname, ClientCaBundle bundle)
    {
        AppRoute route = Route(hostname);
        route.RequireClientCertificate = true;
        route.ClientCaBundleId = bundle.Id;
        route.ClientCaBundle = bundle;
        return route;
    }

    private static ClientCaBundle Bundle(string name, int port)
    {
        using X509Certificate2 ca = CreateCa($"CN={name} Root CA");

        return new ClientCaBundle
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = name,
            ListenerPort = port,
            Certificates =
            [
                new ClientCaCertificate
                {
                    Id = Guid.NewGuid(),
                    Name = $"{name} Root",
                    Pem = ca.ExportCertificatePem(),
                    Subject = ca.Subject,
                    ExpiresAt = ca.NotAfter.ToUniversalTime()
                }
            ]
        };
    }

    private static X509Certificate2 CreateCa(string subject)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static X509Certificate2 CreateLeaf(string subject)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
