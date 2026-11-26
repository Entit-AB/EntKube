using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// One tenant's cluster, a stranger's beside it, and the question every test here asks of them: what
/// values would an apply hand to helm? Asked through <see cref="ComponentLifecycleService.GetInstallCommandAsync"/>
/// rather than of the steps alone, because half of each rule is WHERE in that method the step runs.
/// </summary>
public abstract class ValuesAtApply : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ComponentLifecycleService sut;

    protected ApplicationDbContext Db { get; }
    protected Guid TenantId { get; } = Guid.NewGuid();
    protected Guid ClusterId { get; } = Guid.NewGuid();
    protected Guid StrangersClusterId { get; } = Guid.NewGuid();

    protected ValuesAtApply()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        Db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        Db.Database.EnsureCreated();

        foreach ((Guid tenant, Guid cluster) in new[] { (TenantId, ClusterId), (Guid.NewGuid(), StrangersClusterId) })
        {
            Db.Tenants.Add(new Tenant { Id = tenant, Name = $"t-{tenant:N}", Slug = $"t-{tenant:N}" });
            Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant, Name = "prod" };
            Db.Environments.Add(env);
            Db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = cluster, TenantId = tenant, EnvironmentId = env.Id,
                Name = $"c-{cluster:N}", ApiServerUrl = "https://k8s.example",
            });
        }
        Db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        VaultService vault = new(factory,
            new VaultEncryptionService(Convert.FromBase64String(TestServices.TestRootKeyBase64)),
            TestServices.NoClusterAccess);
        sut = TestServices.BuildLifecycle(factory, vault);
    }

    public void Dispose()
    {
        Db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static string CatalogDefaults(string key) => ComponentCatalog.GetByKey(key)!.DefaultValues!;

    /// <summary>A catalog component as registration stores it, with the values given.</summary>
    protected Guid Install(string key, string? values)
    {
        CatalogEntry entry = ComponentCatalog.GetByKey(key)!;
        return InstallChart(key, entry.HelmChartName!, entry.DefaultReleaseName, entry.DefaultNamespace, values);
    }

    protected Guid InstallChart(string name, string chart, string? release, string? @namespace, string? values)
    {
        ClusterComponent component = new()
        {
            Id = Guid.NewGuid(), ClusterId = ClusterId, Name = name, ComponentType = "HelmChart",
            HelmChartName = chart, ReleaseName = release, Namespace = @namespace, HelmValues = values,
        };
        Db.ClusterComponents.Add(component);
        Db.SaveChanges();
        return component.Id;
    }

    /// <param name="configuration">
    /// The component's Configuration JSON, where an adopted stack records the namespace it really runs in.
    /// </param>
    protected ClusterComponent InstallPrometheus(
        Guid? cluster = null, string? @namespace = "monitoring", ComponentStatus status = ComponentStatus.Installed,
        string? configuration = null)
    {
        ClusterComponent prometheus = new()
        {
            Id = Guid.NewGuid(), ClusterId = cluster ?? ClusterId, Name = "kube-prometheus-stack",
            ComponentType = "HelmChart", HelmChartName = "kube-prometheus-stack",
            ReleaseName = "kube-prometheus-stack", Namespace = @namespace, Status = status,
            Configuration = configuration,
        };
        Db.ClusterComponents.Add(prometheus);
        Db.SaveChanges();
        return prometheus;
    }

    protected async Task<string> ApplyAsync(Guid componentId)
        => (await sut.GetInstallCommandAsync(TenantId, componentId)).ValuesYaml!;

    protected async Task<string?> StoredAsync(Guid componentId)
    {
        Db.ChangeTracker.Clear();
        return (await Db.ClusterComponents.SingleAsync(c => c.Id == componentId)).HelmValues;
    }

    protected static YamlMappingNode Parse(string yaml)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    protected static YamlNode? At(YamlMappingNode root, params string[] path)
    {
        YamlNode current = root;
        foreach (string segment in path)
        {
            if (current is not YamlMappingNode mapping) return null;
            YamlScalarNode? key = mapping.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == segment);
            if (key is null) return null;
            current = mapping.Children[key];
        }
        return current;
    }

    protected static string? Scalar(string yaml, params string[] path) => (At(Parse(yaml), path) as YamlScalarNode)?.Value;

    /// <summary>
    /// What a node says, spelled one way: keys sorted, and a quoted <c>"000"</c> told apart from a plain
    /// <c>000</c>, because to helm the first is a string and the second a number. Compared as text because
    /// FluentAssertions' structural comparison never finishes on YamlDotNet's nodes.
    /// </summary>
    protected static string Said(YamlNode node) => node switch
    {
        YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.Plain } plain => plain.Value ?? "",
        YamlScalarNode text => $"'{text.Value}'",
        YamlSequenceNode list => $"[{string.Join(",", list.Children.Select(Said))}]",
        YamlMappingNode map => "{" + string.Join(",", map.Children
            .Select(kv => $"{Said(kv.Key)}:{Said(kv.Value)}").Order(StringComparer.Ordinal)) + "}",
        _ => throw new InvalidOperationException($"Not a node helm values can hold: {node.GetType().Name}"),
    };

    /// <summary>Everything a document says except one top-level key — to show a step changed only that one.</summary>
    protected static string SaidApartFrom(string yaml, string key)
    {
        YamlMappingNode root = Parse(yaml);
        root.Children.Remove(new YamlScalarNode(key));
        return Said(root);
    }
}

