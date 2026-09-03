using EntKube.Web.Services.SupplyChain;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for container image reference parsing. The registry-vs-repository rule is a
/// Docker convention rather than a syntax rule, and getting it wrong would make every
/// running image fail to match its registry — silently emptying the vulnerability join.
/// </summary>
public class ImageReferenceTests
{
    [Fact]
    public void A_bare_official_image_resolves_to_docker_hub_library()
    {
        ImageReference? image = ImageReference.Parse("nginx");

        image.Should().NotBeNull();
        image!.Registry.Should().Be("docker.io");
        image.Repository.Should().Be("library/nginx");
        image.Tag.Should().Be("latest");
    }

    [Fact]
    public void A_user_namespaced_image_stays_on_docker_hub()
    {
        // "myco/app" is a Docker Hub account, NOT a registry named myco.
        ImageReference image = ImageReference.Parse("myco/app:2.1")!;

        image.Registry.Should().Be("docker.io");
        image.Repository.Should().Be("myco/app");
        image.Tag.Should().Be("2.1");
    }

    [Fact]
    public void A_dotted_first_component_is_treated_as_a_registry_host()
    {
        ImageReference image = ImageReference.Parse("registry.example.com/proj/app:1.2")!;

        image.Registry.Should().Be("registry.example.com");
        image.Repository.Should().Be("proj/app");
        image.Tag.Should().Be("1.2");
    }

    [Fact]
    public void A_registry_port_is_not_mistaken_for_a_tag()
    {
        ImageReference image = ImageReference.Parse("myreg:5000/proj/app:1.2")!;

        image.Registry.Should().Be("myreg:5000");
        image.Repository.Should().Be("proj/app");
        image.Tag.Should().Be("1.2");
    }

    [Fact]
    public void A_registry_port_with_no_tag_still_parses_as_a_registry()
    {
        ImageReference image = ImageReference.Parse("myreg:5000/proj/app")!;

        image.Registry.Should().Be("myreg:5000");
        image.Repository.Should().Be("proj/app");
        image.Tag.Should().Be("latest");
    }

    [Fact]
    public void Localhost_is_recognised_as_a_registry_without_a_dot()
    {
        ImageReference.Parse("localhost/proj/app:1")!.Registry.Should().Be("localhost");
    }

    [Fact]
    public void A_digest_pinned_image_carries_its_digest_and_no_default_tag()
    {
        ImageReference image = ImageReference.Parse(
            "registry.example.com/proj/app@sha256:abc123")!;

        image.Digest.Should().Be("sha256:abc123");
        image.Tag.Should().BeNull();
        image.Reference.Should().Be("sha256:abc123");
    }

    [Fact]
    public void A_tagged_image_uses_its_tag_as_the_registry_reference()
    {
        ImageReference.Parse("registry.example.com/proj/app:1.2")!.Reference.Should().Be("1.2");
    }

    [Fact]
    public void Deeply_nested_repositories_keep_the_project_as_the_first_segment()
    {
        // Harbor nests paths inside a project, so only the FIRST segment is the project.
        ImageReference image = ImageReference.Parse("reg.io/team/group/app:1")!;

        image.Project.Should().Be("team");
        image.HarborRepository.Should().Be("group/app");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_yields_null(string? image)
    {
        ImageReference.Parse(image).Should().BeNull();
    }

    // ── Registry matching ──

    [Theory]
    [InlineData("https://registry.example.com")]
    [InlineData("https://registry.example.com/")]
    [InlineData("registry.example.com")]
    [InlineData("http://registry.example.com")]
    public void Matches_its_registry_regardless_of_how_the_url_was_configured(string configured)
    {
        ImageReference.Parse("registry.example.com/proj/app:1")!
            .IsFromRegistry(configured).Should().BeTrue();
    }

    [Fact]
    public void Matches_a_registry_configured_with_a_non_default_port()
    {
        ImageReference.Parse("registry.example.com:5000/proj/app:1")!
            .IsFromRegistry("https://registry.example.com:5000").Should().BeTrue();
    }

    [Fact]
    public void Does_not_match_a_different_registry()
    {
        ImageReference.Parse("docker.io/library/nginx:1")!
            .IsFromRegistry("https://registry.example.com").Should().BeFalse();
    }

    [Fact]
    public void Registry_matching_is_case_insensitive_on_the_host()
    {
        ImageReference.Parse("Registry.Example.COM/proj/app:1")!
            .IsFromRegistry("https://registry.example.com").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unconfigured_registry_url_never_matches(string? configured)
    {
        ImageReference.Parse("registry.example.com/proj/app:1")!
            .IsFromRegistry(configured).Should().BeFalse();
    }

    [Fact]
    public void The_original_string_is_preserved_for_display()
    {
        ImageReference.Parse("  registry.example.com/proj/app:1.2  ")!
            .Original.Should().Be("registry.example.com/proj/app:1.2");
    }
}
