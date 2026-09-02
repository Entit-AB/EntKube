using System.Text;

namespace EntKube.Installer;

/// <summary>
/// Writes the deployment: <c>docker-compose.yml</c>, <c>Caddyfile</c> and <c>.env</c>.
///
/// The compose file is <b>generated</b> rather than copied from the repository and patched with an
/// override. That is a deliberate trade and the reasoning is worth keeping:
///
/// The choices this wizard offers are structural, not just value substitutions. An external database
/// or SQLite has to remove the postgres service *and* the <c>depends_on … service_healthy</c> that
/// references it; serving on a local port has to remove Caddy and publish a port on the app instead.
/// A compose override file cannot reliably do either — <c>depends_on</c> is a mapping in the base and
/// a sequence in the short form, and clearing one across a merge is not something the spec
/// guarantees. An override that half-applied would leave an idle postgres holding a volume that
/// looks like it holds the data and does not, which is a bad failure to design in.
///
/// Generating means the file on disk says exactly what was chosen, with nothing inherited from a
/// base the operator cannot see. The cost is that <c>docker-compose.yml</c> in the repository root —
/// the reference for a hand-rolled install — is a second place the same knowledge lives. That file
/// stays the reference; this is the installer's own rendering of it, and
/// <c>EntKubeInstallerRendererTests</c> pins the parts that must not drift.
/// </summary>
public sealed class Renderer(Answers answers)
{
    /// <summary>One generated file: what to call it, where it goes, and whether it holds secrets.</summary>
    public sealed record GeneratedFile(string Name, string Path, string Content, bool Secret);

    private readonly Answers _a = answers;

    public string ComposePath => Join(_a.Directory, "docker-compose.yml");

    public string EnvPath => Join(_a.Directory, ".env");

    public string CaddyfilePath => Join(_a.Directory, "Caddyfile");

    /// <summary>
    /// Joins with a forward slash rather than Path.Combine.
    ///
    /// The target may be a Linux server reached from a Windows desktop, and Path.Combine would build
    /// "/opt/entkube\.env" there — a single file with a backslash in its name, silently not the file
    /// compose looks for. Forward slashes are correct on every target this installs to, Windows
    /// included.
    /// </summary>
    private static string Join(string directory, string name) =>
        directory.TrimEnd('/', '\\') + "/" + name;

    /// <summary>
    /// The files this deployment consists of, as content. Pure — nothing is written, which is what
    /// lets a caller show a preview, diff it, or test it without a filesystem.
    ///
    /// <see cref="ApplyTo"/> mutates <paramref name="env"/> on the way through, so the .env is
    /// rendered from the merged result rather than from the answers alone. That is what preserves
    /// keys the installer does not manage.
    /// </summary>
    public IReadOnlyList<GeneratedFile> Files(EnvFile env)
    {
        List<GeneratedFile> files = [new("docker-compose.yml", ComposePath, RenderCompose(), false)];

        if (_a.Exposure == Exposure.PublicTls)
        {
            files.Add(new("Caddyfile", CaddyfilePath, RenderCaddyfile(), false));
        }

        ApplyTo(env);
        files.Add(new(".env", EnvPath, env.Render(EnvLayout), true));

        return files;
    }

    /// <summary>
    /// Writes every file to the target, backing up anything already there. Returns the paths written.
    ///
    /// Backups are not optional and not a prompt. The compose file and the Caddyfile are replaced
    /// wholesale on an upgrade, and an operator who hand-edited one has no other way to get it back
    /// — whereas a stale .bak costs nothing.
    /// </summary>
    public IReadOnlyList<string> Write(IExecutor executor, EnvFile env)
    {
        List<string> written = [];

        foreach (GeneratedFile file in Files(env))
        {
            executor.WriteFile(file.Path, file.Content, file.Secret);
            written.Add(file.Path);
        }

        return written;
    }

