using System.Globalization;

namespace EntKube.Web.Services;

/// <summary>
/// The contract between the thing that builds a node image and the thing that later has to find
/// it. Both sides live in different services and run days apart, so the property names and the
/// version format are pinned here rather than spelled out at each end.
///
/// <para>The version is carried as Glance image metadata, not encoded in the name. A name is a
/// label an operator may rename; metadata is what a query can rely on — and being able to ask
/// "which image serves v1.31.4" is what lets a cluster be created for a Kubernetes version instead
/// of for an image somebody has to remember the name of.</para>
/// </summary>
public static class MachineImageNaming
{
    /// <summary>Glance property holding the Kubernetes version this image was built for.</summary>
    public const string VersionProperty = "entkube_kubernetes_version";

    /// <summary>Glance property holding the build timestamp, so the newest image for a version wins.</summary>
    public const string BuiltAtProperty = "entkube_built_at";

    /// <summary>Glance property naming the base image the build started from, for provenance.</summary>
    public const string BaseImageProperty = "entkube_base_image";

    /// <summary>
    /// Canonical version form: leading "v", no build metadata. "1.31.4", "v1.31.4" and
    /// "v1.31.4+something" are the same version, and a lookup must not care which was typed.
    /// </summary>
    public static string Normalize(string version)
    {
        string trimmed = (version ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        // Drop packaging/build metadata: "v1.30.2+k3s1", "v1.29.5-eks-1234".
        int plus = trimmed.IndexOf('+');
        if (plus > 0)
        {
            trimmed = trimmed[..plus];
        }

        if (!trimmed.StartsWith('v'))
        {
            trimmed = "v" + trimmed;
        }

        return trimmed;
    }

    /// <summary>
    /// Name for a freshly built image. The timestamp keeps successive builds of the same version
    /// distinct — Glance allows duplicate names, and two images called the same thing is how you
    /// end up unable to say which one a cluster is running.
    /// </summary>
    public static string ImageName(string kubernetesVersion, DateTime builtAtUtc) =>
        $"entkube-k8s-{Normalize(kubernetesVersion)}-{builtAtUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";

    /// <summary>The metadata every EntKube-built image carries.</summary>
    public static Dictionary<string, string> Properties(
        string kubernetesVersion, string baseImage, DateTime builtAtUtc) => new()
    {
        [VersionProperty] = Normalize(kubernetesVersion),
        [BuiltAtProperty] = builtAtUtc.ToString("O", CultureInfo.InvariantCulture),
        [BaseImageProperty] = baseImage
    };

    /// <summary>
    /// The package version string for a Kubernetes version, as apt wants it: "v1.31.4" → "1.31.4-*".
    /// Pinning matters — an unpinned kubeadm install produces an image whose version is whatever
    /// the repository served that morning, which is not a thing you can build a cluster on twice.
    /// </summary>
    public static string AptVersionPin(string kubernetesVersion) =>
        Normalize(kubernetesVersion).TrimStart('v') + "-*";

    /// <summary>
    /// The pkgs.k8s.io repository stream for a version: "v1.31.4" → "v1.31". Kubernetes publishes
    /// one repository per minor, so the patch has to come off before it can be addressed.
    /// </summary>
    public static string PackageRepoStream(string kubernetesVersion)
    {
        string normalized = Normalize(kubernetesVersion);
        string[] parts = normalized.TrimStart('v').Split('.');
        return parts.Length >= 2 ? $"v{parts[0]}.{parts[1]}" : normalized;
    }
}
