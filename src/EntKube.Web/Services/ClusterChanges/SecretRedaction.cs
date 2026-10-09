using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EntKube.Web.Services.ClusterChanges;

/// <summary>
/// Removes secret values from anything the acknowledgment dialog is about to show, leaving the
/// keys and a fingerprint.
///
/// <para><b>What the operator needs and what they do not.</b> To decide whether to approve a
/// Secret change they need to know which keys are being added, changed or removed, and where. They
/// do not need the values, and a dialog is a screen that gets shared, screenshotted and recorded
/// in support sessions. So each value becomes <c>&lt;redacted: N bytes, sha256:xxxxxxxx&gt;</c> —
/// a changed value still reads as changed, because the fingerprint changes with it.</para>
///
/// <para><b>Why the default is to redact.</b> This does not look for Secrets; it looks for
/// <em>ConfigMaps</em>, and leaves their data alone. Everything else with a <c>data:</c> or
/// <c>stringData:</c> block is redacted. That inverts the failure mode of misclassification: a
/// document this cannot identify — a diff fragment, a truncated hunk, a kind nobody has thought of
/// yet — is over-redacted rather than exposed. A filter that has to recognise every secret-bearing
/// shape in order to be safe is a filter that leaks the first time something new appears.</para>
///
/// <para><b>Where it is applied.</b> Once, in <see cref="ClusterChangeDiff.DiffText"/>'s
/// initialiser, because the gate builds diff text in nine places and six of them could carry a
/// Secret: <c>kubectl diff</c> output, a server-side dry-run rendering, the raw manifest used when
/// the dry-run itself fails, a deleted resource read back with <c>get -o yaml</c>, a JSON patch
/// body, and the catch-all that shows the manifest when the diff could not be computed at all.
/// Fixing six call sites would leave the seventh to whoever adds it.</para>
/// </summary>
public static class SecretRedaction
{
    /// <summary>How much of the digest to show. Enough to tell two values apart by eye.</summary>
    private const int FingerprintChars = 8;

    /// <summary>
    /// A <c>key: value</c> entry inside a block, optionally carrying a unified-diff prefix.
    ///
    /// <para>The prefix is captured and put back, so a diff stays a diff: <c>-</c> and <c>+</c>
    /// lines keep their markers and the operator still sees which side a key is on.</para>
    /// </summary>
    private static readonly Regex Entry = new(
        @"^(?<prefix>[-+ ]?)(?<indent>\s+)(?<key>[A-Za-z0-9_.\-]+):[ \t]*(?<value>\S.*)$",
        RegexOptions.Compiled);

    /// <summary>The start of a block whose values are content rather than configuration.</summary>
    private static readonly Regex DataBlock = new(
        @"^[-+ ]?\s*(data|stringData):\s*$", RegexOptions.Compiled);

    /// <summary>Any other top-level-ish key, which ends the block.</summary>
    private static readonly Regex OtherKey = new(
        @"^[-+ ]?(?<indent>\s*)[A-Za-z0-9_.\-]+:", RegexOptions.Compiled);

    /// <summary>
    /// Scrubs secret values out of diff or manifest text. Returns the input unchanged when there
    /// is nothing that looks like a data block.
    /// </summary>
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // JSON first: a patch body is one line of JSON, and the line-oriented pass below cannot
        // see into it.
        text = ScrubJsonData(text);

        if (!text.Contains("data:", StringComparison.Ordinal)
            && !text.Contains("stringData:", StringComparison.Ordinal))
        {
            return text;
        }

        List<string> output = [];

