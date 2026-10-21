using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the telemetry collector's ingest defaults. The failure these guard against is silent: a
/// collector installed with the catalog's REPLACE_WITH_* placeholders starts, reports Ready and passes its
/// health check while every export goes to a host that does not resolve — so the only visible symptom is an
/// empty Logs tab on a cluster where "everything is running".
/// </summary>
public class TelemetryIngestDefaultsTests
{
    private const string PublicUrl = "https://entkube.example.com";
    private const string ExpectedIngestUrl = "https://entkube.example.com/ingest/otlp";

    private static readonly Guid ClusterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CatalogEntry Collector =>
        ComponentCatalog.GetByKey(TelemetryIngestDefaults.CollectorKey)!;

    private static (IngestTokenService Tokens, IConfiguration Config) Build(string? publicUrl = PublicUrl)
    {
        IConfiguration config = TestServices.TestConfiguration(publicUrl);
        return (new IngestTokenService(config), config);
    }

    // ──────── Pre-fill (registration time) ────────

    [Fact]
    public void ApplyTo_LeavesTheEndpointUnsetWhenEntKubeHasNoPublicUrl()
    {
        (IngestTokenService tokens, IConfiguration config) = Build(publicUrl: null);
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        // With Telemetry:PublicIngestUrl unset there is no address to fill in, and a blank field is
        // better than a placeholder: the install then refuses with MissingUrlMessage, which names the
        // setting. An address guessed from the request would be right only by luck.
        values.Should().NotContainKey(TelemetryIngestDefaults.EndpointFieldKey);
    }

