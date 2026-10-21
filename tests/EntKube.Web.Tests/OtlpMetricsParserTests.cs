using System.Text.Json;
using EntKube.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Parsing an OTLP/JSON metrics payload. The failures these guard against are quiet ones: a
/// collector that exports happily while every point is dropped, or one whose numbers are stored
/// in the wrong bucket or the wrong units.
/// </summary>
public class OtlpMetricsParserTests
{
    private static readonly DateTime At = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// <see cref="At"/> in unix nanoseconds, as a collector sends it — DERIVED rather than
    /// written out. The hand-computed constant this replaces was wrong by six days, and the test
    /// failed against correct parsing, which is the wrong way round for a fixture.
    /// </summary>
    private static readonly string Nanos =
        ((At - DateTime.UnixEpoch).Ticks * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a payload, substituting the timestamp token.
    ///
    /// <para>The payloads are plain raw strings with a NANOS token rather than interpolated ones:
    /// OTLP JSON closes six or seven braces in a row, and in an interpolated raw string a run of
    /// braces is read as an interpolation rather than as content.</para>
    /// </summary>
    private static List<MetricPoint> Parse(string payload)
    {
        using JsonDocument doc = JsonDocument.Parse(payload.Replace("NANOS", Nanos));
        return OtlpMetricsParser.Parse(doc);
    }

    [Fact]
    public void A_gauge_becomes_a_point_with_its_resource_scope()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{
              "resource":{"attributes":[
                {"key":"service.name","value":{"stringValue":"api"}},
                {"key":"k8s.namespace.name","value":{"stringValue":"shop"}},
                {"key":"k8s.pod.name","value":{"stringValue":"api-7d9"}}]},
              "scopeMetrics":[{"metrics":[
                {"name":"memory_bytes","gauge":{"dataPoints":[
                  {"timeUnixNano":"NANOS","asDouble":1024.5}]}}]}]}]}
            """);

        points.Should().ContainSingle();
        MetricPoint p = points[0];
        p.Name.Should().Be("memory_bytes");
        p.Service.Should().Be("api");
        p.Namespace.Should().Be("shop");
        p.Pod.Should().Be("api-7d9");
        p.Value.Should().Be(1024.5);
        p.Timestamp.Should().Be(At);
    }

    /// <summary>
    /// ProtoJSON sends 64-bit integers as STRINGS. <c>GetDouble()</c> throws on one rather than
    /// returning anything, so a parser that only handled Number would silently drop every counter
    /// a conformant collector sends.
    /// </summary>
    [Fact]
    public void An_integer_sent_as_a_json_string_is_still_a_number()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"requests_total","sum":{"dataPoints":[
                {"timeUnixNano":"NANOS","asInt":"42"}]}}]}]}]}
            """);

        points.Should().ContainSingle().Which.Value.Should().Be(42);
    }

    /// <summary>
    /// <c>startTimeUnixNano</c> is when a cumulative counter BEGAN, which can be days earlier.
    /// Preferring it would pile a whole counter's history into one ancient bucket.
    /// </summary>
    [Fact]
    public void A_point_prefers_its_own_time_over_the_start_of_its_series()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"requests_total","sum":{"dataPoints":[
                {"startTimeUnixNano":"1000000000","timeUnixNano":"NANOS","asInt":1}]}}]}]}]}
            """);

        points.Should().ContainSingle().Which.Timestamp.Should().Be(At);
    }

    [Fact]
    public void A_point_with_only_a_start_time_still_lands_somewhere_sensible()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"up","gauge":{"dataPoints":[
                {"startTimeUnixNano":"NANOS","asDouble":1}]}}]}]}]}
            """);

        points.Should().ContainSingle().Which.Timestamp.Should().Be(At);
    }

    /// <summary>
    /// Nothing in <c>IMetricsQuery</c> can ask for a quantile, so bucket boundaries would be
    /// stored and never read. Sum and count are the two fields a bucketed query can use, and the
    /// suffixes match the Prometheus series an operator already knows.
    /// </summary>
    [Fact]
    public void A_histogram_contributes_its_sum_and_count_and_not_its_buckets()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"request_duration","histogram":{"dataPoints":[
                {"timeUnixNano":"NANOS","sum":12.5,"count":"4",
                 "bucketCounts":["1","2","1"],"explicitBounds":[0.1,0.5]}]}}]}]}]}
            """);

        points.Select(p => p.Name).Order()
            .Should().Equal(["request_duration_count", "request_duration_sum"]);
        points.Single(p => p.Name.EndsWith("_sum")).Value.Should().Be(12.5);
        points.Single(p => p.Name.EndsWith("_count")).Value.Should().Be(4);
    }

    /// <summary>
    /// A missing metric is visible. A metric stored in a shape no reader understands is not.
    /// </summary>
    [Fact]
    public void Kinds_nothing_can_query_are_skipped_rather_than_half_stored()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"latency","exponentialHistogram":{"dataPoints":[
                {"timeUnixNano":"NANOS","sum":1.0,"count":"2","scale":3}]}},
              {"name":"legacy","summary":{"dataPoints":[
                {"timeUnixNano":"NANOS","sum":1.0,"count":"2"}]}}]}]}]}
            """);

        points.Should().BeEmpty();
    }

    /// <summary>
    /// JSON has no NaN literal, so one arrives as a STRING — exactly the lenient path that
    /// accepts an integer sent as "42". One of these poisons every aggregate in its bucket, and
    /// an aggregate that is NaN for ever is invisible in a way a gap is not.
    /// </summary>
    [Fact]
    public void A_value_that_is_NaN_or_infinite_never_leaves_the_parser()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"name":"bad","gauge":{"dataPoints":[
                {"timeUnixNano":"NANOS","asDouble":"NaN"},
                {"timeUnixNano":"NANOS","asDouble":"Infinity"},
                {"timeUnixNano":"NANOS","asDouble":7}]}}]}]}]}
            """);

        points.Should().ContainSingle().Which.Value.Should().Be(7);
    }

    /// <summary>
    /// Neither could ever be queried, and both would occupy a row per bucket for ever.
    /// </summary>
    [Fact]
    public void A_nameless_metric_or_a_valueless_point_is_dropped()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{"scopeMetrics":[{"metrics":[
              {"gauge":{"dataPoints":[{"timeUnixNano":"NANOS","asDouble":1}]}},
              {"name":"no_value","gauge":{"dataPoints":[{"timeUnixNano":"NANOS"}]}}]}]}]}
            """);

        points.Should().BeEmpty();
    }

    /// <summary>
    /// An empty string and an absent attribute describe the same thing, and storing both would
    /// split one series into two that no query could reunite — the scope tuple is uniquely
    /// indexed, so "" and null are different scopes to the database.
    /// </summary>
    [Fact]
    public void Blank_resource_attributes_are_absent_rather_than_empty()
    {
        List<MetricPoint> points = Parse("""
            {"resourceMetrics":[{
              "resource":{"attributes":[{"key":"service.name","value":{"stringValue":""}}]},
              "scopeMetrics":[{"metrics":[
                {"name":"up","gauge":{"dataPoints":[
                  {"timeUnixNano":"NANOS","asDouble":1}]}}]}]}]}
            """);

        points.Should().ContainSingle().Which.Service.Should().BeNull();
    }

    /// <summary>
    /// The endpoint answers 400 on a parse failure so the collector DROPS the batch; an exception
    /// here would become a 500, which it retries for ever.
    /// </summary>
    [Fact]
    public void A_payload_that_is_not_a_metrics_payload_yields_nothing_rather_than_throwing()
    {
        Parse("""{"resourceLogs":[]}""").Should().BeEmpty();
        Parse("""{"resourceMetrics":{}}""").Should().BeEmpty();
        Parse("""{}""").Should().BeEmpty();
        Parse("""[]""").Should().BeEmpty();
    }
}
