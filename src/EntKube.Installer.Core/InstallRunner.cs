using System.Runtime.InteropServices;

namespace EntKube.Installer;

/// <summary>
/// Performs an install against a target: preflight, write, validate, pull, start, wait.
///
/// This is the whole install, and both front-ends call it rather than reproducing it. The console
/// installer passes a <see cref="LocalExecutor"/>; the GUI passes an <see cref="SshExecutor"/>. There
/// is no third path, so there is nothing for the two to drift apart on.
/// </summary>
public sealed class InstallRunner(IExecutor executor, IInstallLog log)
{
    private readonly IExecutor _executor = executor;
    private readonly IInstallLog _log = log;
    private readonly Docker _docker = new(executor);

    /// <summary>Loads the .env already on the target, so a re-run defaults to what is there.</summary>
    public static EnvFile LoadExisting(IExecutor executor, string directory)
    {
        string path = directory.TrimEnd('/', '\\') + "/.env";
        string? content = executor.ReadFile(path);

        return content is null ? new EnvFile() : EnvFile.Parse(content);
    }

    public static bool IsUpgrade(IExecutor executor, string directory) =>
        executor.FileExists(directory.TrimEnd('/', '\\') + "/.env");

    /// <summary>
    /// Works out what is already deployed at the target, so an install the installer did not create
    /// can be adopted from its actual shape rather than from defaults it never recorded.
    /// </summary>
    public DetectedDeployment Inspect(string directory) =>
        new DeploymentInspector(_executor, _docker).Inspect(directory);

    /// <summary>
    /// Reports what was detected and what applying <paramref name="answers"/> would change.
    ///
    /// Called before anything is written. A service in <c>Removed</c> is a container that will be
    /// stopped and left behind, which is worth seeing beforehand rather than discovering afterwards.
    /// </summary>
    public DeploymentChange ReportPlan(DetectedDeployment detected, Answers answers)
    {
        // Findings are deliberately not repeated here. Both front-ends show them when the target is
        // inspected, which is before any answer is offered — printing them again next to the plan
        // reads as two different reports of the same thing.
        DeploymentChange change = detected.ChangeFrom(answers);
        _lastChangeRemovesNothing = change.Removed.Count == 0;

        if (!detected.Exists)
        {
            return change;
        }

        _log.Step($"services ({change.Baseline})",
            string.Join(", ", change.Unchanged.Concat(change.Removed).Order()));

        if (change.Added.Count > 0)
        {
            _log.Step("will be added", string.Join(", ", change.Added));
        }

        if (change.Removed.Count > 0)
        {
            _log.Warn("These services will no longer be part of the deployment: "
                + string.Join(", ", change.Removed));
            _log.Detail(
                "Their containers are stopped and left behind, and named volumes are kept — "
                + "`docker compose down` does not delete data, only `down -v` does. If that is not "
                + "what you meant, go back and change the answer that removes them.");
        }

        if (change.IsNoOp)
        {
            _log.Step("service changes", "none");
        }

        return change;
    }

    /// <summary>Tooling and directory. Run before asking any questions, so answers are not wasted.</summary>
    public void CheckHost(string directory)
    {
        Preflight preflight = new(_executor, _log, _docker);
        preflight.RequireTooling();
        preflight.RequireWritableDirectory(directory);
    }

    /// <summary>
    /// Writes the configuration and starts the deployment.
    ///
    /// Returns false when the pull or the start failed, having already explained why. A failure to
    /// *answer* afterwards is not a failure of the install — see <see cref="WaitForHttp"/>.
    /// </summary>
    public bool Apply(
        Answers answers, EnvFile env, bool skipStart = false, DetectedDeployment? detected = null)
    {
        GuardExistingData(detected ?? DetectedDeployment.Nothing, answers);

        Renderer renderer = new(answers);

        foreach (string path in renderer.Write(_executor, env))
        {
            _log.Step(PathName(path), path);
        }

        if (skipStart)
        {
            return true;
        }

        // Only now, because the answers decide which ports are actually bound.
        Preflight preflight = new(_executor, _log, _docker);
        preflight.CheckPorts(answers.PublishedPorts);
        preflight.ValidateCompose();

        return Pull(answers) && Start(answers, removeOrphans: _lastChangeRemovesNothing);
    }

