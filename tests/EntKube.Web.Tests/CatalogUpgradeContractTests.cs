using EntKube.Contracts.Catalog;
using EntKube.Web.Modules.Api;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Catalog's upgrade report across the contract boundary.
///
/// <para><b>Why this is a read, against the note that said otherwise.</b>
/// <see cref="PublicApiContractAdoptionTests"/> listed <c>ComponentUpgradeService</c> as a gap
/// that persisted because "upgrading installs a chart". That conflates the report with the
/// runner. <c>ComponentUpgradeService</c>'s own doc comment says it is "read-only and side-effect
/// free — it never touches a cluster", acting on a finding is <c>ComponentUpgradeRunner</c>'s job
/// through the ordinary apply path, and the route is <c>OpsRead</c>. The rationale was stale, not
/// the gap.</para>
/// </summary>
public class CatalogUpgradeContractTests
{
    /// <summary>
    /// ⚠️ <b>The enum names are a published API.</b> <c>GET /api/v1/upgrades</c> emits
    /// <c>status</c> and <c>lag</c> as <c>ToString()</c> on these values, so renaming a member
    /// renames a field in a response the CLI and a monitoring integration parse. Names are pinned
    /// in order, which fixes the ordinals too — the same reason
    /// <see cref="AdvisorContractEnumTests"/> exists.
    /// </summary>
    [Fact]
    public void The_published_enum_names_match_the_services_own()
    {
        Enum.GetNames<UpgradeAvailability>().Should().Equal(Enum.GetNames<UpgradeStatus>(),
            "CatalogApi maps these by NAME, so a member present on one side and not the other is "
            + "a runtime failure rather than a compile one");

        Enum.GetNames<UpgradeLag>().Should().Equal(Enum.GetNames<VersionLag>());
    }

    /// <summary>
    /// The two are separate declarations, as rule 2 of the contracts README requires, so being
    /// able to parse every member across is the property that makes <c>CatalogApi</c>'s
    /// name-based mapping total. ⚠️ A cast would have been quieter and wrong: these names are
    /// published, so a reordered member would become a silently incorrect field in a response
    /// rather than an error.
    /// </summary>
    [Fact]
    public void Every_member_round_trips_by_name_in_both_directions()
    {
        foreach (UpgradeStatus s in Enum.GetValues<UpgradeStatus>())
        {
            Enum.Parse<UpgradeAvailability>(s.ToString()).ToString().Should().Be(s.ToString());
        }

        foreach (VersionLag l in Enum.GetValues<VersionLag>())
        {
            Enum.Parse<UpgradeLag>(l.ToString()).ToString().Should().Be(l.ToString());
        }
    }

    /// <summary>
    /// ⭐ Absence of the service is refused rather than answered.
    ///
    /// <para>The dependency is an optional, last constructor parameter so that the ~85 existing
    /// positional constructions of <c>CatalogApi</c> keep compiling — the accommodation
    /// <c>KeycloakService</c>'s gate parameter documents. Unlike that one, absence here is not
    /// harmless, so it is not treated as harmless: an empty report would read as "nothing is out
    /// of date", which is silence that looks like good news about chart currency.</para>
    ///
    /// <para>⚠️ This is the test that keeps the optional parameter honest. Without it the
    /// accommodation would be indistinguishable from the defect pattern this session kept
    /// finding — an optional value that quietly produces a wrong answer.</para>
    /// </summary>
    [Fact]
    public async Task A_catalog_api_built_without_the_upgrade_service_refuses_to_report()
    {
        CatalogApi api = new(new Mocked().Factory);

        Func<Task> act = () => api.GetUpgradeReportAsync(Guid.NewGuid(), DateTime.UtcNow);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("cannot report",
                "the failure has to say what is missing and what to do, not just throw");
    }

    /// <summary>A factory is all the guard needs; it throws before touching the database.</summary>
    private sealed class Mocked : IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection connection;

        public Mocked()
        {
            connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            connection.Open();
            Factory = new TestDbContextFactory(connection);
        }

        public TestDbContextFactory Factory { get; }

        public void Dispose() => connection.Dispose();
    }
}