    // ── .env ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the answers into the env file, leaving every key it does not own alone — which is what
    /// preserves an operator's OIDC block and any hand-added settings across a re-run.
    /// </summary>
    private void ApplyTo(EnvFile env)
    {
        // The three structural choices are RECORDED, not inferred back out of the values they
        // produce. Inferring them was wrong in a way that mattered: a bundled database writes a
        // DATABASE_CONNECTION exactly like an external one does, so a re-run read its own output as
        // "external", dropped the postgres service and left the app pointing at a host that no
        // longer existed. A mode that is written down cannot be misread.
        env.Set("EXPOSE_MODE", _a.Exposure == Exposure.PublicTls ? "tls" : "local");
        env.Set("DATABASE_MODE", _a.Database switch
        {
            Database.Bundled => "bundled",
            Database.ExternalPostgres => "external",
            _ => "sqlite",
        });
        env.Set("TELEMETRY_STORAGE", _a.TelemetryStorage switch
        {
            TelemetryStorage.BundledMinio => "minio",
            TelemetryStorage.ExternalS3 => "s3",
            _ => "disk",
        });

        env.Set("DOMAIN", _a.Exposure == Exposure.PublicTls ? _a.Domain : null);
        env.Set("ACME_EMAIL", _a.Exposure == Exposure.PublicTls ? _a.AcmeEmail : null);
        env.Set("LOCAL_PORT", _a.Exposure == Exposure.LocalPort ? _a.LocalPort.ToString() : null);
        env.Set("PUBLIC_URL", _a.PublicUrl);

        env.Set("REGISTRY", _a.Registry);
        env.Set("IMAGE_TAG", _a.ImageTag);
        env.Set("REGISTRY_USERNAME", _a.RegistryUsername);
        env.Set("REGISTRY_PASSWORD", _a.RegistryPassword);

        env.Set("DATABASE_PROVIDER", _a.Database switch
        {
            Database.Sqlite => "Sqlite",
            _ => "Postgres",
        });

        // Written when bundled, but never CLEARED when not. A password that is removed because the
        // operator moved to SQLite is regenerated if they move back — against a postgres-data volume
        // that still holds the original, which Postgres will not accept and cannot be told to. The
        // stale value costs nothing; losing it costs the database.
        if (_a.Database == Database.Bundled)
        {
            env.Set("POSTGRES_PASSWORD", _a.PostgresPassword);
        }

        env.Set("DATABASE_CONNECTION", _a.Database switch
        {
            Database.Bundled => BundledConnectionString(),
            Database.ExternalPostgres => _a.ExternalConnectionString,
            _ => "Data Source=/app/Data/app.db",
        });

        env.Set("VAULT__ROOTKEY", _a.VaultRootKey);
        env.Set("SEED_ADMIN_EMAIL", _a.SeedAdminEmail);

        env.Set("TELEMETRY_RETENTION_DAYS", _a.TelemetryRetentionDays.ToString());
        env.Set("TELEMETRY_BUCKET", _a.TelemetryStorage == TelemetryStorage.LocalDisk ? null : _a.S3Bucket);

        // Same reasoning as the database password: MinIO bakes its root credentials into the volume
        // on first start, so a regenerated pair after a round trip through another storage choice
        // would not open the data that is already there.
        if (_a.TelemetryStorage == TelemetryStorage.BundledMinio)
        {
            env.Set("MINIO_ROOT_USER", _a.MinioUser);
            env.Set("MINIO_ROOT_PASSWORD", _a.MinioPassword);
        }
        env.Set("TELEMETRY_S3_ENDPOINT", _a.TelemetryStorage == TelemetryStorage.ExternalS3 ? _a.S3Endpoint : null);
        env.Set("TELEMETRY_S3_REGION", _a.TelemetryStorage == TelemetryStorage.ExternalS3 ? _a.S3Region : null);
        env.Set("TELEMETRY_S3_ACCESS_KEY", _a.TelemetryStorage == TelemetryStorage.ExternalS3 ? _a.S3AccessKey : null);
        env.Set("TELEMETRY_S3_SECRET_KEY", _a.TelemetryStorage == TelemetryStorage.ExternalS3 ? _a.S3SecretKey : null);
    }

    /// <summary>
    /// The connection string for the bundled database, naming the adopted service, database and user
    /// where there is one. The host is the compose service name, which is how it resolves on the
    /// shared network.
    /// </summary>
    private string BundledConnectionString()
    {
        AdoptedDatabase? adopted = _a.AdoptedDatabase;

        string host = adopted?.ServiceName ?? "postgres";
        string database = adopted?.DatabaseName ?? "entkube";
        string user = adopted?.Username ?? "entkube";

        return $"Host={host};Port=5432;Database={database};Username={user};Password={_a.PostgresPassword}";
    }

