using EntKube.Installer;

namespace EntKube.Web.Tests;

/// <summary>
/// The parts of the SSH install path that can be checked without a server.
///
/// Quoting is the one that matters most and is why this file leads with it. Every remote command is
/// a string handed to a shell, and the values travelling through it include connection strings and
/// passwords — a value that escapes its quoting is arbitrary code execution on the server being
/// installed to, run through sudo.
/// </summary>
public class InstallerSshTests
{
    [Theory]
    [InlineData("simple", "'simple'")]
    [InlineData("with space", "'with space'")]
    [InlineData("", "''")]
    [InlineData("semi;colon", "'semi;colon'")]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("hash#mark", "'hash#mark'")]
    public void Values_without_a_single_quote_are_wrapped_verbatim(string input, string expected) =>
        Assert.Equal(expected, SshExecutor.Quote(input));

    [Fact]
    public void A_single_quote_is_closed_escaped_and_reopened()
    {
        // The only character that can end a single-quoted string, and so the only one needing
        // handling. Closing, an escaped quote, then reopening is the POSIX idiom.
        Assert.Equal("'it'\\''s'", SshExecutor.Quote("it's"));
    }

    [Fact]
    public void A_quote_cannot_be_used_to_start_a_second_command()
    {
        // The shape of a real injection attempt: end the quoting, run something else, resume.
        string quoted = SshExecutor.Quote("x'; touch /tmp/pwned; echo '");

        // With the escape sequences masked out, no bare quote remains inside the value — so nothing
        // in the payload ever sits where a shell would read it as syntax.
        string masked = quoted[1..^1].Replace("'\\''", "_");

        Assert.DoesNotContain("'", masked);
    }

    [Fact]
    public void Credentials_describe_themselves_without_the_default_port()
    {
        Assert.Equal("ops@srv.example.com",
            new SshCredentials { Host = "srv.example.com", Username = "ops" }.Describe);

        Assert.Equal("ops@srv.example.com:2222",
            new SshCredentials { Host = "srv.example.com", Username = "ops", Port = 2222 }.Describe);
    }

    [Fact]
    public void A_host_key_fingerprint_is_the_form_openssh_prints()
    {
        // So it can be compared by eye with `ssh-keygen -lf`, which is the only way an operator can
        // verify that the dialog is showing them the server they think it is.
        HostKey key = new("srv.example.com", "ssh-ed25519", [1, 2, 3, 4]);

        Assert.StartsWith("SHA256:", key.Sha256);
        Assert.DoesNotContain("=", key.Sha256);
    }
}

/// <summary>
/// The SSH path against a real server.
///
/// These no-op unless ENTKUBE_SSH_TEST_HOST is set, since they need an sshd — but they live in the
/// suite rather than in a loose script because they are what found three defects that reading the
/// code did not: a sudo pipeline that fed the password to <c>cd</c> and then blocked forever, an
/// SFTP permission call that rejected the mode it was handed, and a missing-docker check that named
/// the wrong cause whenever sudo was involved.
///
/// To run them, see docs/installing.md — it carries the throwaway sshd container and the exact
/// environment variables.
/// </summary>
public class InstallerSshIntegrationTests
{
    private static string? Host => Environment.GetEnvironmentVariable("ENTKUBE_SSH_TEST_HOST");

    private static bool Enabled => !string.IsNullOrWhiteSpace(Host);

    private static string Secret =>
        Environment.GetEnvironmentVariable("ENTKUBE_SSH_TEST_PASSWORD") ?? "opspass";

    private static SshExecutor Connect(string directory) => SshExecutor.Connect(
        new SshCredentials
        {
            Host = Host!,
            Port = int.TryParse(Environment.GetEnvironmentVariable("ENTKUBE_SSH_TEST_PORT"), out int p) ? p : 22,
            Username = Environment.GetEnvironmentVariable("ENTKUBE_SSH_TEST_USER") ?? "ops",
            Auth = SshAuth.Password,
            Password = Secret,
            UseSudo = true,
            SudoPassword = Secret,
        },
        directory,
        approveHostKey: _ => true,
        TimeSpan.FromSeconds(15));

    [Fact]
    public void A_hostile_argument_reaches_the_command_as_one_literal_value()
    {
        if (!Enabled)
        {
            return;
        }

        using SshExecutor ssh = Connect("/tmp");

        string hostile = "a b';touch /tmp/pwned;'\"$HOME\" #x";
        ExecResult result = ssh.Run("printf", ["%s", hostile], TimeSpan.FromSeconds(20));

        Assert.Equal(hostile, result.StdOut.TrimEnd('\n'));
        Assert.False(ssh.FileExists("/tmp/pwned"));
    }

    [Fact]
    public void Sudo_elevates_without_blocking_on_a_password_prompt()
    {
        // The regression: `printf pw | cd dir && sudo -S cmd` parses as `(printf | cd) && sudo`, so
        // sudo waited on a stdin that never arrived and the install hung indefinitely.
        if (!Enabled)
        {
            return;
        }

        using SshExecutor ssh = Connect("/tmp");

        Assert.Equal("root", ssh.Run("id", ["-un"], TimeSpan.FromSeconds(20)).StdOut.Trim());
    }

    [Fact]
    public void A_secret_file_is_written_0600_and_replacing_it_leaves_a_backup()
    {
        if (!Enabled)
        {
            return;
        }

        string dir = "/tmp/entkube-sshtest-" + Guid.NewGuid().ToString("N")[..8];

        using SshExecutor ssh = Connect(dir);
        ssh.EnsureWritableDirectory(dir);

        ssh.WriteFile(dir + "/.env", "SECRET=one\n", secret: true);

        Assert.Equal("SECRET=one\n", ssh.ReadFile(dir + "/.env"));
        Assert.Equal("600",
            ssh.Run("stat", ["-c", "%a", dir + "/.env"], TimeSpan.FromSeconds(20)).StdOut.Trim());

        ssh.WriteFile(dir + "/.env", "SECRET=two\n");

        Assert.Equal("SECRET=two\n", ssh.ReadFile(dir + "/.env"));
        Assert.Equal("1",
            ssh.Run("sh", ["-c", "ls " + dir + "/.env.*.bak | wc -l"], TimeSpan.FromSeconds(20)).StdOut.Trim());

        ssh.Run("rm", ["-rf", dir], TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_missing_docker_is_reported_as_missing_rather_than_as_a_stopped_daemon()
    {
        // Through sudo a missing binary exits 1 with "sudo: docker: command not found" rather than
        // 127, and the check used to conclude the daemon was merely stopped.
        if (!Enabled)
        {
            return;
        }

        using SshExecutor ssh = Connect("/tmp");

        InstallAbortedException ex = Assert.Throws<InstallAbortedException>(
            () => new Preflight(ssh, new SilentLog()).RequireTooling());

        Assert.Contains("docker is not installed", ex.Message);
    }

    [Fact]
    public void The_port_probe_distinguishes_a_listening_port_from_a_free_one()
    {
        if (!Enabled)
        {
            return;
        }

        using SshExecutor ssh = Connect("/tmp");

        Assert.False(ssh.IsPortFree(22));
        Assert.True(ssh.IsPortFree(8080));
    }

    private sealed class SilentLog : IInstallLog
    {
        public void Step(string label, string outcome) { }

        public void Warn(string message) { }

        public void Detail(string message) { }
    }
}
