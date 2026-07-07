using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Shared helpers for reading the OTLP/JSON (ProtoJSON) wire shape — the common building blocks used
/// by both the logs (<see cref="OtlpLogsParser"/>) and traces (<see cref="OtlpTracesParser"/>) parsers.
/// ProtoJSON encodes int64/uint64 as strings (but lenient producers may emit numbers), and bytes
/// (trace/span ids) as hex per the OTLP spec.
/// </summary>
public static class OtlpJson
{
    /// <summary>
    /// Removes NUL (U+0000) from a string. Postgres <c>text</c> can't store a 0x00 byte and <c>jsonb</c>
    /// rejects a <c></c> escape, so any NUL in eBPF-derived data (OBI reads raw kernel buffers where
    /// a string field may carry an embedded/uninitialised NUL) would fail the whole COPY batch. Stripping
    /// it is lossless for our purposes — NUL is never meaningful in a span name, label, or log line.
    /// </summary>
    public static string StripNul(string s) => s.IndexOf('\0') < 0 ? s : s.Replace("\0", "");

    /// <summary>Reads an OTLP <c>KeyValue[]</c> attributes array into a flat key→string map.</summary>
    public static void ReadAttributes(JsonElement attrs, Dictionary<string, string> into)
    {
        if (attrs.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement kv in attrs.EnumerateArray())
        {
            if (!kv.TryGetProperty("key", out JsonElement keyEl)) continue;
            string? key = keyEl.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            // Strip NUL from both key and value so the serialized attributes JSON never carries .
            if (kv.TryGetProperty("value", out JsonElement val))
                into[StripNul(key)] = StripNul(AnyValueToString(val));
        }
    }

    /// <summary>
    /// Stringifies an OTLP <c>AnyValue</c> (a one-of: string/bool/int/double/bytes/array/kvlist).
    /// Scalars become their string form; complex values are kept as raw JSON.
    /// </summary>
    public static string AnyValueToString(JsonElement val)
    {
        if (val.ValueKind != JsonValueKind.Object) return val.ToString();
        if (val.TryGetProperty("stringValue", out JsonElement s)) return s.GetString() ?? "";
        // int64 is normally a string; branch on kind since GetString() throws on a Number.
        if (val.TryGetProperty("intValue", out JsonElement i))
            return i.ValueKind == JsonValueKind.String ? (i.GetString() ?? "") : i.GetRawText();
        if (val.TryGetProperty("boolValue", out JsonElement b)) return b.GetRawText();
        if (val.TryGetProperty("doubleValue", out JsonElement d)) return d.GetRawText();
        if (val.TryGetProperty("bytesValue", out JsonElement by))
            return by.ValueKind == JsonValueKind.String ? (by.GetString() ?? "") : by.GetRawText();
        if (val.TryGetProperty("arrayValue", out JsonElement a)) return a.GetRawText();
        if (val.TryGetProperty("kvlistValue", out JsonElement k)) return k.GetRawText();
        return "";
    }

    /// <summary>First present, non-empty value among the given attribute keys (else empty string).</summary>
    public static string FirstOf(Dictionary<string, string> attrs, params string[] keys)
    {
        foreach (string k in keys)
            if (attrs.TryGetValue(k, out string? v) && !string.IsNullOrEmpty(v))
                return v;
        return "";
    }

    /// <summary>Reads a ProtoJSON nanosecond field (string or number) as a long; 0 when absent.</summary>
    public static long ReadUnixNano(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out JsonElement v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(v.GetString(), out long n) ? n : 0,
            JsonValueKind.Number => v.TryGetInt64(out long n) ? n : 0,
            _ => 0
        };
    }

    /// <summary>Unix-nanosecond → UTC DateTime at 100ns tick precision; 0 → now.</summary>
    public static DateTime UnixNanoToUtc(long ns) =>
        ns == 0 ? DateTime.UtcNow : new DateTime(DateTime.UnixEpoch.Ticks + ns / 100, DateTimeKind.Utc);

    /// <summary>
    /// Reads a JSON element as a string only when it actually is one, else null — because
    /// <c>GetString()</c> THROWS on a Number/Bool rather than returning null. Use for any span/log
    /// field a lenient producer might emit with the wrong JSON type (name, ids, …).
    /// </summary>
    public static string? Str(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>True when a hex id string is empty or all zeroes (an absent trace/span id).</summary>
    public static bool IsAbsentId(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return true;
        foreach (char c in hex)
            if (c != '0') return false;
        return true;
    }
}
