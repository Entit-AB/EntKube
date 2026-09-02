using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EntKube.Installer.Gui.ViewModels;
using EntKube.Installer.Gui.Views;

namespace EntKube.Installer.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel viewModel = new();

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            viewModel.Owner = desktop.MainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
