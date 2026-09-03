using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.SupplyChain;
using FluentAssertions;
using YamlDotNet.Serialization;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for supply-chain security: matching a running image to its registry
/// artifact, and generating a Kyverno verifyImages policy that actually parses.
/// </summary>
public class SupplyChainTests
{
    // ── Matching a running image to a registry artifact ──

    private static readonly List<HarborArtifactInfo> Artifacts =
    [
        new() { Digest = "sha256:aaa", Tags = ["1.2", "latest"] },
        new() { Digest = "sha256:bbb", Tags = ["1.1"] },
        new() { Digest = "sha256:ccc", Tags = [] },
    ];

    [Fact]
    public void Matches_a_tagged_image_by_its_tag()
    {
        HarborArtifactInfo? found = SupplyChainService.FindArtifact(
            Artifacts, ImageReference.Parse("reg.io/proj/app:1.1")!);

        found!.Digest.Should().Be("sha256:bbb");
    }

    [Fact]
    public void Matches_a_pinned_image_by_its_digest_not_its_tag()
    {
        HarborArtifactInfo? found = SupplyChainService.FindArtifact(
            Artifacts, ImageReference.Parse("reg.io/proj/app@sha256:ccc")!);

        found!.Digest.Should().Be("sha256:ccc");
    }

    [Fact]
    public void An_untagged_image_resolves_through_the_implicit_latest_tag()
    {
        SupplyChainService.FindArtifact(Artifacts, ImageReference.Parse("reg.io/proj/app")!)!
            .Digest.Should().Be("sha256:aaa");
    }

    [Fact]
    public void An_image_whose_tag_is_gone_from_the_registry_does_not_match()
    {
        // Retagged or deleted upstream — must not silently match a different artifact.
        SupplyChainService.FindArtifact(Artifacts, ImageReference.Parse("reg.io/proj/app:9.9")!)
            .Should().BeNull();
    }

    [Fact]
    public void Tag_matching_is_case_sensitive_because_registry_tags_are()
    {
        SupplyChainService.FindArtifact(Artifacts, ImageReference.Parse("reg.io/proj/app:LATEST")!)
            .Should().BeNull();
    }

    // ── Scan overview semantics ──

    [Theory]
    [InlineData("Success", true)]
    [InlineData("Running", false)]
    [InlineData("Error", false)]
    [InlineData("Queued", false)]
    [InlineData("", false)]
    public void Only_a_successful_scan_counts_as_scanned(string status, bool expected)
    {
        // A running or failed scan must never read as a clean bill of health.
        new HarborScanOverview { ScanStatus = status }.IsScanned.Should().Be(expected);
    }

    [Fact]
    public void A_vulnerability_with_a_fix_version_is_reported_as_fixable()
    {
        new HarborVulnerability { FixVersion = "1.2.4" }.IsFixable.Should().BeTrue();
        new HarborVulnerability { FixVersion = null }.IsFixable.Should().BeFalse();
        new HarborVulnerability { FixVersion = "  " }.IsFixable.Should().BeFalse();
    }

    // ── Kyverno verifyImages policy generation ──