/// <summary>
/// Keycloak's metrics are scraped through EntKube's own ServiceMonitor, so the chart's has to stop.
///
/// <para>The catalog turned the chart's on for every install. Its kind exists only where the Prometheus
/// Operator is, so on a cluster without it helm refused the whole release and Keycloak did not install.
/// And where it did install, EntKube's monitor beside it would have doubled every number.</para>
/// </summary>
public class KeycloakMonitorHandoverTests : ValuesAtApply
{
    /// <summary>The block the catalog shipped from 2026-08-20 until this change, with its neighbours.</summary>
    private const string StoredByTheOldCatalog = """
        database:
          vendor: postgres
        extraEnv: |
          - name: KC_PROXY_HEADERS
            value: xforwarded
          - name: KC_HOSTNAME_STRICT
            value: "false"
        metrics:
          enabled: true
        health:
          enabled: true
        serviceMonitor:
          enabled: true

        # HTTP configuration
        http:
          relativePath: /auth
        """;

    [Fact]
    public void The_catalog_no_longer_asks_the_chart_for_a_monitor()
    {
        At(Parse(CatalogDefaults("keycloak")), "serviceMonitor").Should().BeNull(
            "the chart renders it without asking whether the cluster has the CRD, and EntKube makes its own");

        // The endpoint the monitor reads is still there.
        Scalar(CatalogDefaults("keycloak"), "metrics", "enabled").Should().Be("true");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_keycloak_installs_without_the_charts_monitor_whether_or_not_there_is_a_prometheus(bool prometheus)
    {
        if (prometheus) InstallPrometheus();

        string values = await ApplyAsync(Install("keycloak", CatalogDefaults("keycloak")));

        Scalar(values, "serviceMonitor", "enabled").Should().NotBe("true");
    }

    /// <summary>
    /// Where EntKube's own Prometheus serves the cluster, its engine makes Keycloak's monitor, and the chart's
    /// beside it would be a second scrape of the same pods.
    /// </summary>
    [Fact]
    public async Task Where_entkubes_prometheus_serves_the_cluster_the_old_catalog_block_is_turned_off()
    {
        InstallPrometheus();

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false",
            "off in words rather than removed: an absent key is what the catalog fill-in writes into");
    }

