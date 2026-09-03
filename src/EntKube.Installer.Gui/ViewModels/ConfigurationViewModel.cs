namespace EntKube.Installer.Gui.ViewModels;

/// <summary>
/// Step 2 — the same answers the console wizard collects, as a form.
///
/// It produces an <see cref="Answers"/>, which is the only thing downstream sees, so the GUI and the
/// console installer cannot disagree about what an answer means. Defaults are seeded from the .env
/// already on the target, exactly as the console wizard does, which is what makes re-running this
/// against an installed server safe.
/// </summary>
public sealed class ConfigurationViewModel : ViewModelBase
{
    private bool _publicTls = true;
    private string _domain = string.Empty;
    private string _acmeEmail = string.Empty;
    private string _localPort = "8080";
    private string _database = "bundled";
    private string _connectionString = string.Empty;
    private string _telemetryStorage = "disk";
    private string _retentionDays = "14";
    private string _s3Endpoint = string.Empty;
    private string _s3Bucket = "entkube-telemetry";
    private string _s3Region = "us-east-1";
    private string _s3AccessKey = string.Empty;
    private string _s3SecretKey = string.Empty;
    private string _registry = "entit.azurecr.io";
    private string _imageTag = "latest";
    private string _registryUsername = string.Empty;
    private string _registryPassword = string.Empty;
    private string _seedAdminEmail = string.Empty;

    private EnvFile _existing = new();
    private string _vaultRootKey = string.Empty;
    private string _postgresPassword = string.Empty;
    private string _minioUser = string.Empty;
    private string _minioPassword = string.Empty;

    private static readonly string[] Validity = [nameof(IsValid), nameof(ValidationMessage)];

    public bool IsUpgrade { get; private set; }

    // ── Exposure ─────────────────────────────────────────────────────────────────────────────────

    public bool PublicTls
    {
        get => _publicTls;
        set => Set(ref _publicTls, value, [nameof(LocalOnly), .. Validity]);
    }

    public bool LocalOnly
    {
        get => !_publicTls;
        set => PublicTls = !value;
    }

    public string Domain
    {
        get => _domain;
        set => Set(ref _domain, value, Validity);
    }

    public string AcmeEmail
    {
        get => _acmeEmail;
        set => Set(ref _acmeEmail, value, Validity);
    }

    public string LocalPort
    {
        get => _localPort;
        set => Set(ref _localPort, value, Validity);
    }

    // ── Database ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>"bundled", "external" or "sqlite" — the same vocabulary as the console --database flag.</summary>
    public string Database
    {
        get => _database;
        set => Set(ref _database, value,
            [nameof(IsBundledDatabase), nameof(IsExternalDatabase), nameof(IsSqlite), .. Validity]);
    }

    public bool IsBundledDatabase
    {
        get => _database == "bundled";
        set { if (value) { Database = "bundled"; } }
    }

    public bool IsExternalDatabase
    {
        get => _database == "external";
        set { if (value) { Database = "external"; } }
    }

    public bool IsSqlite
    {
        get => _database == "sqlite";
        set { if (value) { Database = "sqlite"; } }
    }

    public string ConnectionString
    {
        get => _connectionString;
        set => Set(ref _connectionString, value, Validity);
    }

    // ── Telemetry ────────────────────────────────────────────────────────────────────────────────

    public string TelemetryStorage
    {
        get => _telemetryStorage;
        set => Set(ref _telemetryStorage, value,
            [nameof(IsLocalDisk), nameof(IsBundledMinio), nameof(IsExternalS3), .. Validity]);
    }

    public bool IsLocalDisk
    {
        get => _telemetryStorage == "disk";
        set { if (value) { TelemetryStorage = "disk"; } }
    }

    public bool IsBundledMinio
    {
        get => _telemetryStorage == "minio";
        set { if (value) { TelemetryStorage = "minio"; } }
    }

    public bool IsExternalS3
    {
        get => _telemetryStorage == "s3";
        set { if (value) { TelemetryStorage = "s3"; } }
    }

