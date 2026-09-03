using EntKube.Installer;

namespace EntKube.Web.Tests;

/// <summary>
/// Not destroying an existing database.
///
/// This is the worst thing the installer can do, and it does it silently. The generated Postgres
/// service used a fixed image tag, a fixed volume name and fixed credentials; pointed at a
/// deployment whose database differed in any of those, it would start a brand new empty Postgres on
/// a brand new volume while the real data sat in the old one. Nothing is deleted. Everything is
/// gone. The first symptom is an application with no data in it.
///
/// So: an existing database is reproduced exactly, and anything that cannot be reproduced stops the
/// install instead of proceeding on a best effort.
/// </summary>
public class InstallerDatabaseSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "entkube-dbsafety", Guid.NewGuid().ToString("N"));

    public InstallerDatabaseSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static AdoptedDatabase TheirDatabase(
        string service = "db",
        string image = "postgres:15",
        string volume = "pgdata",
        bool bind = false,
        string database = "entkube_prod",
        string user = "entkube_app") =>
        new(service, image, volume, "/var/lib/postgresql/data", bind, database, user);

    private Answers Adopting(AdoptedDatabase? adopted) => new()
    {
        Directory = _dir,
        Exposure = Exposure.PublicTls,
        Domain = "entkube.example.com",
        AcmeEmail = "ops@example.com",
        Database = Database.Bundled,
        AdoptedDatabase = adopted,
        PostgresPassword = "theirs",
        VaultRootKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
    };

    // ── The existing database is reproduced, not replaced ────────────────────────────────────────

    [Fact]
    public void The_existing_image_tag_is_kept()
    {
        // Starting Postgres 17 against a 15 data directory refuses to start. Loud rather than
        // silent, but still a broken deployment that the installer caused.
        string compose = new Renderer(Adopting(TheirDatabase(image: "postgres:15"))).RenderCompose();

        Assert.Contains("image: postgres:15", compose);
        Assert.DoesNotContain("postgres:17", compose);
    }

    [Fact]
    public void The_existing_data_volume_is_kept_and_declared()
    {
        // The whole point. A different volume name is a different, empty database.
        string compose = new Renderer(Adopting(TheirDatabase(volume: "pgdata"))).RenderCompose();

        Assert.Contains("- pgdata:/var/lib/postgresql/data", compose);
        Assert.Contains("  pgdata:", compose);
        Assert.DoesNotContain("postgres-data", compose);
    }

    [Fact]
    public void A_bind_mounted_data_directory_is_kept_and_not_declared_as_a_volume()
    {
        // Declaring a host path in the volumes section would create an empty named volume and
        // shadow the real directory.
        string compose = new Renderer(
            Adopting(TheirDatabase(volume: "/srv/pgdata", bind: true))).RenderCompose();

        Assert.Contains("- /srv/pgdata:/var/lib/postgresql/data", compose);

        string volumesSection = compose[compose.IndexOf("volumes:", StringComparison.Ordinal)..];
        Assert.DoesNotContain("  /srv/pgdata:", volumesSection);
    }

    [Fact]
    public void The_existing_service_name_is_kept_everywhere_it_is_referenced()
    {
        // A second service called "postgres" would leave theirs orphaned, and `up --remove-orphans`
        // would then remove the container holding the live database.
        string compose = new Renderer(Adopting(TheirDatabase(service: "db"))).RenderCompose();

        Assert.Contains("  db:", compose);
        Assert.DoesNotContain("  postgres:", compose);

        // depends_on must name it too, or the app starts before the database is ready.
        Assert.Contains("      db:", compose);
        Assert.Contains("        condition: service_healthy", compose);
    }

    [Fact]
    public void The_connection_string_names_the_existing_host_database_and_user()
    {
        Answers answers = Adopting(TheirDatabase(
            service: "db", database: "entkube_prod", user: "entkube_app"));

        EnvFile env = new();
        new Renderer(answers).Files(env);

        Assert.Equal(
            "Host=db;Port=5432;Database=entkube_prod;Username=entkube_app;Password=theirs",
            env.Get("DATABASE_CONNECTION"));
    }

    [Fact]
    public void The_healthcheck_uses_the_existing_database_and_user()
    {
        // pg_isready against a database that does not exist never reports healthy, and the app then
        // waits forever on depends_on.
        string compose = new Renderer(Adopting(TheirDatabase(
            database: "entkube_prod", user: "entkube_app"))).RenderCompose();

        Assert.Contains("pg_isready -U entkube_app -d entkube_prod", compose);
    }

    [Fact]
    public void The_adopted_service_name_is_what_gets_started()
    {
        Assert.Contains("db", Adopting(TheirDatabase(service: "db")).Services);
        Assert.DoesNotContain("postgres", Adopting(TheirDatabase(service: "db")).Services);
    }

    [Fact]
    public void With_nothing_adopted_the_defaults_are_still_used()
    {
        string compose = new Renderer(Adopting(null)).RenderCompose();

        Assert.Contains("  postgres:", compose);
        Assert.Contains("image: postgres:17", compose);
        Assert.Contains("- postgres-data:/var/lib/postgresql/data", compose);
    }

    // ── What cannot be reproduced stops the install ──────────────────────────────────────────────

    private bool Apply(Answers answers, DetectedDeployment detected)
    {
        using LocalExecutor executor = new(_dir);
        InstallRunner runner = new(executor, new SilentLog());

        return runner.Apply(answers, new EnvFile(), skipStart: true, detected: detected);
    }

    [Fact]
    public void A_database_whose_data_location_cannot_be_read_stops_the_install()
    {
        DetectedDeployment detected = new()
        {
            Exists = true,
            Database = Database.Bundled,
            UnrepresentableDatabase = "the \"postgres\" service declares no volumes",
        };

        InstallAbortedException ex = Assert.Throws<InstallAbortedException>(
            () => Apply(Adopting(null), detected));

        Assert.Contains("Nothing has been changed", ex.Message);

        // And it really did not write anything.
        Assert.False(File.Exists(Path.Combine(_dir, "docker-compose.yml")));
    }

    [Fact]
    public void Switching_an_existing_database_away_from_bundled_stops_the_install()
    {
        DetectedDeployment detected = new()
        {
            Exists = true,
            Database = Database.Bundled,
            AdoptedDatabase = TheirDatabase(),
        };

        Answers toSqlite = Adopting(null);
        toSqlite.Database = Database.Sqlite;

        InstallAbortedException ex = Assert.Throws<InstallAbortedException>(
            () => Apply(toSqlite, detected));

        Assert.Contains("empty database", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "docker-compose.yml")));
    }

    [Fact]
    public void An_existing_database_that_was_read_but_not_carried_over_stops_the_install()
    {
        // A wiring mistake inside the installer rather than anything the operator did — but the
        // consequence is the same, so it is caught rather than trusted.
        DetectedDeployment detected = new()
        {
            Exists = true,
            Database = Database.Bundled,
            AdoptedDatabase = TheirDatabase(),
        };

        InstallAbortedException ex = Assert.Throws<InstallAbortedException>(
            () => Apply(Adopting(null), detected));

        // Asserted on the consequence rather than the phrasing: the message is hard-wrapped, so a
        // longer phrase would straddle a line break and make this test about formatting.
        Assert.Contains("a different data location", ex.Message);
    }

    [Fact]
    public void Adopting_a_database_that_is_carried_over_proceeds()
    {
        AdoptedDatabase theirs = TheirDatabase();

        DetectedDeployment detected = new()
        {
            Exists = true,
            Database = Database.Bundled,
            AdoptedDatabase = theirs,
        };

        Assert.True(Apply(Adopting(theirs), detected));
        Assert.Contains("pgdata", File.ReadAllText(Path.Combine(_dir, "docker-compose.yml")));
    }

    [Fact]
    public void A_fresh_install_is_not_affected_by_any_of_this()
    {
        Assert.True(Apply(Adopting(null), DetectedDeployment.Nothing));
        Assert.True(File.Exists(Path.Combine(_dir, "docker-compose.yml")));
    }

    // ── The network the deployment already uses ─────────────────────────────────────────────────

    private Answers OnNetwork(AdoptedNetwork? network)
    {
        Answers answers = Adopting(null);
        answers.AdoptedNetwork = network;
        return answers;
    }

    [Fact]
    public void An_existing_network_is_declared_under_the_key_it_already_has()
    {
        // Compose stamps a network with the KEY it was declared under, not its name, and refuses a
        // file that declares the same network under a different key:
        //   network entkube was found but has incorrect label com.docker.compose.network
        //   set to "entkube" (expected: "default")
        string compose = new Renderer(
            OnNetwork(new AdoptedNetwork("entkube", "entkube", External: false))).RenderCompose();

        // Scoped to the networks block: the app service is also called "entkube" and sits at the
        // same indentation, so a whole-file search finds the wrong one.
        // Anchored to the TOP-LEVEL networks block. A plain search finds the per-service
        // "    networks: [entkube]" attachment first and starts the slice in the middle of the
        // services, where a service is also called entkube.
        string normalised = compose.ReplaceLineEndings("\n");
        string networks = normalised[normalised.IndexOf("\nnetworks:", StringComparison.Ordinal)..];
        string[] lines = networks.Split('\n');
        int key = Array.IndexOf(lines, "  entkube:");

        Assert.True(key >= 0, "the network is not declared under its existing key");
        Assert.Equal("    name: entkube", lines[key + 1]);
        Assert.DoesNotContain("  default:", compose);
    }

    [Fact]
    public void Every_service_is_attached_when_the_key_is_not_default()
    {
        // Compose joins services to "default" by itself; any other key must be named on each
        // service or they land on a second network and stop resolving each other by hostname.
        Answers answers = OnNetwork(new AdoptedNetwork("entkube", "entkube", External: false));
        answers.TelemetryStorage = TelemetryStorage.BundledMinio;
        answers.MinioUser = "entkube";
        answers.MinioPassword = "m";
        answers.S3Bucket = "b";

        string compose = new Renderer(answers).RenderCompose();

        // entkube, postgres, caddy, minio and minio-init.
        Assert.Equal(5, compose.Split("networks: [entkube]").Length - 1);
    }

    [Fact]
    public void No_attachment_lines_are_added_for_the_default_key()
    {
        // Redundant, and it would make the generated file differ from the one a fresh install
        // produces for no reason.
        string compose = new Renderer(
            OnNetwork(new AdoptedNetwork("default", "entkube", External: false))).RenderCompose();

        Assert.DoesNotContain("networks: [", compose);
        Assert.Contains("name: entkube", compose);
    }

    [Fact]
    public void An_external_network_is_declared_as_external()
    {
        // Compose must attach to it rather than try to create or label it.
        string compose = new Renderer(
            OnNetwork(new AdoptedNetwork("shared", "shared-net", External: true))).RenderCompose();

        Assert.Contains("  shared:", compose);
        Assert.Contains("    name: shared-net", compose);
        Assert.Contains("    external: true", compose);
    }

    [Fact]
    public void A_fresh_install_still_pins_the_default_network()
    {
        // The behaviour that avoids the "<dir>_default" ambiguity must survive all of the above.
        string compose = new Renderer(OnNetwork(null)).RenderCompose();

        Assert.Contains("  default:", compose);
        Assert.Contains("    name: entkube", compose);
        Assert.DoesNotContain("networks: [", compose);
    }

    private sealed class SilentLog : IInstallLog
    {
        public void Step(string label, string outcome) { }

        public void Warn(string message) { }

        public void Detail(string message) { }
    }
}
