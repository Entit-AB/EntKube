using EntKube.Installer;
using EntKube.Installer.Gui.ViewModels;

namespace EntKube.Web.Tests;

/// <summary>
/// Adopting a deployment the installer did not create.
///
/// The failure this guards against is specific and quiet. A deployment stood up by hand records none
/// of the answers the installer writes, so every structural question used to fall back to a default
/// — and the default for telemetry storage is "local disk". Applying that to a deployment running a
/// bundled MinIO regenerates a compose file without it, taking a running service away with no
/// mention of it anywhere.
///
/// So the shape is read from the deployment instead, and anything about to disappear is reported
/// before it does.
/// </summary>
public class InstallerAdoptionTests
{
    private static DetectedDeployment HandRolled(
        Exposure? exposure = Exposure.PublicTls,
        Database? database = Database.Bundled,
        TelemetryStorage? telemetry = TelemetryStorage.BundledMinio,
        string[]? running = null,
        string[]? defined = null) => new()
        {
            Exists = true,
            InstallerOwned = false,
            HasComposeFile = true,
            Exposure = exposure,
            Database = database,
            TelemetryStorage = telemetry,
            RunningServices = running ?? [],
            DefinedServices = defined ?? ["entkube", "postgres", "caddy", "minio", "minio-init"],
        };

    private static ConfigurationViewModel Seeded(EnvFile env, DetectedDeployment detected)
    {
        ConfigurationViewModel vm = new();
        vm.SeedFrom(env, isUpgrade: true, detected);
        return vm;
    }

    // ── Detection fills the gap that recorded answers would have ─────────────────────────────────

    [Fact]
    public void A_hand_rolled_deployment_is_recognised_as_an_adoption()
    {
        Assert.True(HandRolled().IsAdoption);

        DetectedDeployment ours = new() { Exists = true, InstallerOwned = true };
        Assert.False(ours.IsAdoption);
    }

    [Fact]
    public void A_running_minio_is_not_silently_dropped()
    {
        // The regression, stated directly: with no TELEMETRY_STORAGE recorded, the answer must come
        // from the deployment rather than from the default.
        ConfigurationViewModel vm = Seeded(new EnvFile(), HandRolled());

        Assert.Equal("minio", vm.TelemetryStorage);
        Assert.Contains("minio", vm.ToAnswers("/home/ubuntu", true).Services);
    }

    [Fact]
    public void A_detected_sqlite_deployment_does_not_become_a_bundled_postgres_one()
    {
        ConfigurationViewModel vm = Seeded(new EnvFile(),
            HandRolled(database: Database.Sqlite, telemetry: TelemetryStorage.LocalDisk));

        Assert.Equal("sqlite", vm.Database);
        Assert.DoesNotContain("postgres", vm.ToAnswers("/home/ubuntu", true).Services);
    }

    [Fact]
    public void A_detected_local_port_deployment_does_not_gain_a_reverse_proxy()
    {
        DetectedDeployment detected = new()
        {
            Exists = true,
            Exposure = Exposure.LocalPort,
            LocalPort = 8080,
            Database = Database.Bundled,
            TelemetryStorage = TelemetryStorage.LocalDisk,
        };

        ConfigurationViewModel vm = Seeded(new EnvFile(), detected);

        Assert.True(vm.LocalOnly);
        Assert.Equal("8080", vm.LocalPort);
        Assert.DoesNotContain("caddy", vm.ToAnswers("/home/ubuntu", true).Services);
    }

    [Fact]
    public void A_recorded_answer_beats_a_detected_one()
    {
        // Once the installer owns a deployment its own record is authoritative: the operator may
        // have deliberately turned something off, and re-detecting the container they have not
        // removed yet would turn it straight back on.
        EnvFile recorded = new();
        recorded.Set("TELEMETRY_STORAGE", "disk");
        recorded.Set("DATABASE_MODE", "sqlite");
        recorded.Set("EXPOSE_MODE", "local");

        ConfigurationViewModel vm = Seeded(recorded, HandRolled());

        Assert.Equal("disk", vm.TelemetryStorage);
        Assert.Equal("sqlite", vm.Database);
        Assert.True(vm.LocalOnly);
    }

    [Fact]
    public void With_nothing_detected_and_nothing_recorded_the_defaults_still_apply()
    {
        ConfigurationViewModel vm = Seeded(new EnvFile(), DetectedDeployment.Nothing);

        Assert.True(vm.PublicTls);
        Assert.Equal("bundled", vm.Database);
        Assert.Equal("disk", vm.TelemetryStorage);
    }

    [Fact]
    public void A_domain_found_on_the_target_is_not_asked_for_again()
    {
        // The reported problem: an install whose domain lives in its Caddyfile rather than its .env
        // was asked for a URL that was already sitting in a file beside it.
        DetectedDeployment detected = new()
        {
            Exists = true,
            Exposure = Exposure.PublicTls,
            Database = Database.Bundled,
            TelemetryStorage = TelemetryStorage.LocalDisk,
            Domain = "entkube.example.com",
            AcmeEmail = "ops@example.com",
        };

        ConfigurationViewModel vm = Seeded(new EnvFile(), detected);

        Assert.Equal("entkube.example.com", vm.Domain);
        Assert.Equal("ops@example.com", vm.AcmeEmail);

        // And with both filled in, the form is submittable without further questions.
        Assert.True(vm.IsValid);
    }

