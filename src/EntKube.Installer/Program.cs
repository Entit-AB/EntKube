namespace EntKube.Installer;

/// <summary>
/// The EntKube management-plane installer.
///
/// Stands up the management plane on a server: checks the host, asks what is needed, writes the
/// compose file and .env, pulls the images and starts everything. It is one self-contained binary
/// so it can be copied to a bare host and run — there is no runtime to install first, which is the
/// step it exists to remove.
///
/// It is also safe to re-run. An existing .env is read before anything is asked, every answer is
/// offered back as its default, and the two values that must never be regenerated — the vault root
/// key and the bundled database password — are reused without being mentioned. Re-running is the
/// supported way to change one setting or to move to a newer image.
///
/// Exit codes:
///   0  installed, or rendered with --dry-run
///   1  the install failed
///   2  the command was invoked wrongly, or the host is not ready
///   3  cancelled at the confirmation
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;
    private const int ExitCancelled = 3;

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage(Console.Out);
            return ExitOk;
        }

        try
        {
            return await RunAsync(args);
        }
        catch (InstallAbortedException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"entkube-install: {ex.Message}");
            return ExitUsage;
        }
        catch (Exception ex)
        {
            // Anything reaching here is a defect rather than a misconfiguration, so it keeps its
            // type and message — an operator forwarding this needs it to be diagnosable.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"entkube-install: unexpected {ex.GetType().Name}: {ex.Message}");
            return ExitFailure;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        Options options = Options.Parse(args);
        Prompt prompt = new(options.Interactive);

        prompt.Info("EntKube management-plane installer");
        prompt.Info(options.DryRun
            ? "Rendering configuration only — nothing will be pulled or started."
            : $"Installing into {options.Directory}");

        using IExecutor executor = new LocalExecutor(options.Directory);
        InstallRunner runner = new(executor, new ConsoleInstallLog(prompt));

        prompt.Heading("Checking prerequisites");

        // Tooling is checked before the wizard, not after. Asking eight questions and then
        // announcing that docker is missing wastes the operator's time and their answers.
        if (options.DryRun)
        {
            executor.EnsureWritableDirectory(options.Directory);
            prompt.Step("install directory", options.Directory);
        }
        else
        {
            runner.CheckHost(options.Directory);
        }

        bool isUpgrade = InstallRunner.IsUpgrade(executor, options.Directory);
        EnvFile env = InstallRunner.LoadExisting(executor, options.Directory);

        // What is actually deployed here, read before any answer is offered. A deployment this
        // installer did not create records none of its own answers, and defaulting rather than
        // looking is how a running service gets dropped from the regenerated compose file.
        DetectedDeployment detected = runner.Inspect(options.Directory);

        // The wizard applies the flag values itself — see Wizard.Run.
        Answers answers = new Wizard(prompt, env, options, detected).Run(options.Directory, isUpgrade);

        Review(prompt, answers, isUpgrade);
        runner.ReportPlan(detected, answers);

        if (!options.AssumeYes && !prompt.YesNo(
                isUpgrade ? "Apply these changes?" : "Write these files and start EntKube?", @default: true))
        {
            prompt.Blank();
            prompt.Info("Cancelled. Nothing was written.");
            return ExitCancelled;
        }

        prompt.Heading("Writing configuration");

        if (!runner.Apply(answers, env, skipStart: options.DryRun, detected: detected))
        {
            return ExitFailure;
        }

        if (options.DryRun)
        {
            prompt.Blank();
            prompt.Info("Dry run: files written, nothing started.");
            prompt.Info($"Start it yourself with:  cd {options.Directory} && docker compose up -d");
            return ExitOk;
        }

        prompt.Heading("Waiting for EntKube to answer");
        await Task.Run(() => runner.WaitForHttp(answers));

        Finish(prompt, answers, isUpgrade);

        return ExitOk;
    }

    // ── Review ───────────────────────────────────────────────────────────────────────────────────

    private static void Review(Prompt prompt, Answers a, bool isUpgrade)
    {
        prompt.Heading(isUpgrade ? "Review changes" : "Review");

        prompt.Step("URL", a.PublicUrl);
        prompt.Step("TLS", a.Exposure == Exposure.PublicTls
            ? "Caddy, automatic Let's Encrypt"
            : "none — HTTP on a published port");

        prompt.Step("Database", a.Database switch
        {
            Database.Bundled when a.AdoptedDatabase is { } db => $"existing — {db.Describe}",
            Database.Bundled => "bundled PostgreSQL 17, on the postgres-data volume",
            Database.ExternalPostgres => "external PostgreSQL",
            _ => "SQLite on the entkube-data volume",
        });

        prompt.Step("Telemetry storage", a.TelemetryStorage switch
        {
            TelemetryStorage.BundledMinio => $"bundled MinIO, bucket {a.S3Bucket}",
            TelemetryStorage.ExternalS3 => $"{a.S3Endpoint} bucket {a.S3Bucket}",
            _ => "local disk on the entkube-data volume",
        });

        prompt.Step("Telemetry retention", $"{a.TelemetryRetentionDays} days");
        prompt.Step("Image", $"{a.Registry}/entkube:{a.ImageTag}");
        prompt.Step("Registry auth", a.RegistryUsername is null ? "anonymous" : a.RegistryUsername);
        prompt.Step("Seeded admin", a.SeedAdminEmail ?? "none");
        prompt.Step("Vault root key", a.VaultRootKey.Length > 0 && isUpgrade
            ? "reusing the existing key"
            : "generated — back up .env");
        prompt.Step("Services", string.Join(", ", a.Services));

        if (a.Exposure == Exposure.LocalPort)
        {
            prompt.Blank();
            prompt.Warn("No TLS. Traffic, including sign-in credentials and session cookies, is unencrypted.");
            prompt.Note("Only expose this on a trusted network, or put your own proxy in front of it.");
        }

        if (a.Database == Database.Sqlite)
        {
            prompt.Blank();
            prompt.Warn("SQLite is single-node and intended for evaluation.");
        }
    }

    // ── Finish ───────────────────────────────────────────────────────────────────────────────────

    private static void Finish(Prompt prompt, Answers a, bool isUpgrade)
    {
        prompt.Heading(isUpgrade ? "Updated" : "Installed");

        prompt.Info($"  EntKube is at  {a.PublicUrl}");
        prompt.Blank();

        if (!isUpgrade)
        {
            prompt.Info("  Next:");
            prompt.Info("    1. Open the URL and register the first account. Registration is open until");
            prompt.Info("       you turn it off, so do this before the host is reachable by anyone else.");

            if (a.SeedAdminEmail is not null)
            {
                prompt.Info($"    2. Register as {a.SeedAdminEmail} — that address is granted Admin on every start.");
            }
            else
            {
                prompt.Info("    2. Grant that account the Admin role, then set SEED_ADMIN_EMAIL in .env so");
                prompt.Info("       you can recover access if it is ever lost.");
            }

            prompt.Info("    3. Turn off open registration in Admin once your accounts exist.");
            prompt.Blank();
        }

        prompt.Warn("Back up .env somewhere other than this host.");
        prompt.Note(
            "It holds VAULT__ROOTKEY, which encrypts every secret in the vault. Without that exact key "
            + "a restored database is unreadable — there is no recovery path and no way to re-derive it.");

        prompt.Blank();
        prompt.Info($"  Logs:     cd {a.Directory} && docker compose logs -f entkube");
        prompt.Info($"  Restart:  cd {a.Directory} && docker compose restart entkube");
        prompt.Info($"  Update:   entkube-install --directory {a.Directory}   (re-run; answers default to today's)");
    }

    private static void PrintUsage(TextWriter w) => w.Write(
        """
        entkube-install — install the EntKube management plane on this host.

        Usage:
          entkube-install [options]

        Runs a wizard by default, writes docker-compose.yml, Caddyfile and .env into the install
        directory, then pulls the images and starts everything. Safe to re-run: an existing .env is
        read first and every answer defaults to what is already there.

        General:
          -C, --directory <path>     Where to install. Default: the current directory.
              --dry-run              Write the files, start nothing.
          -y, --yes                  Do not ask for confirmation before applying.
              --non-interactive      Ask nothing. Every answer must come from a flag or an existing
                                     .env; anything missing is an error rather than a guess.
          -h, --help                 This text.

        Answers (all optional — each is the default for its wizard question):
              --expose tls|local     HTTPS on a public domain via Caddy, or a plain published port.
              --domain <host>        Public domain, for --expose tls.
              --acme-email <email>   Let's Encrypt account address.
              --local-port <port>    Host port, for --expose local. Default 8080.

              --database bundled|external|sqlite
              --connection-string <s>   Npgsql connection string, for --database external.

              --telemetry-storage disk|minio|s3
              --retention-days <n>      Days of telemetry to keep. Default 14.
              --s3-endpoint <url>       For --telemetry-storage s3.
              --s3-bucket <name>
              --s3-region <name>
              --s3-access-key <key>
              --s3-secret-key <key>

              --registry <host>         Default entit.azurecr.io, which is public.
              --image-tag <tag>         Default 'latest'. A short commit SHA pins a known build.
              --registry-username <u>   Only for a private registry.
              --registry-password <p>
              --seed-admin <email>      Granted Admin on every startup. The way back in.

        Examples:
          entkube-install
          entkube-install --directory /opt/entkube
          entkube-install --non-interactive --directory /opt/entkube \
            --domain entkube.example.com --acme-email ops@example.com --seed-admin ops@example.com

        The vault root key and the bundled database password are generated on a first install and
        reused afterwards. They are never regenerated: a new vault key orphans every stored secret,
        and a new database password does not reach an already-initialised Postgres volume.

        See docs/installing.md.

        """);
}
