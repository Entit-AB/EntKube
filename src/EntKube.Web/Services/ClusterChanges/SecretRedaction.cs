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

        // ⚠️ A JSON document goes through the parser and NOTHING ELSE. It already handles both
        // things the line passes are for — every data and stringData object at any depth, and the
        // last-applied annotation — and it cannot corrupt what it rewrites.
        //
        // The line rule must not touch JSON, and the first version of this did: it replaced
        // everything after the annotation's colon to the end of the line, which on a single-line
        // JSON document is the whole rest of the document. The dialog got truncated, invalid JSON
        // — and the test for the annotation PASSED, because the value it was looking for had been
        // destroyed along with everything after it. See
        // LastAppliedAnnotationRedactionTests.The_scrubbed_document_is_still_valid_json.
        if (TryScrubJsonDocument(text, out string? parsed))
        {
            return parsed!;
        }

        // Not a JSON document: YAML or diff text, where an annotation occupies one line and the
        // rest of the line is its value.
        text = ScrubLastApplied(text);
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
    /// The annotation <c>kubectl apply</c> writes with the entire applied manifest, values
    /// included.
    ///
    /// <para>⚠️ <b>A Secret read back with <c>get -o yaml</c> carries its values TWICE</b> — once
    /// in <c>data:</c>, where they are redacted, and once inside this metadata annotation, where
    /// the data-block pass never looks. The gate does exactly that read for a deleted resource,
    /// and a dry-run rendering returns the same shape.</para>
    ///
    /// <para>The whole value goes, rather than the <c>data</c> object inside it. The operator does
    /// not need a second copy of the manifest, and keying on the ANNOTATION NAME rather than on
    /// the structure inside it is what makes this hold for every shape the value arrives in: a
    /// YAML scalar, a quoted scalar, or a JSON string whose own quotes are escaped. The escaped
    /// form is what defeated the regex this replaces.</para>
    /// </summary>
    private const string LastAppliedAnnotation = "kubectl.kubernetes.io/last-applied-configuration";

    /// <summary>
    /// Replaces the annotation's value on any line that carries it. The JSON case is handled by
    /// the parser instead, which is why this may safely take the rest of the line: YAML puts one
    /// annotation on one line, and a diff line showing a patch is a line too.
    /// </summary>
    private static string ScrubLastApplied(string text)
    {
        if (!text.Contains(LastAppliedAnnotation, StringComparison.Ordinal)) return text;

        List<string> output = [];

        foreach (string line in text.Split('\n'))
        {
            int name = line.IndexOf(LastAppliedAnnotation, StringComparison.Ordinal);
            if (name < 0)
            {
                output.Add(line);
                continue;
            }

            int colon = line.IndexOf(':', name + LastAppliedAnnotation.Length);
            if (colon < 0)
            {
                output.Add(line);
                continue;
            }

            string value = line[(colon + 1)..].Trim();
            output.Add(value.Length == 0
                ? line
                : line[..(colon + 1)] + " " + Fingerprint(value));
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// Walks the document as JSON and redacts every <c>data</c> and <c>stringData</c> object at
    /// any depth, plus the last-applied annotation.
    ///
    /// <para>⚠️ A parser rather than a pattern, because the pattern could not say where an object
    /// ENDS. It bounded the body with <c>[^{}]*</c>, so a value containing a brace — an app
    /// config blob sitting next to a password in <c>stringData</c> is exactly that — ended the
    /// match early and left the following key's value in the clear.</para>
    ///
    /// <para>Returns false for anything that is not a JSON document, which is most of what
    /// reaches this: diff text and YAML manifests go to the line-oriented passes.</para>
    /// </summary>
    private static bool TryScrubJsonDocument(string text, out string? scrubbed)
    {
        scrubbed = null;

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return false;

        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (root is null) return false;

        ScrubNode(root, inData: false);
        scrubbed = root.ToJsonString();
        return true;
    }

    private static void ScrubNode(System.Text.Json.Nodes.JsonNode node, bool inData)
    {
        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (System.Text.Json.Nodes.JsonNode? item in array)
            {
                if (item is not null) ScrubNode(item, inData);
            }

            return;
        }

        if (node is not System.Text.Json.Nodes.JsonObject map) return;

        foreach (string key in map.Select(p => p.Key).ToList())
        {
            System.Text.Json.Nodes.JsonNode? child = map[key];

            // ⚠️ The annotation is a whole manifest in a string. Redact the string itself rather
            // than descending into it: parsing it to redact its data block would leave every
            // other value in it readable, and it is a copy of an object the operator is already
            // being shown.
            if (key == LastAppliedAnnotation)
            {
                map[key] = Fingerprint(child?.ToString() ?? "");
                continue;
            }

            bool childIsData = inData || key is "data" or "stringData";

            if (child is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray)
            {
                ScrubNode(child, childIsData);
                continue;
            }

            // A scalar directly inside a data block is a value.
            if (inData && child is not null)
            {
                map[key] = Fingerprint(child.ToString());
            }
        }
    }

    /// <summary>
    /// Redacts <c>"data"</c> and <c>"stringData"</c> objects in a JSON FRAGMENT — a truncated hunk
    /// or a diff line, which <see cref="TryScrubJsonDocument"/> cannot parse.
    ///
    /// <para>⚠️ Still bounded by <c>[^{}]*</c>, and still unable to say where an object ends when
    /// a value contains a brace. That is why it is now the fallback rather than the path every
    /// JSON document takes: on a fragment, over-redaction is the failure mode and the parser
    /// handles everything well-formed.</para>
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
