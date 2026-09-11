using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Guards the one thing the Components tab cannot recover on its own.
///
/// <para>The edit form is seeded from catalog defaults, then overridden from the component's stored
/// Helm values, then from the vault for <c>StoreAsSecret</c> fields. A field that is neither — a
/// non-secret pseudo-path — has no third source, so without an <see cref="IComponentFormValueProvider"/>
/// it silently reverts to its catalog default on every reopen, and saving writes that default over
/// live configuration. It has happened twice: OpenLDAP would have written a fresh base DN over a
/// running directory, and Stalwart reverted the mail hostname and TLS mode.</para>
///
/// <para>These tests are the reason it cannot happen a third time without someone deciding to let
/// it.</para>
/// </summary>
public class ComponentFormReadBackTests
{
    /// <summary>
    /// Catalog keys whose non-secret pseudo-path fields are repopulated by hand in
    /// <c>ClusterDetail.ToggleComponentDetail</c> rather than through a provider. These work today;
    /// they are listed here so the invariant below can be enforced for everything else, and they are
    /// the natural next candidates to migrate.
    /// </summary>
    private static readonly string[] HandWrittenReadBack =
        ["keycloak", "harbor", "velero", "loki", "mimir", "tempo"];

    /// <summary>
    /// Catalog keys with a known, unfixed gap: their storage-link field has no read-back at all, so
    /// reopening the form shows an empty bucket picker.
    ///
    /// <para>Unlike Loki, Mimir and Tempo — which record the chosen link's id in the component's
    /// Configuration JSON — the telemetry components write only the link's <em>contents</em> (bucket,
    /// endpoint, region) into their Helm values and never persist which link was chosen. There is
    /// therefore nothing to read back, and closing this needs the id to start being stored, not just
    /// a provider. Saving a blank picker is a no-op, so nothing is lost today; the dropdown is simply
    /// blank, which is misleading.</para>
    /// </summary>
    private static readonly string[] KnownGapNoStoredId =
        ["entkube-telemetry-indexer", "entkube-telemetry-query"];

    /// <summary>
    /// A form field is invisible to both automatic mechanisms when its YAML path is a pseudo-path
    /// (kept out of the values on purpose) and it is not vault-backed. Passwords are excluded: they
    /// are never echoed back, and a blank one on save means "unchanged".
    /// </summary>
    private static bool NeedsExplicitReadBack(ComponentFormField field) =>
        field.IsPseudoPath
        && !field.YamlPath.StartsWith("subchart:", StringComparison.Ordinal)
        && !field.StoreAsSecret
        && field.Type != FormFieldType.Password;

    [Fact]
    public void EveryComponentWithNonSecretPseudoPathFieldsCanRepopulateItsForm()
    {
        List<string> providerKeys = [OpenLdapService.CatalogKey, StalwartService.CatalogKey];

        List<string> unhandled = ComponentCatalog.Entries
            .Where(e => e.FormFields.Any(NeedsExplicitReadBack))
            .Select(e => e.Key)
            .Where(k => !providerKeys.Contains(k))
            .Where(k => !HandWrittenReadBack.Contains(k))
            .Where(k => !KnownGapNoStoredId.Contains(k))
            .ToList();

        unhandled.Should().BeEmpty(
            "a non-secret pseudo-path field has no other source, so without a read-back it reverts "
            + "to its catalog default on every reopen and saving writes that over live configuration. "
            + "Implement IComponentFormValueProvider on the service that owns the configuration.");
    }

    [Fact]
    public void TheExemptionListsNameComponentsThatStillExist()
    {
        // A renamed or removed component would otherwise leave a stale exemption behind, quietly
        // excusing the next real omission.
        List<string> keys = ComponentCatalog.Entries.Select(e => e.Key).ToList();

        keys.Should().Contain(HandWrittenReadBack);
        keys.Should().Contain(KnownGapNoStoredId);
    }

    [Fact]
    public void EveryExemptedComponentActuallyHasFieldsThatNeedAReadBack()
    {
        // The other direction: an exemption for a component that no longer has pseudo-path fields is
        // dead weight, and reads as though a problem exists where none does.
        foreach (string key in HandWrittenReadBack.Concat(KnownGapNoStoredId))
        {
            CatalogEntry entry = ComponentCatalog.GetByKey(key)!;
            entry.FormFields.Should().Contain(f => NeedsExplicitReadBack(f),
                $"{key} is exempted from the read-back invariant, so it should still need one");
        }
    }

    [Fact]
    public void AComponentConfiguredElsewhereSaysWhereOnTheComponentsTab()
    {
        // The Components tab only starts these pods; the server is configured and applied from a
        // Services section. An install that reports success with nothing on screen about a second
        // step is how a running-but-empty mail server went undiagnosed for a day.
        foreach (string key in new[] { OpenLdapService.CatalogKey, StalwartService.CatalogKey })
        {
            CatalogEntry entry = ComponentCatalog.GetByKey(key)!;
            entry.ConfiguredIn.Should().NotBeNull($"{key} is configured from a Services tab, not the Components tab");
            entry.ConfiguredIn!.Label.Should().StartWith("Services ›");
            entry.ConfiguredIn.Key.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void AProviderKeyAlwaysNamesARealCatalogEntry()
    {
        List<string> keys = ComponentCatalog.Entries.Select(e => e.Key).ToList();

        keys.Should().Contain(OpenLdapService.CatalogKey);
        keys.Should().Contain(StalwartService.CatalogKey);
    }

    [Fact]
    public void PseudoPathFieldsNeverLookLikeHelmValuePaths()
    {
        // A pseudo-path is recognised by containing ':', and a dot-notation Helm path by containing
        // '.'. One that had both would be ambiguous to the merger and to this invariant alike.
        foreach (CatalogEntry entry in ComponentCatalog.Entries)
        {
            foreach (ComponentFormField field in entry.FormFields.Where(f => f.IsPseudoPath))
            {
                field.YamlPath.Should().NotContain(".", $"{entry.Key}/{field.Key}");
            }
        }
    }
}
