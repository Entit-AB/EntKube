using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace EntKube.Web.Services.Upgrades;

/// <summary>
/// How far an installed version lags behind the newest available one. Ordered
/// most-severe-first so a caller can sort or threshold on it directly.
/// </summary>
public enum VersionLag
{
    /// <summary>A major-version jump is available (breaking changes expected).</summary>
    Major = 0,
    /// <summary>A minor-version jump is available (new features, no breaking changes).</summary>
    Minor = 1,
    /// <summary>Only patch releases are available (fixes).</summary>
    Patch = 2,
    /// <summary>Installed is the newest available — or newer than anything published.</summary>
    UpToDate = 3,
}

/// <summary>
/// A semantic version, tolerant of the shapes Helm charts actually publish:
/// a leading "v" (v1.2.3), two-part versions (1.2), and pre-release / build
/// metadata suffixes (1.2.3-rc.1+build5).
///
/// Helm requires charts to be SemVer 2, but repositories in the wild are looser
/// than the spec, so parsing is deliberately permissive and never throws — an
/// unparseable version is simply not comparable, and callers treat that as
/// "unknown" rather than as an upgrade signal. That matters: guessing wrong here
/// would either hide a needed upgrade or nag about a phantom one.
///
/// We hand-roll this rather than take a NuGet dependency because the comparison
/// rules we need are small and the failure modes above are ours to define.
/// </summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Dot-separated pre-release identifiers ("rc", "1"), empty for a stable release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>True when this is a pre-release (1.2.3-rc.1) rather than a stable release.</summary>
    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>The version exactly as it appeared upstream, including any "v" prefix and build metadata.</summary>
    public string Original { get; }

    private SemVer(int major, int minor, int patch, IReadOnlyList<string> preRelease, string original)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        Original = original;
    }

    /// <summary>
    /// Parses a version string, returning false rather than throwing when the input
    /// isn't a recognisable version. Accepts "1.2.3", "v1.2.3", "1.2", "1",
    /// "1.2.3-rc.1" and "1.2.3+build" (build metadata is kept in
    /// <see cref="Original"/> but ignored for comparison, per SemVer 2).
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SemVer? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string original = input.Trim();
        string work = original;

        // Helm and Kubernetes both publish "v"-prefixed tags; SemVer itself doesn't
        // allow the prefix, so strip it before parsing rather than rejecting the version.
        if (work.Length > 1 && (work[0] == 'v' || work[0] == 'V'))
        {
            work = work[1..];
        }

        // Build metadata is explicitly not part of precedence — drop it before comparing.
        int plus = work.IndexOf('+');
        if (plus >= 0)
        {
            work = work[..plus];
        }

        string[] preRelease = [];
        int dash = work.IndexOf('-');
        if (dash >= 0)
        {
            string suffix = work[(dash + 1)..];
            work = work[..dash];
            // A trailing "-" with nothing after it is malformed, not a pre-release.
            if (suffix.Length == 0)
            {
                return false;
            }
            preRelease = suffix.Split('.');
        }

        string[] parts = work.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        // Missing minor/patch default to 0 so "1.2" and "1.2.0" compare equal, which is
        // how chart authors who publish two-part versions mean them.
        if (!TryParseNumber(parts[0], out int major)
            || !TryParseNumber(parts.Length > 1 ? parts[1] : "0", out int minor)
            || !TryParseNumber(parts.Length > 2 ? parts[2] : "0", out int patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch, preRelease, original);
        return true;
    }

    /// <summary>Parses, or returns null when the input isn't a recognisable version.</summary>
    public static SemVer? Parse(string? input) => TryParse(input, out SemVer? v) ? v : null;

    private static bool TryParseNumber(string text, out int value)
    {
        // Guard the numeric parse explicitly: int.TryParse would accept "+5" and
        // culture-specific separators, neither of which is a valid version component.
        value = 0;
        if (text.Length == 0 || text.Length > 9)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// SemVer 2 precedence for pre-release identifiers: a version WITH a pre-release
    /// sorts before the same version without one (1.2.3-rc.1 &lt; 1.2.3), numeric
    /// identifiers compare numerically, alphanumeric ones compare in ASCII order,
    /// numeric sorts below alphanumeric, and a longer identifier list wins ties.
    /// </summary>
    private static int ComparePreRelease(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 && right.Count == 0) return 0;
        // A stable release outranks any pre-release of the same numbers.
        if (left.Count == 0) return 1;
        if (right.Count == 0) return -1;

        int shared = Math.Min(left.Count, right.Count);
        for (int i = 0; i < shared; i++)
        {
            bool leftNumeric = TryParseNumber(left[i], out int leftValue);
            bool rightNumeric = TryParseNumber(right[i], out int rightValue);

            int result;
            if (leftNumeric && rightNumeric)
            {
                result = leftValue.CompareTo(rightValue);
            }
            else if (leftNumeric != rightNumeric)
            {
                // Numeric identifiers always have lower precedence than alphanumeric ones.
                result = leftNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(left[i], right[i]);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    /// <summary>
    /// Classifies how far <paramref name="installed"/> lags behind
    /// <paramref name="latest"/>. An installed version at or ahead of latest is
    /// <see cref="VersionLag.UpToDate"/> — being ahead is normal when an operator
    /// has pinned a newer chart than the catalog recommends, and is not a finding.
    /// </summary>
    public static VersionLag Compare(SemVer installed, SemVer latest)
    {
        if (installed.CompareTo(latest) >= 0)
        {
            return VersionLag.UpToDate;
        }

        if (installed.Major != latest.Major) return VersionLag.Major;
        if (installed.Minor != latest.Minor) return VersionLag.Minor;
        return VersionLag.Patch;
    }

    /// <summary>
    /// True when <paramref name="candidateVersion"/> is a version
    /// <paramref name="installedVersion"/> should move up to — the question the cluster
    /// components view asks of the catalog pin.
    ///
    /// Compared as versions rather than as strings, because a release installed *ahead* of
    /// the catalog (hand-pinned, or a catalog entry we have not refreshed yet) differs from
    /// the pin without being behind it, and calling that an update advertises — and on click
    /// performs — a downgrade. When either version is unparseable, nothing can be proven
    /// about order, so any difference counts: an adopted release records no version at all,
    /// and pinning it to the catalog is exactly what the operator wants.
    /// </summary>
    public static bool OffersUpgrade(string? installedVersion, string? candidateVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersion))
        {
            return false;
        }

        SemVer? installed = Parse(installedVersion);
        SemVer? candidate = Parse(candidateVersion);

        if (installed is null || candidate is null)
        {
            return !string.Equals(
                Normalize(installedVersion), Normalize(candidateVersion), StringComparison.OrdinalIgnoreCase);
        }

        return candidate > installed;
    }

    /// <summary>
    /// Strips a leading "v" so versions compare on their numbers alone. Charts are
    /// inconsistent about the prefix (jetstack publishes "v0.24.0", most publish "0.24.0")
    /// and a prefix-only difference is not a version difference.
    /// </summary>
    public static string Normalize(string? version) => version?.Trim().TrimStart('v', 'V') ?? "";

    public bool Equals(SemVer? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        Major, Minor, Patch, string.Join('.', PreRelease));

    public override string ToString() => Original;

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;
}
