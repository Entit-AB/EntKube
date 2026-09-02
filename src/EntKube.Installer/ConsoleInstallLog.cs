namespace EntKube.Installer;

/// <summary>
/// Routes the shared install's progress to the console wizard's own formatting, so a step reported
/// by <see cref="InstallRunner"/> looks like every other line the console installer prints.
/// </summary>
public sealed class ConsoleInstallLog(Prompt prompt) : IInstallLog
{
    private readonly Prompt _prompt = prompt;

    public void Step(string label, string outcome) => _prompt.Step(label, outcome);

    public void Warn(string message)
    {
        foreach (string line in message.Split('\n'))
        {
            _prompt.Warn(line.TrimEnd());
        }
    }

    public void Detail(string message) => _prompt.Info("  " + message.TrimEnd());
}