    private static IReadOnlyList<EnvFile.EnvSection> EnvLayout =>
    [
        new("Deployment shape", [
            new EnvFile.EnvEntry("EXPOSE_MODE",
                "tls (Caddy on 80/443) or local (a published port, no TLS). Read back by the installer on "
                + "a re-run — change it there rather than here, since it decides which services exist."),
            new EnvFile.EnvEntry("DATABASE_MODE", "bundled, external or sqlite."),
            new EnvFile.EnvEntry("TELEMETRY_STORAGE", "disk, minio or s3."),
        ]),
        new("Domain / TLS", [
            new EnvFile.EnvEntry("DOMAIN",
                "Public domain pointing at this server. Caddy obtains a Let's Encrypt certificate on first start."),
            new EnvFile.EnvEntry("ACME_EMAIL", "Let's Encrypt account address and expiry notices."),
            new EnvFile.EnvEntry("LOCAL_PORT", "Host port, when serving without Caddy or TLS."),
            new EnvFile.EnvEntry("PUBLIC_URL",
                "How clusters and browsers reach this server. Telemetry ingest URLs are built from it."),
        ]),
        new("Image", [
            new EnvFile.EnvEntry("REGISTRY", "entit.azurecr.io is public — pulls need no login."),
            new EnvFile.EnvEntry("IMAGE_TAG", "'latest', or a short commit SHA to pin a known build."),
            new EnvFile.EnvEntry("REGISTRY_USERNAME",
                "Only for a private registry. Used twice: helm logs in with it inside this container, "
                + "and EntKube builds a pull Secret from it for managed clusters."),
            new EnvFile.EnvEntry("REGISTRY_PASSWORD"),
        ]),
        new("Database", [
            new EnvFile.EnvEntry("DATABASE_PROVIDER", "Postgres, SqlServer or Sqlite."),
            new EnvFile.EnvEntry("DATABASE_CONNECTION", "Connection string for the provider above."),
            new EnvFile.EnvEntry("POSTGRES_PASSWORD",
                "Bundled postgres only. Applied when the volume is FIRST initialised and never again — "
                + "changing it here does not change the server's password, it only breaks the connection."),
        ]),
        new("Vault encryption", [
            new EnvFile.EnvEntry("VAULT__ROOTKEY",
                "32-byte base64 key encrypting every secret in the vault. NEVER regenerate it on a live "
                + "install: the app will start and every stored credential will decrypt to nothing. Back it up."),
        ]),
        new("Telemetry", [
            new EnvFile.EnvEntry("TELEMETRY_RETENTION_DAYS", "Sealed segments older than this are dropped."),
            new EnvFile.EnvEntry("TELEMETRY_BUCKET"),
            new EnvFile.EnvEntry("TELEMETRY_S3_ENDPOINT",
                "Flat S3 config. The production alternative is a StorageLink registered in the app, whose "
                + "credentials live in the vault instead of in this file."),
            new EnvFile.EnvEntry("TELEMETRY_S3_REGION"),
            new EnvFile.EnvEntry("TELEMETRY_S3_ACCESS_KEY"),
            new EnvFile.EnvEntry("TELEMETRY_S3_SECRET_KEY"),
            new EnvFile.EnvEntry("MINIO_ROOT_USER", "Bundled MinIO only."),
            new EnvFile.EnvEntry("MINIO_ROOT_PASSWORD"),
        ]),
        new("Bootstrap", [
            new EnvFile.EnvEntry("SEED_ADMIN_EMAIL",
                "Granted the Admin role on every startup. The way back in if the last admin is lost."),
        ]),
        new("Single sign-on (OIDC)", [
            new EnvFile.EnvEntry("OIDC_ENABLED",
                "Optional, and not configured by the installer. With this false or unset no OIDC scheme is "
                + "registered and the login page is unchanged. See docs/sso.md."),
            new EnvFile.EnvEntry("OIDC_AUTHORITY"),
            new EnvFile.EnvEntry("OIDC_CLIENT_ID"),
            new EnvFile.EnvEntry("OIDC_CLIENT_SECRET"),
            new EnvFile.EnvEntry("OIDC_DISPLAY_NAME"),
            new EnvFile.EnvEntry("OIDC_GROUPS_CLAIM"),
            new EnvFile.EnvEntry("OIDC_SCOPE"),
            new EnvFile.EnvEntry("OIDC_ALLOW_UNMAPPED"),
        ]),
    ];