    /// <summary>
    /// Everywhere else the engine makes nothing: it runs only where Catalog's kube-prometheus-stack serves the
    /// cluster. A Prometheus Operator EntKube did not install (the customer's own, Rancher's) may be the one
    /// scraping the chart's monitor, and turning it off would leave Keycloak unscraped with nothing to say so.
    /// So the block is handed to helm as it was stored.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(ComponentStatus.NotInstalled)]
    [InlineData(ComponentStatus.Failed)]
    [InlineData(ComponentStatus.Installing)]
    public async Task Where_entkubes_prometheus_does_not_serve_the_cluster_the_block_is_left_as_stored(ComponentStatus? status)
    {
        if (status is { } prometheus) InstallPrometheus(status: prometheus);

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:true}",
            "no EntKube monitor follows here, so the chart's is the only one there is");
    }

    [Fact]
    public async Task A_prometheus_on_another_cluster_does_not_hand_keycloak_over()
    {
        InstallPrometheus(cluster: StrangersClusterId);

        string values = await ApplyAsync(Install("keycloak", StoredByTheOldCatalog));

        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:true}");
    }

    [Theory]
    [InlineData("serviceMonitor: {enabled: true}")]
    [InlineData("serviceMonitor:\n  enabled: \"true\"")]
    [InlineData("serviceMonitor:\n  # chart default is off\n  enabled: true # scrape it")]
    public async Task The_same_block_written_another_way_is_still_the_catalogs(string block)
    {
        InstallPrometheus();
        Guid keycloak = Install("keycloak", "metrics:\n  enabled: true\n" + block + "\n");

        Scalar(await ApplyAsync(keycloak), "serviceMonitor", "enabled").Should().Be("false");
    }

    [Theory]
    [InlineData("serviceMonitor:\n  enabled: true\n  interval: 30s")]
    [InlineData("serviceMonitor:\n  enabled: true\n  labels:\n    release: prom")]
    [InlineData("serviceMonitor:\n  enabled: false")]
    [InlineData("serviceMonitor:\n  labels:\n    team: identity")]
    [InlineData("serviceMonitor: {}")]
    [InlineData("extraServiceMonitor:\n  enabled: true")]
    public async Task A_block_the_operator_made_theirs_is_kept_to_the_letter(string block)
    {
        InstallPrometheus();
        string stored = CatalogDefaults("keycloak") + "\n# ours\n" + block + "\n";

        (await ApplyAsync(Install("keycloak", stored))).Should().Be(stored);
    }

    [Fact]
    public async Task An_install_older_than_the_block_is_not_given_one()
    {
        InstallPrometheus();
        Guid keycloak = Install("keycloak", "database:\n  vendor: postgres\nhttp:\n  relativePath: /auth\n");

        string values = await ApplyAsync(keycloak);

        Scalar(values, "metrics", "enabled").Should().Be("true", "the fill-in itself still runs");
        At(Parse(values), "serviceMonitor").Should().BeNull(
            "the fill-in used to add the catalog's monitor to installs that never had one");
    }

    [Fact]
    public async Task Turning_it_off_leaves_one_serviceMonitor_key_and_the_rest_as_it_was()
    {
        InstallPrometheus();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog);

        string values = await ApplyAsync(keycloak);

        // Parse refuses a document with a key twice, so this also says the fill-in did not add a second.
        Said(At(Parse(values), "serviceMonitor")!).Should().Be("{enabled:false}");

        // Only what the fill-in brings (proxy, args, resources, extraEnvFrom) is new; every key that was
        // stored says what it said.
        YamlMappingNode before = Parse(StoredByTheOldCatalog);
        YamlMappingNode after = Parse(values);
        foreach (YamlScalarNode key in before.Children.Keys.Cast<YamlScalarNode>().Where(k => k.Value != "serviceMonitor"))
        {
            Said(after.Children[key]).Should().Be(Said(before.Children[key]), $"{key.Value} was not ours to touch");
        }
    }

    /// <summary>
    /// ⚠️ Pins that the step runs BEFORE the theme fragments are appended. They are joined on as text, so a
    /// component with its own <c>extraVolumes</c> ends up with that key twice: helm takes the last one, and
    /// our parser refuses the document. After them, this Keycloak would keep the chart's monitor.
    /// </summary>
    [Fact]
    public async Task A_keycloak_with_themes_and_volumes_of_its_own_is_handed_over_too()
    {
        InstallPrometheus();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog + "\nextraVolumes: |\n  - name: ours\n    emptyDir: {}\n");
        KeycloakComponentConfig config = new()
        {
            Id = Guid.NewGuid(), TenantId = TenantId, ClusterComponentId = keycloak,
            AdminUrl = "https://id.example/auth", AdminUsername = "admin",
        };
        Db.KeycloakComponentConfigs.Add(config);
        Db.KeycloakThemes.Add(new KeycloakTheme
        {
            Id = Guid.NewGuid(), TenantId = TenantId, KeycloakComponentConfigId = config.Id, Name = "Entit Blue",
        });
        Db.SaveChanges();

        string values = await ApplyAsync(keycloak);

        // Read as text: this is the document our parser will not open.
        values.Split('\n').Count(l => l.StartsWith("extraVolumes:", StringComparison.Ordinal)).Should().Be(2,
            "the theme's fragment is joined on after the operator's — the state this test is about");
        System.Text.RegularExpressions.Regex.Matches(values, @"^serviceMonitor:\r?\n\s+enabled: (\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Should().Equal("false");
    }

    /// <summary>
    /// The step writes the document out again, and the writer used to delete every <c>---</c> that ended a
    /// line — which is the end of a certificate's first line, and the separator in a list of manifests.
    /// </summary>
    [Fact]
    public async Task Certificates_and_manifests_kept_in_the_values_come_through_whole()
    {
        InstallPrometheus();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog + """

            secrets:
              truststore:
                stringData:
                  ca.crt: |
                    -----BEGIN CERTIFICATE-----
                    MIIBszCCAVmgAwIBAgIU
                    -----END CERTIFICATE-----
            extraManifests: |
              apiVersion: v1
              kind: ConfigMap
              ---
              apiVersion: v1
              kind: Secret
            """ + "\n");
        string stored = (await StoredAsync(keycloak))!;

        string values = await ApplyAsync(keycloak);

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false", "the document was written out again");
        foreach (string key in new[] { "secrets", "extraManifests" })
        {
            Said(At(Parse(values), key)!).Should().Be(Said(At(Parse(stored), key)!));
        }
    }

    /// <summary>
    /// A script kept as the last thing in the document keeps its last newline. The chart hashes
    /// <c>startupScripts</c> into the pod template, so losing it would restart Keycloak — and this change
    /// restarts nothing.
    /// </summary>
    [Fact]
    public async Task The_last_text_in_the_document_keeps_its_newline()
    {
        InstallPrometheus();
        Guid keycloak = Install("keycloak", CatalogDefaults("keycloak")
            + "\nserviceMonitor:\n  enabled: true\nstartupScripts:\n  init.sh: |\n    #!/bin/sh\n    echo ready\n");

        string values = await ApplyAsync(keycloak);

        Scalar(values, "serviceMonitor", "enabled").Should().Be("false", "the document was written out again");
        Scalar(values, "startupScripts", "init.sh").Should().Be("#!/bin/sh\necho ready\n");
    }

    [Fact]
    public async Task The_stored_values_are_not_rewritten()
    {
        InstallPrometheus();
        Guid keycloak = Install("keycloak", StoredByTheOldCatalog);

        await ApplyAsync(keycloak);

        (await StoredAsync(keycloak)).Should().Be(StoredByTheOldCatalog);
    }

    [Fact]
    public async Task Another_charts_serviceMonitor_is_not_keycloaks()
    {
        const string stored = "serviceMonitor:\n  enabled: true\n";

        (await ApplyAsync(InstallChart("acme-exporter", "acme-exporter", "acme-exporter", "acme", stored)))
            .Should().Be(stored);
    }
}

