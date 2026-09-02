namespace EntKube.Installer;

/// <summary>
/// Where an install is being performed.
///
/// Everything above this interface — the preflight checks, the compose/.env renderer, the pull and
/// start sequence, the health probe — is written once and runs unchanged whether the target is this
/// machine or a server across an SSH connection. Only the four primitives below differ, which is why
/// they are the whole interface: run a command, write a file, read a file, and answer whether a port
/// is free.
///
/// The alternative was to ship the console installer to the server and run it there. That would have
/// meant a 76 MB upload per install and two copies of the installer inside the GUI, one per server
/// architecture, to cover a decision the GUI cannot make in advance. Sharing the code instead costs
/// this abstraction and nothing else.
/// </summary>
public interface IExecutor : IDisposable
{
    /// <summary>How to name this target in a message an operator reads. "this host", "ops@srv:22".</summary>
    string Target { get; }

    /// <summary>True when the target is this machine, which a few checks can shortcut.</summary>
    bool IsLocal { get; }

    /// <summary>
    /// Runs a command. Arguments are passed as a list rather than a string so that the SSH side can
    /// quote them itself — building a remote command line by concatenation is how a value with a
    /// space in it becomes two arguments, and how one with a semicolon becomes two commands.
    /// </summary>
    ExecResult Run(
        string file,
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        Action<string>? onLine = null);

    /// <summary>
    /// Writes a file in the install directory, keeping a timestamped copy of anything it replaces.
    ///
    /// <paramref name="secret"/> narrows the mode to owner-only where the platform has POSIX modes.
    /// The .env holds the vault root key and the database password, and it is read by whoever runs
    /// compose — nothing needs group or world access to it.
    /// </summary>
    void WriteFile(string path, string content, bool secret = false);

    /// <summary>
    /// True when commands are being elevated (sudo). Purely so a failure can say whether elevation
    /// is a candidate explanation for it.
    /// </summary>
    bool ElevationInUse { get; }

    /// <summary>
    /// Runs a command WITHOUT elevation, whatever <see cref="ElevationInUse"/> says.
    ///
    /// Exists for one diagnostic: a Compose plugin installed for the login user lives under that
    /// user's HOME, and sudo runs with root's, so `docker compose` can work by hand and fail through
    /// this installer. Being able to run the same probe both ways turns a list of things to check
    /// into a statement of what is actually wrong.
    /// </summary>
    ExecResult RunUnelevated(string file, IReadOnlyList<string> args, TimeSpan? timeout = null);

    /// <summary>Null when the file does not exist, rather than throwing — callers check both.</summary>
    string? ReadFile(string path);

    bool FileExists(string path);

    /// <summary>Creates the directory if absent, and throws <see cref="InstallAbortedException"/> if it cannot be written to.</summary>
    void EnsureWritableDirectory(string path);

    /// <summary>
    /// Whether nothing is listening on the port. Best-effort on a remote target, where it depends on
    /// a tool being present — an inconclusive answer is reported as free rather than as busy, since
    /// this only ever produces a warning.
    /// </summary>
    bool IsPortFree(int port);

    /// <summary>
    /// Fetches a URL from the target's point of view, returning the HTTP status or null if nothing
    /// answered.
    ///
    /// It has to be from the target, not from here: a deployment published on a local port is
    /// reachable at localhost *on the server*, and a request from the machine running the GUI would
    /// be measuring the wrong host entirely.
    /// </summary>
    int? ProbeHttp(string url);
}

public sealed record ExecResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Whichever stream the tool actually wrote to. Docker is inconsistent about it.</summary>
    public string Output => StdErr.Trim().Length > 0 ? StdErr.Trim() : StdOut.Trim();
}

/// <summary>
/// Thrown when an install cannot sensibly continue. Carries a message written for an operator rather
/// than a stack trace, and is caught at the top of both front-ends.
/// </summary>
public sealed class InstallAbortedException(string message) : Exception(message);

/// <summary>
/// Where progress goes. The console installer prints it; the GUI appends it to a log pane.
///
/// Separating this from the console <c>Prompt</c> is what let the preflight checks move into the
/// shared library — they report findings rather than drawing them.
/// </summary>
public interface IInstallLog
{
    /// <summary>A named check or action and how it turned out.</summary>
    void Step(string label, string outcome);

    /// <summary>Something the operator should read but which does not stop the install.</summary>
    void Warn(string message);

    /// <summary>Raw output from a command, or a line of explanation.</summary>
    void Detail(string message);
}
