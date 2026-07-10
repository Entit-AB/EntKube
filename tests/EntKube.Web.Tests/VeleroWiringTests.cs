using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Dr;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Velero's storage target rides the same StorageLink rails as Loki/Harbor: the link is a
/// pseudo-path handled by a side-config hook, and the credentials are a hidden secret injected
/// into the Helm values at install. Both are easy to break silently — a leaked pseudo-path or a
/// mangled INI produces a component that installs cleanly and cannot back anything up.
///
/// What the catalog entry itself must look like is covered by DisasterRecoveryTests.
/// </summary>
public class VeleroWiringTests
{
    private static CatalogEntry Velero => ComponentCatalog.GetByKey("velero")!;

    [Fact]
    public void MergeFormValues_DoesNotLeakStorageLinkPseudoPath()
    {
        Guid linkId = Guid.NewGuid();
        string yaml = CatalogComponentRegistrar.MergeFormValues(
            Velero,
            new Dictionary<string, string> { ["storage-link"] = linkId.ToString() },
            []);

        // The storage link is handled by the side-config hook, not merged into YAML.
        yaml.Should().NotContain("storage-link-id");
        yaml.Should().NotContain(linkId.ToString());
        // The base values survive the merge.
        yaml.Should().Contain("velero-plugin-for-aws");
    }

    [Fact]
    public void MultiLineCredentials_RoundTripThroughInjectionMerge()
    {
        // Install-time injection does: values = MergeFormValues(values, { YamlPath -> secret }).
        // The Velero credentials are a multi-line INI (unlike Loki's single-line keys), so verify
        // the merge preserves newlines and the value extracts back byte-for-byte.
        string ini = VeleroService.BuildCredentialsFile("AKIAEXAMPLE00000", "s3cr3t/keyvalue00000000000000000");
        string merged = YamlFormMerger.MergeFormValues(
            Velero.DefaultValues!, new Dictionary<string, string> { ["credentials.secretContents.cloud"] = ini });

        YamlFormMerger.ExtractValue(merged, "credentials.secretContents.cloud").Should().Be(ini);
    }
}