    // ── Caddyfile ────────────────────────────────────────────────────────────────────────────────

    // Not interpolated: every brace here is Caddy's own, and the two placeholders are Caddy env
    // references that compose fills from .env — nothing in it comes from C#.
    public static string RenderCaddyfile() =>
        """
        # Generated by entkube-install. Replaced on the next run — put hand-written config in a
        # separate file and import it, rather than editing this one.
        {
        	email {env.ACME_EMAIL}
        }

        {env.DOMAIN} {
        	reverse_proxy entkube:8080
        }

        """;

    // ── docker-compose.yml ───────────────────────────────────────────────────────────────────────

    public string RenderCompose()
    {
        StringBuilder sb = new();

        sb.AppendLine("# EntKube management plane — generated by entkube-install.");
        sb.AppendLine("#");
        sb.AppendLine("# Re-running the installer REPLACES this file (the previous one is kept as a .bak).");
        sb.AppendLine("# Settings belong in .env; structural changes belong in a docker-compose.override.yml,");
        sb.AppendLine("# which compose merges automatically and the installer never touches.");
        sb.AppendLine();
        sb.AppendLine("services:");

        if (_a.Exposure == Exposure.PublicTls)
        {
            AppendCaddy(sb);
        }

        if (_a.Database == Database.Bundled)
        {
            AppendPostgres(sb);
        }

        if (_a.TelemetryStorage == TelemetryStorage.BundledMinio)
        {
            AppendMinio(sb);
        }

        AppendEntKube(sb);
        AppendNetworksAndVolumes(sb);

        return sb.ToString();
    }

    private void AppendCaddy(StringBuilder sb) => sb.Append(
        """
          caddy:
            image: caddy:2
            ports:
              - "80:80"
              - "443:443"
              - "443:443/udp"
            volumes:
              - ./Caddyfile:/etc/caddy/Caddyfile:ro
              - caddy-data:/data
              - caddy-config:/config
            environment:
              DOMAIN: "${DOMAIN}"
              ACME_EMAIL: "${ACME_EMAIL}"
            depends_on:
              - entkube
            restart: unless-stopped

        """ + ServiceNetworkAttachment + "\n");

    /// <summary>
    /// The bundled database.
    ///
    /// When a database is being adopted, every identifying detail comes from the existing one —
    /// service name, image tag, data location, database and user — rather than from the template
    /// below. Substituting the template's values would start a new empty Postgres on a new volume
    /// and leave the real data stranded in the old one: nothing deleted, everything lost.
    /// </summary>
    private void AppendPostgres(StringBuilder sb)
    {
        AdoptedDatabase? adopted = _a.AdoptedDatabase;

        string service = adopted?.ServiceName ?? "postgres";
        string image = adopted?.Image ?? "postgres:17";
        string database = adopted?.DatabaseName ?? "entkube";
        string user = adopted?.Username ?? "entkube";
        string volume = adopted?.VolumeLine ?? "postgres-data:/var/lib/postgresql/data";

        if (adopted is not null)
        {
            sb.AppendLine($"  # Adopted from the deployment that was already here. The image tag, the data");
            sb.AppendLine($"  # location and the credentials are ITS values, not this installer's defaults —");
            sb.AppendLine($"  # changing any of them would point the app at a different, empty database.");
        }

        sb.AppendLine($"  {service}:");
        sb.AppendLine($"    image: {image}");
        sb.AppendLine("    environment:");
        sb.AppendLine($"      POSTGRES_DB: {database}");
        sb.AppendLine($"      POSTGRES_USER: {user}");
        sb.AppendLine("      POSTGRES_PASSWORD: \"${POSTGRES_PASSWORD}\"");
        sb.AppendLine("    volumes:");
        sb.AppendLine($"      - {volume}");
        sb.AppendLine("    healthcheck:");
        sb.AppendLine($"      test: [\"CMD-SHELL\", \"pg_isready -U {user} -d {database}\"]");
        sb.AppendLine("      interval: 5s");
        sb.AppendLine("      timeout: 5s");
        sb.AppendLine("      retries: 10");
        sb.AppendLine("      start_period: 10s");
        sb.AppendLine("    restart: unless-stopped");
        sb.Append(ServiceNetworkAttachment);
        sb.AppendLine();
    }

