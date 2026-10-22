using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using ContractFindingCategory = EntKube.Contracts.Advisor.FindingCategory;
using ContractFindingHorizon = EntKube.Contracts.Advisor.FindingHorizon;
using ContractFindingSeverity = EntKube.Contracts.Advisor.FindingSeverity;
using ContractFindingStatus = EntKube.Contracts.Advisor.FindingStatus;

namespace EntKube.Web.Tests;

/// <summary>
/// The four enums a finding carries across the Advisor boundary.
///
/// <para><b>Why names and not just ordinals.</b> <c>AdvisorApi</c> maps entity to contract by
/// casting the ordinal, so the ordinals have to line up or a finding is silently re-labelled. But
/// the public API then publishes <c>category</c>, <c>severity</c>, <c>horizon</c> and
/// <c>state</c> as <c>ToString()</c> on the <em>contract</em> value, so the NAMES are a published
/// API. Renaming a member on either side changes a field the CLI and MCP server parse.</para>
///
/// <para>That is not hypothetical in this codebase: <c>PublishedProvisioningStatus</c> exists in
/// the same file because Fleet's entity calls a state <c>None</c> while its contract calls it
/// <c>Registered</c>, and projecting the contract value directly would have renamed a published
/// string as a side effect of an internal refactor.</para>
/// </summary>
public class AdvisorContractEnumTests
{
    [Fact]
    public void The_advisor_enums_agree_member_for_member()
    {
        Enum.GetNames<ContractFindingCategory>().Should().Equal(Enum.GetNames<AdvisorCategory>());
        Enum.GetNames<ContractFindingSeverity>().Should().Equal(Enum.GetNames<AdvisorSeverity>());
        Enum.GetNames<ContractFindingHorizon>().Should().Equal(Enum.GetNames<AdvisorHorizon>());
        Enum.GetNames<ContractFindingStatus>().Should().Equal(Enum.GetNames<AdvisorFindingStatus>());
    }

    /// <summary>
    /// And agree on the ordinals, which is what the projection actually relies on.
    ///
    /// <para>Equal names with a reordered member would pass the test above and still mislabel every
    /// finding, because the mapping is <c>(ContractX)(int)entityX</c> rather than a switch.</para>
    /// </summary>
    [Fact]
    public void The_advisor_enums_agree_on_their_ordinals()
    {
        foreach (AdvisorCategory v in Enum.GetValues<AdvisorCategory>())
        {
            ((ContractFindingCategory)(int)v).ToString().Should().Be(v.ToString());
        }
        foreach (AdvisorSeverity v in Enum.GetValues<AdvisorSeverity>())
        {
            ((ContractFindingSeverity)(int)v).ToString().Should().Be(v.ToString());
        }
        foreach (AdvisorHorizon v in Enum.GetValues<AdvisorHorizon>())
        {
            ((ContractFindingHorizon)(int)v).ToString().Should().Be(v.ToString());
        }
        foreach (AdvisorFindingStatus v in Enum.GetValues<AdvisorFindingStatus>())
        {
            ((ContractFindingStatus)(int)v).ToString().Should().Be(v.ToString());
        }
    }

    /// <summary>
    /// ⚠️ The near-miss: there are <b>two</b> unrelated <c>FindingSeverity</c> enums in this
    /// product, and they are not interchangeable.
    ///
    /// <para><c>EntKube.Web.Services.FindingSeverity</c> belongs to the connectivity analyser and
    /// starts at <c>Error</c>; the advisor's starts at <c>Critical</c>. A file importing both
    /// namespaces compiles against whichever it resolves, so mapping one onto the other would
    /// publish <c>"Error"</c> where a client expects <c>"Critical"</c> — and the ordinals line up,
    /// so a cast would not even fail.</para>
    ///
    /// <para>This test exists to make that collision visible rather than to defend a line of code:
    /// the compiler caught it once (CS0104 in <c>AdvisorApi</c>, which is why the contract enums
    /// are aliased there), and it will not catch it in a file that imports only one of them. Same
    /// shape as <c>RolloutStatus</c> versus <c>DeploymentRolloutStatus</c>, one word apart.</para>
    /// </summary>
    [Fact]
    public void The_connectivity_severity_is_a_different_enum_and_must_not_be_mapped_onto_this_one()
    {
        Enum.GetNames<FindingSeverity>().Should().NotEqual(Enum.GetNames<ContractFindingSeverity>(),
            "if these ever become identical, delete one — two names for one concept is the bug, "
            + "and until then a cast between them is silent mislabelling");

        // The specific trap: same ordinal, different published word.
        FindingSeverity.Error.Should().Be((FindingSeverity)0);
        ContractFindingSeverity.Critical.Should().Be((ContractFindingSeverity)0);
    }
}
