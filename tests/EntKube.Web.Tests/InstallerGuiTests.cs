using EntKube.Installer;
using EntKube.Installer.Gui.Services;
using EntKube.Installer.Gui.ViewModels;

namespace EntKube.Web.Tests;

/// <summary>
/// The GUI's own logic, which is almost entirely the mapping from a form to an
/// <see cref="Answers"/>.
///
/// That mapping is the whole reason a GUI install and a console install produce the same deployment:
/// everything past it is shared code. A defect here would not show up as a broken window — it would
/// show up as a server configured slightly differently from the one the console installer builds,
/// which is far harder to notice.
///
/// Nothing here constructs an Avalonia control, so no UI thread or application instance is needed.
/// </summary>
public class InstallerGuiTests
{
    private static ConfigurationViewModel Seeded(EnvFile existing, bool isUpgrade = true)
    {
        ConfigurationViewModel vm = new();
        vm.SeedFrom(existing, isUpgrade);
        return vm;
    }

    // ── The values that must never be regenerated ────────────────────────────────────────────────

    [Fact]
    public void An_existing_vault_key_is_carried_through_untouched()
    {
        // The same rule the console installer follows, and for the same reason: a new key does not
        // fail loudly — the app starts and every stored secret decrypts to nothing.
        EnvFile existing = new();
        existing.Set("VAULT__ROOTKEY", "an-existing-key");
        existing.Set("POSTGRES_PASSWORD", "an-existing-password");
        existing.Set("DOMAIN", "entkube.example.com");
        existing.Set("ACME_EMAIL", "ops@example.com");

        Answers answers = Seeded(existing).ToAnswers("/opt/entkube", isUpgrade: true);

        Assert.Equal("an-existing-key", answers.VaultRootKey);
        Assert.Equal("an-existing-password", answers.PostgresPassword);
    }

    [Fact]
    public void A_fresh_install_generates_a_valid_vault_key()
    {
        Answers answers = Seeded(new EnvFile(), isUpgrade: false).ToAnswers("/opt/entkube", false);

        Assert.Equal(32, Convert.FromBase64String(answers.VaultRootKey).Length);
    }

    [Fact]
    public void The_placeholder_key_from_the_sample_env_is_replaced()
    {
        EnvFile existing = new();
        existing.Set("VAULT__ROOTKEY", "REPLACE_WITH_BASE64_32_BYTE_KEY");

        Answers answers = Seeded(existing).ToAnswers("/opt/entkube", true);

        Assert.NotEqual("REPLACE_WITH_BASE64_32_BYTE_KEY", answers.VaultRootKey);
        Assert.Equal(32, Convert.FromBase64String(answers.VaultRootKey).Length);
    }

    // ── Structural modes are read, not inferred ──────────────────────────────────────────────────

    [Theory]
    [InlineData("bundled", Database.Bundled)]
    [InlineData("external", Database.ExternalPostgres)]
    [InlineData("sqlite", Database.Sqlite)]
    public void The_database_mode_is_read_back_from_what_the_last_run_recorded(string mode, Database expected)
    {
        // DATABASE_MODE, not the connection string: a bundled install writes one that looks exactly
        // like an external install's, and inferring from it once made a re-run drop the postgres
        // service and point the app at a host that no longer existed.
        EnvFile existing = new();
        existing.Set("DATABASE_MODE", mode);
        existing.Set("DATABASE_CONNECTION", "Host=postgres;Port=5432;Database=entkube;Username=entkube;Password=x");
        existing.Set("DOMAIN", "entkube.example.com");
        existing.Set("ACME_EMAIL", "ops@example.com");

        Assert.Equal(expected, Seeded(existing).ToAnswers("/opt/entkube", true).Database);
    }

    [Fact]
    public void A_local_port_deployment_is_remembered_as_one()
    {
        EnvFile existing = new();
        existing.Set("EXPOSE_MODE", "local");
        existing.Set("LOCAL_PORT", "9000");

        Answers answers = Seeded(existing).ToAnswers("/opt/entkube", true);

        Assert.Equal(Exposure.LocalPort, answers.Exposure);
        Assert.Equal(9000, answers.LocalPort);
        Assert.Equal("http://localhost:9000", answers.PublicUrl);
    }

    [Fact]
    public void A_fresh_install_defaults_to_the_production_shape()
    {
        // Defaulting to the untrusted one would quietly stand up a plaintext deployment for someone
        // who accepted the defaults.
        Assert.True(Seeded(new EnvFile(), isUpgrade: false).PublicTls);
    }