    private void AppendMinio(StringBuilder sb) => sb.Append(
        """
          # Self-hosted object storage for the telemetry segment engine, on this same host.
          minio:
            image: minio/minio:latest
            command: server /data --console-address ":9001"
            environment:
              MINIO_ROOT_USER: "${MINIO_ROOT_USER}"
              MINIO_ROOT_PASSWORD: "${MINIO_ROOT_PASSWORD}"
            volumes:
              - minio-data:/data
            restart: unless-stopped
        """ + ServiceNetworkAttachment + """

          # One-shot: create the telemetry bucket if absent, then exit. Retries until MinIO answers.
          minio-init:
            image: minio/mc:latest
            depends_on:
              - minio
            environment:
              MINIO_ROOT_USER: "${MINIO_ROOT_USER}"
              MINIO_ROOT_PASSWORD: "${MINIO_ROOT_PASSWORD}"
              TELEMETRY_BUCKET: "${TELEMETRY_BUCKET}"
            entrypoint: >
              /bin/sh -c "
              until mc alias set tel http://minio:9000 $$MINIO_ROOT_USER $$MINIO_ROOT_PASSWORD; do echo 'waiting for minio'; sleep 2; done &&
              mc mb --ignore-existing tel/$$TELEMETRY_BUCKET &&
              echo 'telemetry bucket ready'"
            restart: "no"
        """ + ServiceNetworkAttachment + "\n");

