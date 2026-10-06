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
}
