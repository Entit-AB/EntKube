namespace EntKube.Installer;

/// <summary>
/// An existing database service, described precisely enough to be reproduced rather than replaced.
///
/// This type exists because of the worst thing this installer can do. It regenerates the compose
/// file, and the generated Postgres service used a fixed image tag, a fixed volume name and fixed
/// credentials. Pointed at a deployment whose database differed in any of those — a volume called
/// <c>pgdata</c> instead of <c>postgres-data</c>, an image pinned to 15 instead of 17, a service
/// called <c>db</c> — it would have started a brand new, empty Postgres on a brand new volume while
/// the real data sat in the old one, untouched and unused. Nothing is deleted, and everything is
/// gone.
///
/// So an adopted database is carried forward exactly as found: same service name, same image, same
/// data location, same database and user. Anything that cannot be carried forward is a refusal, not
/// a best effort.
/// </summary>
public sealed record AdoptedDatabase(
    string ServiceName,
    string Image,
    string VolumeSource,
    string VolumeTarget,
    bool IsBindMount,
    string DatabaseName,
    string Username)
{
    /// <summary>The compose volume line for this service's data.</summary>
    public string VolumeLine => $"{VolumeSource}:{VolumeTarget}";

    /// <summary>
    /// True when the data lives in a named volume that the compose file must therefore declare.
    /// A bind mount is a host path and is declared nowhere.
    /// </summary>
    public bool NeedsVolumeDeclaration => !IsBindMount;

    public string Describe =>
        $"service \"{ServiceName}\", image {Image}, data in "
        + (IsBindMount ? $"host path {VolumeSource}" : $"volume \"{VolumeSource}\"")
        + $", database \"{DatabaseName}\" as \"{Username}\"";
}
