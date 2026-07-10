using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the OpenStack cluster-provisioning config: JSON round-tripping (so it
/// survives storage on the blueprint's ProvisioningConfig column and re-hydration in
/// the runner), validation, and the CAPO template inputs (clouds.yaml + clusterctl env).
/// </summary>
public class OpenStackProvisioningConfigTests
{
    private static OpenStackProvisioningConfig ValidConfig() => new()
    {
        OpenStackConnectionId = Guid.NewGuid(),
        ClusterName = "prod-eu-1",
        KubernetesVersion = "v1.31.4",
        NodeImageName = "ubuntu-2204-kube-v1.31.4",
        ControlPlaneCount = 3,
        ControlPlaneFlavor = "b.4c8gb",
        WorkerPools = [new WorkerPool { Name = "md-0", Count = 4, Flavor = "b.8c16gb" }],
        ExternalNetworkId = "ext-net-123",
        BootstrapImageName = "ubuntu-22.04",
        BootstrapFlavor = "b.2c4gb",
        BootstrapNetworkId = "tenant-net-456",
    };

    [Fact]
    public void ToJson_FromJson_RoundTrips()
    {
        OpenStackProvisioningConfig original = ValidConfig();

        OpenStackProvisioningConfig restored = OpenStackProvisioningConfig.FromJson(original.ToJson());

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Validate_ReturnsNoErrors_ForCompleteConfig()
    {
        ValidConfig().Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_FlagsMissingRequiredFields()
    {
        OpenStackProvisioningConfig empty = new();

        IReadOnlyList<string> errors = empty.Validate();

        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.Contains("connection"));
        errors.Should().Contain(e => e.Contains("Cluster name"));
        errors.Should().Contain(e => e.Contains("worker pool"));
        errors.Should().Contain(e => e.Contains("External network"));
        errors.Should().Contain(e => e.Contains("Bootstrap VM image"));
    }

    [Fact]
    public void WorkerPool_Autoscale_RoundTripsAndReportsBounds()
    {
        OpenStackProvisioningConfig config = ValidConfig();
        config.WorkerPools = [new WorkerPool { Name = "md-0", Count = 3, Flavor = "b.8c16gb", MinCount = 2, MaxCount = 6 }];

        OpenStackProvisioningConfig restored = OpenStackProvisioningConfig.FromJson(config.ToJson());

        restored.WorkerPools[0].MinCount.Should().Be(2);
        restored.WorkerPools[0].MaxCount.Should().Be(6);
        restored.WorkerPools[0].Autoscale.Should().BeTrue();
    }

    [Fact]
    public void WorkerPool_Autoscale_FalseWhenBoundsUnset()
    {
        new WorkerPool { Count = 3, Flavor = "b" }.Autoscale.Should().BeFalse();
    }

    [Fact]
    public void Validate_FlagsIncompleteOrInvalidAutoscaleBounds()
    {
        OpenStackProvisioningConfig onlyMin = ValidConfig();
        onlyMin.WorkerPools = [new WorkerPool { Count = 3, Flavor = "b", MinCount = 2 }];
        onlyMin.Validate().Should().Contain(e => e.Contains("min and a max"));

        OpenStackProvisioningConfig maxLessThanMin = ValidConfig();
        maxLessThanMin.WorkerPools = [new WorkerPool { Count = 3, Flavor = "b", MinCount = 5, MaxCount = 2 }];
        maxLessThanMin.Validate().Should().Contain(e => e.Contains("min ≤ max"));
    }

    [Fact]
    public void TotalWorkerCount_SumsAllPools()
    {
        OpenStackProvisioningConfig config = ValidConfig();
        config.WorkerPools =
        [
            new WorkerPool { Count = 2, Flavor = "a" },
            new WorkerPool { Count = 3, Flavor = "b" },
        ];

        config.TotalWorkerCount.Should().Be(5);
    }

    [Fact]
    public void BuildCloudsYaml_UsesApplicationCredentialAuth()
    {
        OpenStackConnection connection = new()
        {
            Name = "Cleura",
            AuthUrl = "https://identity.example.com:5000",
            Region = "Sto2",
            ProjectId = "proj-1",
        };
        ApplicationCredential appCred = new() { Id = "cred-id", Name = "entkube", Secret = "s3cr3t" };

        string yaml = CapiTemplateInputs.BuildCloudsYaml(connection, appCred);

        yaml.Should().Contain("auth_type: v3applicationcredential");
        yaml.Should().Contain("application_credential_id: cred-id");
        yaml.Should().Contain("application_credential_secret: s3cr3t");
        // The auth URL is normalized to include the /v3 version segment.
        yaml.Should().Contain("auth_url: https://identity.example.com:5000/v3");
        yaml.Should().Contain("region_name: Sto2");
        yaml.Should().NotContain("password");
    }

    [Fact]
    public void BuildEnv_ProducesClusterctlTemplateVariables()
    {
        OpenStackProvisioningConfig config = ValidConfig();
        string cloudsYaml = "clouds:\n  openstack: {}\n";

        Dictionary<string, string> env = CapiTemplateInputs.BuildEnv(config, cloudsYaml);

        env["CLUSTER_NAME"].Should().Be("prod-eu-1");
        env["KUBERNETES_VERSION"].Should().Be("v1.31.4");
        env["CONTROL_PLANE_MACHINE_COUNT"].Should().Be("3");
        env["WORKER_MACHINE_COUNT"].Should().Be("4");
        env["OPENSTACK_CONTROL_PLANE_MACHINE_FLAVOR"].Should().Be("b.4c8gb");
        env["OPENSTACK_NODE_MACHINE_FLAVOR"].Should().Be("b.8c16gb");
        env["OPENSTACK_IMAGE_NAME"].Should().Be("ubuntu-2204-kube-v1.31.4");
        env["OPENSTACK_EXTERNAL_NETWORK_ID"].Should().Be("ext-net-123");
        env["OPENSTACK_SSH_KEY_NAME"].Should().Be("prod-eu-1-key");

        // clouds.yaml is passed base64-encoded to the CAPO template.
        string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(env["OPENSTACK_CLOUD_YAML_B64"]));
        decoded.Should().Be(cloudsYaml);
    }

    [Fact]
    public void NormalizeV3_AddsVersionSegmentOnlyWhenMissing()
    {
        OpenStackKeystoneClient.NormalizeV3("https://id.example.com:5000").Should().Be("https://id.example.com:5000/v3");
        OpenStackKeystoneClient.NormalizeV3("https://id.example.com:5000/v3").Should().Be("https://id.example.com:5000/v3");
        OpenStackKeystoneClient.NormalizeV3("https://id.example.com:5000/v3/").Should().Be("https://id.example.com:5000/v3");
    }

    [Fact]
    public void BuildCloudConf_ProducesCcmCsiCloudConfig()
    {
        OpenStackConnection connection = new()
        {
            Name = "Cleura", AuthUrl = "https://identity.example.com:5000/v3", Region = "Sto2",
        };
        ApplicationCredential appCred = new() { Id = "cred-id", Name = "entkube", Secret = "s3cr3t" };
        OpenStackProvisioningConfig config = ValidConfig();
        config.ExternalNetworkId = "ext-net-123";

        string conf = CapiTemplateInputs.BuildCloudConf(connection, appCred, config);

        conf.Should().Contain("[Global]");
        conf.Should().Contain("auth-url=https://identity.example.com:5000/v3");
        conf.Should().Contain("application-credential-id=cred-id");
        conf.Should().Contain("application-credential-secret=s3cr3t");
        conf.Should().Contain("[LoadBalancer]");
        conf.Should().Contain("floating-network-id=ext-net-123");
        conf.Should().Contain("[BlockStorage]");
    }
}

/// <summary>
/// Tests for the OpenStack platform + CubeFS catalog entries added for provisioned clusters.
/// </summary>
public class OpenStackCatalogTests
{
    [Theory]
    [InlineData("cilium")]
    [InlineData("openstack-ccm")]
    [InlineData("openstack-cinder-csi")]
    [InlineData("cubefs")]
    public void Catalog_ContainsProvisioningEntries(string key)
    {
        ComponentCatalog.GetByKey(key).Should().NotBeNull();
    }

    [Fact]
    public void CinderCsi_DependsOnCcm_WhichExists()
    {
        CatalogEntry csi = ComponentCatalog.GetByKey("openstack-cinder-csi")!;
        csi.Dependencies.Should().Contain("openstack-ccm");
        // Every declared dependency must resolve to a real catalog entry.
        foreach (string dep in csi.Dependencies)
            ComponentCatalog.GetByKey(dep).Should().NotBeNull($"dependency '{dep}' must exist");
    }

    [Fact]
    public void CubeFs_DependsOnCinderCsi_WhichExists()
    {
        CatalogEntry cubefs = ComponentCatalog.GetByKey("cubefs")!;
        foreach (string dep in cubefs.Dependencies)
            ComponentCatalog.GetByKey(dep).Should().NotBeNull($"dependency '{dep}' must exist");
    }

    [Fact]
    public void CcmAndCsi_ReferenceExistingCloudConfigSecret()
    {
        // Both must NOT let the chart create the secret — provisioning writes it.
        foreach (string key in new[] { "openstack-ccm", "openstack-cinder-csi" })
        {
            CatalogEntry e = ComponentCatalog.GetByKey(key)!;
            e.DefaultValues.Should().Contain("name: cloud-config");
            e.DefaultValues.Should().Contain("create: false");
        }
    }

    [Theory]
    [InlineData("velero")]
    [InlineData("cluster-autoscaler")]
    public void Catalog_ContainsBaselineEntries_WithValidHelmCoords(string key)
    {
        CatalogEntry e = ComponentCatalog.GetByKey(key)!;
        e.Should().NotBeNull();
        e.HelmRepoUrl.Should().StartWith("http");
        e.HelmChartName.Should().NotBeNullOrWhiteSpace();
        e.HelmChartVersion.Should().NotBeNullOrWhiteSpace();
        e.DefaultNamespace.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ClusterAutoscaler_UsesClusterApiProviderInCluster()
    {
        CatalogEntry e = ComponentCatalog.GetByKey("cluster-autoscaler")!;
        e.DefaultValues.Should().Contain("cloudProvider: clusterapi");
        e.DefaultValues.Should().Contain("clusterAPIMode: incluster-incluster");
    }

    [Fact]
    public void ClusterAutoscaler_GrantsRbacForClusterApiResources()
    {
        // The clusterapi provider must read + scale MachineDeployments; the chart's default
        // ClusterRole doesn't cover the CAPI group, so extra RBAC is appended.
        CatalogEntry e = ComponentCatalog.GetByKey("cluster-autoscaler")!;
        e.DefaultValues.Should().Contain("cluster.x-k8s.io");
        e.DefaultValues.Should().Contain("machinedeployments/scale");
    }
}
