using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EntKube.Telemetry;

/// <summary>
/// Decides which errors are the same error. Two lines that differ only in an id, a number, a time or a
/// quoted value are one fault happening twice, so those parts are replaced before hashing and what
/// remains is the shape of the message.
///
/// <para>Only the first line counts. A stack trace's later frames change with every release, and the
/// first line is what a person would call the error.</para>
///
/// <para>⚠️ The order of the replacements matters, because a broad pattern would eat the input of a
/// narrower one: a UUID is mostly hex and digits, so it goes first. Input is cut before any pattern
/// runs, and none of them nest quantifiers, so a hostile line cannot make this slow.</para>
/// </summary>
public static partial class ErrorFingerprint
{
    private const int MaxInputLength = 1000;

    /// <summary>The message with everything that varies replaced by a placeholder.</summary>
    public static string Normalize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";

        string line = message.AsSpan().TrimStart().ToString();

        int newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0) line = line[..newline];

        if (line.Length > MaxInputLength) line = line[..MaxInputLength];

        line = Uuid().Replace(line, "<uuid>");
        line = IsoTime().Replace(line, "<time>");
        line = IpAddress().Replace(line, "<ip>");
        line = HexValue().Replace(line, "<hex>");
        line = DoubleQuoted().Replace(line, "\"<str>\"");
        line = SingleQuoted().Replace(line, "'<str>'");
        line = Number().Replace(line, "<n>");

        return Whitespace().Replace(line, " ").Trim();
    }

    /// <summary>A stable id for a shape within a scope. Parts are separated so "ab","c" is not "a","bc".</summary>
    public static string Compute(params string?[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p ?? ""))));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The workload behind a pod, so an error is one issue however many replicas and rollouts it
    /// crosses. Deployment pods end in a ReplicaSet hash and a suffix, DaemonSet pods in a suffix,
    /// and StatefulSet pods in an ordinal.
    /// </summary>
    public static string Workload(string? pod)
    {
        if (string.IsNullOrEmpty(pod)) return "";

        string workload = DeploymentSuffix().Replace(pod, "");
        if (workload != pod) return workload;

        workload = OrdinalSuffix().Replace(pod, "");
        if (workload != pod) return workload;

        return RandomSuffix().Replace(pod, "");
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?")]
    private static partial Regex IsoTime();

    [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?::\d+)?\b")]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b|\b(?=[0-9a-fA-F]*\d)(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{8,}\b")]
    private static partial Regex HexValue();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex DoubleQuoted();

    [GeneratedRegex(@"(?<![A-Za-z0-9])'[^']*'")]
    private static partial Regex SingleQuoted();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\b")]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Kubernetes draws these suffixes from an alphabet with no vowels, so that a generated name can
    // never spell a word. Matching any lowercase run would read node-exporter-x7k2p as a Deployment
    // called "node" with a ReplicaSet called "exporter".
    [GeneratedRegex("-[bcdfghjklmnpqrstvwxz2456789]{6,10}-[bcdfghjklmnpqrstvwxz2456789]{5}$")]
    private static partial Regex DeploymentSuffix();

    [GeneratedRegex(@"-\d+$")]
    private static partial Regex OrdinalSuffix();

    [GeneratedRegex("-[bcdfghjklmnpqrstvwxz2456789]{5}$")]
    private static partial Regex RandomSuffix();
}