    public string RetentionDays
    {
        get => _retentionDays;
        set => Set(ref _retentionDays, value, Validity);
    }

    public string S3Endpoint
    {
        get => _s3Endpoint;
        set => Set(ref _s3Endpoint, value, Validity);
    }

    public string S3Bucket
    {
        get => _s3Bucket;
        set => Set(ref _s3Bucket, value, Validity);
    }

    public string S3Region
    {
        get => _s3Region;
        set => Set(ref _s3Region, value);
    }

    public string S3AccessKey
    {
        get => _s3AccessKey;
        set => Set(ref _s3AccessKey, value, Validity);
    }

    public string S3SecretKey
    {
        get => _s3SecretKey;
        set => Set(ref _s3SecretKey, value, Validity);
    }

    // ── Image and admin ──────────────────────────────────────────────────────────────────────────

    public string Registry
    {
        get => _registry;
        set => Set(ref _registry, value, Validity);
    }

    public string ImageTag
    {
        get => _imageTag;
        set => Set(ref _imageTag, value, Validity);
    }

    public string RegistryUsername
    {
        get => _registryUsername;
        set => Set(ref _registryUsername, value);
    }

    public string RegistryPassword
    {
        get => _registryPassword;
        set => Set(ref _registryPassword, value);
    }

    public string SeedAdminEmail
    {
        get => _seedAdminEmail;
        set => Set(ref _seedAdminEmail, value);
    }

    // ── Seeding from the target ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the form from the .env already on the target.
    ///
    /// The three structural choices are read from the modes the last run RECORDED rather than
    /// inferred from the values they produced — a bundled database writes a connection string
    /// exactly like an external one does, and guessing from it once caused a re-run to drop the
    /// postgres service and point the app at a host that no longer existed.
    /// </summary>
    public void SeedFrom(EnvFile existing, bool isUpgrade, DetectedDeployment? detected = null)
    {
        _existing = existing;
        IsUpgrade = isUpgrade;
        Detected = detected ?? DetectedDeployment.Nothing;

        // Precedence: what a previous run of this installer recorded, then what the deployment
        // itself turns out to be, then the default.
        //
        // The middle term is what makes adopting a hand-rolled install safe. Without it every
        // structural answer fell back to a default the deployment never agreed to — and for
        // telemetry that default is "local disk", which regenerates a compose file with no MinIO
        // and takes a running service away without saying so.
        PublicTls = existing.Get("EXPOSE_MODE") is { } mode
            ? mode != "local"
            : Detected.Exposure != Exposure.LocalPort;

        // Falling back to what was found on the target, so a deployment whose domain lives in its
        // Caddyfile rather than its .env is not asked for something already on disk beside it.
        Domain = existing.Get("DOMAIN") ?? Detected.Domain ?? string.Empty;
        AcmeEmail = existing.Get("ACME_EMAIL") ?? Detected.AcmeEmail ?? string.Empty;
        LocalPort = existing.Get("LOCAL_PORT")
            ?? Detected.LocalPort?.ToString()
            ?? "8080";

        Database = existing.Get("DATABASE_MODE") ?? Detected.Database switch
        {
            Installer.Database.ExternalPostgres => "external",
            Installer.Database.Sqlite => "sqlite",
            Installer.Database.Bundled => "bundled",
            _ => "bundled",
        };

        ConnectionString = Database == "external" ? existing.Get("DATABASE_CONNECTION") ?? string.Empty : string.Empty;

        TelemetryStorage = existing.Get("TELEMETRY_STORAGE") ?? Detected.TelemetryStorage switch
        {
            Installer.TelemetryStorage.BundledMinio => "minio",
            Installer.TelemetryStorage.ExternalS3 => "s3",
            Installer.TelemetryStorage.LocalDisk => "disk",
            _ => "disk",
        };
        RetentionDays = existing.Get("TELEMETRY_RETENTION_DAYS") ?? "14";
        S3Endpoint = existing.Get("TELEMETRY_S3_ENDPOINT") ?? string.Empty;
        S3Bucket = existing.Get("TELEMETRY_BUCKET") ?? "entkube-telemetry";
        S3Region = existing.Get("TELEMETRY_S3_REGION") ?? "us-east-1";
        S3AccessKey = existing.Get("TELEMETRY_S3_ACCESS_KEY") ?? string.Empty;
        S3SecretKey = existing.Get("TELEMETRY_S3_SECRET_KEY") ?? string.Empty;

        Registry = existing.Get("REGISTRY") ?? "entit.azurecr.io";
        ImageTag = existing.Get("IMAGE_TAG") ?? "latest";
        RegistryUsername = existing.Get("REGISTRY_USERNAME") ?? string.Empty;
        RegistryPassword = existing.Get("REGISTRY_PASSWORD") ?? string.Empty;
        SeedAdminEmail = existing.Get("SEED_ADMIN_EMAIL") ?? string.Empty;

        // Never regenerated for an install that already has one, and never surfaced as a field. A
        // new vault key does not fail loudly — the app starts and every stored secret decrypts to
        // nothing — so there is no safe answer for a form to offer other than "keep it".
        string? key = existing.Get("VAULT__ROOTKEY");
        _vaultRootKey = key is null or "REPLACE_WITH_BASE64_32_BYTE_KEY" ? Secrets.VaultRootKey() : key;

        // Postgres applies its password only when the volume is first initialised, so the same rule
        // applies. Kept even when the mode is not bundled, so switching away and back does not mint
        // a password the existing volume will not accept.
        _postgresPassword = existing.Get("POSTGRES_PASSWORD") ?? Secrets.Password();
        _minioUser = existing.Get("MINIO_ROOT_USER") ?? "entkube";
        _minioPassword = existing.Get("MINIO_ROOT_PASSWORD") ?? Secrets.Password();

        Raise(nameof(IsUpgrade));
        Raise(nameof(VaultKeyNote));
        Raise(nameof(Detected));
        Raise(nameof(IsAdoption));
        Raise(nameof(DetectedSummary));
        Raise(nameof(HasDetectedSummary));
        Raise(nameof(AdoptedDatabaseNote));
        Raise(nameof(HasAdoptedDatabaseNote));
    }

