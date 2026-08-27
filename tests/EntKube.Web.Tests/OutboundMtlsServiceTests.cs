using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for outbound mTLS — the client certificate a customer app presents to a partner API.
///
/// The two things worth pinning are the pieces Istio silently ignores when they are wrong: a
/// DestinationRule without a workloadSelector drops <c>credentialName</c> on the floor and
/// originates no certificate at all, and a mesh-originated credential only works if the app calls
/// the partner in plaintext.
/// </summary>
public class OutboundMtlsServiceTests
{
    // ──────── Secret ────────

    [Fact]
    public void BuildSecretYaml_UsesTheKeyNamesIstiosSdsReads()
    {
        string yaml = OutboundMtlsService.BuildSecretYaml("acme-prod", "partner-api", Bundle(withCa: true));

        yaml.Should().Contain("kind: Secret");
        yaml.Should().Contain("namespace: acme-prod");
        yaml.Should().Contain("tls.crt:");
        yaml.Should().Contain("tls.key:");
        yaml.Should().Contain("ca.crt:");
    }

    [Fact]
    public void BuildSecretYaml_IsOpaqueNotTlsTyped()
    {
        // kubernetes.io/tls cannot carry ca.crt, and the partner's CA is exactly what verifies the
        // far end of the connection.
        string yaml = OutboundMtlsService.BuildSecretYaml("acme-prod", "partner-api", Bundle(withCa: true));

        yaml.Should().Contain("type: Opaque");
        yaml.Should().NotContain("kubernetes.io/tls");
    }

    [Fact]
    public void BuildSecretYaml_Base64EncodesTheMaterial()
    {
        string yaml = OutboundMtlsService.BuildSecretYaml("acme-prod", "partner-api", Bundle(withCa: false));

        // The private key must never appear as readable text in a manifest.
        yaml.Should().NotContain("BEGIN PRIVATE KEY");
        yaml.Should().Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes("key-pem")));
    }

    [Fact]
    public void BuildSecretYaml_WithoutAPartnerCa_OmitsCaCrt()
    {
        string yaml = OutboundMtlsService.BuildSecretYaml("acme-prod", "partner-api", Bundle(withCa: false));

        yaml.Should().NotContain("ca.crt:");
    }

    // ──────── ServiceEntry ────────

    [Fact]
    public void BuildServiceEntryYaml_RedirectsThePlaintextPortToTheTlsPort()
    {
        // targetPort is what lets the app speak HTTP while the partner sees TLS.
        string yaml = OutboundMtlsService.BuildServiceEntryYaml("acme-prod", "partner-api", "api.partner.com", 443, 80);

        yaml.Should().Contain("kind: ServiceEntry");
        yaml.Should().Contain("- api.partner.com");
        yaml.Should().Contain("- number: 80");
        yaml.Should().Contain("targetPort: 443");
        yaml.Should().Contain("resolution: DNS");
    }

    // ──────── DestinationRule ────────

    [Fact]
    public void BuildDestinationRuleYaml_CarriesTheWorkloadSelectorCredentialNameAndSni()
    {
        string yaml = OutboundMtlsService.BuildDestinationRuleYaml(
            "acme-prod", "partner-api", "api.partner.com", 80, "partner-api",
            new Dictionary<string, string> { ["app"] = "billing" });

        yaml.Should().Contain("workloadSelector:");
        yaml.Should().Contain("app: billing");
        yaml.Should().Contain("mode: MUTUAL");
        yaml.Should().Contain("credentialName: partner-api");
        yaml.Should().Contain("sni: api.partner.com");

        // The rule must match the port the app actually connects to, not the partner's TLS port.
        yaml.Should().Contain("number: 80");
    }

    // ──────── Validation ────────

    [Fact]
    public void ParseSelector_ReadsLabels()
    {
        Dictionary<string, string> labels = OutboundMtlsService.ParseSelector("""{"app":"billing","tier":"web"}""");

        labels.Should().HaveCount(2);
        labels["app"].Should().Be("billing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void ParseSelector_TreatsMissingOrBrokenJsonAsNoLabels(string? json)
    {
        // "No labels" is what the mesh-originated validation refuses, so degrading to empty keeps
        // a malformed selector from quietly producing a rule that originates nothing.
        OutboundMtlsService.ParseSelector(json).Should().BeEmpty();
    }

    // ──────── Port pairing and call hint ────────

    [Theory]
    [InlineData(443, 80)]
    [InlineData(8443, 8443)]
    public void PlainPortFor_PairsTheAppSidePort(int partnerPort, int expected)
    {
        OutboundMtlsService.PlainPortFor(partnerPort).Should().Be(expected);
    }

    [Fact]
    public void CallHint_ForMeshOrigination_TellsTheAppToUseHttp()
    {
        // The counter-intuitive half of this feature: the sidecar can only add a client certificate
        // to a handshake it performs itself, so the app must not do its own TLS.
        OutboundMtlsCredential credential = Credential(OutboundMtlsMode.MeshOriginated);

        OutboundMtlsService.CallHint(credential).Should().Be("http://api.partner.com:80");
    }

    [Fact]
    public void CallHint_ForSecretOnly_TellsTheAppToUseHttps()
    {
        OutboundMtlsCredential credential = Credential(OutboundMtlsMode.SecretOnly);

        OutboundMtlsService.CallHint(credential).Should().Be("https://api.partner.com:443");
    }

    // ──────── Whole manifest ────────

    [Fact]
    public void BuildManifest_ForMeshOrigination_EmitsAllThreeResources()
    {
        string manifest = OutboundMtlsService.BuildManifest(
            Credential(OutboundMtlsMode.MeshOriginated), Bundle(withCa: true), "acme-prod");

        manifest.Should().Contain("kind: Secret");
        manifest.Should().Contain("kind: ServiceEntry");
        manifest.Should().Contain("kind: DestinationRule");
    }

    [Fact]
    public void BuildManifest_ForSecretOnly_EmitsNoMeshResources()
    {
        // A workload outside the mesh gets the Secret and nothing that pretends to act on it.
        string manifest = OutboundMtlsService.BuildManifest(
            Credential(OutboundMtlsMode.SecretOnly), Bundle(withCa: true), "acme-prod");

        manifest.Should().Contain("kind: Secret");
        manifest.Should().NotContain("kind: ServiceEntry");
        manifest.Should().NotContain("kind: DestinationRule");
    }

    // ──────── Fixtures ────────

    private static CertificateBundle Bundle(bool withCa) => new()
    {
        Certificate = "cert-pem",
        PrivateKey = "key-pem",
        CaCertificate = withCa ? "ca-pem" : null
    };

    private static OutboundMtlsCredential Credential(OutboundMtlsMode mode) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        AppId = Guid.NewGuid(),
        Name = "partner-api",
        Host = "api.partner.com",
        Port = 443,
        VaultSecretId = Guid.NewGuid(),
        Mode = mode,
        WorkloadSelectorJson = """{"app":"billing"}"""
    };
}
