using System.Text.Json;

namespace EntKube.Installer;

/// <summary>
/// What is already deployed at a target directory, worked out from the deployment itself rather than
/// from what a previous run of this installer recorded.
///
/// This exists because an install the installer did not create has none of the markers it relies on.
/// A deployment stood up by hand — clone the repo, edit .env, `docker compose up` — has no
/// EXPOSE_MODE, no DATABASE_MODE and no TELEMETRY_STORAGE, so every one of those fell back to a
/// default. The dangerous case is telemetry: the default is "local disk", and regenerating the
/// compose file from that answer drops a running MinIO service without ever mentioning it.
///
/// So the shape is read from two authoritative sources instead:
///
///   docker compose ps      what actually has containers — the truth about what is running
///   docker compose config  the resolved configuration, which survives overrides and profiles
///
/// A service that is defined but was never started is not evidence of anything; a container is.
/// </summary>
public sealed class DeploymentInspector(IExecutor executor, Docker docker)
{
    private readonly IExecutor _executor = executor;
    private readonly Docker _docker = docker;

    public DetectedDeployment Inspect(string directory)
    {
        string composePath = directory.TrimEnd('/', '\\') + "/docker-compose.yml";
        string envPath = directory.TrimEnd('/', '\\') + "/.env";

        bool hasCompose = _executor.FileExists(composePath);
        bool hasEnv = _executor.FileExists(envPath);

        if (!hasCompose && !hasEnv)
        {
            return DetectedDeployment.Nothing;
        }

        string? envContent = _executor.ReadFile(envPath);
        EnvFile env = envContent is null ? new EnvFile() : EnvFile.Parse(envContent);

        // The markers this installer writes. Their presence means a previous run owns this
        // deployment and its recorded answers can simply be trusted.
        bool installerOwned = env.Get("EXPOSE_MODE") is not null
            || env.Get("DATABASE_MODE") is not null
            || env.Get("TELEMETRY_STORAGE") is not null;

        List<string> findings = [];

        // Both probes are compose commands, and inspection runs on paths that deliberately skip the
        // tooling check — a dry run renders configuration on a machine that need not have Docker at
        // all. So the absence of Compose degrades what can be detected; it does not fail.
        bool canProbe = _docker.ResolveCompose().Found;

        IReadOnlyList<string> running = canProbe ? RunningServices() : [];
        ComposeConfig config = canProbe ? ReadComposeConfig() : ComposeConfig.Empty;

        if (!canProbe && hasCompose)
        {
            findings.Add(
                "Docker Compose is not available here, so the shape of this deployment could not be "
                + "read from it. Any answer not already recorded in .env falls back to a default — "
                + "check them before applying.");
        }

        if (!hasCompose)
        {
            findings.Add($"There is a .env at {directory} but no docker-compose.yml beside it.");
        }

        if (hasCompose && !installerOwned)
        {
            findings.Add(
                "This deployment was not created by this installer — it has none of the settings a "
                + "previous run would have written. Its shape has been read from the compose file and "
                + "the running containers instead, so nothing is assumed.");
        }

        Exposure? exposure = DetectExposure(running, config, out int? localPort, findings);
        Database? database = DetectDatabase(running, config, findings);
        TelemetryStorage? telemetry = DetectTelemetryStorage(running, config, findings);

        // The domain and the ACME email are usually written straight into the Caddyfile on a
        // hand-built deployment, and never reach .env at all. Reading them there is the difference
        // between adopting a deployment and interrogating its owner about facts already on disk.
        if (config.Network is { } net && net.NeedsExplicitAttachment)
        {
            findings.Add($"Containers share {net.Describe}. It is kept under that same key — compose "
                + "labels a network with the key it was declared under and refuses a file that uses "
                + "a different one.");
        }

        string? caddyfile = _executor.ReadFile(directory.TrimEnd('/', '\\') + "/Caddyfile");
        CaddyfileFacts caddy = caddyfile is null ? CaddyfileFacts.None : CaddyfileFacts.Parse(caddyfile);

        string? domain = env.Get("DOMAIN") ?? caddy.Domain ?? DomainFromIngestUrl(config);
        string? acmeEmail = env.Get("ACME_EMAIL") ?? caddy.AcmeEmail;

        if (env.Get("DOMAIN") is null && domain is not null)
        {
            findings.Add($"The domain {domain} was read from the existing Caddyfile.");
        }

        if (env.Get("ACME_EMAIL") is null && acmeEmail is not null)
        {
            findings.Add($"The Let's Encrypt account address {acmeEmail} was read from the existing Caddyfile.");
        }

        // A Caddyfile that interpolates a variable the .env does not define is a broken deployment,
        // not merely an undetectable one: Caddy would be serving an empty host name.
        if (caddy.UsesEnvPlaceholder && caddy.Domain is null && env.Get("DOMAIN") is null)
        {
            findings.Add(
                "The Caddyfile refers to {env.DOMAIN} but .env does not define DOMAIN — Caddy cannot "
                + "have a host name to serve. Setting the domain below fixes it.");
        }

        return new DetectedDeployment
        {
            Exists = true,
            InstallerOwned = installerOwned,
            HasComposeFile = hasCompose,
            Exposure = exposure,
            LocalPort = localPort,
            Domain = domain,
            AcmeEmail = acmeEmail,
            Database = database,
            AdoptedDatabase = config.Database,
            UnrepresentableDatabase = config.UnrepresentableDatabase,
            AdoptedNetwork = config.Network,
            TelemetryStorage = telemetry,
            RunningServices = running,
            DefinedServices = config.Services,
            Findings = findings,
        };
    }