    /// <summary>What was found at the target, for showing before anything is changed.</summary>
    public DetectedDeployment Detected { get; private set; } = DetectedDeployment.Nothing;

    public bool IsAdoption => Detected.IsAdoption;

    public string DetectedSummary => Detected.Findings.Count == 0
        ? string.Empty
        : string.Join("\n", Detected.Findings.Select(f => "• " + f));

    public bool HasDetectedSummary => Detected.Findings.Count > 0;

    /// <summary>The existing database being carried over, if any — the detail worth reading twice.</summary>
    public string AdoptedDatabaseNote => Detected.AdoptedDatabase is { } db
        ? $"Existing database kept as-is: {db.Describe}. Its data is not moved or recreated."
        : Detected.UnrepresentableDatabase is { } problem
            ? $"There is a PostgreSQL service here, but {problem}. The install will stop rather than "
              + "risk pointing the application at a different, empty database."
            : string.Empty;

    public bool HasAdoptedDatabaseNote => AdoptedDatabaseNote.Length > 0;

    public string VaultKeyNote => IsUpgrade
        ? "Reusing the vault root key and database password already on the target."
        : "A vault root key and database password will be generated. Back up .env afterwards.";

    public EnvFile Existing => _existing;

    // ── Validation ───────────────────────────────────────────────────────────────────────────────

    public bool IsValid => ValidationMessage is null;

