namespace EntKube.Installer.Gui.ViewModels;

/// <summary>
/// Step 1 — which machine is being installed to, and how to reach it.
///
/// Local is offered as well as SSH. The executor abstraction makes it free, and it is genuinely
/// useful: a developer standing up an evaluation instance on their own machine should not have to
/// SSH to localhost to use this.
/// </summary>
public sealed class TargetViewModel : ViewModelBase
{
    private bool _isRemote = true;
    private string _host = string.Empty;
    private string _port = "22";
    private string _username = Environment.UserName;
    private bool _usePrivateKey = true;
    private string _privateKeyPath = SshCredentials.DiscoverPrivateKeys().FirstOrDefault() ?? string.Empty;
    private string _privateKeyPassphrase = string.Empty;
    private string _password = string.Empty;
    private bool _useSudo = true;
    private string _sudoPassword = string.Empty;
    private string _directory = "/opt/entkube";

    private static readonly string[] Validity = [nameof(IsValid), nameof(Summary)];

    public bool IsRemote
    {
        get => _isRemote;
        set
        {
            if (Set(ref _isRemote, value, [nameof(IsLocal), .. Validity]))
            {
                // A local install on a desktop defaults somewhere the user can actually write; the
                // documented /opt/entkube is a server path and needs root.
                Directory = value
                    ? "/opt/entkube"
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "entkube");
            }
        }
    }

    public bool IsLocal
    {
        get => !_isRemote;
        set => IsRemote = !value;
    }

    public string Host
    {
        get => _host;
        set => Set(ref _host, value, Validity);
    }

    public string Port
    {
        get => _port;
        set => Set(ref _port, value, Validity);
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value, Validity);
    }

    public bool UsePrivateKey
    {
        get => _usePrivateKey;
        set => Set(ref _usePrivateKey, value, [nameof(UsePassword), .. Validity]);
    }

    public bool UsePassword
    {
        get => !_usePrivateKey;
        set => UsePrivateKey = !value;
    }

    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set => Set(ref _privateKeyPath, value, Validity);
    }

    public string PrivateKeyPassphrase
    {
        get => _privateKeyPassphrase;
        set => Set(ref _privateKeyPassphrase, value);
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value, Validity);
    }

    public bool UseSudo
    {
        get => _useSudo;
        set => Set(ref _useSudo, value);
    }

    public string SudoPassword
    {
        get => _sudoPassword;
        set => Set(ref _sudoPassword, value);
    }

    public string Directory
    {
        get => _directory;
        set => Set(ref _directory, value, Validity);
    }

    /// <summary>Keys found in ~/.ssh, so the field starts with a sensible value rather than empty.</summary>
    public IReadOnlyList<string> DiscoveredKeys { get; } = SshCredentials.DiscoverPrivateKeys();

    public bool IsValid
    {
        get
        {
            if (Directory.Trim().Length == 0)
            {
                return false;
            }

            if (!IsRemote)
            {
                return true;
            }

            if (Host.Trim().Length == 0 || Username.Trim().Length == 0)
            {
                return false;
            }

            if (!int.TryParse(Port, out int port) || port is < 1 or > 65535)
            {
                return false;
            }

            return UsePrivateKey ? PrivateKeyPath.Trim().Length > 0 : Password.Length > 0;
        }
    }

    public string Summary => IsRemote
        ? $"{Username}@{Host}:{Port} → {Directory}"
        : $"this machine → {Directory}";

    public SshCredentials ToCredentials() => new()
    {
        Host = Host.Trim(),
        Port = int.TryParse(Port, out int p) ? p : 22,
        Username = Username.Trim(),
        Auth = UsePrivateKey ? SshAuth.PrivateKey : SshAuth.Password,
        Password = UsePassword ? Password : null,
        PrivateKeyPath = UsePrivateKey ? PrivateKeyPath.Trim() : null,
        PrivateKeyPassphrase = PrivateKeyPassphrase.Length > 0 ? PrivateKeyPassphrase : null,
        UseSudo = UseSudo,
        SudoPassword = UseSudo && SudoPassword.Length > 0 ? SudoPassword : null,
    };
}