    private void AppendEntKube(StringBuilder sb)
    {
        sb.AppendLine("  entkube:");
        sb.AppendLine("    image: ${REGISTRY}/entkube:${IMAGE_TAG}");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - entkube-data:/app/Data");

        if (_a.Exposure == Exposure.LocalPort)
        {
            sb.AppendLine();
            sb.AppendLine("    # No reverse proxy: the app's own port is published directly. HTTP only —");
            sb.AppendLine("    # do not expose this beyond a trusted network.");
            sb.AppendLine("    ports:");
            sb.AppendLine("      - \"${LOCAL_PORT}:8080\"");
        }

        if (_a.Database == Database.Bundled)
        {
            sb.AppendLine();
            sb.AppendLine("    # Wait for the database to answer, not merely to have a container. EF applies");
            sb.AppendLine("    # migrations at startup and a half-started Postgres fails that, not a retry loop.");
            sb.AppendLine("    depends_on:");
            sb.AppendLine($"      {_a.AdoptedDatabase?.ServiceName ?? "postgres"}:");
            sb.AppendLine("        condition: service_healthy");
        }

        sb.AppendLine();
        sb.AppendLine("    environment:");
        sb.AppendLine("      # --- Database ---");
        sb.AppendLine("      DatabaseProvider: \"${DATABASE_PROVIDER}\"");
        sb.AppendLine("      ConnectionStrings__DefaultConnection: \"${DATABASE_CONNECTION}\"");
        sb.AppendLine();
        sb.AppendLine("      # --- Container registry, for EntKube-published components installed into clusters ---");
        sb.AppendLine("      # One credential, two consumers:");
        sb.AppendLine("      #   1. EntKube pulls the Helm chart — helm runs in THIS container and logs in with these.");
        sb.AppendLine("      #   2. The managed cluster pulls the image — EntKube creates a dockerconfigjson Secret");
        sb.AppendLine("      #      in the release namespace, because the cluster's kubelet does that pull and");
        sb.AppendLine("      #      EntKube's own session does not reach it.");
        sb.AppendLine("      # The key is the registry host with dots replaced by underscores. Empty is fine for a");
        sb.AppendLine("      # public registry: nothing is created and pulls happen anonymously.");
        sb.AppendLine($"      Helm__Registries__{_a.Registry.Replace('.', '_').Replace('-', '_')}__Username: \"${{REGISTRY_USERNAME:-}}\"");
        sb.AppendLine($"      Helm__Registries__{_a.Registry.Replace('.', '_').Replace('-', '_')}__Password: \"${{REGISTRY_PASSWORD:-}}\"");
        sb.AppendLine();
        sb.AppendLine("      # --- Telemetry (Lucene/S3 segment engine — logs / traces / RUM) ---");
        sb.AppendLine("      # Base URL clusters use to reach this server; the app appends /ingest/otlp to it.");
        sb.AppendLine("      # Must be reachable FROM the managed clusters, not just from your browser.");
        sb.AppendLine("      Telemetry__PublicIngestUrl: \"${PUBLIC_URL}\"");
        sb.AppendLine("      Telemetry__RetentionDays: \"${TELEMETRY_RETENTION_DAYS}\"");
        sb.AppendLine("      Telemetry__DataPath: \"/app/Data/telemetry\"");

        AppendTelemetryStorage(sb);

        sb.AppendLine();
        sb.AppendLine("      # --- Vault encryption ---");
        sb.AppendLine("      Vault__RootKey: \"${VAULT__ROOTKEY}\"");
        sb.AppendLine();
        sb.AppendLine("      # --- DataProtection keys ---");
        sb.AppendLine("      # On the persistent volume, so auth and antiforgery cookies survive a restart.");
        sb.AppendLine("      # Without this every deploy signs everyone out.");
        sb.AppendLine("      DataProtection__KeyPath: \"/app/Data/keys\"");
        sb.AppendLine();
        sb.AppendLine("      # --- Reverse proxy ---");

        if (_a.Exposure == Exposure.PublicTls)
        {
            sb.AppendLine("      # Trust X-Forwarded-* from Caddy. Required for Blazor Server's SignalR");
            sb.AppendLine("      # connection to work behind a proxy.");
            sb.AppendLine("      ASPNETCORE_FORWARDEDHEADERS_ENABLED: \"true\"");
        }
        else
        {
            sb.AppendLine("      # No proxy in front, so forwarded headers are NOT trusted — honouring them");
            sb.AppendLine("      # from a direct client would let any caller spoof its own scheme and address.");
            sb.AppendLine("      ASPNETCORE_FORWARDEDHEADERS_ENABLED: \"false\"");
        }

        sb.AppendLine();
        sb.AppendLine("      # --- Auth ---");
        sb.AppendLine("      # Open so the first administrator can register. Turn this off in the app once");
        sb.AppendLine("      # that account exists, or anyone reaching the URL can create one.");
        sb.AppendLine("      Auth__AllowRegistration: \"true\"");

        if (_a.SeedAdminEmail is not null)
        {
            sb.AppendLine();
            sb.AppendLine("      # --- Bootstrap ---");
            sb.AppendLine("      # Granted Admin on every startup; a no-op once it already is.");
            sb.AppendLine("      Seed__AdminEmail: \"${SEED_ADMIN_EMAIL}\"");
        }

        sb.AppendLine();
        sb.AppendLine("      # --- Single sign-on (OIDC) — optional, see docs/sso.md ---");
        sb.AppendLine("      Oidc__Enabled: \"${OIDC_ENABLED:-false}\"");
        sb.AppendLine("      Oidc__Authority: \"${OIDC_AUTHORITY:-}\"");
        sb.AppendLine("      Oidc__ClientId: \"${OIDC_CLIENT_ID:-}\"");
        sb.AppendLine("      Oidc__ClientSecret: \"${OIDC_CLIENT_SECRET:-}\"");
        sb.AppendLine("      Oidc__DisplayName: \"${OIDC_DISPLAY_NAME:-Single sign-on}\"");
        sb.AppendLine("      Oidc__GroupsClaim: \"${OIDC_GROUPS_CLAIM:-groups}\"");
        sb.AppendLine("      Oidc__Scopes__0: \"${OIDC_SCOPE:-groups}\"");
        sb.AppendLine("      Oidc__AllowUsersWithoutMappedGroups: \"${OIDC_ALLOW_UNMAPPED:-false}\"");
        sb.AppendLine("    restart: unless-stopped");
        sb.Append(ServiceNetworkAttachment);
        sb.AppendLine();
    }

