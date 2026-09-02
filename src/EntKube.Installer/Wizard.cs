using System.Text.RegularExpressions;

namespace EntKube.Installer;

/// <summary>
/// The question flow.
///
/// Ordered by what an operator can answer without looking anything up, and kept as short as it can
/// honestly be. Two categories are deliberately *not* asked about:
///
///   SMTP     is optional and has an in-app answer that takes priority — a provider configured
///            under Tenants → Notification providers wins over the Smtp__* configuration keys,
///            which remain as a fallback. Neither is needed to install, and choosing between them
///            is a decision about how the deployment is run rather than how it is installed.
///   OIDC     has eight interdependent values and a provider-side registration step, which is a
///            documented procedure (docs/sso.md) rather than an install question. Existing OIDC
///            settings in an .env are preserved untouched.
///
/// Every answer defaults to the existing value when one is present, so re-running against an
/// installed directory is a safe way to change one thing.
/// </summary>
public sealed partial class Wizard(
    Prompt prompt,
    EnvFile existing,
    Options options,
    DetectedDeployment? detected = null)
{
    private readonly Prompt _prompt = prompt;
    private readonly EnvFile _existing = existing;
    private readonly Options _options = options;

    // What is actually deployed at the target. Used only where the .env does not already record an
    // answer — see the mode defaults below. An install this installer did not create has none of
    // those records, and defaulting instead of looking would drop whatever it does not know about.
    private readonly DetectedDeployment _detected = detected ?? DetectedDeployment.Nothing;

    public Answers Run(string directory, bool isUpgrade)
    {
        Answers answers = new()
        {
            Directory = directory,
            IsUpgrade = isUpgrade,

            // Carried regardless of the answers below: compose labels a network with the key it was
            // declared under and refuses a file that uses a different one, so this is not something
            // the wizard gets to have an opinion about.
            AdoptedNetwork = _detected.AdoptedNetwork,
        };

        // Flags become defaults rather than a separate input path. An interactive run then shows
        // them pre-filled and a non-interactive one simply takes them, so the two paths cannot
        // diverge in what a flag means. Done here rather than by the caller so that driving the
        // wizard from options is self-contained — a caller that forgot the step would silently get
        // a wizard that ignored every flag.
        foreach ((string key, string value) in _options.Seeded)
        {
            _existing.Set(key, value);
        }

        if (_detected.IsAdoption)
        {
            _prompt.Section("Existing deployment found");
            _prompt.Guidance(
                "This deployment was not created by this installer, so there are no recorded answers "
                + "to reuse. The defaults below were read from the deployment itself — its compose "
                + "file and its running containers — rather than assumed.");

            foreach (string finding in _detected.Findings)
            {
                _prompt.Note("- " + finding);
            }

            _prompt.Blank();
            _prompt.Warn("Installing replaces docker-compose.yml and Caddyfile (the old ones are kept as .bak).");
            _prompt.Note(
                "Hand edits to those files will not survive — move them to a docker-compose.override.yml, "
                + "which the installer never touches. Your .env is merged rather than replaced.");
        }
        else if (isUpgrade)
        {
            _prompt.Section("Existing install found");
            _prompt.Guidance(
                "A .env is already present in this directory, so its values are offered back as the "
                + "defaults below. Press Enter through anything you do not want to change. The vault "
                + "root key and the database password are reused as-is and are never regenerated.");
        }

        AskExposure(answers);
        AskDatabase(answers);
        AskVaultKey(answers);
        AskTelemetry(answers);
        AskImage(answers);
        AskAdmin(answers);

        return answers;
    }

    // ── How it is reached ────────────────────────────────────────────────────────────────────────

    private void AskExposure(Answers a)
    {
        _prompt.Section("How will this be reached?");

        string mode = _prompt.Choice(
            "Serve over HTTPS on a public domain, or on a local port?",
            [
                new Prompt.Option("tls", "Public domain with automatic HTTPS",
                    "Caddy binds 80 and 443 and gets a Let's Encrypt certificate on first start. "
                    + "Needs a DNS record already pointing at this host and both ports reachable "
                    + "from the internet."),
                new Prompt.Option("local", "Local port, no TLS",
                    "Publishes the app's own port with no reverse proxy. For evaluation, or behind "
                    + "a proxy you already run. Do not expose this to the internet — sessions and "
                    + "credentials would travel in clear text."),
            ],
            @default: _options.Exposure switch
            {
                Exposure.LocalPort => "local",
                Exposure.PublicTls => "tls",
                // No flag: whatever the last run recorded, falling back to the production shape.
                // Defaulting a fresh install to "local" would quietly stand up an untrusted
                // deployment for someone who pressed Enter through the wizard.
                _ => (_existing.Get("EXPOSE_MODE") ?? DetectedExposure()) == "local" ? "local" : "tls",
            });

        if (mode == "local")
        {
            a.Exposure = Exposure.LocalPort;
            a.LocalPort = int.Parse(_prompt.Text(
                "Port to publish on this host",
                @default: _existing.Get("LOCAL_PORT") ?? "8080",
                validate: v => int.TryParse(v, out int p) && p is > 0 and < 65536
                    ? null
                    : "Enter a port number between 1 and 65535."));
            return;
        }

        a.Exposure = Exposure.PublicTls;

        a.Domain = _prompt.Text(
            "Public domain for this server",
            @default: _existing.Get("DOMAIN") ?? _detected.Domain,
            note: "The name operators and clusters will use. A DNS A/AAAA record for it must already "
                + "resolve to this host — Let's Encrypt validates over HTTP on port 80, and issuance "
                + "fails if it does not.",
            validate: v => DomainPattern().IsMatch(v)
                ? null
                : "That does not look like a hostname. Enter something like entkube.example.com.");

        a.AcmeEmail = _prompt.Text(
            "Email for the Let's Encrypt account",
            @default: _existing.Get("ACME_EMAIL") ?? _detected.AcmeEmail,
            note: "Used for the ACME registration and expiry warnings. Not shown to users.",
            validate: v => v.Contains('@') && v.Length > 3 ? null : "Enter an email address.");
    }

    // ── Where state lives ────────────────────────────────────────────────────────────────────────

    private void AskDatabase(Answers a)
    {
        _prompt.Section("Database");

        // DATABASE_MODE, not DATABASE_PROVIDER: "Postgres" is true of both the bundled and the
        // external answer, and telling them apart from the connection string alone is guesswork.
        string existingDefault = _existing.Get("DATABASE_MODE") ?? DetectedDatabase() ?? "bundled";

        string choice = _prompt.Choice(
            "Which database?",
            [
                new Prompt.Option("bundled", "Bundled PostgreSQL",
                    "A postgres:17 container on a named volume, managed by this compose file. The "
                    + "right answer unless you already run Postgres."),
                new Prompt.Option("external", "External PostgreSQL",
                    "A Postgres you manage — a cloud service, or an existing server. You supply the "
                    + "connection string; nothing about it is created here."),
                new Prompt.Option("sqlite", "SQLite",
                    "A file on the app's data volume. No second container, but single-node only and "
                    + "not suitable for anything you would page someone about. Evaluation only."),
            ],
            @default: _options.Database switch
            {
                Database.Bundled => "bundled",
                Database.ExternalPostgres => "external",
                Database.Sqlite => "sqlite",
                _ => existingDefault,
            });

        switch (choice)
        {
            case "sqlite":
                a.Database = Database.Sqlite;
                _prompt.Warn("SQLite is for evaluation. Moving to Postgres later means migrating the data by hand.");
                break;

            case "external":
                a.Database = Database.ExternalPostgres;
                a.ExternalConnectionString = _prompt.Text(
                    "Npgsql connection string",
                    @default: _existing.Get("DATABASE_CONNECTION"),
                    note: "For example: Host=db.example.com;Port=5432;Database=entkube;Username=entkube;"
                        + "Password=… — the database must already exist; EntKube creates its own schema "
                        + "on first start but not the database itself.",
                    validate: v => v.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : "That does not look like an Npgsql connection string; it needs at least Host=.");
                break;

            default:
                a.Database = Database.Bundled;

                // Carried over exactly as found, so the generated service points at the same data
                // directory rather than at this installer's default one.
                a.AdoptedDatabase = _detected.AdoptedDatabase;

                // Only generated on a first install. Postgres applies POSTGRES_PASSWORD when the data
                // directory is initialised and never again, so a fresh password on an existing volume
                // leaves the server on the old one and the app unable to connect — with an
                // authentication error that points at neither.
                a.PostgresPassword = _existing.Get("POSTGRES_PASSWORD") ?? Secrets.Password();
                break;
        }
    }

    private void AskVaultKey(Answers a)
    {
        string? current = _existing.Get("VAULT__ROOTKEY");

        // Not a question. There is no good answer an operator can type, and the only dangerous
        // action — replacing an existing key — must not be reachable by pressing Enter at a prompt.
        if (current is not null && current != "REPLACE_WITH_BASE64_32_BYTE_KEY")
        {
            a.VaultRootKey = current;
            return;
        }

        a.VaultRootKey = Secrets.VaultRootKey();
    }

    // ── Telemetry ────────────────────────────────────────────────────────────────────────────────

    private void AskTelemetry(Answers a)
    {
        _prompt.Section("Telemetry storage");
        _prompt.Guidance(
            "EntKube's own logs, traces and RUM are indexed into segments that are sealed and moved "
            + "to object storage. The production answer is usually none of the below: register an S3 "
            + "StorageLink in the app afterwards and point telemetry at it, so its credentials live "
            + "in the vault rather than in a file on this host.");

        string existingDefault = _existing.Get("TELEMETRY_STORAGE") ?? DetectedTelemetry() ?? "disk";

        string choice = _prompt.Choice(
            "Where should sealed segments go for now?",
            [
                new Prompt.Option("disk", "Local disk",
                    "Under the app's data volume. Works immediately, single-node only, and bounded "
                    + "by this host's disk. Switchable later without losing anything."),
                new Prompt.Option("minio", "Bundled MinIO",
                    "Starts the compose file's MinIO and creates the bucket. Self-hosted object "
                    + "storage on this same host — better than local disk, still one machine."),
                new Prompt.Option("s3", "External S3-compatible bucket",
                    "An existing bucket, configured here. Credentials land in .env in clear text — "
                    + "a StorageLink in the app keeps them in the vault instead."),
            ],
            @default: _options.TelemetryStorage switch
            {
                TelemetryStorage.LocalDisk => "disk",
                TelemetryStorage.BundledMinio => "minio",
                TelemetryStorage.ExternalS3 => "s3",
                _ => existingDefault,
            });

        a.TelemetryRetentionDays = int.Parse(_prompt.Text(
            "Days of telemetry to retain",
            @default: _existing.Get("TELEMETRY_RETENTION_DAYS") ?? "14",
            note: "Sealed segments older than this are dropped.",
            validate: v => int.TryParse(v, out int d) && d is > 0 and <= 3650
                ? null
                : "Enter a number of days between 1 and 3650."));

        switch (choice)
        {
            case "minio":
                a.TelemetryStorage = TelemetryStorage.BundledMinio;
                a.MinioUser = _existing.Get("MINIO_ROOT_USER") ?? "entkube";
                a.MinioPassword = _existing.Get("MINIO_ROOT_PASSWORD") ?? Secrets.Password();
                a.S3Bucket = _existing.Get("TELEMETRY_BUCKET") ?? "entkube-telemetry";
                break;

            case "s3":
                a.TelemetryStorage = TelemetryStorage.ExternalS3;
                a.S3Endpoint = _prompt.Text("S3 endpoint URL",
                    @default: _existing.Get("TELEMETRY_S3_ENDPOINT"),
                    note: "For example https://s3.eu-north-1.amazonaws.com, or your provider's endpoint.");
                a.S3Bucket = _prompt.Text("Bucket name", @default: _existing.Get("TELEMETRY_BUCKET"));
                a.S3Region = _prompt.Text("Region", @default: _existing.Get("TELEMETRY_S3_REGION") ?? "us-east-1");
                a.S3AccessKey = _prompt.Text("Access key", @default: _existing.Get("TELEMETRY_S3_ACCESS_KEY"));
                a.S3SecretKey = _existing.Get("TELEMETRY_S3_SECRET_KEY")
                    ?? _prompt.Secret("Secret key");
                break;

            default:
                a.TelemetryStorage = TelemetryStorage.LocalDisk;
                break;
        }
    }

    // ── Image ────────────────────────────────────────────────────────────────────────────────────

    private void AskImage(Answers a)
    {
        a.Registry = _existing.Get("REGISTRY") ?? "entit.azurecr.io";
        a.ImageTag = _existing.Get("IMAGE_TAG") ?? "latest";
        a.RegistryUsername = _existing.Get("REGISTRY_USERNAME");
        a.RegistryPassword = _existing.Get("REGISTRY_PASSWORD");

        // entit.azurecr.io is public, so the common case needs nothing here and is not worth a
        // question. Only offered when the operator has already moved off the default registry, or
        // asks for it.
        if (a.Registry == "entit.azurecr.io" && a.RegistryUsername is null && !_prompt.Interactive)
        {
            return;
        }

        _prompt.Section("Image registry");

        if (!_prompt.YesNo("Change the image registry or its credentials?", @default: a.RegistryUsername is not null,
                note: "The default registry (entit.azurecr.io) is public — pulls need no login. Answer "
                    + "yes only if you mirror the images yourself, or you have been given credentials "
                    + "for EntKube-published cluster components."))
        {
            return;
        }

        a.Registry = _prompt.Text("Registry host", @default: a.Registry);
        a.ImageTag = _prompt.Text("Image tag", @default: a.ImageTag,
            note: "'latest' tracks the newest build. Pin a short commit SHA to hold a known version.");

        a.RegistryUsername = _prompt.OptionalText("Registry username", @default: a.RegistryUsername,
            note: "Leave empty for an anonymous pull. Enter '-' to clear an existing value.");

        if (a.RegistryUsername is not null)
        {
            a.RegistryPassword = a.RegistryPassword is not null
                && _prompt.YesNo("Keep the existing registry password?", @default: true)
                    ? a.RegistryPassword
                    : _prompt.Secret("Registry password");
        }
        else
        {
            a.RegistryPassword = null;
        }
    }

    // ── Admin ────────────────────────────────────────────────────────────────────────────────────

    private void AskAdmin(Answers a)
    {
        _prompt.Section("Administrator");

        a.SeedAdminEmail = _prompt.OptionalText(
            "Email to always grant the Admin role",
            @default: _existing.Get("SEED_ADMIN_EMAIL"),
            note: "Optional but recommended. This address is granted Admin on every startup, which is "
                + "the way back in if the last admin account is lost. You still register the account "
                + "itself through the web UI on first visit — no password is set here. Enter '-' to clear.");

        if (a.SeedAdminEmail is not null && !a.SeedAdminEmail.Contains('@'))
        {
            _prompt.Warn($"'{a.SeedAdminEmail}' does not look like an email address; it is being saved as given.");
        }
    }

    private string? DetectedExposure() => _detected.Exposure switch
    {
        Exposure.LocalPort => "local",
        Exposure.PublicTls => "tls",
        _ => null,
    };

    private string? DetectedDatabase() => _detected.Database switch
    {
        Database.Bundled => "bundled",
        Database.ExternalPostgres => "external",
        Database.Sqlite => "sqlite",
        _ => null,
    };

    private string? DetectedTelemetry() => _detected.TelemetryStorage switch
    {
        TelemetryStorage.BundledMinio => "minio",
        TelemetryStorage.ExternalS3 => "s3",
        TelemetryStorage.LocalDisk => "disk",
        _ => null,
    };

    // Hostname, not a URL: labels of alphanumerics and hyphens, at least two of them. Deliberately
    // loose about TLDs — internal domains are legitimate here and an allowlist would reject them.
    [GeneratedRegex(@"^(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))+$")]
    private static partial Regex DomainPattern();
}
