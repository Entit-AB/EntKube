using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Parses the compact JSON beacon the browser RUM snippet posts into flat page-view / error / resource
/// records. Batch-level <c>session</c>/<c>view</c>/<c>path</c>/<c>referrer</c>/<c>ua</c> apply as defaults;
/// individual events may override <c>t</c> (epoch ms) and <c>path</c>. Lenient and bounded — unknown shapes
/// are skipped, strings are truncated to their column budgets, and per-array counts are capped so a single
/// beacon can't create a pathological COPY batch. Tenant/site identity is NOT read from the payload.
/// </summary>
public static class RumIngestParser
{
    private const int MaxEventsPerArray = 1000;

    public sealed record RumBatch(
        List<RumPageViewRecord> PageViews,
        List<RumErrorRecord> Errors,
        List<RumResourceRecord> Resources);

    public static RumBatch Parse(JsonDocument doc)
    {
        List<RumPageViewRecord> views = [];
        List<RumErrorRecord> errors = [];
        List<RumResourceRecord> resources = [];

        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new RumBatch(views, errors, resources);

        string session = Trunc(Str(root, "session") ?? "(unknown)", 200);
        string view = Trunc(Str(root, "view") ?? "(unknown)", 200);
        string? basePath = Str(root, "path");
        string? referrer = Str(root, "referrer");

        string? browser = null, os = null, device = null;
        if (root.TryGetProperty("ua", out JsonElement ua) && ua.ValueKind == JsonValueKind.Object)
        {
            browser = Trunc(Str(ua, "browser"), 100);
            os = Trunc(Str(ua, "os"), 100);
            device = Trunc(Str(ua, "device"), 40);
        }

        if (root.TryGetProperty("views", out JsonElement vs) && vs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in Take(vs))
            {
                views.Add(new RumPageViewRecord(
                    Ts(e), session, view,
                    Trunc(Str(e, "path") ?? basePath ?? "/", 1000),
                    Trunc(referrer, 1000),
                    Num(e, "load"), Num(e, "ttfb"), Num(e, "lcp"), Num(e, "cls"), Num(e, "inp"), Num(e, "fcp"),
                    browser, os, device));
            }
        }

        if (root.TryGetProperty("errors", out JsonElement es) && es.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in Take(es))
            {
                string? msg = Str(e, "msg");
                if (string.IsNullOrEmpty(msg)) continue;   // an error row with no message is noise
                errors.Add(new RumErrorRecord(
                    Ts(e), session, view,
                    Trunc(Str(e, "path") ?? basePath, 1000),
                    Trunc(msg, 2000),
                    Trunc(Str(e, "stack"), 8000),
                    Trunc(Str(e, "src"), 500)));
            }
        }

        if (root.TryGetProperty("resources", out JsonElement rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in Take(rs))
            {
                string? name = Str(e, "name");
                if (string.IsNullOrEmpty(name)) continue;
                resources.Add(new RumResourceRecord(
                    Ts(e), session, view,
                    Trunc(Str(e, "path") ?? basePath, 1000),
                    Trunc(name, 1000),
                    Trunc(Str(e, "kind"), 20),
                    Num(e, "dur"), Int(e, "status"),
                    Trunc(Str(e, "trace"), 64)));
            }
        }

        return new RumBatch(views, errors, resources);
    }

    private static IEnumerable<JsonElement> Take(JsonElement array)
    {
        int n = 0;
        foreach (JsonElement e in array.EnumerateArray())
        {
            if (n++ >= MaxEventsPerArray) yield break;
            if (e.ValueKind == JsonValueKind.Object) yield return e;
        }
    }

    // Event time as UTC from epoch-millis "t"; missing/non-positive → now (beacons are near-real-time).
    private static DateTime Ts(JsonElement e)
    {
        double? ms = Num(e, "t");
        return ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms.Value).UtcDateTime : DateTime.UtcNow;
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double s)) return s;
        return null;
    }

    private static int? Int(JsonElement e, string prop)
    {
        double? n = Num(e, prop);
        return n is null ? null : (int)n.Value;
    }

    [return: NotNullIfNotNull(nameof(s))]
    private static string? Trunc(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
