using FluentAssertions;
using ContractComponentStatus = EntKube.Contracts.Catalog.ComponentStatus;
using EntityComponentStatus = EntKube.Web.Data.ComponentStatus;

namespace EntKube.Web.Tests;

/// <summary>
/// The public API's <c>status</c> string does not change when an enum is edited.
///
/// <para><b>Why this is a test and not a comment.</b> <c>GET /api/v1/clusters/{id}/components</c>
/// serialises the status with <c>Status.ToString()</c>, and that route now asks
/// <c>ICatalogApi</c> — so the string a client receives comes from the <em>contract's</em> copy of
/// the enum rather than the entity's. The two are separate declarations that happen to agree. A
/// member renamed or inserted in one of them silently changes a published wire format, which is
/// the sort of break that is only noticed by the caller.</para>
///
/// <para>The names are what matter here, not the ordinals: nothing casts between these two, the
/// projection reads <c>InstalledComponent.Status</c> directly. (The ordinals matter for
/// <c>StorageProviderKind</c>, which <em>is</em> cast — see <c>ModuleApiTests</c>.)</para>
/// </summary>
public class ApiComponentStatusTests
{
    [Fact]
    public void The_contract_and_entity_component_status_names_agree()
    {
        string[] entity = Enum.GetNames<EntityComponentStatus>().Order().ToArray();
        string[] contract = Enum.GetNames<ContractComponentStatus>().Order().ToArray();

        contract.Should().Equal(entity,
            "the public API serialises this enum by name, so the two declarations drifting apart "
            + "changes a published JSON contract — add the member to both, or the route starts "
            + "returning a status its clients have never seen");
    }

    /// <summary>
    /// Every status the API can emit, written out. Renaming a member is a wire-format change even
    /// when both enums are edited together, and this is what makes that visible in a diff rather
    /// than only to whoever is parsing the field.
    /// </summary>
    [Fact]
    public void The_published_status_strings_are_what_clients_already_parse()
    {
        Enum.GetValues<ContractComponentStatus>().Select(s => s.ToString())
            .Should().BeEquivalentTo(
                ["NotInstalled", "Installing", "Installed", "Failed", "Uninstalling"],
                "these exact strings are published by GET /api/v1/clusters/{clusterId}/components");
    }
}
