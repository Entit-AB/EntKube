using Avalonia;

namespace EntKube.Installer.Gui;

/// <summary>
/// The EntKube installer, with a window.
///
/// It does the same install the console installer does — literally the same code, through
/// <see cref="InstallRunner"/> — but against a server reached over SSH rather than the machine it is
/// running on, and it can put the client-side tools on the local machine afterwards.
///
/// The console installer remains the right tool when you are already on the server. This one is for
/// installing from a desktop to a server you have SSH access to, without first working out how to
/// get a binary onto it.
/// </summary>
public static class Program
{
    // Must not use any Avalonia type before AppMain is called, so the platform is initialised first.
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // A GUI that fails before it has a window has nowhere to show an error, and on Windows
            // there is no console attached either. stderr is still the best of the available options
            // and is visible when launched from a terminal, which is how a failing app gets run the
            // second time.
            Console.Error.WriteLine($"entkube-installer failed to start: {ex}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Bundling a font rather than relying on the system's means the layout is the same on a
            // minimal Linux desktop as on macOS, instead of falling back to whatever is installed.
            .WithInterFont()
            .LogToTrace();
}