    // ── Probes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Services that actually have a container, running or stopped. `-a` on purpose: a stopped
    /// postgres is still this deployment's database, and treating it as absent would regenerate a
    /// compose file without it.
    /// </summary>
    private IReadOnlyList<string> RunningServices()
    {
        ExecResult result = _docker.Ps();

        return result.Ok
            ? [.. result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct()]
            : [];
    }

    /// <summary>
    /// The resolved compose configuration. `--profile *` so a service behind a profile — the bundled
    /// MinIO is — is still reported as defined rather than looking like it does not exist.
    /// </summary>
    private ComposeConfig ReadComposeConfig()
    {
        ExecResult result = _docker.ConfigJson();

        if (!result.Ok || result.StdOut.Trim().Length == 0)
        {
            return ComposeConfig.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(result.StdOut);

            if (!doc.RootElement.TryGetProperty("services", out JsonElement services))
            {
                return ComposeConfig.Empty;
            }

            List<string> names = [];
            Dictionary<string, string> entkubeEnv = new(StringComparer.Ordinal);
            List<string> entkubePublishedPorts = [];
            AdoptedDatabase? database = null;
            string? unrepresentableDatabase = null;
            AdoptedNetwork? network = ReadNetwork(doc.RootElement, services);

            foreach (JsonProperty service in services.EnumerateObject())
            {
                names.Add(service.Name);

                // Identified by image, not by name. A hand-built deployment frequently calls it
                // "db", and matching on the name alone reported a bundled database as external —
                // which would have generated a compose file with no database service in it at all.
                string image = service.Value.TryGetProperty("image", out JsonElement img)
                    ? img.GetString() ?? string.Empty
                    : string.Empty;

                if (image.Contains("postgres", StringComparison.OrdinalIgnoreCase)
                    && !image.Contains("exporter", StringComparison.OrdinalIgnoreCase))
                {
                    database = ReadDatabase(service.Name, image, service.Value, out unrepresentableDatabase);
                }

                if (service.Name != "entkube")
                {
                    continue;
                }

                if (service.Value.TryGetProperty("environment", out JsonElement environment)
                    && environment.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty variable in environment.EnumerateObject())
                    {
                        if (variable.Value.ValueKind == JsonValueKind.String)
                        {
                            entkubeEnv[variable.Name] = variable.Value.GetString() ?? string.Empty;
                        }
                    }
                }

                if (service.Value.TryGetProperty("ports", out JsonElement ports)
                    && ports.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement port in ports.EnumerateArray())
                    {
                        if (port.TryGetProperty("published", out JsonElement published))
                        {
                            string? value = published.ValueKind == JsonValueKind.String
                                ? published.GetString()
                                : published.ToString();

                            if (!string.IsNullOrEmpty(value))
                            {
                                entkubePublishedPorts.Add(value);
                            }
                        }
                    }
                }
            }