    private bool _lastChangeRemovesNothing = true;

    /// <summary>
    /// Refuses to proceed where applying these answers would leave an existing database behind.
    ///
    /// This is the one failure that is both silent and total. Postgres applied to a different volume
    /// does not error — it initialises a new, empty data directory and starts perfectly, while the
    /// real data sits in a volume nothing mounts any more. Nothing is deleted and everything is
    /// gone, and the first sign of it is an empty application.
    ///
    /// So anything that would change which data directory is used, or that would drop the database
    /// service outright, stops here and says what it found.
    /// </summary>
    private void GuardExistingData(DetectedDeployment detected, Answers answers)
    {
        if (!detected.Exists)
        {
            return;
        }

        // A database service that exists but could not be read. Proceeding would generate the
        // template's own Postgres, which is the data-loss case exactly.
        if (detected.UnrepresentableDatabase is { } problem && answers.Database == Database.Bundled)
        {
            throw new InstallAbortedException(
                $"There is already a PostgreSQL service here, but {problem}.\n\n"
                + "Continuing would write a database service with this installer's own defaults —\n"
                + "a different data location — and the application would come up against an empty\n"
                + "database while the existing data stayed where it is.\n\n"
                + "Nothing has been changed. Either give the existing service a named volume mounted\n"
                + "at /var/lib/postgresql/data, or point this installer at the database as an\n"
                + "external one and leave its service out of the generated file.");
        }

        // The database exists and was read, but the answers would not use it.
        if (detected.AdoptedDatabase is { } existing)
        {
            if (answers.Database != Database.Bundled)
            {
                throw new InstallAbortedException(
                    $"This deployment has a PostgreSQL service ({existing.Describe}), but the chosen\n"
                    + $"database option would remove it from the compose file.\n\n"
                    + "Its data would remain in place but nothing would mount it, and the application\n"
                    + "would run against a different, empty database.\n\n"
                    + "Nothing has been changed. Choose the bundled database to keep using it, or move\n"
                    + "the data yourself first if you really mean to switch.");
            }

            if (!ReferenceEquals(answers.AdoptedDatabase, existing) && answers.AdoptedDatabase is null)
            {
                throw new InstallAbortedException(
                    $"This deployment's PostgreSQL service ({existing.Describe}) was found but is not\n"
                    + "being carried over, so the generated file would use this installer's defaults\n"
                    + "and a different data location.\n\n"
                    + "Nothing has been changed. This is a defect in the installer rather than\n"
                    + "something you did — please report it.");
            }
        }
    }

    private bool Pull(Answers a)
    {
        _log.Detail("Pulling images. The first run downloads a few hundred MB.");

        ExecResult pull = _docker.Pull(a.Services, _log.Detail);

        if (pull.Ok)
        {
            return true;
        }

        _log.Warn("Pull failed.");
        _log.Detail(pull.Output);
        _log.Warn(ExplainPullFailure(pull.Output, a, _executor));

        return false;
    }

    private bool Start(Answers a, bool removeOrphans)
    {
        if (!removeOrphans)
        {
            _log.Warn("Starting without --remove-orphans, because this change drops services.");
            _log.Detail(
                "Their containers are left running rather than removed, so nothing you are still "
                + "using disappears as a side effect. Remove them deliberately when you are ready: "
                + "docker compose down --remove-orphans");
        }

        ExecResult up = _docker.Up(a.Services, _log.Detail, removeOrphans);

        if (up.Ok)
        {
            return true;
        }

        _log.Warn("Start failed.");
        _log.Detail(up.Output);

        return false;
    }