/// <summary>
/// SeaweedFS's NetworkPolicies admit only the release's own pods and the S3 port, so Prometheus cannot
/// reach the metrics port of any of its four servers. One rule lets Prometheus in, and nobody else: the
/// same port serves Go's pprof.
/// </summary>
public class SeaweedFsLetsPrometheusInTests : ValuesAtApply
{
    private static string ThePrometheusRule(string @namespace, params int[] ports) =>
        "{from:[{namespaceSelector:{matchLabels:{kubernetes.io/metadata.name:'" + @namespace
        + "'}},podSelector:{matchLabels:{app.kubernetes.io/name:prometheus}}}],ports:["
        + string.Join(",", (ports.Length == 0 ? [9327] : ports).Select(p => $"{{port:{p},protocol:TCP}}")) + "]}";

    /// <summary>The catalog's values with <paramref name="line"/> as the first line of each server's block.</summary>
    private static string CatalogWithServerLines(params (string Server, string Line)[] lines)
    {
        string values = CatalogDefaults("seaweedfs");
        foreach ((string server, string line) in lines)
        {
            string header = $"\n{server}:\n";
            values.Should().Contain(header);
            values = values.Replace(header, $"{header}  {line}\n");
        }
        return values;
    }

    private const string TheS3Rule = "{ports:[{port:8333,protocol:TCP}]}";

    private static List<string> Rules(string values)
        => [.. ((YamlSequenceNode)At(Parse(values), "networkPolicy", "extraIngress")!).Children.Select(Said)];

    /// <summary>The catalog's values with its <c>networkPolicy</c> block replaced by the operator's.</summary>
    private static string CatalogWithPolicy(string policy)
    {
        string values = CatalogDefaults("seaweedfs");
        int start = values.IndexOf("networkPolicy:", StringComparison.Ordinal);
        int end = values.IndexOf("\n\n", start, StringComparison.Ordinal);
        return values[..start] + policy + values[end..];
    }