            return new ComposeConfig(
                names, entkubeEnv, entkubePublishedPorts, database, unrepresentableDatabase, network);
        }
        catch (JsonException)
        {
            // A compose file this installer cannot parse is not a reason to refuse to adopt it — the
            // container list still says most of what matters, and everything unknown stays null so
            // the caller asks rather than assumes.
            return ComposeConfig.Empty;
        }
    }

    // ── Inference ────────────────────────────────────────────────────────────────────────────────

    private static Exposure? DetectExposure(
        IReadOnlyList<string> running, ComposeConfig config, out int? localPort, List<string> findings)
    {
        localPort = null;

        if (running.Contains("caddy") || config.Services.Contains("caddy"))
        {
            findings.Add("Caddy is present, so this is served over HTTPS on a public domain.");
            return Exposure.PublicTls;
        }

        if (config.EntKubePublishedPorts.Count > 0)
        {
            // "8080:8080" or "8080" — the published side is what the host binds.
            string first = config.EntKubePublishedPorts[0];
            string hostPart = first.Split(':')[0];

            if (int.TryParse(hostPart, out int parsed))
            {
                localPort = parsed;
            }

            findings.Add($"No reverse proxy; the app publishes port {hostPart} directly, without TLS.");
            return Exposure.LocalPort;
        }

        return null;
    }

    private static Database? DetectDatabase(
        IReadOnlyList<string> running, ComposeConfig config, List<string> findings)
    {
        if (config.Database is { } adopted)
        {
            findings.Add($"A PostgreSQL service is part of this deployment: {adopted.Describe}. "
                + "It will be carried over exactly as it is.");
            return Database.Bundled;
        }

        if (config.UnrepresentableDatabase is { } problem)
        {
            findings.Add($"This deployment has a PostgreSQL service, but {problem}.");
            return Database.Bundled;
        }

        if (running.Contains("postgres") || config.Services.Contains("postgres"))
        {
            findings.Add("A bundled PostgreSQL service is part of this deployment.");
            return Database.Bundled;
        }

        config.EntKubeEnvironment.TryGetValue("DatabaseProvider", out string? provider);

        switch (provider)
        {
            case "Sqlite":
                findings.Add("The app is configured for SQLite.");
                return Database.Sqlite;

            case "Postgres" or "SqlServer":
                findings.Add($"The app points at an external {provider} database.");
                return Database.ExternalPostgres;

            default:
                return null;
        }
    }

    /// <summary>
    /// The host out of Telemetry__PublicIngestUrl, when the compose file carries a literal one.
    ///
    /// A third-choice source: it is right when it is there, but the repository's own compose file
    /// interpolates it from DOMAIN, so on most deployments it resolves to nothing useful.
    /// </summary>
    private static string? DomainFromIngestUrl(ComposeConfig config)
    {
        if (!config.EntKubeEnvironment.TryGetValue("Telemetry__PublicIngestUrl", out string? url)
            || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed)
            && parsed.Host.Length > 0
            && parsed.Host != "localhost"
                ? parsed.Host
                : null;
    }

    private static TelemetryStorage? DetectTelemetryStorage(
        IReadOnlyList<string> running, ComposeConfig config, List<string> findings)
    {
        // The one that matters most. Getting this wrong regenerates a compose file with no MinIO and
        // takes a running service away with it.
        if (running.Contains("minio") || config.Services.Contains("minio"))
        {
            findings.Add("A bundled MinIO provides telemetry object storage.");
            return TelemetryStorage.BundledMinio;
        }

        if (config.EntKubeEnvironment.TryGetValue("Telemetry__ObjectStorage__Endpoint", out string? endpoint)
            && !string.IsNullOrWhiteSpace(endpoint))
        {
            findings.Add($"Telemetry segments go to an external S3 endpoint ({endpoint}).");
            return TelemetryStorage.ExternalS3;
        }

        if (config.Services.Count > 0)
        {
            findings.Add("Telemetry segments are kept on local disk.");
            return TelemetryStorage.LocalDisk;
        }

        return null;
    }

    /// <summary>
    /// The network this deployment's containers share.
    ///
    /// Read from the key the app service attaches to, because that key is what compose puts in the
    /// network's label and therefore what a later file has to match. Falling back to the single
    /// declared network, then to compose's own default.
    /// </summary>
    private static AdoptedNetwork? ReadNetwork(JsonElement root, JsonElement services)
    {
        if (!root.TryGetProperty("networks", out JsonElement networks)
            || networks.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? key = null;

        // What the app itself is on. If it is attached to several, the first is the one compose
        // resolves service names on first, and is the one worth preserving.
        if (services.TryGetProperty("entkube", out JsonElement entkube)
            && entkube.TryGetProperty("networks", out JsonElement attached)
            && attached.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty attachment in attached.EnumerateObject())
            {
                key = attachment.Name;
                break;
            }
        }

        // No app service yet, or it declares nothing: fall back to the only network there is.
        if (key is null)
        {
            List<string> declared = [.. networks.EnumerateObject().Select(n => n.Name)];

            if (declared.Count != 1)
            {
                return null;
            }

            key = declared[0];
        }

        if (!networks.TryGetProperty(key, out JsonElement definition))
        {
            return null;
        }

        string name = definition.TryGetProperty("name", out JsonElement n)
            ? n.GetString() ?? key
            : key;

        bool external = definition.TryGetProperty("external", out JsonElement ext)
            && ext.ValueKind == JsonValueKind.True;

        return new AdoptedNetwork(key, name, external);
    }

    /// <summary>
    /// Reads a database service into something reproducible, or explains why it is not.
    ///
    /// The data mount is the part that must be exact. Everything else being wrong produces a
    /// container that fails to start, which is loud; the wrong data directory produces one that
    /// starts perfectly against nothing.
    /// </summary>
    private static AdoptedDatabase? ReadDatabase(
        string serviceName, string image, JsonElement service, out string? problem)
    {
        problem = null;

        if (!service.TryGetProperty("volumes", out JsonElement volumes)
            || volumes.ValueKind != JsonValueKind.Array)
        {
            problem = $"the \"{serviceName}\" service declares no volumes, so its data is inside the "
                + "container and would not survive being recreated";
            return null;
        }

        foreach (JsonElement volume in volumes.EnumerateArray())
        {
            string target = volume.TryGetProperty("target", out JsonElement t) ? t.GetString() ?? "" : "";

            // The Postgres data directory, whatever else is mounted alongside it.
            if (!target.Contains("/var/lib/postgresql", StringComparison.Ordinal))
            {
                continue;
            }

            string source = volume.TryGetProperty("source", out JsonElement sv) ? sv.GetString() ?? "" : "";
            string type = volume.TryGetProperty("type", out JsonElement ty) ? ty.GetString() ?? "" : "";

            if (source.Length == 0)
            {
                problem = $"the data mount on \"{serviceName}\" is anonymous, so there is no name to "
                    + "carry over and recreating the service would not find it again";
                return null;
            }

            IReadOnlyDictionary<string, string> env = ReadEnvironment(service);

            return new AdoptedDatabase(
                serviceName,
                image,
                source,
                target,
                IsBindMount: type == "bind",
                DatabaseName: env.GetValueOrDefault("POSTGRES_DB", "entkube"),
                Username: env.GetValueOrDefault("POSTGRES_USER", "entkube"));
        }

        problem = $"the \"{serviceName}\" service mounts nothing at /var/lib/postgresql/data, so its "
            + "data location could not be determined";

        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironment(JsonElement service)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        if (service.TryGetProperty("environment", out JsonElement environment)
            && environment.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty variable in environment.EnumerateObject())
            {
                if (variable.Value.ValueKind == JsonValueKind.String)
                {
                    values[variable.Name] = variable.Value.GetString() ?? string.Empty;
                }
            }
        }

        return values;
    }

    private sealed record ComposeConfig(
        IReadOnlyList<string> Services,
        IReadOnlyDictionary<string, string> EntKubeEnvironment,
        IReadOnlyList<string> EntKubePublishedPorts,
        AdoptedDatabase? Database = null,
        string? UnrepresentableDatabase = null,
        AdoptedNetwork? Network = null)
    {
        public static ComposeConfig Empty { get; } =
            new([], new Dictionary<string, string>(StringComparer.Ordinal), []);
    }
}

