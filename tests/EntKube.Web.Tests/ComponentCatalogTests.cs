using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the ComponentCatalog — the static registry of well-known
/// infrastructure components. Verifies that catalog entries are valid,
/// the lookup methods work, and that catalog-based registration flows
/// correctly through the lifecycle service.
/// </summary>
public class ComponentCatalogTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly ComponentLifecycleService lifecycleService;
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid tenantId = Guid.NewGuid();

    public ComponentCatalogTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        // Seed a tenant, environment, and cluster for integration tests.

        Guid envId = Guid.NewGuid();
        Tenant tenant = new() { Id = tenantId, Name = "CatalogTenant", Slug = $"catalog-{tenantId:N}" };
        Data.Environment env = new() { Id = envId, TenantId = tenantId, Name = "production" };
        KubernetesCluster cluster = new()
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = envId,
            Name = "catalog-cluster",
            ApiServerUrl = "https://k8s.example.com",
            Kubeconfig = "apiVersion: v1\nkind: Config"
        };

        db.Set<Tenant>().Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        db.SaveChanges();

        byte[] testRootKey = Convert.FromBase64String("dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");
        VaultEncryptionService encryption = new(testRootKey);
        VaultService vaultService = new(dbFactory, encryption, TestServices.NoClusterAccess);
        lifecycleService = TestServices.BuildLifecycle(dbFactory, vaultService);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ──────── Catalog integrity ────────

    [Fact]
    public void Catalog_HasEntries()
    {
        // The catalog should never be empty — we ship with known components.

        ComponentCatalog.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public void Catalog_AllEntriesHaveRequiredFields()
    {
        // Every catalog entry must have all the fields needed for a valid
        // Helm install. Missing fields would cause install failures.

        foreach (CatalogEntry entry in ComponentCatalog.Entries)
        {
            entry.Key.Should().NotBeNullOrWhiteSpace($"entry must have a key");
            entry.DisplayName.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a display name");
            entry.Description.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a description");
            entry.Icon.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have an icon");
            entry.Category.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a category");
            entry.DefaultNamespace.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a default namespace");

            // Helm-specific fields are only required for HelmChart components.
            // Manifest / ManifestUrl components install raw YAML and have no chart.
            if (entry.ComponentType == "HelmChart")
            {
                entry.HelmRepoUrl.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a Helm repo URL");
                entry.HelmChartName.Should().NotBeNullOrWhiteSpace($"{entry.Key} must have a Helm chart name");
            }
        }
    }

    [Fact]
    public void Catalog_KeysAreUnique()
    {
        // Duplicate keys would cause lookup ambiguity.

        List<string> keys = ComponentCatalog.Entries.Select(e => e.Key).ToList();
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void OtelEbpf_ExposesResourceFieldsForBothWorkloads()
    {
        // The eBPF chart ships `resources: {}` for the agent DaemonSet AND for the k8sCache
        // Deployment, so without these fields both run BestEffort with no way to bound them from
        // the UI — which is how the agent ends up quietly pressing against a limit it never set.
        CatalogEntry entry = ComponentCatalog.GetByKey("otel-ebpf")!;

        string[] expected =
        [
            "resources.requests.cpu", "resources.requests.memory",
            "resources.limits.cpu", "resources.limits.memory",
            "k8sCache.resources.requests.cpu", "k8sCache.resources.requests.memory",
            "k8sCache.resources.limits.cpu", "k8sCache.resources.limits.memory",
        ];

        entry.FormFields.Select(f => f.YamlPath).Should().Contain(expected);
        entry.FormFields.Where(f => expected.Contains(f.YamlPath))
            .Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.DefaultValue),
                "a blank default would merge nothing and leave the workload BestEffort");
    }

    [Fact]
    public void OtelEbpf_ResourceFieldsMergeOntoTheChartsResourcePaths()
    {
        // Guards the dotted paths against a chart rename: a wrong path merges silently into a key
        // Helm ignores, so the values look edited while the pod keeps running unbounded.
        CatalogEntry entry = ComponentCatalog.GetByKey("otel-ebpf")!;
        Dictionary<string, string> values = entry.FormFields
            .Where(f => f.DefaultValue is { Length: > 0 })
            .ToDictionary(f => f.Key, f => f.DefaultValue!);

        string merged = CatalogComponentRegistrar.MergeFormValues(entry, values, []);

        YamlFormMerger.ExtractValue(merged, "resources.limits.memory").Should().Be("1Gi");
        YamlFormMerger.ExtractValue(merged, "resources.requests.cpu").Should().Be("100m");
        YamlFormMerger.ExtractValue(merged, "k8sCache.resources.limits.memory").Should().Be("512Mi");
        YamlFormMerger.ExtractValue(merged, "k8sCache.resources.requests.cpu").Should().Be("50m");
    }

    [Fact]
    public void OtelEbpf_CapsRouteCardinality()
    {
        // Without a routes section OBI falls back to its `heuristic` decorator, which replaces
        // only the path segments that LOOK like ids — a path shaped like a directory tree stays
        // unique forever, so every variant becomes another permanent metric series. Turning the
        // metric exporters off hides that; it does not fix it, and it returns the moment the RED
        // metrics field is set back to 9090. The cap is what makes that switch safe to flip.
        CatalogEntry entry = ComponentCatalog.GetByKey("otel-ebpf")!;
        Dictionary<string, string> values = entry.FormFields
            .Where(f => f.DefaultValue is { Length: > 0 })
            .ToDictionary(f => f.Key, f => f.DefaultValue!);

        string merged = CatalogComponentRegistrar.MergeFormValues(entry, values, []);

        YamlFormMerger.ExtractValue(merged, "config.data.routes.unmatched")
            .Should().Be("low-cardinality");
        YamlFormMerger.ExtractValue(merged, "config.data.routes.max_path_segment_cardinality")
            .Should().Be("10");
    }

    [Fact]
    public void OtelEbpf_ShipsWithBothMetricExportersOff()
    {
        // Both have to be said out loud, because config.data is a MAP-MERGE: the chart's own
        // defaults put an OTLP metrics endpoint at ${HOST_IP}:4318 and a Prometheus endpoint on
        // :9090, and simply not mentioning them here leaves both in place. That combination had
        // OBI aggregating RED metrics for every service and route on the node and sending them to
        // a collector with no metrics pipeline and to a scrape endpoint with no scraper — and
        // OBI's Prometheus registry only evicts expired label sets inside Collect(), which runs
        // on a scrape and nowhere else, so with no scraper nothing was ever evicted.
        CatalogEntry entry = ComponentCatalog.GetByKey("otel-ebpf")!;

        YamlFormMerger.ExtractValue(entry.DefaultValues!, "config.data.prometheus_export.port")
            .Should().Be("0", "an unscraped Prometheus endpoint never runs its own reaper");
        YamlFormMerger.ExtractValue(entry.DefaultValues!, "config.data.otel_metrics_export.endpoint")
            .Should().BeNullOrEmpty("the EntKube collector runs no metrics pipeline to receive them");

        // Traces are this component's remaining job and must still reach the collector.
        YamlFormMerger.ExtractValue(entry.DefaultValues!, "config.data.otel_traces_export.endpoint")
            .Should().Be("http://otel-collector.monitoring:4317");
    }

    [Fact]
    public void OtelEbpf_DeclaresInstrumentationsUnderTheExporterNotAtTheTopLevel()
    {
        // OBI has no top-level `instrumentations` key — the setting belongs to each exporter. It
        // parses its config non-strictly, so a top-level one is accepted, silently ignored, and
        // leaves the full default protocol list loaded: a dozen protocols, each another eBPF
        // program plus its maps on every instrumented executable, while the config on disk looks
        // like it selected three.
        CatalogEntry entry = ComponentCatalog.GetByKey("otel-ebpf")!;

        YamlMappingNode data = (YamlMappingNode)Navigate(entry.DefaultValues!, "config", "data");
        data.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value)
            .Should().NotContain("instrumentations",
                "a top-level instrumentations key is accepted, ignored, and changes nothing");

        YamlSequenceNode declared = (YamlSequenceNode)Navigate(
            entry.DefaultValues!, "config", "data", "otel_traces_export", "instrumentations");
        declared.Children.OfType<YamlScalarNode>().Should().NotBeEmpty();
    }

    /// <summary>
    /// Walks a YAML document by key. YamlFormMerger.ExtractValue only descends through mappings
    /// to a scalar leaf, so it cannot see a sequence or assert on a mapping's key set.
    /// </summary>
    private static YamlNode Navigate(string yaml, params string[] path)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        YamlNode current = stream.Documents[0].RootNode;

        foreach (string segment in path)
        {
            YamlMappingNode mapping = (YamlMappingNode)current;
            YamlScalarNode key = mapping.Children.Keys.OfType<YamlScalarNode>()
                .FirstOrDefault(k => k.Value == segment)
                ?? throw new InvalidOperationException($"no key '{segment}' in {string.Join('.', path)}");
            current = mapping.Children[key];
        }

        return current;
    }

    // ──────── Lookup ────────

    [Fact]
    public void GetByKey_ExistingEntry_ReturnsEntry()
    {
        CatalogEntry? entry = ComponentCatalog.GetByKey("kube-prometheus-stack");

        entry.Should().NotBeNull();
        entry!.DisplayName.Should().Be("Kube Prometheus Stack");
        entry.Category.Should().Be("Monitoring");
    }

    // ── SeaweedFS: the defaults that make the chart safe to install ──

    [Fact]
    public void SeaweedFS_keeps_its_data_on_persistent_volumes()
    {
        // The chart defaults every directory to hostPath: a pod rescheduled to another node comes up
        // empty, and two releases on one node share a directory. An emptyDir is no better for data —
        // it is wiped on every restart — so data must be a PersistentVolumeClaim.
        string values = ComponentCatalog.GetByKey("seaweedfs")!.DefaultValues!;
        string Type(params string[] path) => ((YamlScalarNode)Navigate(values, path)).Value!;

        List<string> dataTypes = [Type("master", "data", "type"), Type("filer", "data", "type")];
        YamlSequenceNode dataDirs = (YamlSequenceNode)Navigate(values, "volume", "dataDirs");
        dataDirs.Children.Should().NotBeEmpty();
        dataTypes.AddRange(dataDirs.Children.Cast<YamlMappingNode>()
            .Select(d => ((YamlScalarNode)d.Children[new YamlScalarNode("type")]).Value!));

        dataTypes.Should().OnlyContain(t => t == "persistentVolumeClaim");
    }

    [Theory]
    [InlineData("master")]
    [InlineData("volume")]
    [InlineData("filer")]
    [InlineData("s3")]
    public void SeaweedFS_keeps_its_logs_off_the_node(string server)
    {
        // Logs may be lost on restart, but they must not land in a hostPath on the node either.
        string values = ComponentCatalog.GetByKey("seaweedfs")!.DefaultValues!;

        ((YamlScalarNode)Navigate(values, server, "logs", "type")).Value
            .Should().BeOneOf("emptyDir", "persistentVolumeClaim");
    }

    [Theory]
    [InlineData("master")]
    [InlineData("volume")]
    [InlineData("filer")]
    [InlineData("s3")]
    public void SeaweedFS_runs_every_server_as_a_non_root_user(string server)
    {
        // The image declares no USER, so without an explicit uid it runs as root. The chart renders a
        // security context only when its "enabled" flag is set.
        string values = ComponentCatalog.GetByKey("seaweedfs")!.DefaultValues!;
        string Value(params string[] path) => ((YamlScalarNode)Navigate(values, [server, .. path])).Value!;

        Value("podSecurityContext", "enabled").Should().Be("true");
        Value("podSecurityContext", "runAsNonRoot").Should().Be("true");
        int.Parse(Value("podSecurityContext", "runAsUser")).Should().BePositive();
        Value("containerSecurityContext", "enabled").Should().Be("true");
        Value("containerSecurityContext", "allowPrivilegeEscalation").Should().Be("false");
    }

    [Fact]
    public void SeaweedFS_gateway_refuses_anonymous_requests()
    {
        // Without enableAuth the S3 gateway serves anyone who can reach the Service.
        string values = ComponentCatalog.GetByKey("seaweedfs")!.DefaultValues!;

        ((YamlScalarNode)Navigate(values, "s3", "enabled")).Value.Should().Be("true");
        ((YamlScalarNode)Navigate(values, "s3", "enableAuth")).Value.Should().Be("true");
    }

    [Fact]
    public void SeaweedFS_volume_size_field_writes_the_first_data_directory()
    {
        // The volume size lives inside a list. The form path has to reach into it rather than
        // create a sibling key the chart never reads.
        CatalogEntry entry = ComponentCatalog.GetByKey("seaweedfs")!;
        ComponentFormField field = entry.FormFields.Single(f => f.Key == "volume-size");

        string merged = YamlFormMerger.MergeFormValues(entry.DefaultValues!, new Dictionary<string, string> { [field.YamlPath] = "200Gi" });

        YamlMappingNode first = (YamlMappingNode)((YamlSequenceNode)Navigate(merged, "volume", "dataDirs")).Children[0];
        ((YamlScalarNode)first.Children[new YamlScalarNode("size")]).Value.Should().Be("200Gi");
        ((YamlScalarNode)first.Children[new YamlScalarNode("type")]).Value.Should().Be("persistentVolumeClaim");

        // And it must read back. The edit form fills each field from the installed values; a path
        // that cannot be read shows the default, and saving writes that default over 200Gi.
        YamlFormMerger.ExtractValue(merged, field.YamlPath).Should().Be("200Gi");
        YamlFormMerger.ExtractValue(entry.DefaultValues!, field.YamlPath).Should().Be(field.DefaultValue);
    }

    [Fact]
    public void SeaweedFS_filer_and_volume_servers_refuse_unsigned_requests()
    {
        // s3.enableAuth guards only the gateway. Without signing, any pod can read or delete every
        // object through the filer (8888) or a volume server (8080) without the S3 keys.
        string values = ComponentCatalog.GetByKey("seaweedfs")!.DefaultValues!;

        foreach (string key in new[] { "volumeWrite", "volumeRead", "filerWrite", "filerRead" })
        {
            ((YamlScalarNode)Navigate(values, "global", "seaweedfs", "securityConfig", "jwtSigning", key))
                .Value.Should().Be("true", $"jwtSigning.{key} closes a path around the S3 gateway");
        }
    }

    [Fact]
    public void SeaweedFS_admits_only_the_S3_port_from_outside_the_release()
    {
        // The master deletes whole buckets over unauthenticated HTTP (/col/delete) and the filer and
        // volume gRPC ports read and delete objects with no token. Only the NetworkPolicy keeps other
        // pods away from them: on, and with exactly one extra rule, for 8333 and nothing else.
        string values = ComponentCatalog.GetByKey("seaweedfs")!.DefaultValues!;

        ((YamlScalarNode)Navigate(values, "networkPolicy", "enabled")).Value.Should().Be("true");

        YamlSequenceNode extra = (YamlSequenceNode)Navigate(values, "networkPolicy", "extraIngress");
        YamlMappingNode rule = (YamlMappingNode)extra.Children.Should().ContainSingle().Subject;
        YamlSequenceNode ports = (YamlSequenceNode)rule.Children[new YamlScalarNode("ports")];
        ports.Children.Cast<YamlMappingNode>()
            .Select(p => ((YamlScalarNode)p.Children[new YamlScalarNode("port")]).Value)
            .Should().Equal("8333");
    }

    [Fact]
    public void SeaweedFS_grows_one_volume_per_new_bucket()
    {
        // Each bucket is a collection; the chart's default grows 7 volumes on a collection's first
        // write, which exhausts a 50Gi volume server after about seven buckets.
        string values = ComponentCatalog.GetByKey("seaweedfs")!.DefaultValues!;

        ((YamlScalarNode)Navigate(values, "master", "extraEnvironmentVars", "WEED_MASTER_VOLUME_GROWTH_COPY_1"))
            .Value.Should().Be("1");
    }

    [Fact]
    public void SeaweedFS_form_offers_nothing_that_breaks_an_installed_release()
    {
        // The filer's and master's volumeClaimTemplates are immutable (the chart's resize hook covers
        // only volume servers), and each extra volume server needs a node of its own.
        ComponentCatalog.GetByKey("seaweedfs")!.FormFields.Select(f => f.YamlPath)
            .Should().NotContain(["filer.data.size", "master.data.size", "volume.replicas", "master.replicas", "filer.replicas"]);
    }

    [Theory]
    [InlineData("seaweedfs-csi-driver", "seaweedfs-csi-driver")]
    [InlineData("seaweedfs-operator", "seaweedfs-operator")]
    public void A_sibling_chart_of_seaweedfs_does_not_resolve_to_the_seaweedfs_entry(string release, string chart)
    {
        // The release name contains "seaweedfs", but the chart says it is the CSI driver or the
        // operator. Resolving it to the seaweedfs entry would record the wrong chart on import and
        // offer an "upgrade" that replaces it with a whole SeaweedFS cluster.
        ComponentCatalog.FindByRelease(release, chart)?.Key.Should().NotBe("seaweedfs");
    }

    [Theory]
    [InlineData("seaweedfs", "seaweedfs")]
    [InlineData("objects", "seaweedfs")]
    [InlineData("seaweedfs-prod", null)]
    [InlineData("seaweedfs-prod", "seaweedfs")]
    public void A_seaweedfs_release_still_resolves_to_the_seaweedfs_entry(string release, string? chart)
    {
        ComponentCatalog.FindByRelease(release, chart)!.Key.Should().Be("seaweedfs");
    }

    [Fact]
    public void GetByKey_CaseInsensitive_ReturnsEntry()
    {
        // Operators shouldn't have to remember exact casing.

        CatalogEntry? entry = ComponentCatalog.GetByKey("SEAWEEDFS");

        entry.Should().NotBeNull();
        entry!.Key.Should().Be("seaweedfs");
    }

    [Fact]
    public void GetByKey_Unknown_ReturnsNull()
    {
        CatalogEntry? entry = ComponentCatalog.GetByKey("nonexistent-component");

        entry.Should().BeNull();
    }

    [Fact]
    public void GetByCategory_GroupsCorrectly()
    {
        IReadOnlyList<IGrouping<string, CatalogEntry>> groups = ComponentCatalog.GetByCategory();

        groups.Should().NotBeEmpty();

        // Every entry that can still be INSTALLED, which is no longer every entry: a retired one is
        // deliberately withheld here while staying resolvable everywhere else, so that a cluster which
        // already runs it can still uninstall it. See CatalogEntry.Retired and RetiredCatalogEntryTests.
        groups.SelectMany(g => g).Should()
            .HaveCount(ComponentCatalog.Entries.Count(e => !e.Retired));

        groups.SelectMany(g => g).Should().NotContain(e => e.Retired);
    }

    // ──────── Registration from catalog ────────

    [Fact]
    public void ToRegistration_FillsAllHelmDetails()
    {
        // When we create a registration from a catalog entry, it should
        // carry over all the Helm details so the operator doesn't need
        // to enter them manually.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);

        registration.Name.Should().Be("kube-prometheus-stack");
        registration.ComponentType.Should().Be("HelmChart");
        registration.Namespace.Should().Be("monitoring");
        registration.HelmRepoUrl.Should().Be("https://prometheus-community.github.io/helm-charts");
        registration.HelmChartName.Should().Be("kube-prometheus-stack");
        registration.ReleaseName.Should().Be("kube-prometheus-stack");
        registration.HelmValues.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RegisterFromCatalog_CreatesComponentWithDefaults()
    {
        // The full flow: pick from catalog → register → component is ready
        // with all Helm details pre-filled.

        CatalogEntry entry = ComponentCatalog.GetByKey("seaweedfs")!;
        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);

        ClusterComponent component = await lifecycleService.RegisterComponentAsync(tenantId, clusterId, registration);

        component.Name.Should().Be("seaweedfs");
        component.HelmRepoUrl.Should().Be("https://seaweedfs.github.io/seaweedfs/helm");
        component.HelmChartName.Should().Be("seaweedfs");
        component.Namespace.Should().Be("seaweedfs");
        component.Status.Should().Be(ComponentStatus.NotInstalled);
        component.HelmValues.Should().Contain("enableAuth");
    }

    [Fact]
    public async Task RegisterFromCatalog_CanOverrideValues()
    {
        // Operators should be able to modify the default values before
        // registering — for example, changing resource limits or passwords.

        CatalogEntry entry = ComponentCatalog.GetByKey("cert-manager")!;
        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);

        // Override the values with custom config.

        registration.HelmValues = """
            crds:
              enabled: true
            resources:
              requests:
                memory: 256Mi
                cpu: 100m
            """;

        ClusterComponent component = await lifecycleService.RegisterComponentAsync(tenantId, clusterId, registration);

        component.HelmValues.Should().Contain("256Mi");
        component.HelmValues.Should().NotContain("128Mi");
    }

    [Fact]
    public async Task RegisterFromCatalog_DuplicateRejected()
    {
        // Can't install the same catalog component twice on one cluster.

        CatalogEntry entry = ComponentCatalog.GetByKey("traefik")!;
        ComponentRegistration registration = ComponentCatalog.ToRegistration(entry);
        await lifecycleService.RegisterComponentAsync(tenantId, clusterId, registration);

        // Second attempt should be rejected.

        Func<Task> secondAttempt = () => lifecycleService.RegisterComponentAsync(tenantId, 
            clusterId, ComponentCatalog.ToRegistration(entry));

        await secondAttempt.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    // ──────── Dependency resolution ────────

    [Fact]
    public void CheckDependencies_NoDependencies_Satisfied()
    {
        // Components with no dependencies should always pass the check.

        CatalogEntry entry = ComponentCatalog.GetByKey("seaweedfs")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeTrue();
        result.MissingDependencies.Should().BeEmpty();
        result.MissingOneOfRequirements.Should().BeEmpty();
    }

    [Fact]
    public void CheckDependencies_DirectDependencyMissing_NotSatisfied()
    {
        // kube-prometheus-stack requires cert-manager and letsencrypt-issuer.
        // If they're not installed, the check should report them missing.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeFalse();
        result.MissingDependencies.Should().Contain("cert-manager");
        result.MissingDependencies.Should().Contain("letsencrypt-issuer");
    }

    [Fact]
    public void CheckDependencies_OneOfMissing_NotSatisfied()
    {
        // kube-prometheus-stack needs either traefik or istio.
        // If neither is present, the one-of requirement should be missing.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(
            entry, ["cert-manager", "letsencrypt-issuer"]);

        result.IsSatisfied.Should().BeFalse();
        result.MissingOneOfRequirements.Should().HaveCount(1);
        result.MissingOneOfRequirements[0].Label.Should().Be("Ingress Controller");
    }

    [Fact]
    public void CheckDependencies_TraefikSatisfiesIngress()
    {
        // Installing traefik should satisfy the "one of ingress" requirement.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(
            entry, ["cert-manager", "letsencrypt-issuer", "traefik"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CheckDependencies_IstioSatisfiesIngress()
    {
        // Installing istio should also satisfy the "one of ingress" requirement.

        CatalogEntry entry = ComponentCatalog.GetByKey("kube-prometheus-stack")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(
            entry, ["cert-manager", "letsencrypt-issuer", "istio"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CheckDependencies_IstioRequiresBase()
    {
        // The istio gateway depends on istio-base (istiod).

        CatalogEntry entry = ComponentCatalog.GetByKey("istio")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeFalse();
        result.MissingDependencies.Should().Contain("istio-base");
    }

    [Fact]
    public void CheckDependencies_IstioWithBase_Satisfied()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey("istio")!;

        // istio depends on both gateway-api-crds and istio-base.
        DependencyCheckResult result = ComponentCatalog.CheckDependencies(
            entry, ["gateway-api-crds", "istio-base"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void CheckDependencies_LetsEncryptRequiresCertManager()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey("letsencrypt-issuer")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, []);

        result.IsSatisfied.Should().BeFalse();
        result.MissingDependencies.Should().Contain("cert-manager");
    }

    [Fact]
    public void CheckDependencies_CaseInsensitive()
    {
        // Installed component names should match case-insensitively.

        CatalogEntry entry = ComponentCatalog.GetByKey("letsencrypt-issuer")!;

        DependencyCheckResult result = ComponentCatalog.CheckDependencies(entry, ["Cert-Manager"]);

        result.IsSatisfied.Should().BeTrue();
    }

    [Fact]
    public void Catalog_IstioBaseExists()
    {
        // Ensure the istio-base entry exists since istio depends on it.
        // It installs the istio "base" chart (CRDs) with istiod as a subchart.

        CatalogEntry? entry = ComponentCatalog.GetByKey("istio-base");
        entry.Should().NotBeNull();
        entry!.HelmChartName.Should().Be("base");
    }

    [Fact]
    public void Catalog_LetsEncryptIssuerExists()
    {
        CatalogEntry? entry = ComponentCatalog.GetByKey("letsencrypt-issuer");
        entry.Should().NotBeNull();
        entry!.Dependencies.Should().Contain("cert-manager");
    }

    // ──────── Istio gateway matching (shared "gateway" chart) ────────
    // Both istio gateway entries use HelmChartName "gateway", distinguished only by
    // release name. Real deployments use custom release names (e.g. "istio-gw-external")
    // that don't equal the catalog DefaultReleaseName, so matching must not require an
    // exact release-name match.

    [Fact]
    public void IsComponentMatch_ExternalGateway_CustomReleaseName_MatchesExternalEntry()
    {
        CatalogEntry external = ComponentCatalog.GetByKey("istio")!;

        ComponentCatalog.IsComponentMatch(
            "istio-gw-external", external, helmChartName: "gateway", releaseName: "istio-gw-external")
            .Should().BeTrue();
    }

    [Fact]
    public void IsComponentMatch_InternalGateway_CustomReleaseName_MatchesInternalEntry()
    {
        CatalogEntry @internal = ComponentCatalog.GetByKey("istio-internal")!;

        ComponentCatalog.IsComponentMatch(
            "istio-gw-internal", @internal, helmChartName: "gateway", releaseName: "istio-gw-internal")
            .Should().BeTrue();
    }

    [Fact]
    public void IsComponentMatch_Gateways_DoNotCrossMatch()
    {
        CatalogEntry external = ComponentCatalog.GetByKey("istio")!;
        CatalogEntry @internal = ComponentCatalog.GetByKey("istio-internal")!;

        // External component must not mark the Internal entry as added, and vice versa.
        ComponentCatalog.IsComponentMatch(
            "istio-gw-external", @internal, helmChartName: "gateway", releaseName: "istio-gw-external")
            .Should().BeFalse();
        ComponentCatalog.IsComponentMatch(
            "istio-gw-internal", external, helmChartName: "gateway", releaseName: "istio-gw-internal")
            .Should().BeFalse();
    }

    [Fact]
    public void ResolveForComponent_Gateways_ResolveToDistinctEntries()
    {
        ComponentCatalog.ResolveForComponent("istio-gw-external", "gateway")?.Key.Should().Be("istio");
        ComponentCatalog.ResolveForComponent("istio-gw-internal", "gateway")?.Key.Should().Be("istio-internal");
    }

    [Fact]
    public void KubePrometheusStack_ScrapesMonitorsCreatedByOtherOperators()
    {
        // The chart defaults these to true, which restricts Prometheus to the PodMonitors and
        // ServiceMonitors carrying its own Helm release label. Operator-created ones — a CNPG
        // database's PodMonitor, for instance — are then never scraped, and the database
        // monitoring panel has nothing to show.
        CatalogEntry? entry = ComponentCatalog.GetByKey("kube-prometheus-stack");

        entry.Should().NotBeNull();
        entry!.DefaultValues.Should().Contain("podMonitorSelectorNilUsesHelmValues: false");
        entry.DefaultValues.Should().Contain("serviceMonitorSelectorNilUsesHelmValues: false");
    }

    [Fact]
    public void AllCatalogDefaultValuesAreParseableYaml()
    {
        // A stray indent in one of these raw string literals only surfaces as a failed helm
        // install against a real cluster, so parse them all here instead. Entries carrying
        // %%PLACEHOLDER%% tokens are templates that are substituted at apply time — a bare '%'
        // opens a YAML directive, so stand in a value first and check the structure around it.
        foreach (CatalogEntry entry in ComponentCatalog.Entries.Where(e => !string.IsNullOrWhiteSpace(e.DefaultValues)))
        {
            string yamlText = System.Text.RegularExpressions.Regex.Replace(
                entry.DefaultValues!, "%%[A-Z0-9_]+%%", "placeholder");

            Action parse = () =>
            {
                YamlDotNet.RepresentationModel.YamlStream yaml = [];
                yaml.Load(new StringReader(yamlText));
            };
            parse.Should().NotThrow($"'{entry.Key}' default values must be valid YAML");
        }
    }

    // ── Detection: a component is found by what it OWNS, never by what it uses ──

    [Fact]
    public void NoTwoEntriesDetectByTheSameCrd()
    {
        // A CRD names its owner. When a second entry detects by it, the scan reports that entry
        // installed wherever the owner is: Cinder CSI was detected by the snapshot CRDs, so every
        // EKS, GKE and AKS cluster listed an OpenStack CSI driver it does not have.
        List<string> claimedTwice = ComponentCatalog.Entries
            .SelectMany(e => e.DetectionCrds.Select(crd => (crd, e.Key)))
            .GroupBy(x => x.crd, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Key))}")
            .ToList();

        claimedTwice.Should().BeEmpty();
    }

    [Fact]
    public void CinderCsi_IsDetectedByItsOwnCsiDriver_NotByTheSnapshotCrds()
    {
        CatalogEntry cinder = ComponentCatalog.GetByKey("openstack-cinder-csi")!;

        cinder.DetectionCrds.Should().NotContain(crd => crd.EndsWith(".snapshot.storage.k8s.io"));
        cinder.DetectionResource.Should().NotBeNull();
        cinder.DetectionResource!.Accepts(CsiDriver("cinder.csi.openstack.org")).Should().BeTrue();
        cinder.DetectionResource.Accepts(CsiDriver("ebs.csi.aws.com")).Should().BeFalse();
    }

    [Fact]
    public void MetricsServer_IsFoundByTheExactResourceMetricsApi()
    {
        // Read by exact name. KEDA registers v1beta1.external.metrics.k8s.io, and a substring match on
        // "metrics.k8s.io" would report metrics-server present on every cluster running KEDA, which is
        // exactly where the HPAs that need it are.
        CatalogEntry entry = ComponentCatalog.GetByKey("metrics-server")!;

        entry.ProvidedByApiService.Should().Be("v1beta1.metrics.k8s.io");
        entry.DetectionResource.Should().BeNull("the APIService lookup replaces substring detection");
        entry.DetectionCrds.Should().BeEmpty();
    }

    [Fact]
    public void MetricsServer_VerifiesKubeletCertificatesByDefault()
    {
        // Skipping verification is a per-cluster decision for kind and plain kubeadm, written out
        // in the default values for an operator to uncomment. It must not be what everyone gets.
        CatalogEntry entry = ComponentCatalog.GetByKey("metrics-server")!;
        YamlStream stream = new();
        stream.Load(new StringReader(entry.DefaultValues!));
        YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;

        root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).Should().NotContain("args");
    }

    [Fact]
    public void SnapshotController_KeepsItsCrdsWhenUninstalled()
    {
        // Deleting a CRD deletes every object of that kind. The chart's default would take every
        // VolumeSnapshot on the cluster with the release.
        CatalogEntry entry = ComponentCatalog.GetByKey("snapshot-controller")!;

        ((YamlScalarNode)Navigate(entry.DefaultValues!, "keepCRDs")).Value.Should().Be("true");
    }

    [Fact]
    public void Reloader_RestartsOnlyAnnotatedWorkloadsByDefault()
    {
        // Cluster-wide restarts reach every other component's pods; that has to be chosen.
        CatalogEntry entry = ComponentCatalog.GetByKey("reloader")!;

        ((YamlScalarNode)Navigate(entry.DefaultValues!, "reloader", "autoReloadAll")).Value.Should().Be("false");
        entry.FormFields.Single(f => f.Key == "auto-reload-all").DefaultValue.Should().Be("false");
    }


    // ── Node and workload maintenance: the defaults that keep them from hurting ──

    [Fact]
    public void Kured_reboots_only_inside_a_night_window_and_never_through_a_failed_drain()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey("kured")!;
        string Value(string key) => ((YamlScalarNode)Navigate(entry.DefaultValues!, "configuration", key)).Value!;

        // Without a window kured reboots at any hour, office hours included.
        Value("startTime").Should().NotBeNullOrEmpty();
        Value("endTime").Should().NotBeNullOrEmpty();
        Value("timeZone").Should().NotBeNullOrEmpty();
        // A PDB that never allows eviction would hold the drain open for ever; giving up is fine,
        // rebooting through it is not.
        Value("drainTimeout").Should().NotBeNullOrEmpty().And.NotBe("0");
        Value("forceReboot").Should().Be("false");
        Value("concurrency").Should().Be("1");
        // A node that dies holding the lock would otherwise stop every later reboot.
        Value("lockTtl").Should().NotBe("0");
    }

    [Fact]
    public void Descheduler_never_moves_pods_with_persistent_volumes()
    {
        CatalogEntry entry = ComponentCatalog.GetByKey("descheduler")!;

        YamlSequenceNode plugins = (YamlSequenceNode)Navigate(
            entry.DefaultValues!, "deschedulerPolicy", "profiles");
        YamlMappingNode evictor = ((YamlSequenceNode)((YamlMappingNode)plugins[0])
                .Children[new YamlScalarNode("pluginConfig")])
            .OfType<YamlMappingNode>()
            .Single(p => ((YamlScalarNode)p.Children[new YamlScalarNode("name")]).Value == "DefaultEvictor");

        YamlSequenceNode protections = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)evictor
                .Children[new YamlScalarNode("args")]).Children[new YamlScalarNode("podProtections")])
            .Children[new YamlScalarNode("extraEnabled")];

        protections.OfType<YamlScalarNode>().Select(n => n.Value).Should().Contain("PodsWithPVC");
    }

    [Fact]
    public void Tetragon_export_is_rate_limited_by_default()
    {
        // Every process start in every pod is an event. Unlimited, the stream can outgrow the
        // telemetry store the in-cluster collector ships it to.
        CatalogEntry entry = ComponentCatalog.GetByKey("tetragon")!;
        string limit = ((YamlScalarNode)Navigate(entry.DefaultValues!, "tetragon", "exportRateLimit")).Value!;

        int.Parse(limit).Should().BePositive();
        entry.FormFields.Single(f => f.Key == "export-rate-limit").DefaultValue.Should().Be(limit);
    }

    private static string CsiDriver(string name) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            apiVersion = "storage.k8s.io/v1", kind = "CSIDriver",
            metadata = new { name },
            spec = new { attachRequired = true }
        });
}
