namespace EntKube.Installer;

/// <summary>How the management plane is reached from outside the host.</summary>
public enum Exposure
{
    /// <summary>Caddy on 80/443, automatic Let's Encrypt for a public domain. The production shape.</summary>
    PublicTls,

    /// <summary>The app's own port published directly, no Caddy and no TLS. Evaluation only.</summary>
    LocalPort,
}

/// <summary>Where application state lives.</summary>
public enum Database
{
    /// <summary>The postgres service in the compose file, on a named volume.</summary>
    Bundled,

    /// <summary>A Postgres reachable from this host, managed by someone else.</summary>
    ExternalPostgres,

    /// <summary>A file on the entkube-data volume. Single node, evaluation only.</summary>
    Sqlite,
}

/// <summary>Where the telemetry engine's sealed segments go.</summary>
public enum TelemetryStorage
{
    /// <summary>Local disk under the entkube-data volume. Single node.</summary>
    LocalDisk,

    /// <summary>The bundled MinIO, started with the compose "objectstore" profile.</summary>
    BundledMinio,

    /// <summary>An S3-compatible bucket configured here rather than as an in-app StorageLink.</summary>
    ExternalS3,
}

/// <summary>
/// Everything the wizard collects, and everything the renderer needs. Deliberately a plain record
/// with no behaviour: the value of separating it from both is that a scripted install can populate
/// it from flags and reach exactly the same renderer the interactive path does.
/// </summary>
public sealed class Answers
{
    public required string Directory { get; init; }

    public Exposure Exposure { get; set; } = Exposure.PublicTls;

    /// <summary>Set when <see cref="Exposure"/> is <see cref="Exposure.PublicTls"/>.</summary>
    public string? Domain { get; set; }

    public string? AcmeEmail { get; set; }

    /// <summary>Set when <see cref="Exposure"/> is <see cref="Exposure.LocalPort"/>.</summary>
    public int LocalPort { get; set; } = 8080;

    public Database Database { get; set; } = Database.Bundled;

    public string? ExternalConnectionString { get; set; }

    /// <summary>Only meaningful for <see cref="Database.Bundled"/>.</summary>
    public string PostgresPassword { get; set; } = string.Empty;

    /// <summary>
    /// An existing database being adopted, to be reproduced rather than replaced.
    ///
    /// When set, the generated compose file uses this service's name, image, data location and
    /// credentials instead of the defaults. Getting this wrong does not fail loudly — it starts a
    /// new, empty Postgres on a new volume and leaves the real data stranded — which is why it is
    /// carried explicitly rather than reconstructed from a template.
    /// </summary>
    public AdoptedDatabase? AdoptedDatabase { get; set; }

    /// <summary>
    /// An existing compose network being adopted. When set, the generated file declares it under the
    /// same key it already has — compose labels a network with its key and refuses a file that
    /// declares the same network under a different one.
    /// </summary>
    public AdoptedNetwork? AdoptedNetwork { get; set; }

    /// <summary>
    /// Never regenerated for an install that already has one. See <see cref="EnvFile"/> — a new key
    /// silently orphans every secret in the vault.
    /// </summary>
    public string VaultRootKey { get; set; } = string.Empty;

    public TelemetryStorage TelemetryStorage { get; set; } = TelemetryStorage.LocalDisk;

    public int TelemetryRetentionDays { get; set; } = 14;

    public string? S3Endpoint { get; set; }

    public string? S3Bucket { get; set; }

    public string? S3Region { get; set; }

    public string? S3AccessKey { get; set; }

    public string? S3SecretKey { get; set; }

    public string? MinioUser { get; set; }

    public string? MinioPassword { get; set; }

    public string Registry { get; set; } = "entit.azurecr.io";

    public string ImageTag { get; set; } = "latest";

    /// <summary>
    /// Optional. One credential with two consumers — see the comment in docker-compose.yml. Left
    /// unset for a public registry, which is what entit.azurecr.io is.
    /// </summary>
    public string? RegistryUsername { get; set; }

    public string? RegistryPassword { get; set; }

    /// <summary>Granted the Admin role on every startup. The way back in when nobody can sign in.</summary>
    public string? SeedAdminEmail { get; set; }

    /// <summary>
    /// True when this directory already held an install. Governs whether secrets are generated or
    /// preserved, and whether the summary calls itself an install or an upgrade.
    /// </summary>
    public bool IsUpgrade { get; init; }

    /// <summary>The base URL the app is reached on, used for the telemetry ingest URL and the summary.</summary>
    public string PublicUrl => Exposure == Exposure.PublicTls
        ? $"https://{Domain}"
        : $"http://localhost:{LocalPort}";

    /// <summary>
    /// The compose services to pull and start. Not simply "everything": the postgres service is
    /// pointless for an external database or SQLite, and starting it would leave an idle container
    /// holding a volume that looks like it holds the data and does not.
    /// </summary>
    public IReadOnlyList<string> Services
    {
        get
        {
            List<string> services = ["entkube"];

            if (Database == Database.Bundled)
            {
                // The adopted service keeps its own name; a deployment that calls it "db" must not
                // gain a second one called "postgres".
                services.Add(AdoptedDatabase?.ServiceName ?? "postgres");
            }

            if (Exposure == Exposure.PublicTls)
            {
                services.Add("caddy");
            }

            if (TelemetryStorage == TelemetryStorage.BundledMinio)
            {
                services.Add("minio");
                services.Add("minio-init");
            }

            return services;
        }
    }

    public IReadOnlyList<int> PublishedPorts =>
        Exposure == Exposure.PublicTls ? [80, 443] : [LocalPort];
}
