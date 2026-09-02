using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace EntKube.Installer;

/// <summary>
/// Installs onto a server over SSH.
///
/// Holds one SSH connection for commands and one SFTP connection for files, both opened by
/// <see cref="Connect"/> and closed on dispose. Everything above <see cref="IExecutor"/> is unaware
/// which of the two executors it is talking to.
/// </summary>
public sealed class SshExecutor : IExecutor
{
    private readonly SshCredentials _credentials;
    private readonly string _directory;
    private readonly SshClient _ssh;
    private readonly SftpClient _sftp;

    private SshExecutor(SshCredentials credentials, string directory, SshClient ssh, SftpClient sftp)
    {
        _credentials = credentials;
        _directory = directory;
        _ssh = ssh;
        _sftp = sftp;
    }

    public string Target => _credentials.Describe;

    public bool IsLocal => false;

    public bool ElevationInUse => _credentials.UseSudo;

    public ExecResult RunUnelevated(string file, IReadOnlyList<string> args, TimeSpan? timeout = null)
    {
        if (!_credentials.UseSudo)
        {
            return Run(file, args, timeout);
        }

        string command = $"cd {Quote(_directory)} && {Quote(file)}"
            + string.Concat(args.Select(a => " " + Quote(a)));

        return Execute(command, timeout);
    }

    /// <summary>
    /// Opens both connections.
    ///
    /// <paramref name="approveHostKey"/> is called only for a key that is not already in
    /// known_hosts, and returning false aborts the connection. It is a required parameter rather
    /// than an optional one so that no caller can accidentally get trust-everything behaviour by
    /// leaving an argument off — this session carries a sudo password and writes the vault root key,
    /// so an unverified peer is not an acceptable default.
    /// </summary>
    public static SshExecutor Connect(
        SshCredentials credentials,
        string directory,
        Func<HostKey, bool> approveHostKey,
        TimeSpan? timeout = null)
    {
        ConnectionInfo info = BuildConnectionInfo(credentials, timeout ?? TimeSpan.FromSeconds(20));

        SshClient ssh = new(info);
        SftpClient sftp = new(info);

        bool hostKeyRejected = false;

        void OnHostKey(object? _, HostKeyEventArgs e)
        {
            HostKey key = new(credentials.Host, e.HostKeyName, e.HostKey);

            if (key.IsInKnownHosts())
            {
                e.CanTrust = true;
                return;
            }

            e.CanTrust = approveHostKey(key);
            hostKeyRejected = !e.CanTrust;
        }

        ssh.HostKeyReceived += OnHostKey;
        sftp.HostKeyReceived += OnHostKey;

        try
        {
            ssh.Connect();
            sftp.Connect();
        }
        catch (Exception ex)
        {
            ssh.Dispose();
            sftp.Dispose();

            if (hostKeyRejected)
            {
                throw new InstallAbortedException(
                    $"The host key for {credentials.Host} was not accepted, so nothing was sent to it.");
            }

            throw new InstallAbortedException(Explain(ex, credentials));
        }

        return new SshExecutor(credentials, directory, ssh, sftp);
    }

    private static ConnectionInfo BuildConnectionInfo(SshCredentials c, TimeSpan timeout)
    {
        AuthenticationMethod method;

        if (c.Auth == SshAuth.Password)
        {
            method = new PasswordAuthenticationMethod(c.Username, c.Password ?? string.Empty);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(c.PrivateKeyPath) || !File.Exists(c.PrivateKeyPath))
            {
                throw new InstallAbortedException(
                    $"Private key file not found: {c.PrivateKeyPath ?? "(none given)"}");
            }

            PrivateKeyFile key;

            try
            {
                key = string.IsNullOrEmpty(c.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(c.PrivateKeyPath)
                    : new PrivateKeyFile(c.PrivateKeyPath, c.PrivateKeyPassphrase);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                throw new InstallAbortedException(
                    $"{c.PrivateKeyPath} is encrypted and needs a passphrase.");
            }
            catch (SshException ex)
            {
                throw new InstallAbortedException(
                    $"Could not read {c.PrivateKeyPath}: {ex.Message}\n"
                    + "If the passphrase is wrong this is what it looks like.");
            }

            method = new PrivateKeyAuthenticationMethod(c.Username, key);
        }

        return new ConnectionInfo(c.Host, c.Port, c.Username, method) { Timeout = timeout };
    }

