using System.Diagnostics;
using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

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

    // ──────── Managed clusters have no control-plane node ────────

    /// <summary>
    /// virt-operator's placement exactly as the pinned v1.9.1 kubevirt-operator.yaml ships it. The rest
    /// of the Deployment is left out: kustomize patches by path and needs only the object's identity.
    /// </summary>
    private const string ReleasedVirtOperator = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: virt-operator
          namespace: kubevirt
        spec:
          template:
            spec:
              affinity:
                nodeAffinity:
                  preferredDuringSchedulingIgnoredDuringExecution:
                    - weight: 100
                      preference:
                        matchExpressions:
                          - key: node-role.kubernetes.io/worker
                            operator: DoesNotExist
                  requiredDuringSchedulingIgnoredDuringExecution:
                    nodeSelectorTerms:
                      - matchExpressions:
                          - key: node-role.kubernetes.io/control-plane
                            operator: Exists
                      - matchExpressions:
                          - key: node-role.kubernetes.io/master
                            operator: Exists
                podAntiAffinity:
                  preferredDuringSchedulingIgnoredDuringExecution:
                    - weight: 1
                      podAffinityTerm:
                        labelSelector:
                          matchExpressions:
                            - key: kubevirt.io
                              operator: In
                              values: [virt-operator]
                        topologyKey: kubernetes.io/hostname
              nodeSelector:
                kubernetes.io/os: linux
              tolerations:
                - key: CriticalAddonsOnly
                  operator: Exists
                - key: node-role.kubernetes.io/control-plane
                  operator: Exists
                  effect: NoSchedule
                - key: node-role.kubernetes.io/master
                  operator: Exists
                  effect: NoSchedule
        """;

    /// <summary>
    /// What a Gardener cluster showed: virt-operator Pending for want of a control-plane node. Run
    /// through the real kustomize in kubectl, the same kustomization the apply uses, so a patch that
    /// does not apply to this shape fails here rather than on a cluster.
    /// </summary>
    [Fact]
    public async Task The_operator_patch_drops_the_control_plane_requirement_and_keeps_the_preference()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey("kubevirt-operator")!;
        DirectoryInfo dir = Directory.CreateTempSubdirectory("kubevirt-kustomize-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "operator.yaml"), ReleasedVirtOperator);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "kustomization.yaml"),
                KubernetesClientFactory.BuildKustomization("operator.yaml", entry.ManifestPatches));

            YamlMappingNode podSpec = PodSpec(await KustomizeAsync(dir.FullName));
            YamlMappingNode nodeAffinity = Map(Map(podSpec, "affinity"), "nodeAffinity");

            nodeAffinity.Children.Keys.Select(k => k.ToString())
                .Should().NotContain("requiredDuringSchedulingIgnoredDuringExecution");
            nodeAffinity.ToString().Should().Contain("node-role.kubernetes.io/control-plane");
            // Untouched: the control plane stays schedulable where there is one.
            Map(podSpec, "affinity").Children.Keys.Select(k => k.ToString()).Should().Contain("podAntiAffinity");
            podSpec["tolerations"].ToString().Should().Contain("node-role.kubernetes.io/control-plane");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_kubevirt_resource_places_its_infra_without_requiring_a_control_plane_node()
    {
        // Leaving spec.infra.nodePlacement out is what REQUIRES a control-plane node for virt-api and
        // virt-controller: virt-operator falls back to that default only when no placement is given.
        YamlMappingNode kubeVirt = (YamlMappingNode)LoadYaml(ComponentCatalog.GetByKey("kubevirt")!.DefaultValues!);
        YamlMappingNode placement = Map(Map(Map(kubeVirt, "spec"), "infra"), "nodePlacement");

        placement.ToString().Should().NotContain("requiredDuringSchedulingIgnoredDuringExecution")
            .And.Contain("node-role.kubernetes.io/control-plane");
    }

    private static async Task<string> KustomizeAsync(string directory)
    {
        ProcessStartInfo start = new("kubectl", ["kustomize", directory])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await stderr);
        return await stdout;
    }

    private static YamlNode LoadYaml(string yaml)
    {
        YamlStream stream = [];
        stream.Load(new StringReader(yaml));
        return stream.Documents[0].RootNode;
    }

    private static YamlMappingNode PodSpec(string deploymentYaml) =>
        Map(Map(Map((YamlMappingNode)LoadYaml(deploymentYaml), "spec"), "template"), "spec");

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node[key];
}
