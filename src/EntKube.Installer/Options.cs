namespace EntKube.Installer;

/// <summary>
/// Command-line options.
///
/// Most of these exist to make a scripted install possible: with <c>--non-interactive</c> the wizard
/// asks nothing and every answer has to come from somewhere, either a flag here or an existing .env.
/// They are the same answers the wizard collects, so the two paths reach an identical renderer rather
/// than one being a reduced version of the other.
/// </summary>
public sealed class Options
{
    public string Directory { get; private set; } = Environment.CurrentDirectory;

    public bool Interactive { get; private set; } = true;

    /// <summary>Render the files and stop, without pulling or starting anything.</summary>
    public bool DryRun { get; private set; }

    /// <summary>Skip the confirmation before applying. Implied by <c>--non-interactive</c>.</summary>
    public bool AssumeYes { get; private set; }

    public Exposure? Exposure { get; private set; }

    public Database? Database { get; private set; }

    public TelemetryStorage? TelemetryStorage { get; private set; }

    /// <summary>
    /// Flag values that are simply defaults for a wizard question. Seeded into the env file the
    /// wizard reads, so a flag and a value left over from a previous install take the same path.
    /// </summary>
    public Dictionary<string, string> Seeded { get; } = new(StringComparer.Ordinal);

    public static Options Parse(string[] args)
    {
        Options o = new();

        string Value(ref int i, string flag) => ++i < args.Length
            ? args[i]
            : throw new InstallAbortedException($"{flag} needs a value.");

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--directory" or "-C": o.Directory = Path.GetFullPath(Value(ref i, args[i])); break;
                case "--non-interactive": o.Interactive = false; o.AssumeYes = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--yes" or "-y": o.AssumeYes = true; break;

                case "--expose":
                    o.Exposure = Value(ref i, "--expose") switch
                    {
                        "tls" => Installer.Exposure.PublicTls,
                        "local" => Installer.Exposure.LocalPort,
                        var v => throw new InstallAbortedException($"--expose must be tls or local, not '{v}'."),
                    };
                    break;

                case "--database":
                    o.Database = Value(ref i, "--database") switch
                    {
                        "bundled" => Installer.Database.Bundled,
                        "external" => Installer.Database.ExternalPostgres,
                        "sqlite" => Installer.Database.Sqlite,
                        var v => throw new InstallAbortedException(
                            $"--database must be bundled, external or sqlite, not '{v}'."),
                    };
                    break;

                case "--telemetry-storage":
                    o.TelemetryStorage = Value(ref i, "--telemetry-storage") switch
                    {
                        "disk" => Installer.TelemetryStorage.LocalDisk,
                        "minio" => Installer.TelemetryStorage.BundledMinio,
                        "s3" => Installer.TelemetryStorage.ExternalS3,
                        var v => throw new InstallAbortedException(
                            $"--telemetry-storage must be disk, minio or s3, not '{v}'."),
                    };
                    break;

                case "--domain": o.Seeded["DOMAIN"] = Value(ref i, "--domain"); break;
                case "--acme-email": o.Seeded["ACME_EMAIL"] = Value(ref i, "--acme-email"); break;
                case "--local-port": o.Seeded["LOCAL_PORT"] = Value(ref i, "--local-port"); break;
                case "--connection-string": o.Seeded["DATABASE_CONNECTION"] = Value(ref i, "--connection-string"); break;
                case "--retention-days": o.Seeded["TELEMETRY_RETENTION_DAYS"] = Value(ref i, "--retention-days"); break;
                case "--registry": o.Seeded["REGISTRY"] = Value(ref i, "--registry"); break;
                case "--image-tag": o.Seeded["IMAGE_TAG"] = Value(ref i, "--image-tag"); break;
                case "--registry-username": o.Seeded["REGISTRY_USERNAME"] = Value(ref i, "--registry-username"); break;
                case "--registry-password": o.Seeded["REGISTRY_PASSWORD"] = Value(ref i, "--registry-password"); break;
                case "--seed-admin": o.Seeded["SEED_ADMIN_EMAIL"] = Value(ref i, "--seed-admin"); break;
                case "--s3-endpoint": o.Seeded["TELEMETRY_S3_ENDPOINT"] = Value(ref i, "--s3-endpoint"); break;
                case "--s3-bucket": o.Seeded["TELEMETRY_BUCKET"] = Value(ref i, "--s3-bucket"); break;
                case "--s3-region": o.Seeded["TELEMETRY_S3_REGION"] = Value(ref i, "--s3-region"); break;
                case "--s3-access-key": o.Seeded["TELEMETRY_S3_ACCESS_KEY"] = Value(ref i, "--s3-access-key"); break;
                case "--s3-secret-key": o.Seeded["TELEMETRY_S3_SECRET_KEY"] = Value(ref i, "--s3-secret-key"); break;

                default:
                    throw new InstallAbortedException($"Unknown option '{args[i]}'. Try --help.");
            }
        }

        return o;
    }
}
