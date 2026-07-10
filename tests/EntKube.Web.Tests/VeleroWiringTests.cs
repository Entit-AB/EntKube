using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Velero → S3 (CubeFS) auto-wiring: the catalog entry now consumes a StorageLink like Loki/Harbor,
/// its credentials ride the hidden-secret injection rails, and the storage-link pseudo-path never
/// leaks into the rendered Helm values.
/// </summary>
public class VeleroWiringTests
{
    private static CatalogEntry Velero => ComponentCatalog.GetByKey("velero")!;

    [Fact]
    public void Velero_HasStorageLinkField()
    {
        ComponentFormField link = Velero.FormFields.Single(f => f.Key == "storage-link");
        link.Type.Should().Be(FormFieldType.StorageLink);
        link.YamlPath.Should().Be("velero:storage-link-id");
    }

    [Fact]
    public void Velero_CredentialsField_IsHiddenSecret_OnAwsPluginPath()
    {
        ComponentFormField cred = Velero.FormFields.Single(f => f.Key == "velero-s3-credentials");
        cred.StoreAsSecret.Should().BeTrue();
        cred.Hidden.Should().BeTrue();
        cred.SecretName.Should().Be(VeleroService.CredentialsSecretName);
        // Injected at install into the AWS-plugin shared-credentials file.
        cred.YamlPath.Should().Be("credentials.secretContents.cloud");
    }

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
    public void BuildCredentialsFile_ProducesAwsSharedCredentialsIni()
    {
        string ini = VeleroService.BuildCredentialsFile("AKIAEXAMPLE00000", "secret/keyvalue0000000000000000000");

        ini.Should().Contain("[default]");
        ini.Should().Contain("aws_access_key_id=AKIAEXAMPLE00000");
        ini.Should().Contain("aws_secret_access_key=secret/keyvalue0000000000000000000");
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

    [Fact]
    public void Velero_DefaultValues_NoLongerCarryUnresolvedPlaceholders()
    {
        // The ${velero_*} blueprint-variable placeholders are gone — the target is filled from a
        // StorageLink now, so a bare install can't ship half-substituted tokens.
        Velero.DefaultValues.Should().NotContain("${velero_");
        Velero.DefaultValues.Should().Contain("useSecret: true");
    }
}
