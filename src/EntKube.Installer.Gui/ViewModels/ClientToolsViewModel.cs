using System.Collections.ObjectModel;
using EntKube.Installer.Gui.Services;

namespace EntKube.Installer.Gui.ViewModels;

/// <summary>One tool with a checkbox and whether it is actually available to install.</summary>
public sealed class ClientToolChoice(ClientTool tool) : ViewModelBase
{
    private bool _selected;

    public ClientTool Tool { get; } = tool;

    public string Name => Tool.Name;

    public string Summary => Tool.Summary;

    public string? SourcePath { get; } = ToolBundle.Locate(tool);

    public bool IsAvailable => SourcePath is not null;

    /// <summary>Selected by default when present — someone who bundled it probably wants it.</summary>
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Availability => IsAvailable
        ? "ready"
        : $"not bundled — build with: scripts/release.sh {Tool.Key} --rid {ToolBundle.CurrentRid}";
}

/// <summary>
/// Step 4 — put the client-side tools on the machine running the GUI.
///
/// Entirely optional and entirely local: nothing here touches the server. It is on the end of the
/// server install because that is when the URL is known and when someone is most likely to want a
/// CLI pointed at what they just built.
/// </summary>
public sealed class ClientToolsViewModel : ViewModelBase
{
    private string _directory = ClientToolInstaller.DefaultDirectory;
    private string _nextSteps = string.Empty;
    private bool _hasRun;

    public ClientToolsViewModel()
    {
        foreach (ClientTool tool in ToolBundle.All)
        {
            ClientToolChoice choice = new(tool) { Selected = false };
            Tools.Add(choice);
        }

        // Preselect what is actually present, so the common case is one click.
        foreach (ClientToolChoice choice in Tools.Where(t => t.IsAvailable))
        {
            choice.Selected = true;
        }
    }

    public ObservableCollection<ClientToolChoice> Tools { get; } = [];

    public ObservableCollection<string> Results { get; } = [];

    /// <summary>Set from the install step so the generated snippets carry the real URL.</summary>
    public string? ServerUrl { get; set; }

    public string Directory
    {
        get => _directory;
        set => Set(ref _directory, value, [nameof(PathWarning), nameof(HasPathWarning)]);
    }

    public bool AnyAvailable => Tools.Any(t => t.IsAvailable);

    public string BundleStatus => AnyAvailable
        ? $"Installing for {ToolBundle.CurrentRid}."
        : "No tools were bundled with this build. Run: scripts/release.sh gui";

    public string? PathWarning => ClientToolInstaller.IsOnPath(Directory)
        ? null
        : $"{Directory} is not on your PATH — the tools will install correctly but will not be found by name.";

    public bool HasPathWarning => PathWarning is not null;

    public string NextSteps
    {
        get => _nextSteps;
        private set => Set(ref _nextSteps, value, [nameof(HasNextSteps)]);
    }

    public bool HasNextSteps => _nextSteps.Length > 0;

    public bool HasRun
    {
        get => _hasRun;
        private set => Set(ref _hasRun, value);
    }

    public void Install()
    {
        Results.Clear();

        List<ClientTool> selected = [.. Tools.Where(t => t.Selected && t.IsAvailable).Select(t => t.Tool)];

        if (selected.Count == 0)
        {
            Results.Add("Nothing selected.");
            HasRun = true;
            return;
        }

        IReadOnlyList<ClientToolInstaller.Result> results =
            new ClientToolInstaller().Install(selected, Directory.Trim(), ServerUrl);

        foreach (ClientToolInstaller.Result result in results)
        {
            Results.Add($"{(result.Installed ? "✓" : "✗")}  {result.Tool.Name} — {result.Message}");
        }

        NextSteps = ClientToolInstaller.NextSteps(results, Directory.Trim(), ServerUrl);
        HasRun = true;
    }
}
