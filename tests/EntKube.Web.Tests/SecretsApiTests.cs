using EntKube.Contracts.Secrets;
using EntKube.Web.Modules.Api;
using EntKube.Web.Services;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The sixth module contract, and the mapping between the module's own types and the contract's.
///
/// <para>⚠️ <b>TWO ENUMS IN TWO PROJECTS DO NOT STAY ALIGNED BY LUCK.</b> README rule 2 forbids a
/// contract exposing a type from the web application, so <see cref="SyncFamily"/> and
/// <see cref="SecretBindingKind"/> are declared separately and a cast between them would be a bug
/// waiting for the next member. A family added to one and not the other is either a mapping that
/// throws or — worse, and the reason this is a test rather than a comment — a binding family
/// silently relabelled in an operator's report.</para>
/// </summary>
public class SecretsApiTests
{
    /// <summary>
    /// ⚠️ The measure, not just the comparison. A mapping test that only checks the members it
    /// knows about passes forever after someone adds a sixth family to one enum. This asserts the
    /// two enums have the same membership, so the exhaustive switch below cannot be out of date
    /// without something going red.
    /// </summary>
    [Fact]
    public void The_two_family_enums_have_the_same_members()
    {
        IEnumerable<string> module = Enum.GetNames<SyncFamily>().OrderBy(n => n, StringComparer.Ordinal);
        IEnumerable<string> contract = Enum.GetNames<SecretBindingKind>().OrderBy(n => n, StringComparer.Ordinal);

        contract.Should().Equal(module,
            "the contract's enum is a restatement of the module's across a project boundary; a "
            + "member on one side only is a mapping that throws, or a family mislabelled in a report");
    }

    [Theory]
    [InlineData(SyncFamily.Database, SecretBindingKind.Database)]
    [InlineData(SyncFamily.Cache, SecretBindingKind.Cache)]
    [InlineData(SyncFamily.Messaging, SecretBindingKind.Messaging)]
    [InlineData(SyncFamily.Storage, SecretBindingKind.Storage)]
    [InlineData(SyncFamily.Identity, SecretBindingKind.Identity)]
    public void Each_family_maps_to_its_own_kind(SyncFamily family, SecretBindingKind expected)
        => SecretsApi.ToKind(family).Should().Be(expected);

    /// <summary>
    /// ⚠️ Names match today, so a cast would also pass every case above. This is why the mapping is
    /// a switch and not <c>(SecretBindingKind)family</c>: the two enums' numeric values are an
    /// accident of declaration order in two different files, and reordering either — a harmless
    /// edit, and alphabetising is exactly the harmless edit someone makes — would silently relabel
    /// every binding. Pinning one pair by VALUE rather than by name is what notices.
    /// </summary>
    [Fact]
    public void The_mapping_does_not_rely_on_the_two_enums_sharing_numeric_values()
    {
        // Identity is last in both today, so a cast is currently indistinguishable from the switch.
        SecretsApi.ToKind(SyncFamily.Identity).Should().Be(SecretBindingKind.Identity);

        // The real assertion: the switch is exhaustive over the module's enum, so every member
        // resolves without falling into the throw. A cast could not fail this either — which is
        // the point of the membership test above, and why the three tests are needed together.
        foreach (SyncFamily family in Enum.GetValues<SyncFamily>())
        {
            SecretsApi.ToKind(family).ToString().Should().Be(family.ToString(),
                $"{family} must map to the contract member of the same name, not to whatever "
                + "shares its ordinal");
        }
    }

    // ── The report shape the API depends on ───────────────────────────────────────────

    /// <summary>
    /// ⚠️ A sync is partially successful by design — one binding failing does not stop its
    /// siblings — so the contract deliberately does not return a boolean. <c>AnythingFailed</c>
    /// exists so a caller wanting one bit does not invent its own rule; a route that read only a
    /// success flag would report a wholly unreachable cluster as fine whenever one other binding
    /// worked.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void The_result_says_whether_anything_failed(
        bool bindingFailed, bool familyUnlisted, bool fatal, bool expected)
    {
        AppSecretSyncResult result = new(
            Output: "ok",
            Bindings: bindingFailed
                ? [new SecretSyncOutcome(SecretBindingKind.Database, "app-db", "unreachable")]
                : [new SecretSyncOutcome(SecretBindingKind.Database, "app-db", null)],
            UnlistedFamilies: familyUnlisted
                ? [new SecretSyncFamilyFailure(SecretBindingKind.Cache, "redis down")]
                : [],
            FatalError: fatal ? "stopped early" : null);

        result.AnythingFailed.Should().Be(expected);
    }

    /// <summary>
    /// The contract crosses a process boundary through the public API, so what it carries matters.
    /// A secret's NAME is what an operator needs to know which binding failed; a secret's VALUE
    /// would be a credential in an HTTP response.
    /// </summary>
    [Fact]
    public void An_outcome_carries_the_secrets_name_and_no_field_that_could_hold_its_value()
    {
        IEnumerable<string> fields = typeof(SecretSyncOutcome)
            .GetProperties()
            .Select(p => p.Name);

        fields.Should().BeEquivalentTo(["Kind", "SecretName", "Error", "Succeeded"],
            "a field added here reaches an HTTP response body — anything carrying a secret's "
            + "value does not belong on a type the public API serialises");
    }
}
