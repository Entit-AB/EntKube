using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EntKube.Installer.Gui.Views;

/// <summary>
/// Asks whether to trust a server's host key.
///
/// This exists because the alternative — accepting whatever key is offered — would leave the
/// connection open to interception by anything on the network path, and this particular connection
/// carries a sudo password and writes the vault root key. SSH.NET does not check known_hosts itself,
/// so if the application does not do it, nothing does.
///
/// A key already in known_hosts never reaches this dialog; see <see cref="SshExecutor.Connect"/>.
/// </summary>
public partial class HostKeyDialog : Window
{
    private bool _accepted;

    public HostKeyDialog() => InitializeComponent();

    /// <summary>
    /// Shows the dialog and returns whether to trust the key. Must be called on the UI thread.
    ///
    /// With no owner window — which should not happen, but would leave a modal dialog with no parent
    /// and no way to show it — the answer is no. Defaulting to "trust" when the question cannot be
    /// put to anyone is exactly the wrong way for this to fail.
    /// </summary>
    public static async Task<bool> AskAsync(Window? owner, HostKey key)
    {
        if (owner is null)
        {
            return false;
        }

        HostKeyDialog dialog = new();

        dialog.HostText.Text = key.Host;
        dialog.AlgorithmText.Text = key.Algorithm;
        dialog.FingerprintText.Text = key.Sha256;

        dialog.AcceptButton.Click += dialog.OnAccept;
        dialog.RejectButton.Click += dialog.OnReject;

        await dialog.ShowDialog(owner);

        if (dialog._accepted && dialog.RememberBox.IsChecked == true)
        {
            try
            {
                key.AddToKnownHosts();
            }
            catch (IOException)
            {
                // Not being able to record the decision does not invalidate it — the operator still
                // approved this key for this session. It only means they will be asked again.
            }
        }

        return dialog._accepted;
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void OnReject(object? sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }
}
