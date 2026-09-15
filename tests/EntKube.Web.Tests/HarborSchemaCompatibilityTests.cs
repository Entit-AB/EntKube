using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Refusing to install Harbor over a database a newer Harbor already wrote.
///
/// <para>Harbor's migrator only runs forward. Handed a schema from a later release it does not fail at
/// connect time, where the address would be the obvious suspect — it connects, reads a version it has no
/// migration for, and exits with <c>no migration found for version N</c>. By then helm has been waiting
/// for several minutes, every other Harbor pod has failed against a core that never came up, and the one
/// line that explains it is at the end of one pod's log among a namespace full of errors.</para>
/// </summary>
public class HarborSchemaCompatibilityTests
{
    /// <summary>
    /// The pin and the schema ceiling are one fact recorded twice, and the second copy is the one that
    /// gets forgotten. Chart 1.19.x is Harbor 2.15.x, whose last migration is 0180; if this pin moves,
    /// both constants beside it must move too.
    /// </summary>
    [Fact]
    public void The_schema_ceiling_matches_the_pinned_chart()
    {
        CatalogEntry harbor = ComponentCatalog.GetByKey("harbor")!;

        harbor.HelmChartVersion.Should().StartWith("1.19.",
            "chart 1.19.x is Harbor 2.15.x — HarborSchemaVersion and HarborAppVersion are derived from it");
        HarborService.HarborSchemaVersion.Should().Be(180);
        HarborService.HarborAppVersion.Should().StartWith("2.15.");
    }

    [Fact]
    public void A_schema_from_a_newer_harbor_is_refused_and_says_which_versions_are_involved()
    {
        string? conflict = HarborService.DescribeSchemaConflict(190, dirty: false, "harbor");

        conflict.Should().NotBeNull();
        conflict.Should().Contain("190");
        conflict.Should().Contain(HarborService.HarborAppVersion);
        conflict.Should().Contain("empty database");
    }

    [Fact]
    public void A_schema_this_harbor_can_migrate_forward_is_allowed()
    {
        HarborService.DescribeSchemaConflict(150, dirty: false, "harbor").Should().BeNull();
    }

    /// <summary>The exact version we ship is not "newer than us" — off-by-one here would refuse every reinstall.</summary>
    [Fact]
    public void A_schema_at_our_own_version_is_allowed()
    {
        HarborService.DescribeSchemaConflict(
            HarborService.HarborSchemaVersion, dirty: false, "harbor").Should().BeNull();
    }

    /// <summary>A half-applied migration is a different problem with a different remedy, so it says so.</summary>
    [Fact]
    public void A_dirty_migration_is_refused_with_its_own_explanation()
    {
        string? conflict = HarborService.DescribeSchemaConflict(150, dirty: true, "harbor");

        conflict.Should().NotBeNull();
        conflict.Should().Contain("dirty");
        conflict.Should().Contain("backup");
    }

    [Theory]
    [InlineData("180|f", 180, false)]
    [InlineData("180|t", 180, true)]
    [InlineData("  150|false  ", 150, false)]
    [InlineData("\n180|f\n", 180, false)]
    public void Psql_tuples_only_output_is_read_back(string output, int version, bool dirty)
    {
        CnpgService.ParseMigrateSchemaVersion(output).Should().Be((version, dirty));
    }

    /// <summary>
    /// An empty result is the shape of a database nothing has migrated yet — the normal case for a fresh
    /// install. It must read as "nothing known", never as a conflict.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("ERROR:  relation \"schema_migrations\" does not exist")]
    public void Nothing_to_read_is_not_a_conflict(string output)
    {
        CnpgService.ParseMigrateSchemaVersion(output).Should().BeNull();
    }
}
