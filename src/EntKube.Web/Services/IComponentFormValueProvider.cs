namespace EntKube.Web.Services;

/// <summary>
/// Supplies a component's catalog-form values back to the Components tab when an operator reopens
/// an installed component.
///
/// <para><b>Why this exists.</b> The edit form is seeded from each field's catalog default and then
/// overridden from the component's stored Helm values. That covers ordinary fields, and the vault
/// covers <c>StoreAsSecret</c> ones — but a field whose <c>YamlPath</c> is a pseudo-path
/// (<c>ldap:</c>, <c>stalwart:</c>, <c>cnpg:</c> …) is deliberately kept out of the YAML, so there
/// is nothing for either mechanism to read. Without a provider such a field silently reverts to its
/// catalog default every time the form is opened, and saving writes that default over live
/// configuration — a fresh base DN over a running directory, a different hostname for a mail
/// server. The failure is quiet in exactly the way that matters: the form looks filled in.</para>
///
/// <para>Implement this on the service that owns the component's configuration, register it in DI
/// alongside that service, and the Components tab picks it up with no change of its own. A test
/// asserts that every catalog entry with non-secret pseudo-path fields has one, so the next
/// component cannot rediscover this the hard way.</para>
/// </summary>
public interface IComponentFormValueProvider
{
    /// <summary>The <see cref="CatalogEntry.Key"/> this provider supplies values for.</summary>
    string CatalogKey { get; }

    /// <summary>
    /// The form values for an installed component, keyed by <see cref="ComponentFormField.Key"/>.
    /// Returns an empty dictionary when the component has no stored configuration yet, which leaves
    /// the catalog defaults in place — the correct answer for a component that was never configured.
    ///
    /// <para>Secrets must be omitted rather than returned blank. Omitting a key leaves whatever the
    /// form already holds, and the save path treats a blank secret as "unchanged"; returning an
    /// empty string for one would echo a cleared password back into the UI and risk saving it.</para>
    /// </summary>
    Task<Dictionary<string, string>> ReadFormValuesAsync(
        Guid tenantId, Guid clusterComponentId, CancellationToken ct = default);
}