    [Fact]
    public void ApplyTo_StillDoesNotOverrideAnEndpointTheOperatorTyped()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = new()
        {
            [TelemetryIngestDefaults.EndpointFieldKey] = "https://chosen.example.com/ingest/otlp",
        };

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        // The management plane is a better default, not a licence to redirect a deliberate choice. This
        // discipline is the one thing that did not change with the direction.
        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be("https://chosen.example.com/ingest/otlp");
    }

    [Fact]
    public void ApplyTo_FillsBlankIngestUrlAndToken()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        // ⚠️ This assertion was the opposite until the telemetry store came back to the management plane
        // (docs/telemetry-central-again.md): it used to require the cluster's own indexer and treat the
        // public URL as a regression. The destination is EntKube again, so the default is EntKube.
        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be(ExpectedIngestUrl);
        values[TelemetryIngestDefaults.TokenFieldKey].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ApplyTo_MintsATokenThatTheIngestEndpointAccepts()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        tokens.TryValidate(values[TelemetryIngestDefaults.TokenFieldKey], out Guid tenant, out Guid cluster)
            .Should().BeTrue();
        tenant.Should().Be(TenantId);
        cluster.Should().Be(ClusterId);
    }

    [Fact]
    public void ApplyTo_LeavesOperatorSuppliedValuesAlone()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = new()
        {
            [TelemetryIngestDefaults.EndpointFieldKey] = "https://internal.example.net/ingest/otlp",
            [TelemetryIngestDefaults.TokenFieldKey] = "operator-supplied-token",
        };

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be("https://internal.example.net/ingest/otlp");
        values[TelemetryIngestDefaults.TokenFieldKey].Should().Be("operator-supplied-token");
    }

    [Fact]
    public void ApplyTo_ReplacesAValueThatIsItselfAPlaceholder()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = new()
        {
            [TelemetryIngestDefaults.EndpointFieldKey] = TelemetryIngestDefaults.EndpointPlaceholder,
            [TelemetryIngestDefaults.TokenFieldKey] = TelemetryIngestDefaults.TokenPlaceholder,
        };

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        values[TelemetryIngestDefaults.EndpointFieldKey].Should().Be(ExpectedIngestUrl);
        values[TelemetryIngestDefaults.TokenFieldKey].Should().NotContain("REPLACE_WITH");
    }

    [Fact]
    public void ApplyTo_IgnoresEveryOtherCatalogEntry()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        CatalogEntry other = ComponentCatalog.GetByKey("otel-ebpf")!;
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(other, values, ClusterId, TenantId, tokens, config);

        values.Should().BeEmpty();
    }

    [Fact]
    public void ApplyTo_StillMintsTheTokenWhenTheIngestUrlIsUnknown()
    {
        // A missing PublicIngestUrl is an operator-fixable gap; it must not cost the token as well.
        (IngestTokenService tokens, IConfiguration config) = Build(publicUrl: null);
        Dictionary<string, string> values = [];

        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        values.Should().NotContainKey(TelemetryIngestDefaults.EndpointFieldKey);
        values[TelemetryIngestDefaults.TokenFieldKey].Should().NotBeNullOrWhiteSpace();
    }

    // ──────── Merge into the values document ────────

    [Fact]
    public void PreFilledValues_LandOnTheCollectorsExporterEndpointPath()
    {
        // The endpoint's YAML path contains a '/' inside one segment (exporters."otlphttp/entkube"), so this
        // guards the dotted-path merge as much as the pre-fill.
        (IngestTokenService tokens, IConfiguration config) = Build();
        Dictionary<string, string> values = [];
        TelemetryIngestDefaults.ApplyTo(Collector, values, ClusterId, TenantId, tokens, config);

        string merged = CatalogComponentRegistrar.MergeFormValues(Collector, values, []);

        merged.Should().NotContain(TelemetryIngestDefaults.HostMarker);
        YamlFormMerger.ExtractValue(merged, "config.exporters.otlphttp/entkube.endpoint")
            .Should().Be(ExpectedIngestUrl);
    }

    // ──────── Heal (install time) ────────

    [Fact]
    public void FillPlaceholders_ReplacesBothPlaceholdersInTheCatalogDefaults()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();

        (string? yaml, string? minted) = TelemetryIngestDefaults.FillPlaceholders(
            Collector.DefaultValues, ClusterId, TenantId, tokens, config);

        yaml.Should().NotBeNull();
        yaml!.Should().NotContain("REPLACE_WITH");
        // The management plane's own ingest URL — the placeholder resolves to nothing else.
        yaml.Should().Contain(ExpectedIngestUrl);
        minted.Should().NotBeNull();
        tokens.TryValidate(minted, out Guid tenant, out Guid cluster).Should().BeTrue();
        tenant.Should().Be(TenantId);
        cluster.Should().Be(ClusterId);
    }

    [Fact]
    public void FillPlaceholders_LeavesAConfiguredDocumentUntouched()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        const string configured = """
            config:
              exporters:
                otlphttp/entkube:
                  endpoint: https://already.example.net/ingest/otlp
              extensions:
                bearertokenauth:
                  token: "ek1.already.set"
            """;

        (string? yaml, string? minted) = TelemetryIngestDefaults.FillPlaceholders(
            configured, ClusterId, TenantId, tokens, config);

        yaml.Should().Be(configured);
        minted.Should().BeNull();
    }

    [Fact]
    public void FillPlaceholders_SubstitutesABareHostMarker()
    {
        (IngestTokenService tokens, IConfiguration config) = Build();
        const string handEdited = """
            config:
              exporters:
                otlphttp/entkube:
                  endpoint: https://REPLACE_WITH_ENTKUBE_URL/custom/path
            """;

        (string? yaml, _) = TelemetryIngestDefaults.FillPlaceholders(
            handEdited, ClusterId, TenantId, tokens, config);

        yaml!.Should().Contain("https://entkube.example.com/custom/path");
        yaml.Should().NotContain("REPLACE_WITH");
    }

    [Fact]
    public void FillPlaceholders_RefusesToInstallACollectorWithNowhereToShip()
    {
        // The whole point of this class: a collector whose endpoint stays a placeholder installs, reports
        // Ready, and exports into a hostname that does not resolve. Refusing names the one setting that
        // fixes it instead of leaving an empty Logs tab to diagnose.
        (IngestTokenService tokens, IConfiguration config) = Build(publicUrl: null);

        Action act = () => TelemetryIngestDefaults.FillPlaceholders(
            Collector.DefaultValues, ClusterId, TenantId, tokens, config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Telemetry:PublicIngestUrl*");
    }

    [Fact]
    public void TokenSecretName_MatchesTheCatalogFieldsVaultName()
    {
        TelemetryIngestDefaults.TokenSecretName(Collector).Should().Be("otel-ingest-token");
    }

    // ──────── Repointing an installed collector back at the management plane ────────

    /// <summary>
    /// The address EntKube generated for a cluster's own indexer while clusters held their own telemetry.
    /// It is no longer a destination anything is pointed AT — it is the address a repoint moves away from,
    /// and the one shape <see cref="TelemetryIngestDefaults.IsInClusterIndexerDestination"/> has to
    /// recognise as EntKube's own to rewrite.
    /// </summary>
    private const string InClusterUrl = "http://tel-entkube-telemetry-indexer.monitoring:8080/ingest/otlp";

    private const string CollectorValues = """
        config:
          exporters:
            otlphttp/entkube:
              endpoint: PLACEHOLDER
        """;

    private static string ValuesWith(string endpoint) => CollectorValues.Replace("PLACEHOLDER", endpoint);

    [Fact]
    public void An_installed_collector_is_repointed_from_the_cluster_back_to_the_management_plane()
    {
        (_, IConfiguration config) = Build();

        // The migration case: every collector installed while clusters held their own telemetry is pointed
        // at an indexer that is being removed. On the next apply it has to move, or it exports into a
        // Service that no longer exists and the cluster goes quiet with nothing looking wrong.
        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToManagementPlane(
            ValuesWith(InClusterUrl), config);

        repointed.Should().BeTrue();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath)
            .Should().Be(ExpectedIngestUrl);
    }

    [Fact]
    public void A_destination_the_operator_chose_is_left_alone()
    {
        (_, IConfiguration config) = Build();
        const string chosen = "https://collector-gateway.example.com/ingest/otlp";

        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToManagementPlane(
            ValuesWith(chosen), config);

        // Only a URL EntKube generated is ours to change. Redirecting a deliberate choice would silently
        // send someone's telemetry somewhere they did not pick — the one rule that did not change when the
        // direction did.
        repointed.Should().BeFalse();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath).Should().Be(chosen);
    }

    [Fact]
    public void A_placeholder_endpoint_is_repointed_too()
    {
        (_, IConfiguration config) = Build();

        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToManagementPlane(
            ValuesWith("https://REPLACE_WITH_ENTKUBE_URL/ingest/otlp"), config);

        repointed.Should().BeTrue();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath)
            .Should().Be(ExpectedIngestUrl);
    }

    [Fact]
    public void Nothing_happens_when_the_management_plane_has_no_public_url()
    {
        (_, IConfiguration config) = Build(publicUrl: null);

        // There is nowhere to move it TO. Leaving the stale in-cluster address in place is worse than
        // nothing, but writing a blank endpoint is worse still: the values document would then merge a
        // field that the install-time refusal can no longer explain.
        (string? yaml, bool repointed) = TelemetryIngestDefaults.RepointToManagementPlane(
            ValuesWith(InClusterUrl), config);

        repointed.Should().BeFalse();
        YamlFormMerger.ExtractValue(yaml!, TelemetryIngestDefaults.EndpointYamlPath).Should().Be(InClusterUrl);
    }

    [Fact]
    public void Repointing_an_already_repointed_collector_changes_nothing()
    {
        (_, IConfiguration config) = Build();

        // Runs on every install, so it has to be idempotent — otherwise every apply reports a change.
        (_, bool repointed) = TelemetryIngestDefaults.RepointToManagementPlane(
            ValuesWith(ExpectedIngestUrl), config);

        repointed.Should().BeFalse();
    }

    [Fact]
    public void Already_there_wins_over_ours_to_rewrite_when_both_are_true()
    {
        // The one case where "is the endpoint already the management plane?" is not answered by "is it
        // an address EntKube generated?": an installation whose own public hostname's first label ends
        // in -indexer. Both questions then say yes about the SAME address, and without the equality
        // check first, every apply would merge the value it already has, save the component and log a
        // repoint that moved nothing.
        //
        // Removing the equality check leaves every other test in this file green — it is subsumed by the
        // ownership guard everywhere else — so this is the test that holds it in place.
        (_, IConfiguration config) = Build(publicUrl: "http://tel-entkube-telemetry-indexer.monitoring:8080");

        TelemetryIngestDefaults.IngestUrl(config).Should().Be(InClusterUrl, "the overlap is real, not contrived");
        TelemetryIngestDefaults.IsInClusterIndexerDestination(InClusterUrl)
            .Should().BeTrue("and the same address is one we would otherwise rewrite");

        (_, bool repointed) = TelemetryIngestDefaults.RepointToManagementPlane(
            ValuesWith(InClusterUrl), config);

        repointed.Should().BeFalse();
    }

    // ──────── Which addresses are EntKube's own to rewrite ────────

    [Fact]
    public void An_indexer_service_address_is_recognised_as_ours_to_move()
    {
        TelemetryIngestDefaults.IsInClusterIndexerDestination(InClusterUrl).Should().BeTrue();
    }

    [Theory]
    // Right path, but a host EntKube never generated.
    [InlineData("https://collector-gateway.example.com/ingest/otlp")]
    // Right host, but a path EntKube never generated — an operator pointing the collector's own exporter
    // at something else that happens to run beside the indexer.
    [InlineData("http://tel-entkube-telemetry-indexer.monitoring:8080/v1/logs")]
    // Not an address at all.
    [InlineData("tel-entkube-telemetry-indexer.monitoring:8080/ingest/otlp")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_ours_to_move(string? endpoint)
    {
        // Both halves have to match: this decides whether a repoint overwrites somebody's chosen
        // destination, so "probably ours" is not good enough in either direction.
        TelemetryIngestDefaults.IsInClusterIndexerDestination(endpoint).Should().BeFalse();
    }
}
