using System.Text.Json;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for <see cref="OtlpMetricsParser"/>: gauge/sum passthrough, sum kind classification, and the
/// histogram → <c>name_count</c>/<c>name_sum</c> expansion (the RED metrics OBI/apps emit as histograms,
/// which were previously dropped — leaving only <c>target_info</c> in the metrics view).
/// </summary>
public class OtlpMetricsParserTests
{
    private static List<MetricIngestRecord> Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return OtlpMetricsParser.Parse(doc);
    }

    // One resource (service+namespace+pod) with a gauge, a cumulative monotonic sum, and a cumulative
    // histogram — mirrors an OBI/OTLP metrics export.
    private const string Payload = """
    {
      "resourceMetrics": [{
        "resource": { "attributes": [
          { "key": "service.name", "value": { "stringValue": "checkout" } },
          { "key": "k8s.namespace.name", "value": { "stringValue": "shop" } },
          { "key": "k8s.pod.name", "value": { "stringValue": "checkout-abc" } }
        ]},
        "scopeMetrics": [{ "metrics": [
          {
            "name": "target_info",
            "gauge": { "dataPoints": [ { "asDouble": 1, "timeUnixNano": "1751797200000000000" } ] }
          },
          {
            "name": "http.server.request.count",
            "sum": {
              "aggregationTemporality": 2, "isMonotonic": true,
              "dataPoints": [ { "asInt": "42", "timeUnixNano": "1751797200000000000" } ]
            }
          },
          {
            "name": "http.server.request.duration",
            "histogram": {
              "aggregationTemporality": 2,
              "dataPoints": [ {
                "count": "10", "sum": 3.5, "timeUnixNano": "1751797200000000000",
                "attributes": [ { "key": "http.response.status_code", "value": { "intValue": "200" } } ]
              } ]
            }
          }
        ]}]
      }]
    }
    """;

    [Fact]
    public void Expands_histogram_into_count_and_sum_series()
    {
        List<MetricIngestRecord> records = Parse(Payload);

        MetricIngestRecord count = records.Single(r => r.Name == "http.server.request.duration_count");
        count.Value.Should().Be(10);
        count.Kind.Should().Be(MetricKind.Counter);          // cumulative → rate-charted
        count.Service.Should().Be("checkout");
        count.Namespace.Should().Be("shop");
        count.LabelsJson.Should().Contain("status_code");    // data-point attributes carried through

        MetricIngestRecord sum = records.Single(r => r.Name == "http.server.request.duration_sum");
        sum.Value.Should().Be(3.5);
        sum.Kind.Should().Be(MetricKind.Counter);
    }

    [Fact]
    public void Still_parses_gauge_and_sum()
    {
        List<MetricIngestRecord> records = Parse(Payload);

        records.Single(r => r.Name == "target_info").Kind.Should().Be(MetricKind.Gauge);
        MetricIngestRecord c = records.Single(r => r.Name == "http.server.request.count");
        c.Value.Should().Be(42);
        c.Kind.Should().Be(MetricKind.Counter);
    }

    [Fact]
    public void Delta_histogram_is_classified_as_delta_sum()
    {
        const string delta = """
        {
          "resourceMetrics": [{
            "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "api" } } ] },
            "scopeMetrics": [{ "metrics": [ {
              "name": "rpc.server.duration",
              "histogram": {
                "aggregationTemporality": 1,
                "dataPoints": [ { "count": "3", "sum": 0.9, "timeUnixNano": "1751797200000000000" } ]
              }
            } ]}]
          }]
        }
        """;

        List<MetricIngestRecord> records = Parse(delta);
        records.Should().OnlyContain(r => r.Kind == MetricKind.DeltaSum);
        records.Select(r => r.Name).Should().BeEquivalentTo("rpc.server.duration_count", "rpc.server.duration_sum");
    }

    [Fact]
    public void Histogram_without_sum_still_emits_count()
    {
        const string noSum = """
        {
          "resourceMetrics": [{
            "scopeMetrics": [{ "metrics": [ {
              "name": "op.duration",
              "histogram": { "aggregationTemporality": 2, "dataPoints": [ { "count": "7", "timeUnixNano": "1751797200000000000" } ] }
            } ]}]
          }]
        }
        """;

        List<MetricIngestRecord> records = Parse(noSum);
        records.Should().ContainSingle();
        records[0].Name.Should().Be("op.duration_count");
        records[0].Value.Should().Be(7);
    }
}
