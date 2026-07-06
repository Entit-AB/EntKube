using System.Text.Json;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for <see cref="RumIngestParser"/>, pinning the wire contract between the browser rum.js snippet
/// and the ingest backend: field names, batch-level defaults with per-event overrides, epoch-ms timestamps,
/// the AJAX trace_id link, lenient skipping, and field truncation.
/// </summary>
public class RumIngestParserTests
{
    // Mirrors exactly what RumSnippet.Js send() posts.
    private const string SnippetPayload = """
    {
      "session": "sess-1",
      "view": "view-1",
      "path": "/checkout",
      "referrer": "https://google.com/",
      "ua": { "browser": "Chrome", "os": "macOS", "device": "desktop" },
      "views": [ { "t": 1751797200000, "path": "/checkout", "load": 1234, "ttfb": 120, "lcp": 900, "cls": 0.02, "inp": 50, "fcp": 600 } ],
      "errors": [ { "t": 1751797201000, "msg": "TypeError: x is undefined", "src": "app.js:1:2", "stack": "at foo", "path": "/cart" } ],
      "resources": [ { "t": 1751797202000, "name": "/api/cart", "kind": "fetch", "dur": 230, "status": 200, "trace": "abc123trace" } ]
    }
    """;

    private static RumIngestParser.RumBatch Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return RumIngestParser.Parse(doc);
    }

    [Fact]
    public void Parses_page_view_with_web_vitals_and_ua()
    {
        RumIngestParser.RumBatch b = Parse(SnippetPayload);

        b.PageViews.Should().ContainSingle();
        RumPageViewRecord pv = b.PageViews[0];
        pv.SessionId.Should().Be("sess-1");
        pv.ViewId.Should().Be("view-1");
        pv.Path.Should().Be("/checkout");
        pv.Referrer.Should().Be("https://google.com/");
        pv.LoadMs.Should().Be(1234);
        pv.TtfbMs.Should().Be(120);
        pv.LcpMs.Should().Be(900);
        pv.Cls.Should().Be(0.02);
        pv.InpMs.Should().Be(50);
        pv.FcpMs.Should().Be(600);
        pv.Browser.Should().Be("Chrome");
        pv.Os.Should().Be("macOS");
        pv.Device.Should().Be("desktop");
        pv.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1751797200000).UtcDateTime);
        pv.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Parses_error_with_per_event_path_override()
    {
        RumIngestParser.RumBatch b = Parse(SnippetPayload);

        b.Errors.Should().ContainSingle();
        RumErrorRecord e = b.Errors[0];
        e.SessionId.Should().Be("sess-1");
        e.Message.Should().Be("TypeError: x is undefined");
        e.Source.Should().Be("app.js:1:2");
        e.Stack.Should().Be("at foo");
        e.Path.Should().Be("/cart");   // per-event path overrides the batch path
    }

    [Fact]
    public void Parses_resource_with_trace_link_and_batch_path_fallback()
    {
        RumIngestParser.RumBatch b = Parse(SnippetPayload);

        b.Resources.Should().ContainSingle();
        RumResourceRecord r = b.Resources[0];
        r.Name.Should().Be("/api/cart");
        r.Kind.Should().Be("fetch");
        r.DurationMs.Should().Be(230);
        r.Status.Should().Be(200);
        r.TraceId.Should().Be("abc123trace");   // links to the spans waterfall
        r.Path.Should().Be("/checkout");         // falls back to the batch path
    }

    [Fact]
    public void Skips_error_with_no_message_and_resource_with_no_name()
    {
        RumIngestParser.RumBatch b = Parse("""
        { "session": "s", "view": "v",
          "errors": [ { "t": 1, "stack": "x" }, { "t": 2, "msg": "real" } ],
          "resources": [ { "t": 1, "kind": "fetch" }, { "t": 2, "name": "/ok" } ] }
        """);

        b.Errors.Should().ContainSingle(e => e.Message == "real");
        b.Resources.Should().ContainSingle(r => r.Name == "/ok");
    }

    [Fact]
    public void Missing_session_view_default_and_missing_timestamp_uses_now()
    {
        DateTime before = DateTime.UtcNow;
        RumIngestParser.RumBatch b = Parse("""{ "views": [ { "path": "/x" } ] }""");
        DateTime after = DateTime.UtcNow;

        RumPageViewRecord pv = b.PageViews.Should().ContainSingle().Subject;
        pv.SessionId.Should().Be("(unknown)");
        pv.ViewId.Should().Be("(unknown)");
        pv.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Truncates_overlong_fields_to_column_budgets()
    {
        string longMsg = new('a', 5000);
        RumIngestParser.RumBatch b = Parse($$"""
        { "session": "s", "view": "v", "errors": [ { "t": 1, "msg": "{{longMsg}}" } ] }
        """);

        b.Errors.Should().ContainSingle();
        b.Errors[0].Message.Should().HaveLength(2000);
    }

    [Fact]
    public void Out_of_range_epoch_does_not_throw_and_does_not_drop_the_batch()
    {
        DateTime before = DateTime.UtcNow;
        // A pathological/attacker `t` (far outside DateTimeOffset's range) must not throw and abort the whole beacon.
        RumIngestParser.RumBatch b = Parse("""
        { "session": "s", "view": "v",
          "views": [ { "t": 99999999999999999, "path": "/bad" }, { "t": 1751797200000, "path": "/good" } ] }
        """);
        DateTime after = DateTime.UtcNow;

        b.PageViews.Should().HaveCount(2);
        b.PageViews[0].Path.Should().Be("/bad");
        b.PageViews[0].Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);   // clamped to now
        b.PageViews[1].Path.Should().Be("/good");
        b.PageViews[1].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1751797200000).UtcDateTime);
    }

    [Fact]
    public void Empty_or_non_object_root_yields_empty_batch()
    {
        RumIngestParser.RumBatch b = Parse("[]");
        b.PageViews.Should().BeEmpty();
        b.Errors.Should().BeEmpty();
        b.Resources.Should().BeEmpty();
    }
}
