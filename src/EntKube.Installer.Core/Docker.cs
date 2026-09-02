namespace EntKube.Installer;

/// <summary>
/// The docker commands the installer issues, in one place, against whichever target the executor
/// points at.
///
/// Compose is resolved once, on first use, because it is invoked in two different ways in the wild —
/// see <see cref="ComposeInvocation"/>. Everything else here goes through whichever one was found,
/// so the rest of the installer never has to care which.
/// </summary>
public sealed class Docker(IExecutor executor)
{
    private readonly IExecutor _executor = executor;
    private ComposeInvocation? _compose;

    public ExecResult Version() =>
        _executor.Run("docker", ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(30));

    /// <summary>
    /// Finds a usable Compose v2, or explains what is there instead.
    ///
    /// The plugin is tried first because it is the form Docker installs today and the one most
    /// targets have. The standalone binary is tried second rather than dismissed: a v2 standalone
    /// build is genuinely v2 and runs these compose files correctly, and rejecting it would refuse a
    /// working host over the shape of its filename.
    ///
    /// Only v1 is refused, and only after its version has actually been read.
    /// </summary>
    public ComposeResolution ResolveCompose()
    {
        if (_compose is not null)
        {
            return new ComposeResolution(_compose, null, null);
        }

        ExecResult plugin = _executor.Run(
            "docker", ["compose", "version", "--short"], TimeSpan.FromSeconds(30));

        if (plugin.Ok && MajorVersion(plugin.StdOut) >= 2)
        {
            _compose = ComposeInvocation.Plugin(plugin.StdOut.Trim());
            return new ComposeResolution(_compose, null, null);
        }

        ExecResult standalone = _executor.Run(
            "docker-compose", ["version", "--short"], TimeSpan.FromSeconds(30));

        if (standalone.Ok && MajorVersion(standalone.StdOut) >= 2)
        {
            _compose = ComposeInvocation.Standalone(standalone.StdOut.Trim());
            return new ComposeResolution(_compose, null, null);
        }

        // Nothing worked as invoked. Before reporting that, find out whether it works *without*
        // elevation — because if it does, the cause is known rather than suspected: a Compose plugin
        // installed for the login user lives under that user's HOME, and sudo runs with root's.
        bool worksUnelevated = false;

        if (_executor.ElevationInUse)
        {
            ExecResult unelevated = _executor.RunUnelevated(
                "docker", ["compose", "version", "--short"], TimeSpan.FromSeconds(30));

            worksUnelevated = unelevated.Ok && MajorVersion(unelevated.StdOut) >= 2;
        }

        return new ComposeResolution(null, plugin, standalone, worksUnelevated);
    }

    /// <summary>
    /// The leading integer of a version string, or -1 when there is not one.
    ///
    /// Compose prints "2.29.7" for --short, but a v1 build prints "1.29.2" and some wrappers add a
    /// "v" prefix, so this reads the first run of digits rather than assuming a format.
    /// </summary>
    internal static int MajorVersion(string text)
    {
        string trimmed = text.Trim().TrimStart('v', 'V');
        int end = 0;

        while (end < trimmed.Length && char.IsAsciiDigit(trimmed[end]))
        {
            end++;
        }

        return end > 0 && int.TryParse(trimmed[..end], out int major) ? major : -1;
    }

    /// <summary>
    /// The resolved invocation. Only valid after <see cref="ResolveCompose"/> has succeeded, which
    /// preflight guarantees before anything else runs.
    /// </summary>
    private ComposeInvocation Compose => _compose
        ?? throw new InvalidOperationException(
            "Compose has not been resolved. Preflight must run before any compose command.");

    public ExecResult Pull(IReadOnlyList<string> services, Action<string>? onLine = null) =>
        RunCompose(["pull", .. services], TimeSpan.FromMinutes(20), onLine);

    /// <summary>
    /// Starts the deployment.
    ///
    /// <paramref name="removeOrphans"/> is off whenever the change drops a service. `--remove-orphans`
    /// removes containers for services no longer in the file, and during an adoption those can be
    /// containers the operator is still relying on — including a database service under a name this
    /// installer did not generate.
    /// </summary>
    public ExecResult Up(
        IReadOnlyList<string> services, Action<string>? onLine = null, bool removeOrphans = true) =>
        RunCompose(
            removeOrphans ? ["up", "-d", "--remove-orphans", .. services] : ["up", "-d", .. services],
            TimeSpan.FromMinutes(15),
            onLine);

    public ExecResult Config() => RunCompose(["config", "--quiet"], TimeSpan.FromMinutes(2));

    /// <summary>Every service the file defines, including any hidden behind a profile.</summary>
    public ExecResult ConfigJson() =>
        RunCompose(["--profile", "*", "config", "--format", "json"], TimeSpan.FromMinutes(2));

    /// <summary>Services that have a container, running or not.</summary>
    public ExecResult Ps() =>
        RunCompose(["ps", "-a", "--format", "{{.Service}}"], TimeSpan.FromMinutes(1));

    public ExecResult PsDetailed() =>
        RunCompose(["ps", "--format", "{{.Name}}\t{{.State}}\t{{.Status}}"], TimeSpan.FromMinutes(1));

    public ExecResult Logs(string service, int lines) =>
        RunCompose(["logs", "--no-color", "--tail", lines.ToString(), service], TimeSpan.FromMinutes(2));

    private ExecResult RunCompose(
        IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine = null) =>
        _executor.Run(Compose.File, [.. Compose.Prefix, .. args], timeout, onLine);
}

/// <summary>
/// The outcome of looking for Compose: either an invocation to use, or what each attempt reported so
/// the failure can name a cause rather than a symptom.
/// </summary>
public sealed record ComposeResolution(
    ComposeInvocation? Invocation,
    ExecResult? PluginAttempt,
    ExecResult? StandaloneAttempt,
    bool WorksWithoutElevation = false)
{
    public bool Found => Invocation is not null;
}
