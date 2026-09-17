using System.Text.Json;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Trace/log correlation for logs that arrive the way logs actually arrive here.
///
/// <para>A log line is joined to a trace only when its indexed <c>trace_id</c> matches the one the trace
/// detail searches with. That used to require the OTLP log record's own <c>traceId</c> field, which
/// essentially nothing on this platform sets: logs are tailed from container stdout by the filelog
/// receiver, whose <c>container</c> operator unwraps the runtime envelope and leaves the app's payload as
/// body text. So an app that dutifully logged its trace id still got "No logs carry this trace id", and the
/// only documented remedy was an edit to collector config shared by every app on the cluster.</para>
///
/// <para>The parser now recovers the id from the record's attributes, or from the line itself. These are
/// the shapes that have to work, and the near-misses that must not.</para>
/// </summary>
public class OtlpLogTraceIdRecoveryTests
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    private static LogIngestRecord ParseOne(string body, object? attributes = null, string? recordTraceId = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["timeUnixNano"] = "1789000000000000000",
            ["severityNumber"] = 9,
            ["body"] = new { stringValue = body },
        };
        if (recordTraceId is not null) record["traceId"] = recordTraceId;
        if (attributes is not null) record["attributes"] = attributes;

        string json = JsonSerializer.Serialize(new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = new[]
                        {
                            new { key = "k8s.namespace.name", value = new { stringValue = "shop" } },
                            new { key = "k8s.pod.name", value = new { stringValue = "api-7d9f" } },
                            new { key = "k8s.container.name", value = new { stringValue = "api" } },
                        },
                    },
                    scopeLogs = new[] { new { logRecords = new[] { record } } },
                },
            },
        });

        using JsonDocument doc = JsonDocument.Parse(json);
        return OtlpLogsParser.Parse(doc).Should().ContainSingle().Subject;
    }

    [Theory]
    // Structured JSON written to stdout — Serilog, zap, structlog, Logback's JSON encoder. The whole line
    // arrives as body text because nothing has parsed the payload.
    [InlineData($$"""{"level":"error","trace_id":"{{TraceId}}","msg":"checkout failed"}""")]
    [InlineData($$"""{"level":"error","traceId":"{{TraceId}}","msg":"checkout failed"}""")]
    [InlineData($$"""{"TraceId":"{{TraceId}}","Message":"checkout failed"}""")]
    // logfmt — the other common stdout shape.
    [InlineData($"level=error trace_id={TraceId} msg=\"checkout failed\"")]
    // Plain text with the id appended, which is what a hand-rolled formatter tends to produce.
    [InlineData($"ERROR checkout failed trace-id: {TraceId}")]
    [InlineData($"ERROR checkout failed traceID = {TraceId}")]
    public void ATraceIdWrittenIntoTheLineIsRecovered(string body) =>
        ParseOne(body).TraceId.Should().Be(TraceId);

    [Fact]
    public void ATraceIdInTheRecordsAttributesIsRecovered()
    {
        object attrs = new[] { new { key = "trace_id", value = new { stringValue = TraceId } } };
        ParseOne("checkout failed", attrs).TraceId.Should().Be(TraceId);
    }

    [Fact]
    public void TheRecordsOwnTraceIdStillWins()
    {
        // A producer that knows the span context is authoritative; nothing in the text may override it.
        const string authoritative = "00000000000000000000000000000abc";
        ParseOne($"trace_id={TraceId}", recordTraceId: authoritative)
            .TraceId.Should().Be(authoritative);
    }

    [Fact]
    public void ARecoveredIdIsLowerCased()
    {
        // The indexed trace_id is an exact-term StringField and the trace detail searches with the
        // lower-case id OTLP/JSON renders on the span side, so an upper-case recovery would never match.
        ParseOne($"trace_id={TraceId.ToUpperInvariant()}").TraceId.Should().Be(TraceId);
    }

    [Theory]
    // "trace" appears, but no trace id does — the common stack-trace case.
    [InlineData("java.lang.RuntimeException: boom\n\tat com.shop.Checkout (stack trace follows)")]
    // A 16-hex span id must not be promoted to a trace id.
    [InlineData("span_id=00f067aa0ba902b7 trace_id=00f067aa0ba902b7")]
    // An all-zero id is absent, not a value.
    [InlineData("trace_id=00000000000000000000000000000000")]
    // A bare hex blob with no trace-id key must not be picked up.
    [InlineData("checksum 4bf92f3577b34da6a3ce929d0e0e4736 verified")]
    // Wrong length either side of 32.
    [InlineData("trace_id=4bf92f3577b34da6a3ce929d0e0e47")]
    public void NearMissesAreNotMistakenForATraceId(string body) =>
        ParseOne(body).TraceId.Should().BeNull();

    [Fact]
    public void ALineWithNoTraceIdAtAllCostsNothingAndStaysNull() =>
        ParseOne("GET /healthz 200 1.2ms").TraceId.Should().BeNull();
}