/// <summary>
/// What was found at a target. Every inferred value is nullable: "could not tell" is a real answer,
/// and it means the caller falls back to its own default rather than to a confident wrong one.
/// </summary>
public sealed class DetectedDeployment
{
    public static DetectedDeployment Nothing { get; } = new();

    /// <summary>A compose file or a .env is present, so something is already deployed here.</summary>
    public bool Exists { get; init; }

    /// <summary>A previous run of this installer wrote it, so its recorded answers can be trusted.</summary>
    public bool InstallerOwned { get; init; }

    public bool HasComposeFile { get; init; }

    public Exposure? Exposure { get; init; }

    public int? LocalPort { get; init; }

    /// <summary>The domain this deployment already serves, from .env, the Caddyfile or the ingest URL.</summary>
    public string? Domain { get; init; }

    public string? AcmeEmail { get; init; }

    /// <summary>
    /// The existing database, described precisely enough to be reproduced. Null when there is none,
    /// or when one exists that could not be read — see <see cref="UnrepresentableDatabase"/>.
    /// </summary>
    public AdoptedDatabase? AdoptedDatabase { get; init; }

    /// <summary>Why an existing database could not be carried over, when that is the case.</summary>
    public string? UnrepresentableDatabase { get; init; }

    /// <summary>
    /// The network the existing deployment uses. Carried over because compose labels a network with
    /// the KEY it had in the file, and refuses a file that names the same network under a different
    /// key.
    /// </summary>
    public AdoptedNetwork? AdoptedNetwork { get; init; }

