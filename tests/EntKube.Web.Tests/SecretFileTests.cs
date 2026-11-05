using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// A secret written to disk is readable only by its owner.
///
/// <para><b>What this is guarding.</b> Reaching a cluster with kubectl or helm means the
/// kubeconfig has to exist on disk for the child process. Thirty-four places wrote one to a path
/// built from <c>Path.GetTempPath()</c>, which creates at the process umask — 0644 — so a
/// cluster's admin credential sat world-readable in <c>/tmp</c> for the length of the call.</para>
///
/// <para>The first test deliberately performs the <em>old</em> write as well, so it demonstrates
/// the difference rather than asserting a number. A test that only checked for 0600 would still
/// pass if the helper were quietly replaced by a plain write on a machine with a stricter
/// umask.</para>
/// </summary>
public class SecretFileTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("entkube-secretfile").FullName;

    public void Dispose()
    {
        Directory.Delete(dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_secret_file_is_not_readable_by_anyone_else_whereas_a_plain_write_is()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix modes do not apply; SecretFile.Restrict is a documented no-op there.
        }

        string viaHelper = Path.Combine(dir, "helper.kubeconfig");
        string viaPlainWrite = Path.Combine(dir, "plain.kubeconfig");

        await SecretFile.WriteAsync(viaHelper, "apiVersion: v1");
        await File.WriteAllTextAsync(viaPlainWrite, "apiVersion: v1");

        UnixFileMode helper = File.GetUnixFileMode(viaHelper);
        UnixFileMode plain = File.GetUnixFileMode(viaPlainWrite);

        helper.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        helper.Should().NotBe(plain,
            "the point of the helper is that it differs from the write it replaced; if these "
            + "ever match, either the helper stopped restricting or this machine's umask is "
            + "hiding the bug the helper exists to fix");

        plain.Should().HaveFlag(UnixFileMode.OtherRead,
            "the umask here is the permissive one production runs under, so the comparison above "
            + "is meaningful rather than accidental");
    }

    [Fact]
    public async Task The_content_is_written_and_readable_by_the_owner()
    {
        string path = Path.Combine(dir, "content.kubeconfig");

        await SecretFile.WriteAsync(path, "hello: world");

        (await File.ReadAllTextAsync(path)).Should().Be("hello: world",
            "restricting the mode before writing must not cost the write itself");
    }

    [Fact]
    public async Task Writing_over_an_existing_file_leaves_it_restricted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(dir, "reused.kubeconfig");

        await File.WriteAllTextAsync(path, "old");          // starts life at 0644
        await SecretFile.WriteAsync(path, "new");

        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        (await File.ReadAllTextAsync(path)).Should().Be("new");
    }
    // ── The directory, which is what decides exposure for anything a CHILD writes ──

    /// <summary>
    /// ⚠️ Same shape as the test above, for the same reason: it performs the plain
    /// <c>Directory.CreateDirectory</c> as well, so it demonstrates a difference rather than
    /// asserting a number that a stricter umask could satisfy on its own.
    /// </summary>
    [Fact]
    public void A_secret_directory_cannot_be_entered_by_anyone_else_whereas_a_plain_one_can()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix modes do not apply; the restriction is a documented no-op there.
        }

        string viaHelper = SecretFile.CreateDirectory(Path.Combine(dir, "helper-workdir"));
        string viaPlainCreate = Path.Combine(dir, "plain-workdir");
        Directory.CreateDirectory(viaPlainCreate);

        UnixFileMode helper = File.GetUnixFileMode(viaHelper);
        UnixFileMode plain = File.GetUnixFileMode(viaPlainCreate);

        helper.Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            "owner-only, and execute is part of it — a directory without execute cannot be "
            + "traversed, which is what puts its contents out of reach");

        helper.Should().NotBe(plain, "or the helper is not changing anything");

        plain.Should().HaveFlag(
            UnixFileMode.OtherExecute,
            "the umask here is the permissive one production runs under, so the comparison above "
            + "is meaningful rather than accidental");
    }

    /// <summary>
    /// ⚠️ <b>The argument the directory helper exists for.</b> A provisioning run shells out to
    /// <c>clusterctl</c> and <c>kubectl</c>, which write kubeconfigs into its working directory
    /// themselves — and nothing in this process chooses the mode of a file another process
    /// creates. Restricting each file we write would leave those exposed.
    ///
    /// <para>This writes a file into the restricted directory with a PLAIN write, standing in for
    /// that child process, and shows the file's own mode is still the permissive one while the
    /// directory denies traversal. In-process we are the owner, so this asserts the two modes
    /// rather than a denied open — the mode is the mechanism, and a test that tried to open the
    /// file as another user could not run here.</para>
    /// </summary>
    [Fact]
    public async Task A_file_written_into_a_secret_directory_by_something_else_is_still_out_of_reach()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string workDir = SecretFile.CreateDirectory(Path.Combine(dir, "child-writes-here"));

        // Exactly what clusterctl does, as far as this process is concerned.
        string dropped = Path.Combine(workDir, "bootstrap.kubeconfig");
        await File.WriteAllTextAsync(dropped, "apiVersion: v1");

        File.GetUnixFileMode(dropped).Should().HaveFlag(
            UnixFileMode.OtherRead,
            "the file itself is as exposed as it ever was — nothing restricted it, and nothing "
            + "could have");

        File.GetUnixFileMode(workDir).Should().NotHaveFlag(
            UnixFileMode.OtherExecute,
            "so the only thing standing between that credential and another account on the box is "
            + "the directory, which is why the directory is the fix");
    }

    [Fact]
    public void A_secret_directory_is_created_and_returned_on_every_platform()
    {
        string path = SecretFile.CreateDirectory(Path.Combine(dir, "returned"));

        path.Should().Be(Path.Combine(dir, "returned"), "callers use the return value as the path");
        Directory.Exists(path).Should().BeTrue();

        // Idempotent, because a provisioning run can be retried against the same cluster id.
        SecretFile.CreateDirectory(path).Should().Be(path);
        Directory.Exists(path).Should().BeTrue();
    }

    /// <summary>
    /// ⚠️ The WIRING, not the helper. Everything above proves <see cref="SecretFile"/> restricts
    /// what it is given; this proves the one caller that handles an SSH private key, two
    /// kubeconfigs and an OpenStack application credential actually asks it to.
    ///
    /// <para>Reverting that call to a plain <c>Directory.CreateDirectory</c> would otherwise be
    /// caught by nothing — the rest of that provisioning method needs a real cloud and cannot be
    /// tested at all, which is why the one decision that can be was lifted out of it.</para>
    /// </summary>
    [Fact]
    public void A_provisioning_run_s_working_directory_is_owner_only()
    {
        Guid clusterId = Guid.NewGuid();
        string workDir = ClusterProvisioningService.CreateWorkDirectory(clusterId);

        try
        {
            Directory.Exists(workDir).Should().BeTrue();
            workDir.Should().Contain(clusterId.ToString("N"), "one directory per cluster, not a shared one");

            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.GetUnixFileMode(workDir).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                "an SSH private key, two cluster kubeconfigs and an OpenStack application "
                + "credential are written in here, some of them by child processes whose mode "
                + "this process does not choose");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

}
