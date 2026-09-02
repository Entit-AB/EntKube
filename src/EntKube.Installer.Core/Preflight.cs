namespace EntKube.Installer;

/// <summary>
/// The checks worth making before anything is written or started.
///
/// Each one exists because its failure is otherwise discovered late and reads as something else: a
/// missing compose plugin surfaces as an unhelpful "unknown command", an unreachable daemon as a
/// permission error, and a busy port 443 as an ACME failure several minutes into the first start.
///
/// All of them run identically against a local install and one over SSH — that is the point of
/// <see cref="IExecutor"/>. The messages name the target rather than assuming "this machine".
/// </summary>
public sealed class Preflight(IExecutor executor, IInstallLog log, Docker? docker = null)
{
    private readonly IExecutor _executor = executor;

    // Shared with the caller where possible: Docker caches which Compose invocation works, and
    // re-resolving it costs two more round trips on every construction over SSH.
    private readonly Docker _docker = docker ?? new Docker(executor);
    private readonly IInstallLog _log = log;

    /// <summary>
    /// Tooling. Fatal: nothing about the install works without these, and continuing only moves the
    /// failure somewhere less obvious.
    /// </summary>
    public void RequireTooling()
    {
        ExecResult version = _docker.Version();

        if (!version.Ok)
        {
            string where = _executor.IsLocal ? string.Empty : $" on {_executor.Target}";

            // 127 is the shell's marker for "could not run it at all", but it does not survive
            // every path: run through sudo, a missing binary exits 1 with "sudo: docker: command
            // not found", and trusting the code alone would tell an operator with no docker at all
            // to go and start their daemon. So the message is checked too.
            string output = version.Output;

            bool notInstalled = version.ExitCode == 127
                || output.Contains("command not found", StringComparison.OrdinalIgnoreCase)
                || output.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
                || output.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);

            throw new InstallAbortedException(notInstalled
                ? $"docker is not installed{where}, or is not on PATH.\n"
                  + "Install Docker Engine (https://docs.docker.com/engine/install/) and try again."
                : $"docker is installed{where} but the daemon is not reachable:\n\n"
                  + Indent(version.Output) + "\n\n"
                  + "Start it (systemctl start docker), or add this user to the docker group.\n"
                  + (_executor.IsLocal
                      ? "A new group membership only applies to a new login session."
                      : "A new group membership only applies to a new SSH session, so reconnect after\n"
                        + "adding it — or turn on \"use sudo\" on the connection page."));
        }

        _log.Step("docker", $"engine {version.StdOut.Trim()}");

        ComposeResolution compose = _docker.ResolveCompose();

        if (!compose.Found)
        {
            throw new InstallAbortedException(ExplainMissingCompose(compose));
        }