    // ── Validation ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_public_deployment_will_not_proceed_without_a_domain_and_an_email()
    {
        ConfigurationViewModel vm = Seeded(new EnvFile(), isUpgrade: false);

        Assert.False(vm.IsValid);
        Assert.Contains("domain", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);

        vm.Domain = "entkube.example.com";
        Assert.Contains("email", vm.ValidationMessage!, StringComparison.OrdinalIgnoreCase);

        vm.AcmeEmail = "ops@example.com";
        Assert.True(vm.IsValid);
    }

    [Theory]
    [InlineData("not a hostname")]
    [InlineData("https://entkube.example.com")]
    [InlineData("localhost")]
    public void An_implausible_domain_is_rejected_before_a_certificate_is_ever_ordered(string domain)
    {
        ConfigurationViewModel vm = Seeded(new EnvFile(), isUpgrade: false);
        vm.Domain = domain;
        vm.AcmeEmail = "ops@example.com";

        Assert.False(vm.IsValid);
    }

    [Fact]
    public void An_external_database_will_not_proceed_without_a_connection_string()
    {
        ConfigurationViewModel vm = Seeded(new EnvFile(), isUpgrade: false);
        vm.Domain = "entkube.example.com";
        vm.AcmeEmail = "ops@example.com";
        vm.IsExternalDatabase = true;

        Assert.False(vm.IsValid);

        vm.ConnectionString = "Host=db.example.com;Database=entkube;Username=u;Password=p";
        Assert.True(vm.IsValid);
    }

    [Fact]
    public void Selecting_one_radio_option_deselects_the_others()
    {
        // The radio buttons bind two-way to these, so a setter that failed to notify the siblings
        // would leave two options visibly selected at once.
        ConfigurationViewModel vm = Seeded(new EnvFile(), isUpgrade: false);

        vm.IsSqlite = true;

        Assert.True(vm.IsSqlite);
        Assert.False(vm.IsBundledDatabase);
        Assert.False(vm.IsExternalDatabase);
    }

    // ── Round trip ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_form_produces_the_same_deployment_the_console_installer_would()
    {
        // The point of the whole exercise: what the GUI hands to the shared renderer is an Answers
        // like any other, so the generated compose file is identical.
        ConfigurationViewModel vm = Seeded(new EnvFile(), isUpgrade: false);
        vm.Domain = "entkube.example.com";
        vm.AcmeEmail = "ops@example.com";
        vm.IsBundledMinio = true;
        vm.SeedAdminEmail = "ops@example.com";

        Answers answers = vm.ToAnswers("/opt/entkube", false);
        string compose = new Renderer(answers).RenderCompose();

        Assert.Contains("postgres:17", compose);
        Assert.Contains("minio/minio:latest", compose);
        Assert.Contains("caddy:2", compose);
        Assert.Contains("Seed__AdminEmail", compose);
        Assert.Equal("https://entkube.example.com", answers.PublicUrl);
    }

    // ── Client tools ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_client_tool_has_a_platform_correct_file_name()
    {
        foreach (ClientTool tool in ToolBundle.All)
        {
            Assert.Equal(OperatingSystem.IsWindows(), tool.PlatformFileName.EndsWith(".exe"));
        }
    }

    [Fact]
    public void A_directory_that_is_not_on_PATH_is_detected()
    {
        // Tools install correctly into a directory off PATH and then appear not to exist, which is
        // confusing enough to be worth warning about up front.
        Assert.False(ClientToolInstaller.IsOnPath(
            Path.Combine(Path.GetTempPath(), "entkube-definitely-not-on-path")));
    }

    [Fact]
    public void A_directory_that_is_on_PATH_is_recognised()
    {
        string? first = Environment.GetEnvironmentVariable("PATH")
            ?.Split(OperatingSystem.IsWindows() ? ';' : ':', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => Directory.Exists(p));

        // A machine with no usable PATH entry has nothing to assert against; that is an environment
        // fact, not a failure of the code under test.
        if (first is null)
        {
            return;
        }

        Assert.True(ClientToolInstaller.IsOnPath(first));
    }

    [Fact]
    public void A_tool_that_was_not_bundled_reports_how_to_build_it_rather_than_failing()
    {
        ClientToolInstaller installer = new();
        string destination = Path.Combine(Path.GetTempPath(), "entkube-tools-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            IReadOnlyList<ClientToolInstaller.Result> results =
                installer.Install(ToolBundle.All, destination, "https://entkube.example.com");

            // Whether the binaries exist depends on whether this checkout has been built, so the
            // assertion is about the shape of the answer, not about which branch it took.
            foreach (ClientToolInstaller.Result result in results)
            {
                if (!result.Installed)
                {
                    Assert.Contains("scripts/release.sh", result.Message);
                }
            }
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }
}
