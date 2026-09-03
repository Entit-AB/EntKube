using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using EntKube.Installer.Gui.ViewModels;

namespace EntKube.Installer.Gui.Services;

/// <summary>
/// Receives progress from the shared <see cref="InstallRunner"/> and puts it on screen.
///
/// The install runs on a background thread — it makes blocking SSH calls that would freeze the
/// window — so every append is marshalled to the UI thread. Avalonia throws when a bound collection
/// changes off it, which would turn a slow pull into a crash rather than a slow pull.
/// </summary>
public sealed class GuiInstallLog(ObservableCollection<LogLineView> sink) : IInstallLog
{
    private readonly ObservableCollection<LogLineView> _sink = sink;

    public void Step(string label, string outcome) =>
        Append(new LogLineView($"{label}: {outcome}", LogKind.Step));

    public void Warn(string message) => AppendMultiline(message, LogKind.Warning);

    public void Detail(string message) => AppendMultiline(message, LogKind.Detail);

    private void AppendMultiline(string message, LogKind kind)
    {
        foreach (string line in message.Split('\n'))
        {
            string trimmed = line.TrimEnd();

            if (trimmed.Length > 0)
            {
                Append(new LogLineView(trimmed, kind));
            }
        }
    }

    private void Append(LogLineView line)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _sink.Add(line);
            return;
        }

        // Post, not InvokeAsync: the install thread should not wait on the UI thread to render, and
        // a pull that emits hundreds of progress lines would otherwise spend its time blocked.
        Dispatcher.UIThread.Post(() => _sink.Add(line));
    }
}