        _log.Step("docker compose", $"{compose.Invocation!.Display} v{compose.Invocation.Version}");
    }

    /// <summary>
    /// Says which of the several different things went wrong, rather than one message for all of them.
    ///
    /// "Compose v2 is not available" is true of every case here and useful in none of them: an
    /// operator who has just watched `docker-compose --version` print a v2 number reads it as the
    /// installer being wrong. Each branch below names something they can check or change.
    /// </summary>
    private string ExplainMissingCompose(ComposeResolution resolution)
    {
        string plugin = resolution.PluginAttempt?.Output.Trim() ?? string.Empty;
        string standalone = resolution.StandaloneAttempt?.Output.Trim() ?? string.Empty;
        int standaloneMajor = Docker.MajorVersion(resolution.StandaloneAttempt?.StdOut ?? string.Empty);

        string where = _executor.IsLocal ? "this host" : _executor.Target;

        // A real v1. The only case where refusing is the right answer.
        if (standaloneMajor == 1)
        {
            return $"Only Compose v1 is available on {where} (docker-compose {resolution.StandaloneAttempt!.StdOut.Trim()}).\n\n"
                + "This install needs v2. The two differ in profile handling and in\n"
                + "`depends_on` conditions, which the generated compose file relies on, so v1 would\n"
                + "produce a subtly different deployment rather than an obviously broken one.\n\n"
                + "Install it with:  sudo apt-get install docker-compose-plugin\n"
                + "or see https://docs.docker.com/compose/install/";
        }

        // The case worth naming outright rather than listing as a possibility: it works as the login
        // user and not through sudo. That is a plugin under the user's HOME, which root cannot see.
        if (resolution.WorksWithoutElevation)
        {
            return $"Docker Compose v2 is available to {_executor.Target} but not through sudo.\n\n"
                + "`docker compose version` succeeds as that user and fails under `sudo`, which means\n"
                + "the Compose plugin is installed for the user rather than system-wide: it lives in\n"
                + "~/.docker/cli-plugins, and sudo runs with root's HOME, so root cannot see it.\n\n"
                + "Either of these fixes it:\n\n"
                + "  Install Compose system-wide, so root can see it too:\n"
                + "      sudo apt-get install docker-compose-plugin\n\n"
                + "  Or stop using sudo — add the user to the docker group, log out and back in,\n"
                + "  then turn off \"use sudo\" on the connection page:\n"
                + "      sudo usermod -aG docker $USER\n\n"
                + "The second only works if the install directory is also writable by that user.";
        }

        // Both probes failed for some other reason.
        string message =
            $"Could not find Docker Compose v2 on {where}.\n\n"
            + $"  `docker compose version` said:  {Summarise(plugin)}\n"
            + $"  `docker-compose version` said:  {Summarise(standalone)}\n\n"
            + "Both forms are accepted — the CLI plugin and the standalone binary are both v2, and\n"
            + "either will do. Note that `docker-compose --version` succeeding by hand does not mean\n"
            + "this check should pass, because the installer may be running as a different user.\n\n"
            + "Worth checking, in order:\n"
            + "  1. Run exactly this, as the user the installer connects as:\n"
            + "       docker compose version\n";

        if (!_executor.IsLocal)
        {
            message +=
                "  2. If that works but this does not, the difference is sudo or PATH. A plugin\n"
                + "     installed for your user lives in ~/.docker/cli-plugins, and sudo runs with\n"
                + "     root's HOME, so root cannot see it. Either install it system-wide\n"
                + "     (sudo apt-get install docker-compose-plugin), or add your user to the\n"
                + "     docker group and turn off \"use sudo\" on the connection page.\n"
                + "  3. Confirm with:  sudo docker compose version\n";
        }
        else
        {
            message += "  2. Confirm the shell running this installer has the same PATH as yours.\n";
        }

        return message;
    }

    private static string Summarise(string output)
    {
        if (output.Length == 0)
        {
            return "(nothing)";
        }

        string first = output.Split('\n')[0].Trim();

        return first.Length > 120 ? first[..117] + "..." : first;
    }

    /// <summary>
    /// The install directory. Created if missing; refused if it cannot be written to, because every
    /// later step writes there.
    /// </summary>
    public void RequireWritableDirectory(string path)
    {
        _executor.EnsureWritableDirectory(path);
        _log.Step("install directory", path);
    }

    /// <summary>
    /// The ports the deployment will bind. A warning rather than an error: the usual cause is this
    /// same deployment already running, which is what a re-run looks like. A genuinely foreign
    /// listener is the operator's to resolve, and refusing to proceed would be presumptuous when
    /// they may be about to stop it.
    /// </summary>
    public void CheckPorts(IReadOnlyList<int> ports)
    {
        foreach (int port in ports)
        {
            if (_executor.IsPortFree(port))
            {
                _log.Step($"port {port}", "free");
                continue;
            }

            _log.Step($"port {port}", "in use");
            _log.Warn($"Something is already listening on port {port}.");
            _log.Detail(
                "If that is a previous EntKube install, this run replaces it and the port is released "
                + "as part of that. If it is another service — a distribution's nginx or apache is the "
                + "usual one — stop it first, or Caddy will fail to bind and no certificate will be issued.");
        }
    }

    /// <summary>
    /// Catches an interpolation mistake — a variable the renderer failed to write — before any image
    /// is pulled, which is the difference between a five-second failure and a five-minute one.
    /// </summary>
    public void ValidateCompose()
    {
        ExecResult config = _docker.Config();

        if (!config.Ok)
        {
            throw new InstallAbortedException(
                "The generated compose file is not valid:\n\n" + Indent(config.Output) + "\n\n"
                + "This is a defect in the installer, not something you did. The files are on disk;\n"
                + "please report the output above.");
        }

        _log.Step("docker compose config", "ok");
    }

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(l => "  " + l.TrimEnd()));
}
