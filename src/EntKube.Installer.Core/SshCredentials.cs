using System.Security.Cryptography;
using System.Text;

namespace EntKube.Installer;

/// <summary>How to authenticate to the server.</summary>
public enum SshAuth
{
    /// <summary>A private key file, optionally passphrase-protected. The usual answer.</summary>
    PrivateKey,

    /// <summary>A password. Common enough on freshly provisioned hosts to be worth supporting.</summary>
    Password,
}

/// <summary>
/// Everything needed to reach the server.
///
/// <see cref="SudoPassword"/> is separate from <see cref="Password"/> on purpose: the two are
/// frequently different, and an install into /opt needs root while the SSH login usually is not.
/// </summary>
public sealed class SshCredentials
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    public SshAuth Auth { get; init; } = SshAuth.PrivateKey;

    public string? Password { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>
    /// Run privileged commands through <c>sudo</c>. Needed whenever the login user is not root and
    /// not in the docker group, which is most of the time.
    /// </summary>
    public bool UseSudo { get; init; }

    /// <summary>
    /// Null when sudo is configured NOPASSWD. Supplied on stdin to <c>sudo -S</c> rather than on the
    /// command line, where it would be visible in the process list to every other user on the host.
    /// </summary>
    public string? SudoPassword { get; init; }

    public string Describe => Port == 22 ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}";

    /// <summary>
    /// The private keys OpenSSH would try, newest algorithm first, so the GUI can offer a sensible
    /// default rather than an empty file picker.
    /// </summary>
    public static IReadOnlyList<string> DiscoverPrivateKeys()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string ssh = Path.Combine(home, ".ssh");

        if (!Directory.Exists(ssh))
        {
            return [];
        }

        List<string> found = [];

        foreach (string name in (string[])["id_ed25519", "id_ecdsa", "id_rsa"])
        {
            string path = Path.Combine(ssh, name);

            if (File.Exists(path))
            {
                found.Add(path);
            }
        }

        return found;
    }
}

/// <summary>
/// A server's host key, presented for approval the first time it is seen.
///
/// Accepting any key without checking would leave the connection open to interception by anything on
/// the path — which matters more here than in most places, because this session carries a sudo
/// password and writes the vault root key. So the fingerprint is checked against known_hosts, and an
/// unrecognised one is a question for the operator rather than a detail to assume.
/// </summary>
public sealed record HostKey(string Host, string Algorithm, byte[] Fingerprint)
{
    /// <summary>The SHA256:… form OpenSSH prints, so it can be compared with `ssh-keyscan` output.</summary>
    public string Sha256 => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Fingerprint)).TrimEnd('=');

    /// <summary>
    /// Whether <c>~/.ssh/known_hosts</c> already contains this exact key, in which case there is
    /// nothing to ask.
    ///
    /// Only plain (unhashed) entries can be matched by host name. OpenSSH may store hashed host
    /// names, so a miss here means "not confirmed by this check", not "the host is unknown to you" —
    /// which is why a miss prompts rather than refuses.
    /// </summary>
    public bool IsInKnownHosts()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

        if (!File.Exists(path))
        {
            return false;
        }

        string encoded = Convert.ToBase64String(Fingerprint);

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith('#') || line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // hosts  keytype  base64key
                if (parts.Length >= 3 && parts[2] == encoded)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Appends the key, so the next connection — and plain `ssh` — recognises it.</summary>
    public void AddToKnownHosts()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Directory.CreateDirectory(dir);

        string entry = $"{Host} {Algorithm} {Convert.ToBase64String(Fingerprint)}";
        string path = Path.Combine(dir, "known_hosts");

        // A file that does not end in a newline would otherwise have this appended to its last entry,
        // corrupting both.
        string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        string prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;

        File.AppendAllText(path, prefix + entry + "\n", Encoding.ASCII);
    }
}