    [Fact]
    public async Task Where_there_is_a_prometheus_its_pods_may_reach_the_metrics_port()
    {
        InstallPrometheus(@namespace: "monitoring");

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("monitoring"));
    }

    /// <summary>
    /// ⚠️ Two entries under <c>from</c> would mean OR: every pod in the monitoring namespace, and every pod
    /// calling itself prometheus in SeaweedFS's own. One entry holding both selectors means AND.
    /// </summary>
    [Fact]
    public async Task The_namespace_and_the_pod_have_to_match_together()
    {
        InstallPrometheus();

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        YamlSequenceNode rules = (YamlSequenceNode)At(Parse(values), "networkPolicy", "extraIngress")!;
        YamlSequenceNode from = (YamlSequenceNode)At((YamlMappingNode)rules.Children[^1], "from")!;
        from.Children.Count.Should().Be(1);
        ((YamlMappingNode)from.Children[0]).Children.Keys.Cast<YamlScalarNode>().Select(k => k.Value)
            .Should().BeEquivalentTo("namespaceSelector", "podSelector");
    }

    [Fact]
    public async Task The_rule_names_the_namespace_this_clusters_prometheus_runs_in()
    {
        InstallPrometheus(@namespace: "observability");

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("observability"));
    }

    /// <summary>
    /// Kubernetes allows a namespace called 2026 and refuses a label value that is a number. And whatever is
    /// recorded as the namespace must never turn the rule into an empty one, which would admit everyone.
    /// </summary>
    [Theory]
    [InlineData("2026")]
    [InlineData("a: [b")]
    public async Task The_namespace_is_a_text_whatever_it_looks_like(string @namespace)
    {
        InstallPrometheus(@namespace: @namespace);

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule(@namespace));
    }

    [Fact]
    public async Task Nothing_but_the_rule_changes()
    {
        InstallPrometheus();
        string stored = CatalogDefaults("seaweedfs");

        string values = await ApplyAsync(Install("seaweedfs", stored));

        Rules(values).Count.Should().Be(2, "the rule was added");
        SaidApartFrom(values, "networkPolicy").Should().Be(SaidApartFrom(stored, "networkPolicy"));
        Scalar(values, "networkPolicy", "enabled").Should().Be("true");
    }

    [Fact]
    public async Task The_operators_own_rules_stay_as_they_are_and_where_they_are()
    {
        InstallPrometheus();
        Guid seaweed = Install("seaweedfs", """
            networkPolicy:
              enabled: true
              extraIngress:
                - from:
                    - ipBlock:
                        cidr: 10.0.0.0/8
                  ports:
                    - protocol: TCP
                      port: 9327
                - ports:
                    - protocol: TCP
                      port: 8333
              components:
                filer:
                  extraIngress:
                    - ports:
                        - port: 8888
            """);

        string values = await ApplyAsync(seaweed);

        Rules(values).Should().Equal(
            "{from:[{ipBlock:{cidr:10.0.0.0/8}}],ports:[{port:9327,protocol:TCP}]}",
            TheS3Rule,
            ThePrometheusRule("monitoring"));
        Said(At(Parse(values), "networkPolicy", "components")!).Should().Be("{filer:{extraIngress:[{ports:[{port:8888}]}]}}");
    }

    [Fact]
    public async Task Values_that_already_hold_the_rule_are_handed_to_helm_as_they_are()
    {
        InstallPrometheus();
        string applied = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));
        Rules(applied).Should().Contain(ThePrometheusRule("monitoring"));

        Db.ClusterComponents.RemoveRange(Db.ClusterComponents.Where(c => c.HelmChartName == "seaweedfs"));
        Db.SaveChanges();

        (await ApplyAsync(Install("seaweedfs", applied))).Should().Be(applied, "the rule is there, however it got there");
    }

    /// <summary>
    /// ⚠️ Pins the ORDER. An install with no <c>networkPolicy</c> key gets the catalog's from the fill-in,
    /// enabled, with the S3 rule. Run before the fill-in, this step would see no policy and add nothing —
    /// and the fill-in would then close the port to Prometheus.
    /// </summary>
    [Fact]
    public async Task An_install_the_fill_in_gives_a_policy_to_gets_the_rule_with_it()
    {
        InstallPrometheus();
        Guid seaweed = Install("seaweedfs", "s3:\n  enabled: true\n  enableAuth: true\n");

        string values = await ApplyAsync(seaweed);

        Scalar(values, "networkPolicy", "enabled").Should().Be("true");
        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("monitoring"));
    }

    [Theory]
    [InlineData("networkPolicy:\n  enabled: true")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress:")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress: null")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress: []")]
    public async Task A_policy_with_no_extra_rules_is_given_this_one(string policy)
    {
        InstallPrometheus();

        string values = await ApplyAsync(Install("seaweedfs", CatalogWithPolicy(policy)));

        Rules(values).Should().Equal(ThePrometheusRule("monitoring"));
    }

    /// <summary>
    /// Off means the chart renders no policy and every port is reachable already. Nothing is written, and in
    /// particular no policy is switched on: it would be the first to select these pods, and would cut off S3.
    /// </summary>
    [Theory]
    [InlineData("networkPolicy:\n  enabled: false\n  extraIngress:\n    - ports:\n        - port: 8333")]
    [InlineData("networkPolicy: {}")]
    [InlineData("networkPolicy:\n  extraIngress: []")]
    [InlineData("networkPolicy:")]
    public async Task Where_the_operator_has_the_policy_off_nothing_is_written(string policy)
    {
        InstallPrometheus();
        string stored = CatalogWithPolicy(policy);

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    /// <summary>A list EntKube cannot read is not a list it may replace.</summary>
    [Theory]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress:\n    s3: 8333")]
    [InlineData("networkPolicy:\n  enabled: true\n  extraIngress: \"see wiki\"")]
    public async Task Rules_in_a_shape_that_is_not_a_list_are_left_alone(string policy)
    {
        InstallPrometheus();
        string stored = CatalogWithPolicy(policy);

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ComponentStatus.NotInstalled)]
    [InlineData(ComponentStatus.Failed)]
    public async Task Without_a_prometheus_nothing_is_opened(ComponentStatus? status)
    {
        if (status is { } prometheus) InstallPrometheus(status: prometheus);
        string stored = CatalogDefaults("seaweedfs");

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    [Fact]
    public async Task Another_tenants_prometheus_does_not_count()
    {
        InstallPrometheus(cluster: StrangersClusterId);
        string stored = CatalogDefaults("seaweedfs");

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    /// <summary>
    /// The engine places Prometheus where <see cref="PrometheusService.GetPrometheusConfig(ClusterComponent)"/>
    /// says, and an adopted stack's Configuration overrides the column. Opening the port to the column's
    /// namespace instead lets in prometheus-labelled pods of a namespace that is not Prometheus's, and leaves
    /// the real one blocked while the panel tells the operator to allow it in.
    /// </summary>
    [Theory]
    [InlineData("monitoring", "{\"namespace\":\"observability\"}", "observability")]
    [InlineData(null, "{\"namespace\":\"observability\"}", "observability")]
    [InlineData("  ", "{\"Namespace\":\"observability\"}", "observability")]
    [InlineData("monitoring", "{\"namespace\":\"\"}", "monitoring")]
    [InlineData("monitoring", "{\"serviceName\":\"prom\"}", "monitoring")]
    [InlineData("monitoring", "not json", "monitoring")]
    public async Task The_rule_names_the_namespace_the_engine_places_prometheus_in(
        string? column, string configuration, string expected)
    {
        ClusterComponent prometheus = InstallPrometheus(@namespace: column, configuration: configuration);

        string values = await ApplyAsync(Install("seaweedfs", CatalogDefaults("seaweedfs")));

        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule(expected));
        PrometheusService.GetPrometheusConfig(prometheus).Namespace.Should().Be(expected,
            "the apply and the engine give one answer");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_prometheus_whose_namespace_is_not_recorded_opens_nothing(string? @namespace)
    {
        InstallPrometheus(@namespace: @namespace);
        string stored = CatalogDefaults("seaweedfs");

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored,
            "a guessed namespace would admit somebody else's pods to the port that serves pprof");
    }

    /// <summary>
    /// The rule EntKube adds lives only in the copy handed to helm, so once Prometheus is gone the next
    /// apply renders without it and helm takes it away — nothing to undo here. A rule found in the STORED
    /// values was written by someone, and is kept: nothing tells it from the operator's own.
    /// </summary>
    [Fact]
    public async Task Once_prometheus_is_gone_entkubes_rule_goes_and_a_stored_one_stays()
    {
        InstallPrometheus();
        Guid seaweed = Install("seaweedfs", CatalogDefaults("seaweedfs"));
        string withPrometheus = await ApplyAsync(seaweed);
        Rules(withPrometheus).Should().Contain(ThePrometheusRule("monitoring"));
        (await StoredAsync(seaweed)).Should().Be(CatalogDefaults("seaweedfs"), "the rule was never stored");

        ClusterComponent prometheus = await Db.ClusterComponents.SingleAsync(c => c.HelmChartName == "kube-prometheus-stack");
        prometheus.Status = ComponentStatus.NotInstalled;
        await Db.SaveChangesAsync();

        (await ApplyAsync(seaweed)).Should().Be(CatalogDefaults("seaweedfs"));

        ClusterComponent row = await Db.ClusterComponents.SingleAsync(c => c.Id == seaweed);
        row.HelmValues = withPrometheus;
        await Db.SaveChangesAsync();

        (await ApplyAsync(seaweed)).Should().Be(withPrometheus);
    }

    /// <summary>
    /// The engine finds each server's port by its name, wherever its <c>metricsPort</c> put it; the rule has
    /// to open that same number, or a release with a port of its own stays blocked for good.
    /// </summary>
    [Fact]
    public async Task Each_servers_configured_metrics_port_is_opened()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: 9401"), ("volume", "metricsPort: \"9402\""), ("filer", "metricsPort: 9401"));

        string values = await ApplyAsync(Install("seaweedfs", stored));

        // The S3 gateway sets none, so it has the chart's 9327. Each number once, in order.
        Rules(values).Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9327, 9401, 9402));
    }

    [Fact]
    public async Task Where_every_server_names_one_port_only_that_port_is_opened()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: 9500"), ("volume", "metricsPort: 9500"),
            ("filer", "metricsPort: 9500"), ("s3", "metricsPort: 9500"));

        Rules(await ApplyAsync(Install("seaweedfs", stored)))
            .Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9500));
    }

    /// <summary>
    /// A port EntKube cannot read is not one it may guess: 9327 may be something else on that server. The
    /// others are still opened.
    /// </summary>
    [Theory]
    [InlineData("\"{{ .Values.port }}\"")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("{port: 9500}")]
    public async Task A_metrics_port_that_is_not_a_port_number_is_not_guessed(string unreadable)
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", $"metricsPort: {unreadable}"), ("volume", "metricsPort: 9500"),
            ("filer", "metricsPort: 9500"), ("s3", "metricsPort: 9500"));

        Rules(await ApplyAsync(Install("seaweedfs", stored)))
            .Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9500));
    }

    /// <summary>
    /// ⚠️ With no port it can read, there is no rule at all. A rule with an empty ports list is not "no
    /// port": it admits Prometheus's pods to every port, the unauthenticated gRPC ones among them.
    /// </summary>
    [Fact]
    public async Task Where_no_server_has_a_port_that_can_be_read_nothing_is_opened()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: abc"), ("volume", "metricsPort: abc"),
            ("filer", "metricsPort: abc"), ("s3", "metricsPort: abc"));

        (await ApplyAsync(Install("seaweedfs", stored))).Should().Be(stored);
    }

    [Fact]
    public async Task A_server_switched_off_opens_no_port_of_its_own()
    {
        InstallPrometheus();
        string stored = CatalogWithServerLines(
            ("master", "metricsPort: 9500"), ("volume", "metricsPort: 9500"), ("filer", "metricsPort: 9500"))
            .Replace("\ns3:\n  enabled: true\n", "\ns3:\n  enabled: false\n  metricsPort: 9600\n");
        stored.Should().Contain("metricsPort: 9600");

        Rules(await ApplyAsync(Install("seaweedfs", stored)))
            .Should().Equal(TheS3Rule, ThePrometheusRule("monitoring", 9500));
    }

    [Fact]
    public async Task Another_charts_network_policy_is_not_seaweedfs()
    {
        InstallPrometheus();
        const string stored = "networkPolicy:\n  enabled: true\n  extraIngress: []\n";

        (await ApplyAsync(InstallChart("acme-store", "acme-store", "acme-store", "acme", stored))).Should().Be(stored);
    }
}