    /// <summary>
    /// Why the form cannot be submitted, or null. One message rather than per-field errors: the form
    /// is short, and a single sentence next to the disabled button is easier to act on than a red
    /// mark somewhere above the fold.
    /// </summary>
    public string? ValidationMessage
    {
        get
        {
            if (PublicTls)
            {
                if (Domain.Trim().Length == 0)
                {
                    return "A public domain is required.";
                }

                if (!Domain.Contains('.') || Domain.Contains(' ') || Domain.Contains('/'))
                {
                    return "That does not look like a hostname — try entkube.example.com.";
                }

                if (!AcmeEmail.Contains('@'))
                {
                    return "A Let's Encrypt account email is required.";
                }
            }
            else if (!int.TryParse(LocalPort, out int port) || port is < 1 or > 65535)
            {
                return "The local port must be between 1 and 65535.";
            }

            if (IsExternalDatabase)
            {
                if (ConnectionString.Trim().Length == 0)
                {
                    return "A connection string is required for an external database.";
                }

                if (!ConnectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
                {
                    return "That does not look like an Npgsql connection string; it needs at least Host=.";
                }
            }

            if (!int.TryParse(RetentionDays, out int days) || days is < 1 or > 3650)
            {
                return "Retention must be between 1 and 3650 days.";
            }

            if (IsExternalS3)
            {
                if (S3Endpoint.Trim().Length == 0 || S3Bucket.Trim().Length == 0)
                {
                    return "An S3 endpoint and bucket are required.";
                }

                if (S3AccessKey.Trim().Length == 0 || S3SecretKey.Length == 0)
                {
                    return "S3 access and secret keys are required.";
                }
            }

            if (Registry.Trim().Length == 0 || ImageTag.Trim().Length == 0)
            {
                return "A registry and image tag are required.";
            }

            return null;
        }
    }

    /// <summary>Produces the answers the shared installer consumes.</summary>
    public Answers ToAnswers(string directory, bool isUpgrade) => new()
    {
        Directory = directory,
        IsUpgrade = isUpgrade,
        Exposure = PublicTls ? Exposure.PublicTls : Exposure.LocalPort,
        Domain = PublicTls ? Domain.Trim() : null,
        AcmeEmail = PublicTls ? AcmeEmail.Trim() : null,
        LocalPort = int.TryParse(LocalPort, out int port) ? port : 8080,
        Database = Database switch
        {
            "external" => Installer.Database.ExternalPostgres,
            "sqlite" => Installer.Database.Sqlite,
            _ => Installer.Database.Bundled,
        },
        ExternalConnectionString = IsExternalDatabase ? ConnectionString.Trim() : null,
        PostgresPassword = _postgresPassword,

        // Carried only while the bundled database is still the answer. Switching away from it is
        // guarded separately; attaching it here as well would describe a service the generated file
        // is not going to contain.
        AdoptedDatabase = IsBundledDatabase ? Detected.AdoptedDatabase : null,

        // Always carried, whatever else changes: the network belongs to the deployment, not to any
        // one answer, and declaring it under a different key is refused outright by compose.
        AdoptedNetwork = Detected.AdoptedNetwork,
        VaultRootKey = _vaultRootKey,
        TelemetryStorage = TelemetryStorage switch
        {
            "minio" => Installer.TelemetryStorage.BundledMinio,
            "s3" => Installer.TelemetryStorage.ExternalS3,
            _ => Installer.TelemetryStorage.LocalDisk,
        },
        TelemetryRetentionDays = int.TryParse(RetentionDays, out int days) ? days : 14,
        S3Endpoint = IsExternalS3 ? S3Endpoint.Trim() : null,
        S3Bucket = IsLocalDisk ? null : S3Bucket.Trim(),
        S3Region = IsExternalS3 ? S3Region.Trim() : null,
        S3AccessKey = IsExternalS3 ? S3AccessKey.Trim() : null,
        S3SecretKey = IsExternalS3 ? S3SecretKey : null,
        MinioUser = IsBundledMinio ? _minioUser : null,
        MinioPassword = IsBundledMinio ? _minioPassword : null,
        Registry = Registry.Trim(),
        ImageTag = ImageTag.Trim(),
        RegistryUsername = RegistryUsername.Trim().Length > 0 ? RegistryUsername.Trim() : null,
        RegistryPassword = RegistryPassword.Length > 0 ? RegistryPassword : null,
        SeedAdminEmail = SeedAdminEmail.Trim().Length > 0 ? SeedAdminEmail.Trim() : null,
    };
}