        foreach (string segment in Segments(text))
        {
            // ConfigMap data is configuration an operator is meant to read. Everything else that
            // carries a data block is treated as secret-bearing.
            bool keepValues = segment.Contains("kind: ConfigMap", StringComparison.Ordinal);

            output.Add(keepValues ? segment : ScrubSegment(segment));
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// Splits text into one segment per Kubernetes document, so a manifest holding a ConfigMap and
    /// a Secret has each judged on its own. Document separators and diff file headers both start a
    /// new segment; the separator stays at the head of the segment it introduces.
    /// </summary>
    private static IEnumerable<string> Segments(string text)
    {
        List<string> current = [];

        foreach (string line in text.Split('\n'))
        {
            bool boundary = IsDocumentBoundary(line);

            if (boundary && current.Count > 0)
            {
                yield return string.Join('\n', current);
                current = [];
            }

            current.Add(line);
        }

        if (current.Count > 0) yield return string.Join('\n', current);
    }

    /// <summary>
    /// Whether a line starts a new document.
    ///
    /// <para>⚠️ Not <c>TrimStart(' ', '-', '+')</c>, which was the first attempt: that strips
    /// <em>all</em> leading dashes, so a bare <c>---</c> became the empty string and no separator
    /// was ever found — a ConfigMap and a Secret in one manifest were judged as a single document,
    /// and the ConfigMap's presence kept the Secret's values visible. The failure mode of a
    /// separator bug here is a leak, so it is matched explicitly: the line itself, or the line
    /// behind exactly one diff marker.</para>
    ///
    /// <para><c>--- /tmp/path</c>, a unified-diff file header, is deliberately not a separator by
    /// this test — it is caught by the <c>diff -</c> check instead, which starts the segment one
    /// line earlier.</para>
    /// </summary>
    private static bool IsDocumentBoundary(string line)
    {
        if (line.StartsWith("diff -", StringComparison.Ordinal)) return true;

        if (line.Trim() is "---" or "...") return true;

        return line.Length > 1
            && line[0] is ' ' or '+' or '-'
            && line[1..].Trim() is "---" or "...";
    }

    private static string ScrubSegment(string segment)
    {
        List<string> output = [];
        string? blockIndent = null;

        foreach (string line in segment.Split('\n'))
        {
            if (DataBlock.IsMatch(line))
            {
                blockIndent = IndentOf(line);
                output.Add(line);
                continue;
            }

            if (blockIndent is null)
            {
                output.Add(line);
                continue;
            }

            Match entry = Entry.Match(line);

            // Still inside the block while the line is indented further than the block's own key.
            if (entry.Success && entry.Groups["indent"].Value.Length > blockIndent.Length)
            {
                output.Add(
                    entry.Groups["prefix"].Value
                    + entry.Groups["indent"].Value
                    + entry.Groups["key"].Value
                    + ": "
                    + Fingerprint(entry.Groups["value"].Value));
                continue;
            }

            // A key at or above the block's indent ends it. A blank or continuation line does not.
            Match other = OtherKey.Match(line);
            if (other.Success && other.Groups["indent"].Value.Length <= blockIndent.Length)
            {
                blockIndent = null;
            }
            else if (line.Trim().Length > 0 && !other.Success)
            {
                // An unparseable line inside a data block is redacted rather than passed through:
                // a wrapped base64 continuation is still the value.
                output.Add(RedactedLine(line));
                continue;
            }

            output.Add(line);
        }

        return string.Join('\n', output);
    }

    private static string IndentOf(string line)
    {
        string body = line.Length > 0 && line[0] is '-' or '+' or ' ' ? line[1..] : line;
        return body[..(body.Length - body.TrimStart().Length)];
    }

    private static string RedactedLine(string line)
    {
        string prefix = line.Length > 0 && line[0] is '-' or '+' ? line[..1] : "";
        string body = prefix.Length > 0 ? line[1..] : line;
        string indent = body[..(body.Length - body.TrimStart().Length)];

        return prefix + indent + Fingerprint(body.Trim());
    }

    /// <summary>
    /// What replaces a value: its length and a short digest. Length is included because "a 4-byte
    /// password" is worth seeing, and the digest because two values that differ must look
    /// different — otherwise a diff of a changed secret would read as no change at all.
    /// </summary>
    private static string Fingerprint(string value)
    {
        string trimmed = value.Trim();

        // Quoted empty, or explicitly null: there is nothing to hide and saying so is useful.
        if (trimmed is "\"\"" or "''" or "null" or "~") return trimmed;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        string hex = Convert.ToHexStringLower(digest)[..FingerprintChars];

        return $"<redacted: {trimmed.Length} bytes, sha256:{hex}>";
    }

    /// <summary>
    /// Redacts <c>"data"</c> and <c>"stringData"</c> objects in JSON, for the patch bodies the
    /// gate shows verbatim. Deliberately narrow: it rewrites the values of a flat object following
    /// one of those keys and leaves the rest of the document alone.
    /// </summary>
    private static string ScrubJsonData(string text)
    {
        if (!text.Contains("\"data\"", StringComparison.Ordinal)
            && !text.Contains("\"stringData\"", StringComparison.Ordinal))
        {
            return text;
        }

        return Regex.Replace(
            text,
            @"""(data|stringData)""\s*:\s*\{(?<body>[^{}]*)\}",
            m => $"\"{m.Groups[1].Value}\":{{{ScrubJsonPairs(m.Groups["body"].Value)}}}",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));
    }

    private static string ScrubJsonPairs(string body) =>
        Regex.Replace(
            body,
            @"""(?<key>(?:[^""\\]|\\.)*)""\s*:\s*""(?<value>(?:[^""\\]|\\.)*)""",
            m => $"\"{m.Groups["key"].Value}\":\"{Fingerprint(m.Groups["value"].Value)}\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));
}
