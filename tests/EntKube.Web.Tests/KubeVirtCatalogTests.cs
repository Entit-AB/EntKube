using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The four Virtualization entries: KubeVirt and CDI, each an operator release manifest plus the custom
/// resource that tells the operator to deploy.
/// </summary>
public class KubeVirtCatalogTests
{
    public static TheoryData<string> OperatorKeys => new() { "kubevirt-operator", "kubevirt-cdi-operator" };

    public static TheoryData<string, string> ResourceKeys => new()
    {
        { "kubevirt", "kubevirt-operator" },
        { "kubevirt-cdi", "kubevirt-cdi-operator" }
    };

    [Theory]
    [MemberData(nameof(OperatorKeys))]
    public void An_operator_manifest_is_pinned_to_a_release_tag(string key)
    {
        // For a ManifestUrl the URL IS the version. A "latest" URL would make every re-apply an
        // unannounced upgrade, and "upgrade" a button that cannot say what it upgrades to.
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;

        entry.ComponentType.Should().Be("ManifestUrl");
        entry.HelmRepoUrl.Should().MatchRegex(@"/releases/download/v\d+\.\d+\.\d+/");
        entry.HelmRepoUrl.Should().NotContain("latest");
    }

    [Theory]
    [MemberData(nameof(ResourceKeys))]
    public void A_resource_requires_its_operator_and_is_detected_by_instance_not_by_crd(string key, string operatorKey)
    {
        // The CRD is installed by the operator. Detecting the resource entry by it would report KubeVirt
        // deployed on a cluster where only the operator is.
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;

        entry.ComponentType.Should().Be("Manifest");
        entry.Dependencies.Should().Equal(operatorKey);
        entry.DetectionCrds.Should().BeEmpty();
        entry.DetectionResource.Should().NotBeNull();
    }

    [Fact]
    public void The_cdi_keys_cannot_be_claimed_by_an_unrelated_release()
    {
        // FindByRelease falls back to "release name contains the key". A bare "cdi" key would
        // resolve any release with those three letters in it to this entry.
        ComponentCatalog.FindByRelease("acdiscovery", null).Should().BeNull();
        ComponentCatalog.FindByRelease("kubevirt-cdi-operator", null)!.Key.Should().Be("kubevirt-cdi-operator");
        ComponentCatalog.FindByRelease("kubevirt-operator", null)!.Key.Should().Be("kubevirt-operator");
    }

    [Fact]
    public void Turning_on_emulation_writes_a_boolean_into_the_kubevirt_resource()
    {
        // KubeVirt rejects the resource if useEmulation is the string "true".
        CatalogEntry entry = ComponentCatalog.GetByKey("kubevirt")!;
        ComponentFormField field = entry.FormFields.Single(f => f.Key == "use-emulation");

        string merged = YamlFormMerger.MergeFormValues(
            entry.DefaultValues!, new Dictionary<string, string> { [field.YamlPath] = "true" });

        merged.Should().Contain("useEmulation: true").And.NotContain("useEmulation: \"true\"");
        YamlFormMerger.ExtractValue(merged, field.YamlPath).Should().Be("true");
        YamlFormMerger.ExtractValue(entry.DefaultValues!, field.YamlPath).Should().Be(field.DefaultValue);
    }

    [Fact]
    public void Uninstalling_kubevirt_refuses_while_virtual_machines_exist()
    {
        // Deleting the KubeVirt resource tears KubeVirt down. Without this the uninstall button would
        // stop every VM on the cluster.
        YamlFormMerger.ExtractValue(ComponentCatalog.GetByKey("kubevirt")!.DefaultValues!, "spec.uninstallStrategy")
            .Should().Be("BlockUninstallIfWorkloadsExist");
    }
}
