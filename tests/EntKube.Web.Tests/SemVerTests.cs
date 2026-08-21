using EntKube.Web.Services.Upgrades;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for SemVer — the version parser and comparer behind the upgrade planner.
/// Covers the shapes Helm repositories actually publish (v-prefixes, two-part
/// versions, pre-releases, build metadata) and SemVer 2 precedence rules.
/// </summary>
public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1", 1, 0, 0)]
    [InlineData("65.1.0", 65, 1, 0)]
    [InlineData("1.2.3+build5", 1, 2, 3)]
    [InlineData("1.2.3-rc.1", 1, 2, 3)]
    [InlineData("  1.2.3  ", 1, 2, 3)]
    public void Parses_the_version_shapes_helm_repos_publish(string input, int major, int minor, int patch)
    {
        SemVer? version = SemVer.Parse(input);

        version.Should().NotBeNull();
        version!.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
        version.Patch.Should().Be(patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("main")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    [InlineData("+5.0.0")]
    public void Returns_null_rather_than_throwing_on_unparseable_input(string? input)
    {
        SemVer.Parse(input).Should().BeNull();
        SemVer.TryParse(input, out SemVer? _).Should().BeFalse();
    }

    [Fact]
    public void Keeps_the_original_string_including_prefix_and_build_metadata()
    {
        SemVer.Parse("v1.2.3+build5")!.Original.Should().Be("v1.2.3+build5");
        SemVer.Parse("v1.2.3+build5")!.ToString().Should().Be("v1.2.3+build5");
    }

    [Fact]
    public void Two_part_and_three_part_versions_compare_equal_when_patch_is_zero()
    {
        SemVer.Parse("1.2")!.Should().Be(SemVer.Parse("1.2.0")!);
    }

    [Fact]
    public void A_v_prefix_does_not_affect_ordering()
    {
        SemVer.Parse("v1.2.3")!.Should().Be(SemVer.Parse("1.2.3")!);
    }

    [Fact]
    public void Build_metadata_is_ignored_for_precedence()
    {
        SemVer.Parse("1.2.3+a")!.Should().Be(SemVer.Parse("1.2.3+b")!);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.2.3", "2.0.0")]
    [InlineData("1.9.0", "1.10.0")]   // numeric, not lexical
    [InlineData("9.0.0", "10.0.0")]
    public void Orders_by_numeric_component_not_string(string lower, string higher)
    {
        (SemVer.Parse(lower)! < SemVer.Parse(higher)!).Should().BeTrue();
        (SemVer.Parse(higher)! > SemVer.Parse(lower)!).Should().BeTrue();
    }

    [Fact]
    public void A_prerelease_sorts_below_the_matching_stable_release()
    {
        (SemVer.Parse("1.2.3-rc.1")! < SemVer.Parse("1.2.3")!).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]  // numeric below alphanumeric
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]      // numeric identifiers compare numerically
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    public void Follows_semver2_prerelease_precedence(string lower, string higher)
    {
        (SemVer.Parse(lower)! < SemVer.Parse(higher)!).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.2.3", "2.0.0", VersionLag.Major)]
    [InlineData("1.2.3", "1.3.0", VersionLag.Minor)]
    [InlineData("1.2.3", "1.2.9", VersionLag.Patch)]
    [InlineData("1.2.3", "1.2.3", VersionLag.UpToDate)]
    public void Classifies_lag_by_the_most_significant_differing_component(
        string installed, string latest, VersionLag expected)
    {
        SemVer.Compare(SemVer.Parse(installed)!, SemVer.Parse(latest)!).Should().Be(expected);
    }

    [Fact]
    public void An_installed_version_ahead_of_latest_is_up_to_date_not_a_finding()
    {
        // An operator who pinned a newer chart than the catalog recommends is not behind.
        SemVer.Compare(SemVer.Parse("2.0.0")!, SemVer.Parse("1.9.0")!)
            .Should().Be(VersionLag.UpToDate);
    }

    [Fact]
    public void Sorting_a_mixed_version_list_puts_the_newest_stable_on_top()
    {
        List<SemVer> versions =
        [
            SemVer.Parse("1.2.3")!, SemVer.Parse("1.10.0")!, SemVer.Parse("1.10.0-rc.1")!,
            SemVer.Parse("0.9.9")!, SemVer.Parse("2.0.0")!,
        ];

        versions.Sort();
        versions.Select(v => v.Original).Should().ContainInOrder(
            "0.9.9", "1.2.3", "1.10.0-rc.1", "1.10.0", "2.0.0");
    }
}