    private void AppendTelemetryStorage(StringBuilder sb)
    {
        switch (_a.TelemetryStorage)
        {
            case TelemetryStorage.BundledMinio:
                sb.AppendLine();
                sb.AppendLine("      # Sealed segments go to the MinIO above. Path-style addressing because MinIO");
                sb.AppendLine("      # does not serve virtual-hosted bucket names without DNS for each one.");
                sb.AppendLine("      Telemetry__ObjectStorage__Endpoint: \"http://minio:9000\"");
                sb.AppendLine("      Telemetry__ObjectStorage__Bucket: \"${TELEMETRY_BUCKET}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__Region: \"us-east-1\"");
                sb.AppendLine("      Telemetry__ObjectStorage__AccessKey: \"${MINIO_ROOT_USER}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__SecretKey: \"${MINIO_ROOT_PASSWORD}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__ForcePathStyle: \"true\"");
                break;

            case TelemetryStorage.ExternalS3:
                sb.AppendLine();
                sb.AppendLine("      # Flat S3 config. A StorageLink registered in the app takes priority over this");
                sb.AppendLine("      # and keeps its credentials in the vault rather than in .env.");
                sb.AppendLine("      Telemetry__ObjectStorage__Endpoint: \"${TELEMETRY_S3_ENDPOINT}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__Bucket: \"${TELEMETRY_BUCKET}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__Region: \"${TELEMETRY_S3_REGION}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__AccessKey: \"${TELEMETRY_S3_ACCESS_KEY}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__SecretKey: \"${TELEMETRY_S3_SECRET_KEY}\"");
                sb.AppendLine("      Telemetry__ObjectStorage__ForcePathStyle: \"true\"");
                break;

            default:
                sb.AppendLine("      # No object storage configured: sealed segments stay on local disk under");
                sb.AppendLine("      # Telemetry__DataPath. Single node. Register an S3 StorageLink in the app");
                sb.AppendLine("      # and point telemetry at it to move them off this host.");
                break;
        }
    }

    /// <summary>
    /// The line attaching a service to the adopted network, or nothing when compose does it.
    ///
    /// Compose joins services to <c>default</c> on its own. Any other key has to be named on every
    /// service, or they quietly end up on a second network and stop resolving each other by
    /// hostname — which looks like a database outage, not a networking mistake.
    /// </summary>
    private string ServiceNetworkAttachment => _a.AdoptedNetwork is { } net && net.NeedsExplicitAttachment
        ? $"    networks: [{net.Key}]\n"
        : string.Empty;

    private void AppendNetworksAndVolumes(StringBuilder sb)
    {
        sb.AppendLine("networks:");

        if (_a.AdoptedNetwork is { } adopted)
        {
            sb.AppendLine("  # Carried over from the deployment that was already here, under the SAME KEY it had.");
            sb.AppendLine("  # Compose stamps a network with the key it was declared under, and refuses a file that");
            sb.AppendLine("  # declares the same network under a different one:");
            sb.AppendLine("  #   network X was found but has incorrect label com.docker.compose.network ...");
            sb.AppendLine($"  {adopted.Key}:");
            sb.AppendLine($"    name: {adopted.Name}");

            if (adopted.External)
            {
                sb.AppendLine("    external: true");
            }

            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("  # Pinned to a fixed name so it never depends on the directory this is run from or on");
            sb.AppendLine("  # COMPOSE_PROJECT_NAME. Every service joins it automatically, which avoids the");
            sb.AppendLine("  # \"<dir>_default\" ambiguity that has previously split services onto separate networks");
            sb.AppendLine("  # and broken DNS resolution of the \"postgres\" hostname.");
            sb.AppendLine("  default:");
            sb.AppendLine("    name: entkube");
            sb.AppendLine();
        }
        sb.AppendLine("volumes:");
        sb.AppendLine("  # Named volumes survive \"docker compose down\". Only \"down -v\" removes them.");
        sb.AppendLine("  entkube-data:");

        if (_a.Database == Database.Bundled)
        {
            // A bind-mounted data directory is a host path and is declared nowhere; declaring it as
            // a named volume would create an empty one and quietly shadow the real data.
            if (_a.AdoptedDatabase is { } adoptedDb)
            {
                if (adoptedDb.NeedsVolumeDeclaration)
                {
                    sb.AppendLine($"  {adoptedDb.VolumeSource}:");
                }
            }
            else
            {
                sb.AppendLine("  postgres-data:");
            }
        }

        if (_a.TelemetryStorage == TelemetryStorage.BundledMinio)
        {
            sb.AppendLine("  minio-data:");
        }

        if (_a.Exposure == Exposure.PublicTls)
        {
            sb.AppendLine("  caddy-data:");
            sb.AppendLine("  caddy-config:");
        }
    }
}