    [Fact]
    public void A_domain_recorded_in_env_still_wins_over_a_detected_one()
    {
        EnvFile recorded = new();
        recorded.Set("DOMAIN", "recorded.example.com");
        recorded.Set("ACME_EMAIL", "recorded@example.com");

        DetectedDeployment detected = new()
        {
            Exists = true,
            Domain = "from-caddyfile.example.com",
            AcmeEmail = "caddy@example.com",
        };

        ConfigurationViewModel vm = Seeded(recorded, detected);

        Assert.Equal("recorded.example.com", vm.Domain);
        Assert.Equal("recorded@example.com", vm.AcmeEmail);
    }

    [Fact]
    public void With_no_domain_anywhere_the_form_still_asks()
    {
        // Detection filling in a blank is a convenience, not a licence to invent one.
        ConfigurationViewModel vm = Seeded(new EnvFile(), new DetectedDeployment { Exists = true });

        Assert.Equal(string.Empty, vm.Domain);
        Assert.False(vm.IsValid);
    }

    // ── The change report ────────────────────────────────────────────────────────────────────────

    private static Answers AnswersFor(TelemetryStorage telemetry, Database database = Database.Bundled) => new()
    {
        Directory = "/home/ubuntu",
        Exposure = Exposure.PublicTls,
        Domain = "entkube.example.com",
        AcmeEmail = "ops@example.com",
        Database = database,
        TelemetryStorage = telemetry,
        VaultRootKey = "k",
        PostgresPassword = "p",
    };

    [Fact]
    public void Adopting_without_changing_anything_reports_no_service_changes()
    {
        DeploymentChange change = HandRolled(running: ["entkube", "postgres", "caddy", "minio", "minio-init"])
            .ChangeFrom(AnswersFor(TelemetryStorage.BundledMinio));

        Assert.True(change.IsNoOp);
        Assert.Empty(change.Removed);
    }

    [Fact]
    public void Turning_off_a_running_service_is_reported_as_a_removal()
    {
        DeploymentChange change = HandRolled(running: ["entkube", "postgres", "caddy", "minio"])
            .ChangeFrom(AnswersFor(TelemetryStorage.LocalDisk));

        Assert.Contains("minio", change.Removed);
        Assert.False(change.IsNoOp);
    }

    [Fact]
    public void Adding_a_missing_service_is_reported_as_an_addition()
    {
        DeploymentChange change = HandRolled(running: ["entkube", "postgres", "caddy"])
            .ChangeFrom(AnswersFor(TelemetryStorage.BundledMinio));

        Assert.Contains("minio", change.Added);
        Assert.Empty(change.Removed);
    }

    [Fact]
    public void The_bucket_initialiser_is_not_reported_as_a_service_change()
    {
        // minio-init is a one-shot that exits as soon as the bucket exists. Reporting it as
        // appearing and disappearing is noise in a summary about what the deployment consists of.
        DeploymentChange change = HandRolled(running: ["entkube", "postgres", "caddy"])
            .ChangeFrom(AnswersFor(TelemetryStorage.BundledMinio));

        Assert.DoesNotContain("minio-init", change.Added);
        Assert.DoesNotContain("minio-init", change.Removed);
    }

    [Fact]
    public void A_stopped_deployment_is_compared_against_what_it_defines()
    {
        // With no containers at all, comparing against nothing would report every service as newly
        // added — which reads as though the installer were about to build something from scratch.
        DeploymentChange change = HandRolled(running: [])
            .ChangeFrom(AnswersFor(TelemetryStorage.BundledMinio));

        Assert.True(change.IsNoOp);
        Assert.False(change.FromRunningContainers);
        Assert.Equal("defined but not running", change.Baseline);
    }

    [Fact]
    public void A_running_deployment_is_compared_against_its_containers()
    {
        DeploymentChange change = HandRolled(running: ["entkube", "postgres", "caddy", "minio"])
            .ChangeFrom(AnswersFor(TelemetryStorage.BundledMinio));

        Assert.True(change.FromRunningContainers);
        Assert.Equal("running now", change.Baseline);
    }

    // ── Adoption is a one-way transition ─────────────────────────────────────────────────────────

    [Fact]
    public void Adopting_writes_the_markers_so_the_next_run_needs_no_detection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "entkube-adopt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            EnvFile handWritten = new();
            handWritten.Set("DOMAIN", "entkube.example.com");
            handWritten.Set("ACME_EMAIL", "ops@example.com");
            handWritten.Set("VAULT__ROOTKEY", "an-existing-key");
            handWritten.Set("POSTGRES_PASSWORD", "an-existing-password");

            ConfigurationViewModel vm = Seeded(handWritten, HandRolled());
            Answers answers = vm.ToAnswers(dir, isUpgrade: true);

            using LocalExecutor executor = new(dir);
            new Renderer(answers).Write(executor, handWritten);

            EnvFile after = EnvFile.Load(Path.Combine(dir, ".env"));

            Assert.Equal("tls", after.Get("EXPOSE_MODE"));
            Assert.Equal("bundled", after.Get("DATABASE_MODE"));
            Assert.Equal("minio", after.Get("TELEMETRY_STORAGE"));

            // And the secrets it found are the secrets it kept.
            Assert.Equal("an-existing-key", after.Get("VAULT__ROOTKEY"));
            Assert.Equal("an-existing-password", after.Get("POSTGRES_PASSWORD"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