    public Database? Database { get; init; }

    public TelemetryStorage? TelemetryStorage { get; init; }

    public IReadOnlyList<string> RunningServices { get; init; } = [];

    public IReadOnlyList<string> DefinedServices { get; init; } = [];

    /// <summary>Plain-language notes about what was found, for showing before anything is changed.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>
    /// True when this is an existing deployment that this installer did not create — the case that
    /// warrants showing what was detected and what will change before touching anything.
    /// </summary>
    public bool IsAdoption => Exists && !InstallerOwned;

    /// <summary>
    /// What applying <paramref name="answers"/> would add and remove, against what is there now.
    ///
    /// This is the check that turns "the installer rewrote my compose file" into a decision. A
    /// service dropping out is one that stops being part of the deployment, which is worth seeing
    /// before it happens rather than afterwards.
    ///
    /// The baseline is what has containers, falling back to what the compose file defines. Running
    /// containers are the better evidence — a service defined behind a profile and never started is
    /// not something anyone loses — but a deployment that is merely stopped still has a shape, and
    /// comparing it against nothing would report every one of its services as newly added.
    /// </summary>
    public DeploymentChange ChangeFrom(Answers answers)
    {
        bool fromContainers = RunningServices.Count > 0;

        HashSet<string> current = new(
            fromContainers ? RunningServices : DefinedServices, StringComparer.Ordinal);
        HashSet<string> planned = new(answers.Services, StringComparer.Ordinal);

        // The bucket initialiser is a one-shot that exits as soon as it has made the bucket; it is
        // noise in a diff about what the deployment consists of.
        current.Remove("minio-init");
        planned.Remove("minio-init");

        return new DeploymentChange(
            [.. planned.Except(current).Order()],
            [.. current.Except(planned).Order()],
            [.. planned.Intersect(current).Order()],
            fromContainers);
    }
}

/// <summary>The service-level difference between what is there now and what is about to be applied.</summary>
public sealed record DeploymentChange(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Unchanged,
    bool FromRunningContainers)
{
    public bool IsNoOp => Added.Count == 0 && Removed.Count == 0;

    /// <summary>How to describe the baseline, so the report says what it actually compared against.</summary>
    public string Baseline => FromRunningContainers ? "running now" : "defined but not running";
}