    /// <summary>
    /// Turns a pull failure into the thing to actually do about it.
    ///
    /// The platform case is the one worth detecting by hand. Docker reports it as "no match for
    /// platform in manifest", which reads like a registry fault and is not one — it means the
    /// manifest list holds no entry for the target's architecture. Guessing "check your credentials"
    /// at an operator on an arm64 server would send them somewhere with no answer in it.
    /// </summary>
    internal static string ExplainPullFailure(string output, Answers a, IExecutor executor)
    {
        bool platformMismatch =
            output.Contains("no match for platform", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no matching manifest", StringComparison.OrdinalIgnoreCase);

        if (platformMismatch)
        {
            // Only meaningful for a local install; a remote host's architecture is whatever the
            // error message just named, and asserting this machine's would be wrong.
            string where = executor.IsLocal
                ? $"This host is {RuntimeInformation.OSArchitecture}, and"
                : "The target's architecture is named in the message above, and";

            // Deliberately not "the tag is amd64 only": the official image is built for linux/amd64
            // and linux/arm64, so on the default registry this now means a tag that predates that
            // change, or a mirror built for one architecture. Naming a cause that is no longer true
            // would send someone to fix the wrong thing.
            return $"{where} {a.Registry}/entkube:{a.ImageTag} has no image for it. This is not a "
                + "credentials or network problem — the manifest list simply has no entry for that "
                + "architecture. Official images are published for linux/amd64 and linux/arm64, so "
                + "check whether this tag predates that, or whether this registry is a mirror built "
                + "for one architecture. To build both yourself: scripts/release.sh web "
                + "--platforms linux/amd64,linux/arm64 --registry <yours> --push";
        }

        if (output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || output.Contains("authentication required", StringComparison.OrdinalIgnoreCase))
        {
            return a.RegistryUsername is null
                ? $"{a.Registry} requires authentication. Supply a registry username and password."
                : $"{a.Registry} rejected the credentials for '{a.RegistryUsername}'.";
        }

        return $"Check that the target can reach {a.Registry}, and that the tag '{a.ImageTag}' exists.";
    }

    /// <summary>
    /// Waits for the app to answer, and reports what it found either way.
    ///
    /// Not answering is <b>not</b> treated as a failed install. On a public domain the first request
    /// waits on an ACME order, which needs DNS to have propagated and port 80 to be reachable from
    /// Let's Encrypt — neither is under this installer's control, and both can resolve themselves
    /// minutes later. Reporting a healthy start as a failure would send an operator hunting for a
    /// problem that is about to disappear, so a timeout says what to check instead.
    /// </summary>
    public bool WaitForHttp(Answers a, CancellationToken cancellation = default)
    {
        TimeSpan budget = a.Exposure == Exposure.PublicTls ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(2);
        DateTime deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline && !cancellation.IsCancellationRequested)
        {
            int? status = _executor.ProbeHttp(a.PublicUrl);

            if (status is > 0)
            {
                // Any HTTP answer at all means the app is up and routing. The sign-in redirect is
                // the expected one, so a 200 is not required — only that something replied.
                _log.Step("HTTP", $"{status} from {a.PublicUrl}");
                return true;
            }

            if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                break;
            }
        }

        _log.Warn($"No answer from {a.PublicUrl} after {budget.TotalMinutes:0} minutes.");

        ExecResult ps = _docker.PsDetailed();

        foreach (string line in ps.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            _log.Detail("  " + line.Replace('\t', ' '));
        }

        _log.Detail(a.Exposure == Exposure.PublicTls
            ? $"The containers may still be fine. A public domain waits on a Let's Encrypt order, "
              + $"which needs {a.Domain} to already resolve to this host and port 80 to be reachable "
              + "from the internet. Check with:  docker compose logs caddy"
            : "Check the application log with:  docker compose logs entkube");

        return false;
    }

    private static string PathName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