    private static KyvernoPolicy VerifyPolicy(KyvernoPolicyService.VerifyImagesConfig config) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        PolicyType = KyvernoPolicyType.VerifyImageSignatures,
        ValidationFailureAction = KyvernoValidationFailureAction.Enforce,
        Configuration = KyvernoPolicyService.SerializeVerifyImagesConfig(config),
    };

    /// <summary>Parses the generated YAML, throwing if it is malformed. Returns object,
    /// not dynamic — extension methods like Should() do not bind on a dynamic receiver.</summary>
    private static object ParseYaml(string yaml) =>
        new DeserializerBuilder().Build().Deserialize<object>(yaml)!;

    [Fact]
    public void Generates_parseable_yaml_for_a_public_key_attestor()
    {
        string yaml = KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new()
            {
                ImagePatterns = ["registry.example.com/*"],
                PublicKey = "-----BEGIN PUBLIC KEY-----\nMFkwEwYH\n-----END PUBLIC KEY-----",
            })],
            "acme-prod");

        yaml.Should().NotBeEmpty();

        // The indentation of an embedded PEM block is the fragile part — parse it rather
        // than trusting that the interpolation lined up.
        Action parse = () => ParseYaml(yaml);
        parse.Should().NotThrow();

        yaml.Should().Contain("verifyImages");
        yaml.Should().Contain("publicKeys");
        yaml.Should().Contain("BEGIN PUBLIC KEY");
        yaml.Should().Contain("namespace: acme-prod");
        yaml.Should().Contain("validationFailureAction: Enforce");
    }

    [Fact]
    public void Generates_parseable_yaml_for_a_keyless_attestor()
    {
        string yaml = KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new()
            {
                ImagePatterns = ["registry.example.com/*", "ghcr.io/acme/*"],
                Issuer = "https://token.actions.githubusercontent.com",
                Subject = "https://github.com/acme/app/*",
            })],
            "acme-prod");

        Action parse = () => ParseYaml(yaml);
        parse.Should().NotThrow();

        yaml.Should().Contain("keyless");
        yaml.Should().Contain("rekor");
        yaml.Should().Contain("ghcr.io/acme/*");
    }

    [Fact]
    public void Emits_every_configured_image_pattern()
    {
        string yaml = KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new()
            {
                ImagePatterns = ["a.io/*", "b.io/*", "c.io/*"],
                PublicKey = "-----BEGIN PUBLIC KEY-----\nX\n-----END PUBLIC KEY-----",
            })],
            "ns");

        yaml.Should().Contain("a.io/*").And.Contain("b.io/*").And.Contain("c.io/*");

        Action parse = () => ParseYaml(yaml);
        parse.Should().NotThrow();
    }

    [Fact]
    public void The_required_flag_is_carried_into_the_policy()
    {
        string enforced = KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new() { ImagePatterns = ["a.io/*"], PublicKey = "k", Required = true })], "ns");
        string permissive = KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new() { ImagePatterns = ["a.io/*"], PublicKey = "k", Required = false })], "ns");

        enforced.Should().Contain("required: true");
        permissive.Should().Contain("required: false");
    }

    [Fact]
    public void An_incomplete_config_produces_no_policy_rather_than_one_that_verifies_nothing()
    {
        // A signature policy that silently passes everything is worse than no policy,
        // because the governance page would show it as protection.
        KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new() { ImagePatterns = [], PublicKey = "k" })], "ns")
            .Should().BeEmpty();

        KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new() { ImagePatterns = ["a.io/*"] })], "ns")
            .Should().BeEmpty();

        // Keyless needs BOTH issuer and subject; an issuer alone would match any identity.
        KyvernoPolicyService.BuildManifest(
            [VerifyPolicy(new() { ImagePatterns = ["a.io/*"], Issuer = "https://x" })], "ns")
            .Should().BeEmpty();
    }

    [Fact]
    public void Round_trips_its_configuration_through_storage()
    {
        KyvernoPolicyService.VerifyImagesConfig original = new()
        {
            ImagePatterns = ["reg.io/*"],
            Issuer = "https://issuer",
            Subject = "https://subject",
            Required = false,
        };

        KyvernoPolicyService.VerifyImagesConfig loaded =
            KyvernoPolicyService.GetVerifyImagesConfig(VerifyPolicy(original));

        loaded.ImagePatterns.Should().BeEquivalentTo(["reg.io/*"]);
        loaded.Issuer.Should().Be("https://issuer");
        loaded.Subject.Should().Be("https://subject");
        loaded.Required.Should().BeFalse();
    }

    [Fact]
    public void Unreadable_configuration_yields_an_unusable_config_not_an_exception()
    {
        KyvernoPolicy broken = new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EnvironmentId = Guid.NewGuid(),
            PolicyType = KyvernoPolicyType.VerifyImageSignatures,
            Configuration = "{not json",
        };

        KyvernoPolicyService.GetVerifyImagesConfig(broken).IsUsable.Should().BeFalse();
    }
}
