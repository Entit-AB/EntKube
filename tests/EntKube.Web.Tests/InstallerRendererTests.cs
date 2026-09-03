using EntKube.Installer;

namespace EntKube.Web.Tests;

/// <summary>
/// Covers the installer's rendering, and specifically the parts whose failure is silent.
///
/// Two properties matter more than the rest and are the reason this file exists:
///
///   A re-run must not regenerate the vault root key. A new key does not fail — the app starts
///   normally and every secret in the vault decrypts to nothing. Nothing else in the system
///   notices, so a test has to.
///
///   A re-run must not regenerate the bundled database password. Postgres applies POSTGRES_PASSWORD
///   only when the volume is first initialised, so a new one leaves the server on the old password
///   and the app unable to connect.
///
/// The structural cases are covered too, because the service list is derived rather than fixed: an
/// external database that still emits a postgres service would leave an idle container holding a
/// volume that looks like it holds the data and does not.
/// </summary>
public class InstallerRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "entkube-installer-tests", Guid.NewGuid().ToString("N"));

    public InstallerRendererTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private Answers Tls() => new()
    {
        Directory = _dir,
        Exposure = Exposure.PublicTls,
        Domain = "entkube.example.com",
        AcmeEmail = "ops@example.com",
        Database = Database.Bundled,
        PostgresPassword = "pg-password",
        VaultRootKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
    };

    /// <summary>
    /// Writes through a LocalExecutor, which is the same path a real local install takes — so the
    /// backup and permission behaviour under test is the behaviour that ships, not a stand-in.
    /// </summary>
    private void Write(Answers answers, EnvFile? env = null)
    {
        using LocalExecutor executor = new(_dir);
        new Renderer(answers).Write(executor, env ?? new EnvFile());
    }

    private string WriteAndReadEnv(Answers answers, EnvFile? env = null)
    {
        Write(answers, env);
        return File.ReadAllText(Path.Combine(_dir, ".env"));
    }

    private string WriteAndReadCompose(Answers answers, EnvFile? env = null)
    {
        Write(answers, env);
        return File.ReadAllText(Path.Combine(_dir, "docker-compose.yml"));
    }

    // ── The two values that must never be regenerated ────────────────────────────────────────────

    [Fact]
    public void Rerunning_keeps_the_existing_vault_root_key()
    {
        Answers first = Tls();
        WriteAndReadEnv(first);

        EnvFile reloaded = EnvFile.Load(Path.Combine(_dir, ".env"));
        string original = reloaded.Get("VAULT__ROOTKEY")!;

        // What the wizard does on a re-run: take the key it finds rather than inventing one.
        Answers second = Tls();
        second.VaultRootKey = reloaded.Get("VAULT__ROOTKEY")!;

        string env = WriteAndReadEnv(second, reloaded);

        Assert.Contains($"VAULT__ROOTKEY={original}", env);
    }

    [Fact]
    public void Wizard_reuses_an_existing_vault_key_rather_than_generating_one()
    {
        EnvFile existing = new();
        existing.Set("VAULT__ROOTKEY", "an-existing-key");
        existing.Set("DATABASE_MODE", "bundled");
        existing.Set("POSTGRES_PASSWORD", "an-existing-password");
        existing.Set("DOMAIN", "entkube.example.com");
        existing.Set("ACME_EMAIL", "ops@example.com");

        Answers answers = new Wizard(new Prompt(interactive: false), existing, Options.Parse([]))
            .Run(_dir, isUpgrade: true);

        Assert.Equal("an-existing-key", answers.VaultRootKey);
        Assert.Equal("an-existing-password", answers.PostgresPassword);
    }

    [Fact]
    public void Wizard_generates_a_vault_key_only_when_there_is_none()
    {
        Answers answers = new Wizard(new Prompt(interactive: false), new EnvFile(), Options.Parse(
                ["--domain", "entkube.example.com", "--acme-email", "ops@example.com"]))
            .Run(_dir, isUpgrade: false);

        // 32 bytes, base64 — the app rejects any other length.
        Assert.Equal(32, Convert.FromBase64String(answers.VaultRootKey).Length);
    }

    [Fact]
    public void The_placeholder_key_from_the_sample_env_is_replaced_not_kept()
    {
        // .env.example ships this literal. Carrying it forward would give every install that copied
        // the sample the same vault key, which is worse than having none.
        EnvFile existing = new();
        existing.Set("VAULT__ROOTKEY", "REPLACE_WITH_BASE64_32_BYTE_KEY");

        Answers answers = new Wizard(new Prompt(interactive: false), existing, Options.Parse(
                ["--domain", "entkube.example.com", "--acme-email", "ops@example.com"]))
            .Run(_dir, isUpgrade: true);

        Assert.NotEqual("REPLACE_WITH_BASE64_32_BYTE_KEY", answers.VaultRootKey);
        Assert.Equal(32, Convert.FromBase64String(answers.VaultRootKey).Length);
    }

    // ── The structural choices are recorded, not inferred ────────────────────────────────────────

    [Fact]
    public void The_deployment_shape_is_written_down_so_a_rerun_cannot_misread_it()
    {
        // Regression: the mode used to be inferred from DATABASE_CONNECTION, which a bundled install
        // writes exactly like an external one. A re-run therefore read its own output as "external",
        // dropped the postgres service, and left the app pointing at a host that no longer existed.
        string env = WriteAndReadEnv(Tls());

        Assert.Contains("DATABASE_MODE=bundled", env);
        Assert.Contains("EXPOSE_MODE=tls", env);
        Assert.Contains("TELEMETRY_STORAGE=disk", env);

        EnvFile reloaded = EnvFile.Load(Path.Combine(_dir, ".env"));
        Answers rerun = new Wizard(new Prompt(interactive: false), reloaded, Options.Parse([]))
            .Run(_dir, isUpgrade: true);

        Assert.Equal(Database.Bundled, rerun.Database);
        Assert.Equal(Exposure.PublicTls, rerun.Exposure);
    }

    // ── Services follow the answers ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_bundled_database_gets_a_postgres_service_and_a_health_gated_dependency()
    {
        string compose = WriteAndReadCompose(Tls());

        Assert.Contains("postgres:17", compose);
        Assert.Contains("condition: service_healthy", compose);
        Assert.Contains("postgres-data:", compose);
    }

    [Fact]
    public void An_external_database_emits_no_postgres_service_and_no_dependency_on_one()
    {
        Answers a = Tls();
        a.Database = Database.ExternalPostgres;
        a.ExternalConnectionString = "Host=db.example.com;Database=entkube;Username=u;Password=p";

        string compose = WriteAndReadCompose(a);

        Assert.DoesNotContain("postgres:17", compose);
        Assert.DoesNotContain("condition: service_healthy", compose);
        Assert.DoesNotContain("postgres-data:", compose);
        Assert.DoesNotContain("postgres", a.Services);
    }

    [Fact]
    public void Sqlite_emits_no_database_service_at_all()
    {
        Answers a = Tls();
        a.Database = Database.Sqlite;

        string compose = WriteAndReadCompose(a);
        string env = File.ReadAllText(Path.Combine(_dir, ".env"));

        Assert.DoesNotContain("postgres:17", compose);
        Assert.Contains("DATABASE_PROVIDER=Sqlite", env);
        Assert.Contains("Data Source=/app/Data/app.db", env);
        Assert.Equal(["entkube", "caddy"], a.Services);
    }

    [Fact]
    public void Serving_on_a_local_port_drops_caddy_and_publishes_the_app_instead()
    {
        Answers a = Tls();
        a.Exposure = Exposure.LocalPort;
        a.LocalPort = 8080;

        string compose = WriteAndReadCompose(a);

        Assert.DoesNotContain("caddy:2", compose);
        Assert.DoesNotContain("caddy-data:", compose);
        Assert.Contains("\"${LOCAL_PORT}:8080\"", compose);
        Assert.Equal([8080], a.PublishedPorts);
    }

    [Fact]
    public void Forwarded_headers_are_trusted_only_when_a_proxy_is_actually_in_front()
    {
        // Honouring X-Forwarded-* with no proxy lets any direct caller claim its own scheme and
        // client address, which is what the header is used to decide.
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED: \"true\"", WriteAndReadCompose(Tls()));

        Answers local = Tls();
        local.Exposure = Exposure.LocalPort;

        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED: \"false\"", WriteAndReadCompose(local));
    }

    [Fact]
    public void Bundled_minio_brings_its_bucket_initialiser_and_its_volume()
    {
        Answers a = Tls();
        a.TelemetryStorage = TelemetryStorage.BundledMinio;
        a.MinioUser = "entkube";
        a.MinioPassword = "minio-password";
        a.S3Bucket = "entkube-telemetry";

        string compose = WriteAndReadCompose(a);

        Assert.Contains("minio/minio:latest", compose);
        Assert.Contains("minio/mc:latest", compose);
        Assert.Contains("minio-data:", compose);
        Assert.Contains("Telemetry__ObjectStorage__ForcePathStyle: \"true\"", compose);
        Assert.Contains("minio-init", a.Services);
    }

    [Fact]
    public void The_registry_config_key_matches_the_registry_that_was_chosen()
    {
        // The key is the host with dots replaced by underscores. A hard-coded entit_azurecr_io would
        // leave a private mirror's credentials in a key nothing reads, and the pull would fail with
        // an authentication error while the credentials sat right there in the file.
        Answers a = Tls();
        a.Registry = "registry.internal.example.com";

        string compose = WriteAndReadCompose(a);

        Assert.Contains("Helm__Registries__registry_internal_example_com__Username", compose);
        Assert.DoesNotContain("Helm__Registries__entit_azurecr_io__", compose);
    }

    // ── .env handling ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_the_installer_does_not_manage_survive_a_rerun()
    {
        WriteAndReadEnv(Tls());

        string path = Path.Combine(_dir, ".env");
        File.AppendAllText(path, "\nOIDC_ENABLED=true\nSOMETHING_HAND_ADDED=keep-me\n");

        EnvFile reloaded = EnvFile.Load(path);
        string env = WriteAndReadEnv(Tls(), reloaded);

        Assert.Contains("OIDC_ENABLED=true", env);
        Assert.Contains("SOMETHING_HAND_ADDED=keep-me", env);
    }

    [Fact]
    public void A_value_containing_a_hash_is_quoted()
    {
        // Compose truncates an unquoted value at '#', and the resulting authentication failure names
        // neither the character nor the file.
        Answers a = Tls();
        a.PostgresPassword = "pass#word with spaces";

        string env = WriteAndReadEnv(a);

        Assert.Contains("POSTGRES_PASSWORD=\"pass#word with spaces\"", env);
    }

    [Fact]
    public void Quotes_do_not_accumulate_across_reruns()
    {
        Answers a = Tls();
        a.PostgresPassword = "pass#word";

        WriteAndReadEnv(a);
        EnvFile reloaded = EnvFile.Load(Path.Combine(_dir, ".env"));

        Assert.Equal("pass#word", reloaded.Get("POSTGRES_PASSWORD"));
    }

    [Fact]
    public void Replacing_a_file_leaves_the_previous_one_behind_as_a_backup()
    {
        WriteAndReadCompose(Tls());

        Answers changed = Tls();
        changed.Database = Database.Sqlite;
        WriteAndReadCompose(changed);

        Assert.NotEmpty(Directory.GetFiles(_dir, "docker-compose.yml.*.bak"));
    }

    [Fact]
    public void Switching_away_from_the_bundled_database_and_back_keeps_the_original_password()
    {
        // Postgres applies POSTGRES_PASSWORD only when the volume is first initialised. If moving to
        // SQLite dropped the password, moving back would generate a fresh one against a
        // postgres-data volume that still holds the original — which cannot be reconciled.
        Answers bundled = Tls();
        WriteAndReadEnv(bundled);

        EnvFile afterFirst = EnvFile.Load(Path.Combine(_dir, ".env"));
        string original = afterFirst.Get("POSTGRES_PASSWORD")!;

        Answers sqlite = Tls();
        sqlite.Database = Database.Sqlite;
        WriteAndReadEnv(sqlite, afterFirst);

        EnvFile afterSwitch = EnvFile.Load(Path.Combine(_dir, ".env"));
        Assert.Equal(original, afterSwitch.Get("POSTGRES_PASSWORD"));

        // And the wizard picks it back up rather than inventing one.
        afterSwitch.Set("DATABASE_MODE", "bundled");
        Answers back = new Wizard(new Prompt(interactive: false), afterSwitch, Options.Parse([]))
            .Run(_dir, isUpgrade: true);

        Assert.Equal(original, back.PostgresPassword);
    }

    [Fact]
    public void Switching_away_from_bundled_minio_and_back_keeps_its_root_credentials()
    {
        Answers minio = Tls();
        minio.TelemetryStorage = TelemetryStorage.BundledMinio;
        minio.MinioUser = "entkube";
        minio.MinioPassword = "minio-original";
        minio.S3Bucket = "entkube-telemetry";
        WriteAndReadEnv(minio);

        EnvFile afterFirst = EnvFile.Load(Path.Combine(_dir, ".env"));

        Answers disk = Tls();
        WriteAndReadEnv(disk, afterFirst);

        EnvFile afterSwitch = EnvFile.Load(Path.Combine(_dir, ".env"));
        Assert.Equal("minio-original", afterSwitch.Get("MINIO_ROOT_PASSWORD"));
    }

    // ── Generated secrets ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generated_passwords_avoid_the_characters_that_are_syntax_downstream()
    {
        // These land in a dotenv file, a compose interpolation and a connection string, and each of
        // those treats at least one of them as syntax.
        for (int i = 0; i < 200; i++)
        {
            string password = Secrets.Password();

            Assert.Equal(32, password.Length);
            Assert.DoesNotContain('#', password);
            Assert.DoesNotContain('$', password);
            Assert.DoesNotContain('"', password);
            Assert.DoesNotContain('\'', password);
            Assert.DoesNotContain('\\', password);
            Assert.DoesNotContain(';', password);
        }
    }

    [Fact]
    public void Generated_vault_keys_are_32_bytes_and_not_repeated()
    {
        HashSet<string> seen = [];

        for (int i = 0; i < 100; i++)
        {
            string key = Secrets.VaultRootKey();
            Assert.Equal(32, Convert.FromBase64String(key).Length);
            Assert.True(seen.Add(key));
        }
    }

    // ── Option parsing ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("--expose", "sideways")]
    [InlineData("--database", "mysql")]
    [InlineData("--telemetry-storage", "tape")]
    public void An_invalid_choice_is_rejected_by_name(string flag, string value)
    {
        InstallAbortedException ex = Assert.Throws<InstallAbortedException>(
            () => Options.Parse([flag, value]));

        Assert.Contains(value, ex.Message);
    }

    [Fact]
    public void A_flag_with_no_value_is_rejected_rather_than_silently_ignored()
    {
        Assert.Throws<InstallAbortedException>(() => Options.Parse(["--domain"]));
    }

    [Fact]
    public void A_non_interactive_run_with_no_answer_and_no_default_stops()
    {
        // --expose tls needs a domain. Guessing one would produce a certificate order for a name
        // that is not this host.
        Assert.Throws<InstallAbortedException>(
            () => new Wizard(new Prompt(interactive: false), new EnvFile(), Options.Parse([]))
                .Run(_dir, isUpgrade: false));
    }
}
