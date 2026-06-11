using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the YamlFormMerger utility that takes form field values
/// (key-value pairs with dot-notation paths) and merges them into a
/// YAML string. This is how form-based configuration gets applied on
/// top of the raw YAML that users can also edit directly.
/// </summary>
public class YamlFormMergerTests
{
    // ──────── Simple top-level paths ────────

    [Fact]
    public void MergeFormValues_WithSimplePath_SetsValue()
    {
        // A form field targeting "rootUser" should set the top-level key.

        string baseYaml = """
            rootUser: admin
            rootPassword: changeme
            """;

        Dictionary<string, string> formValues = new() { ["rootUser"] = "minio-admin" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("rootUser: minio-admin");
    }

    [Fact]
    public void MergeFormValues_WithNestedPath_SetsNestedValue()
    {
        // A dot-notation path like "grafana.adminPassword" should set the
        // nested YAML property grafana → adminPassword.

        string baseYaml = """
            grafana:
              enabled: true
              adminPassword: admin
            """;

        Dictionary<string, string> formValues = new() { ["grafana.adminPassword"] = "s3cret!" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("adminPassword: s3cret!");
    }

    [Fact]
    public void MergeFormValues_WithDeeplyNestedPath_SetsValue()
    {
        // Paths can be arbitrarily deep, like "prometheus.prometheusSpec.retention".

        string baseYaml = """
            prometheus:
              prometheusSpec:
                retention: 15d
                resources:
                  requests:
                    memory: 512Mi
            """;

        Dictionary<string, string> formValues = new() { ["prometheus.prometheusSpec.retention"] = "30d" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("retention: 30d");
        result.Should().NotContain("retention: 15d");
    }

    [Fact]
    public void MergeFormValues_WithEmptyBaseYaml_CreatesStructure()
    {
        // When the base YAML is empty, the merger should create the nested
        // structure from scratch based on the dot-notation path.

        string baseYaml = "";

        Dictionary<string, string> formValues = new() { ["service.type"] = "LoadBalancer" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("service:");
        result.Should().Contain("type: LoadBalancer");
    }

    [Fact]
    public void MergeFormValues_WithMultipleValues_SetsAll()
    {
        // Multiple form fields should all be applied.

        string baseYaml = """
            rootUser: admin
            rootPassword: changeme
            persistence:
              size: 50Gi
            """;

        Dictionary<string, string> formValues = new()
        {
            ["rootUser"] = "operator",
            ["rootPassword"] = "strong-pass",
            ["persistence.size"] = "100Gi"
        };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("rootUser: operator");
        result.Should().Contain("rootPassword: strong-pass");
        result.Should().Contain("size: 100Gi");
    }

    [Fact]
    public void MergeFormValues_WithNewPath_AddsToExistingStructure()
    {
        // If the path doesn't exist in the base YAML but parent nodes do,
        // the merger should add the missing leaf without destroying siblings.

        string baseYaml = """
            grafana:
              enabled: true
            """;

        Dictionary<string, string> formValues = new() { ["grafana.adminPassword"] = "secret" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("enabled: true");
        result.Should().Contain("adminPassword: secret");
    }

    [Fact]
    public void MergeFormValues_WithBooleanValue_WritesUnquoted()
    {
        // Boolean-like values (true/false) should remain unquoted in YAML.

        string baseYaml = """
            alertmanager:
              enabled: true
            """;

        Dictionary<string, string> formValues = new() { ["alertmanager.enabled"] = "false" };

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("enabled: false");
        result.Should().NotContain("enabled: 'false'");
        result.Should().NotContain("enabled: \"false\"");
    }

    [Fact]
    public void MergeFormValues_WithEmptyFormValues_ReturnsBaseYamlUnchanged()
    {
        // If no form values are provided, the YAML comes back untouched.

        string baseYaml = """
            rootUser: admin
            rootPassword: changeme
            """;

        Dictionary<string, string> formValues = new();

        string result = YamlFormMerger.MergeFormValues(baseYaml, formValues);

        result.Should().Contain("rootUser: admin");
        result.Should().Contain("rootPassword: changeme");
    }
}
