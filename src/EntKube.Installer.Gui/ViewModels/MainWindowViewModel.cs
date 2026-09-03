using System.Collections.ObjectModel;
using Avalonia.Controls;
using EntKube.Installer.Gui.Services;
using EntKube.Installer.Gui.Views;

namespace EntKube.Installer.Gui.ViewModels;

public enum Step
{
    Target,
    Configure,
    Install,
    ClientTools,
}

/// <summary>
/// The wizard: which step is showing, and what happens when the buttons are pressed.
///
/// The install itself is not implemented here. It is <see cref="InstallRunner"/>, the same type the
/// console installer calls, pointed at an <see cref="SshExecutor"/> instead of a
/// <see cref="LocalExecutor"/>. This class connects a form to it and keeps the window responsive
/// while it runs.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private Step _step = Step.Target;
    private bool _busy;
    private string _status = string.Empty;
    private string? _error;
    private bool _connected;
    private bool _installSucceeded;

    private IExecutor? _executor;
    private Answers? _answers;
    private DetectedDeployment _detected = DetectedDeployment.Nothing;

    public TargetViewModel TargetStep { get; } = new();

    public ConfigurationViewModel ConfigureStep { get; } = new();

    public ClientToolsViewModel ClientToolsStep { get; } = new();

    public ObservableCollection<LogLineView> Log { get; } = [];

    /// <summary>Set by App so dialogs — the host key prompt, the folder picker — have a parent.</summary>
    public Window? Owner { get; set; }

    // ── Step state ───────────────────────────────────────────────────────────────────────────────

    public Step Step
    {
        get => _step;
        private set => Set(ref _step, value,
        [
            nameof(IsTargetStep), nameof(IsConfigureStep), nameof(IsInstallStep), nameof(IsClientToolsStep),
            nameof(CanGoBack), nameof(PrimaryLabel), nameof(CanGoForward), nameof(Title), nameof(Subtitle),
        ]);
    }

    public bool IsTargetStep => _step == Step.Target;

    public bool IsConfigureStep => _step == Step.Configure;

    public bool IsInstallStep => _step == Step.Install;

    public bool IsClientToolsStep => _step == Step.ClientTools;

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value, [nameof(NotBusy), nameof(CanGoForward), nameof(CanGoBack)]);
    }

    public bool NotBusy => !_busy;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>The last failure, shown in a banner. Cleared whenever an action starts.</summary>
    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value, [nameof(HasError)]);
    }

    public bool HasError => _error is not null;

    public bool InstallSucceeded
    {
        get => _installSucceeded;
        private set => Set(ref _installSucceeded, value);
    }

    public string Title => _step switch
    {
        Step.Target => "Where should EntKube be installed?",
        Step.Configure => ConfigureStep.IsUpgrade ? "Review the configuration" : "Configure EntKube",
        Step.Install => "Installing",
        _ => "Client tools",
    };

    public string Subtitle => _step switch
    {
        Step.Target =>
            "The management plane runs under Docker Compose. Install it on a server over SSH, or on "
            + "this machine.",
        Step.Configure => ConfigureStep.IsAdoption
            ? "This target already has a deployment that was not created by this installer. The "
              + "answers below were read from it — from its compose file and its running containers "
              + "— so nothing is assumed. Change only what you mean to; the vault root key and "
              + "database password are reused as they are."
            : ConfigureStep.IsUpgrade
                ? "This target already has an install. Every value below is what is there now — "
                  + "change only what you mean to. The vault root key and database password are reused."
                : "These are the same questions the command-line installer asks.",
        Step.Install => "Writing the configuration, pulling the images and starting the stack.",
        _ => "Optional, and entirely local — nothing here touches the server.",
    };

    public string PrimaryLabel => _step switch
    {
        Step.Target => "Connect and check",
        Step.Configure => "Install",
        Step.Install => InstallSucceeded ? "Client tools" : "Retry",
        _ => "Finish",
    };

    public bool CanGoForward => !Busy && _step switch
    {
        Step.Target => TargetStep.IsValid,
        Step.Configure => ConfigureStep.IsValid,
        Step.Install => !Busy,
        _ => true,
    };

    public bool CanGoBack => !Busy && _step is Step.Configure or Step.Install;

    /// <summary>Re-evaluated when a form's validity changes, since the button lives out here.</summary>
    public void RefreshNavigation()
    {
        Raise(nameof(CanGoForward));
        Raise(nameof(CanGoBack));
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────────

    public void Back()
    {
        Error = null;

        Step = _step switch
        {
            Step.Configure => Step.Target,
            Step.Install => Step.Configure,
            _ => _step,
        };
    }

    public async Task PrimaryAsync()
    {
        Error = null;

        switch (_step)
        {
            case Step.Target:
                await ConnectAsync();
                break;

            case Step.Configure:
                Step = Step.Install;
                await InstallAsync();
                break;

            case Step.Install:
                if (InstallSucceeded)
                {
                    ClientToolsStep.ServerUrl = _answers?.PublicUrl;
                    Step = Step.ClientTools;
                }
                else
                {
                    await InstallAsync();
                }

                break;

            case Step.ClientTools:
                Owner?.Close();
                break;
        }
    }

    // ── Connect ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the connection, checks the host, and reads any existing .env so the next step can offer
    /// it back.
    ///
    /// All of it runs off the UI thread: SSH.NET is synchronous, and a connection to an unreachable
    /// host blocks for the full timeout. Doing this inline would freeze the window for twenty
    /// seconds and look like a crash.
    /// </summary>
    private async Task ConnectAsync()
    {
        Busy = true;
        Status = TargetStep.IsRemote ? $"Connecting to {TargetStep.Host}…" : "Checking this machine…";
        Log.Clear();

        string directory = TargetStep.Directory.Trim();

        try
        {
            _executor?.Dispose();
            _executor = null;

            IExecutor executor = TargetStep.IsRemote
                ? await ConnectSshAsync(directory)
                : new LocalExecutor(directory);

            GuiInstallLog log = new(Log);
            InstallRunner runner = new(executor, log);

            await Task.Run(() => runner.CheckHost(directory));

            bool isUpgrade = InstallRunner.IsUpgrade(executor, directory);
            EnvFile existing = InstallRunner.LoadExisting(executor, directory);

            // Read what is actually deployed before offering any answers, so an install this
            // installer did not create is adopted from its real shape rather than from defaults.
            DetectedDeployment detected = await Task.Run(() => runner.Inspect(directory));

            foreach (string finding in detected.Findings)
            {
                log.Detail(finding);
            }

            ConfigureStep.SeedFrom(existing, isUpgrade, detected);
            _detected = detected;

            _executor = executor;
            Status = detected.Exists
                ? detected.InstallerOwned
                    ? $"{executor.Target} — existing install found."
                    : $"{executor.Target} — existing deployment found; its settings have been read from it."
                : $"{executor.Target} — ready.";

            Step = Step.Configure;
        }
        catch (InstallAbortedException ex)
        {
            Error = ex.Message;
            Status = "Not connected.";
        }
        catch (Exception ex)
        {
            Error = $"Unexpected {ex.GetType().Name}: {ex.Message}";
            Status = "Not connected.";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Connects over SSH, asking about an unrecognised host key on the UI thread.
    ///
    /// The approval callback is invoked from inside SSH.NET on a background thread, so it hops back
    /// to the UI thread and blocks there until the operator answers. Blocking is correct: the
    /// handshake cannot proceed without the decision, and continuing on a maybe would defeat the
    /// point of asking.
    /// </summary>
    private Task<IExecutor> ConnectSshAsync(string directory)
    {
        SshCredentials credentials = TargetStep.ToCredentials();

        return Task.Run<IExecutor>(() => SshExecutor.Connect(
            credentials,
            directory,
            approveHostKey: key => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => HostKeyDialog.AskAsync(Owner, key)).GetAwaiter().GetResult()));
    }

    // ── Install ──────────────────────────────────────────────────────────────────────────────────

    private async Task InstallAsync()
    {
        if (_executor is null)
        {
            Error = "Not connected.";
            Step = Step.Target;
            return;
        }

        Busy = true;
        InstallSucceeded = false;
        Log.Clear();
        Status = "Installing…";

        try
        {
            string directory = TargetStep.Directory.Trim();
            bool isUpgrade = ConfigureStep.IsUpgrade;

            Answers answers = ConfigureStep.ToAnswers(directory, isUpgrade);
            _answers = answers;

            IExecutor executor = _executor;
            EnvFile env = ConfigureStep.Existing;
            GuiInstallLog log = new(Log);
            InstallRunner runner = new(executor, log);

            // What is about to change, before it changes. A service that disappears from the plan is
            // a container that gets stopped and left behind, and seeing that first is the difference
            // between a decision and a surprise.
            runner.ReportPlan(_detected, answers);

            bool applied = await Task.Run(() => runner.Apply(answers, env, detected: _detected));

            if (!applied)
            {
                Error = "The install did not complete. The log above says why.";
                Status = "Failed.";
                return;
            }

            Status = "Waiting for EntKube to answer…";

            bool answered = await Task.Run(() => runner.WaitForHttp(answers));

            InstallSucceeded = true;
            Status = answered
                ? $"Installed. EntKube is at {answers.PublicUrl}"
                : $"Started, but {answers.PublicUrl} has not answered yet — see the log.";
        }
        catch (InstallAbortedException ex)
        {
            Error = ex.Message;
            Status = "Failed.";
        }
        catch (Exception ex)
        {
            Error = $"Unexpected {ex.GetType().Name}: {ex.Message}";
            Status = "Failed.";
        }
        finally
        {
            Busy = false;
            Raise(nameof(PrimaryLabel));
        }
    }

    public void Dispose() => _executor?.Dispose();
}