    /// <summary>Turns SSH.NET's exception types into something an operator can act on.</summary>
    private static string Explain(Exception ex, SshCredentials c) => ex switch
    {
        SshAuthenticationException =>
            $"{c.Describe} refused the credentials.\n"
            + (c.Auth == SshAuth.Password
                ? "Check the username and password. Many servers disable password login entirely — "
                  + "if so, use a private key."
                : $"Check that {c.PrivateKeyPath} is the right key and that its public half is in "
                  + $"~{c.Username}/.ssh/authorized_keys on the server."),

        SshConnectionException =>
            $"The connection to {c.Host}:{c.Port} was refused or dropped: {ex.Message}",

        System.Net.Sockets.SocketException =>
            $"Could not reach {c.Host}:{c.Port}: {ex.Message}\n"
            + "Check the hostname, the port, and that a firewall is not in the way.",

        OperationCanceledException or TimeoutException =>
            $"Timed out connecting to {c.Host}:{c.Port}.",

        _ => $"Could not connect to {c.Describe}: {ex.Message}",
    };

    public void Dispose()
    {
        _sftp.Dispose();
        _ssh.Dispose();
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────

    public ExecResult Run(
        string file,
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        Action<string>? onLine = null) => Execute(BuildCommand(file, args), timeout, onLine);

    private ExecResult Execute(string command, TimeSpan? timeout = null, Action<string>? onLine = null)
    {
        using SshCommand cmd = _ssh.CreateCommand(command);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(30);

        try
        {
            // Reading the stream as it arrives, rather than calling Execute() and taking the result,
            // is what makes a long `docker compose pull` show progress instead of appearing hung for
            // several minutes.
            IAsyncResult async = cmd.BeginExecute();
            StringBuilder stdout = new();

            using (StreamReader reader = new(cmd.OutputStream))
            {
                while (!async.IsCompleted || !reader.EndOfStream)
                {
                    string? line = reader.ReadLine();

                    if (line is null)
                    {
                        continue;
                    }

                    stdout.AppendLine(line);
                    onLine?.Invoke(line);
                }
            }

            cmd.EndExecute(async);

            string stderr = cmd.Error ?? string.Empty;

            foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                onLine?.Invoke(line.TrimEnd());
            }

            return new ExecResult(cmd.ExitStatus ?? 0, stdout.ToString(), stderr);
        }
        catch (SshOperationTimeoutException)
        {
            // The assembled command line rather than a program name: at this point the caller's
            // file/args have already been folded into it, and truncating keeps a long compose
            // invocation from filling the message.
            return new ExecResult(124, string.Empty,
                $"Timed out on {Target}: {(command.Length > 80 ? command[..77] + "..." : command)}");
        }
        catch (SshException ex)
        {
            return new ExecResult(255, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Assembles the remote command line: cd into the install directory, optionally via sudo, with
    /// every argument quoted.
    ///
    /// The sudo password goes to stdin (<c>sudo -S</c>), never on the command line, where it would
    /// be readable in the process list by any other user on the host.
    /// </summary>
    private string BuildCommand(string file, IReadOnlyList<string> args)
    {
        StringBuilder command = new();
        command.Append(Quote(file));

        foreach (string arg in args)
        {
            command.Append(' ').Append(Quote(arg));
        }

        if (!_credentials.UseSudo)
        {
            return $"cd {Quote(_directory)} && {command}";
        }

        if (_credentials.SudoPassword is null)
        {
            // -n: fail immediately rather than hang on a prompt that nothing is going to answer.
            return $"cd {Quote(_directory)} && sudo -n {command}";
        }

        // The cd must come BEFORE the pipe, not inside it. `printf … | cd dir && sudo -S …` parses
        // as `(printf | cd) && sudo`, so the password is fed to cd — which ignores stdin — and sudo
        // then blocks forever on a prompt with nothing behind it. That is not a slow install; it is
        // an install that never returns, and over SSH it looks exactly like a hung network.
        //
        // printf rather than echo: echo's handling of backslashes varies between shells, and a
        // password is exactly the kind of string that contains one.
        return $"cd {Quote(_directory)} && printf '%s\\n' {Quote(_credentials.SudoPassword)} "
            + $"| sudo -S -p '' {command}";
    }

    /// <summary>
    /// POSIX single-quoting. Everything inside single quotes is literal, so the only thing needing
    /// care is a single quote itself, which is closed, escaped and reopened.
    /// </summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Runs a raw shell snippet. For the few checks that are a pipeline rather than a command.</summary>
    private ExecResult RunShell(string script, TimeSpan? timeout = null)
    {
        using SshCommand cmd = _ssh.CreateCommand(script);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(1);

        try
        {
            string output = cmd.Execute();
            return new ExecResult(cmd.ExitStatus ?? 0, output, cmd.Error ?? string.Empty);
        }
        catch (SshException ex)
        {
            return new ExecResult(255, string.Empty, ex.Message);
        }
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────

    public void WriteFile(string path, string content, bool secret = false)
    {
        if (_sftp.Exists(path) && ReadFile(path) != content)
        {
            string backup = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";

            try
            {
                // No SFTP copy exists, and a download/upload round trip would be slower and could
                // corrupt a binary. cp on the server is both faster and exact.
                RunShell($"cp {Quote(path)} {Quote(backup)}");
            }
            catch (SshException)
            {
                // A backup that cannot be taken is not a reason to refuse the write; the operator is
                // re-running an installer that told them it replaces these files.
            }
        }

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(content));
        _sftp.UploadFile(stream, path, canOverride: true);

        if (secret)
        {
            // 600, written as the literal digits rather than as decimal 384: SSH.NET's
            // ChangePermissions takes the octal digits of the mode as a number and rejects anything
            // above 999, so the usual C-style 0600 is an ArgumentOutOfRangeException here.
            //
            // The .env holds the vault root key and the database password, and only the user running
            // compose needs to read it.
            _sftp.ChangePermissions(path, 600);
        }
    }

    public string? ReadFile(string path)
    {
        try
        {
            return _sftp.Exists(path) ? _sftp.ReadAllText(path) : null;
        }
        catch (SshException)
        {
            return null;
        }
    }

    public bool FileExists(string path)
    {
        try
        {
            return _sftp.Exists(path);
        }
        catch (SshException)
        {
            return false;
        }
    }

    public void EnsureWritableDirectory(string path)
    {
        // mkdir -p through the shell rather than SFTP, because it may need sudo — /opt/entkube is the
        // documented location and is not writable by an ordinary login user.
        string mkdir = _credentials.UseSudo
            ? $"sudo -n mkdir -p {Quote(path)} && sudo -n chown {Quote(_credentials.Username)} {Quote(path)}"
            : $"mkdir -p {Quote(path)}";

        if (_credentials is { UseSudo: true, SudoPassword: not null })
        {
            mkdir = $"printf '%s\\n' {Quote(_credentials.SudoPassword)} | sudo -S -p '' mkdir -p {Quote(path)} && "
                + $"printf '%s\\n' {Quote(_credentials.SudoPassword)} | sudo -S -p '' chown {Quote(_credentials.Username)} {Quote(path)}";
        }

        ExecResult made = RunShell(mkdir);

        if (!made.Ok)
        {
            throw new InstallAbortedException(
                $"Cannot create {path} on {Target}:\n\n  {made.Output}\n\n"
                + (_credentials.UseSudo
                    ? "sudo was used and still failed — check that this user may run sudo, and that the\n"
                      + "sudo password is right."
                    : "Choose a directory this user owns, or turn on \"use sudo\" on the connection page.\n"
                      + "/opt/entkube usually needs root."));
        }

        // Ownership is handed to the login user above precisely so that SFTP — which does not go
        // through sudo — can write the files afterwards. Prove it worked rather than assume it.
        string probe = $"{path.TrimEnd('/')}/.entkube-install-probe";
        ExecResult wrote = RunShell($"touch {Quote(probe)} && rm -f {Quote(probe)}");

        if (!wrote.Ok)
        {
            throw new InstallAbortedException(
                $"{path} exists on {Target} but {_credentials.Username} cannot write to it:\n\n"
                + $"  {wrote.Output}\n\n"
                + "The configuration files are uploaded over SFTP, which does not go through sudo, so\n"
                + "this directory has to be writable by the login user.");
        }
    }

    // ── Probes ───────────────────────────────────────────────────────────────────────────────────

    public bool IsPortFree(int port)
    {
        // ss first, then netstat: ss is standard on anything modern, netstat is what older or
        // minimal images still carry. If neither exists the check is inconclusive, and inconclusive
        // reports "free" — this only ever raises a warning, and a warning invented from a missing
        // tool is worse than no warning.
        ExecResult result = RunShell(
            $"(command -v ss > /dev/null && ss -ltn) || (command -v netstat > /dev/null && netstat -ltn) || echo __NOTOOL__");

        if (!result.Ok || result.StdOut.Contains("__NOTOOL__"))
        {
            return true;
        }

        foreach (string line in result.StdOut.Split('\n'))
        {
            // The local-address column ends in ":<port>". Matching the whole token avoids 8080
            // matching 80, which a plain Contains would.
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.LastIndexOf(':');

                if (colon > 0 && token[(colon + 1)..] == port.ToString())
                {
                    return false;
                }
            }
        }

        return true;
    }

    public int? ProbeHttp(string url)
    {
        // From the server's point of view, which is the only one that can see a deployment published
        // on a local port. --insecure because a freshly issued certificate may not have propagated
        // to this host's trust store yet; the check is "is anything serving", not "is the TLS good".
        ExecResult result = RunShell(
            $"curl -s -k -o /dev/null -m 10 -w '%{{http_code}}' {Quote(url)} || "
            + $"wget -q -O /dev/null -T 10 {Quote(url)} && echo 200",
            TimeSpan.FromSeconds(30));

        string body = result.StdOut.Trim();

        return int.TryParse(body, out int status) && status > 0 ? status : null;
    }
}
