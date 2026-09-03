namespace EntKube.Web.Services.Upgrades;

/// <summary>Support state of a Kubernetes minor version at a point in time.</summary>
public enum KubernetesSupportState
{
    /// <summary>Past its end-of-life date — no more patches, including security patches.</summary>
    EndOfLife = 0,
    /// <summary>End-of-life within the alerting window; plan the upgrade now.</summary>
    NearingEndOfLife = 1,
    /// <summary>Actively supported.</summary>
    Supported = 2,
    /// <summary>Not in the calendar (newer than this build knows about, or unparseable).</summary>
    Unknown = 3,
}

/// <summary>How a cluster's Kubernetes version stands against the upstream support window.</summary>
public sealed record KubernetesVersionStatus
{
    public required KubernetesSupportState State { get; init; }

    /// <summary>Minor version in "1.31" form, when it could be determined.</summary>
    public string? MinorVersion { get; init; }

    /// <summary>Upstream end-of-life date for that minor, when known.</summary>
    public DateOnly? EndOfLife { get; init; }

    /// <summary>Days until end-of-life; negative once it has passed. Null when unknown.</summary>
    public int? DaysRemaining { get; init; }

    /// <summary>Newest minor version this calendar knows about, for "how far behind" context.</summary>
    public string? LatestKnownMinor { get; init; }

    /// <summary>How many minor releases behind the newest known version, when determinable.</summary>
    public int? MinorsBehind { get; init; }
}

/// <summary>
/// Upstream Kubernetes end-of-life dates, used to warn before a cluster stops
/// receiving security patches.
///
/// This is a static table rather than a live feed on purpose: the management plane
/// must be able to answer "is this cluster EOL?" in an air-gapped install, and the
/// upstream schedule is published a year ahead and effectively never moves. The
/// trade-off is that the table needs a refresh when new minors ship — a version
/// newer than the table reports <see cref="KubernetesSupportState.Unknown"/> rather
/// than a wrong answer, which is the safe direction to fail.
///
/// Dates are the published end-of-life (end of patch support) per
/// kubernetes.io/releases/patch-releases.
/// </summary>
public static class KubernetesReleaseCalendar
{
    /// <summary>
    /// How far ahead of end-of-life to start warning. A minor-version upgrade across a
    /// fleet is weeks of work, so a month's notice is the minimum that is actionable.
    /// </summary>
    public static readonly int WarnWindowDays = 90;

    private static readonly IReadOnlyDictionary<string, DateOnly> EndOfLifeByMinor =
        new Dictionary<string, DateOnly>(StringComparer.Ordinal)
        {
            ["1.26"] = new DateOnly(2024, 2, 28),
            ["1.27"] = new DateOnly(2024, 6, 28),
            ["1.28"] = new DateOnly(2024, 10, 28),
            ["1.29"] = new DateOnly(2025, 2, 28),
            ["1.30"] = new DateOnly(2025, 6, 28),
            ["1.31"] = new DateOnly(2025, 10, 28),
            ["1.32"] = new DateOnly(2026, 2, 28),
            ["1.33"] = new DateOnly(2026, 6, 28),
            ["1.34"] = new DateOnly(2026, 10, 28),
        };

    /// <summary>The newest minor version this table knows about.</summary>
    public static string LatestKnownMinor { get; } = EndOfLifeByMinor.Keys
        .Select(SemVer.Parse)
        .Where(v => v is not null)
        .OrderByDescending(v => v!)
        .Select(v => $"{v!.Major}.{v.Minor}")
        .First();

    /// <summary>
    /// Classifies a reported Kubernetes version string (a kubelet version such as
    /// "v1.31.4" or "v1.30.2+k3s1") against the support calendar.
    /// </summary>
    public static KubernetesVersionStatus Classify(string? version, DateOnly today)
    {
        SemVer? parsed = SemVer.Parse(StripDistroSuffix(version));
        if (parsed is null)
        {
            return new KubernetesVersionStatus { State = KubernetesSupportState.Unknown };
        }

        string minor = $"{parsed.Major}.{parsed.Minor}";
        SemVer? latestKnown = SemVer.Parse(LatestKnownMinor);
        int? minorsBehind = latestKnown is not null && parsed.Major == latestKnown.Major
            ? Math.Max(0, latestKnown.Minor - parsed.Minor)
            : null;

        if (!EndOfLifeByMinor.TryGetValue(minor, out DateOnly eol))
        {
            return new KubernetesVersionStatus
            {
                State = KubernetesSupportState.Unknown,
                MinorVersion = minor,
                LatestKnownMinor = LatestKnownMinor,
                MinorsBehind = minorsBehind,
            };
        }

        int daysRemaining = eol.DayNumber - today.DayNumber;
        KubernetesSupportState state = daysRemaining switch
        {
            < 0 => KubernetesSupportState.EndOfLife,
            _ when daysRemaining <= WarnWindowDays => KubernetesSupportState.NearingEndOfLife,
            _ => KubernetesSupportState.Supported,
        };

        return new KubernetesVersionStatus
        {
            State = state,
            MinorVersion = minor,
            EndOfLife = eol,
            DaysRemaining = daysRemaining,
            LatestKnownMinor = LatestKnownMinor,
            MinorsBehind = minorsBehind,
        };
    }

    /// <summary>
    /// Trims distribution suffixes that kubelet reports alongside the upstream version
    /// ("v1.30.2+k3s1", "v1.29.5-eks-1234"). These are build metadata for our purposes:
    /// the support window tracks the upstream minor, not the distro's packaging.
    /// </summary>
    private static string? StripDistroSuffix(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        string work = version.Trim();
        int plus = work.IndexOf('+');
        if (plus >= 0)
        {
            work = work[..plus];
        }

        // "-eks-1234" / "-gke.1" are packaging markers, not SemVer pre-releases, and would
        // otherwise make the version sort BELOW the same upstream release.
        int dash = work.IndexOf('-');
        if (dash >= 0)
        {
            work = work[..dash];
        }

        return work;
    }
}
