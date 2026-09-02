using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EntKube.Installer.Gui.ViewModels;

namespace EntKube.Installer.Gui.Views;

/// <summary>
/// The wizard window.
///
/// Buttons are wired here rather than through commands: the app has four of them, and an ICommand
/// implementation plus its CanExecute plumbing would be more code than the handlers it replaces.
/// Everything they do lives on the view model.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // x:Name in the XAML generates these fields, so the wiring is checked at compile time
        // rather than failing at run time on a typo'd control name.
        PrimaryButton.Click += OnPrimary;
        BackButton.Click += OnBack;
        InstallToolsButton.Click += OnInstallTools;
        BrowseKeyButton.Click += OnBrowseKey;
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private async void OnPrimary(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            await model.PrimaryAsync();
        }
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Model?.Back();

    private void OnInstallTools(object? sender, RoutedEventArgs e) => Model?.ClientToolsStep.Install();

    /// <summary>
    /// Picks a private key file.
    ///
    /// No file-type filter: SSH keys have no extension by convention (id_ed25519), so filtering by
    /// one would hide exactly the files being looked for.
    /// </summary>
    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an SSH private key",
            AllowMultiple = false,
        });

        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            model.TargetStep.PrivateKeyPath = path;
        }
    }
}
