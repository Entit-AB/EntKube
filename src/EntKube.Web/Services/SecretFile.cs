namespace EntKube.Web.Services;

/// <summary>
/// Writes a secret to a file only its owner can read.
///
/// <para><b>Why this exists.</b> Reaching a cluster with <c>kubectl</c> or <c>helm</c> means the
/// kubeconfig has to be on disk for the child process to read, and thirty-three places wrote one
/// with <c>File.WriteAllTextAsync</c> to a path built from <see cref="Path.GetTempPath"/>. That
/// creates the file at the process umask — 0644 on every machine EntKube runs on — so a cluster's
/// admin credential sat world-readable in <c>/tmp</c> for the length of the call. Three other
/// places had noticed and called <see cref="File.SetUnixFileMode"/>; the rest had not, which is
/// what one shared helper is for.</para>
///
/// <para><b>Why the file is created before it is written.</b> Writing and then restricting leaves
/// a window in which the credential is on disk and readable. Creating it empty, restricting it,
/// and only then writing means there is no moment when the content exists and the mode is
/// wrong.</para>
///
/// <para>Note that <see cref="Path.GetTempFileName"/> already yields 0600, so the writes using it
/// — <c>KubernetesClientFactory</c>'s thirteen among them — were never exposed. This is for the
/// ones that build their own path, which is the more common habit here.</para>
///
/// <para>⚠️ <b>Which means counting calls to this helper measures nothing.</b> Exposure is decided
/// by how the <em>path</em> was created, not by which write API followed — so a file written with
/// <c>File.WriteAllTextAsync</c> to a <c>GetTempFileName</c> path is owner-only, and one written
/// with this helper to a path that was already world-readable is too. A tally of
/// "<c>File.WriteAllTextAsync</c> sites remaining" therefore says nothing about whether any
/// credential is exposed, and reporting one as though it did is a mistake this note exists to
/// stop. <c>SecretTempFileTests</c> checks the two modes on the platform and is the measurement
/// that counts.</para>
/// </summary>
public static class SecretFile
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>, owner-readable only.</summary>
    public static async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        // Create empty, restrict, then write — see the note above on ordering.
        await using (File.Create(path))
        {
        }

        Restrict(path);

        await File.WriteAllTextAsync(path, content, ct);
    }

    /// <summary>
    /// Creates a directory only its owner can enter, and returns the path.
    ///
    /// <para>⚠️ <b>This is the part the note above says actually decides exposure.</b>
    /// <c>Directory.CreateDirectory</c> under <see cref="Path.GetTempPath"/> yields 0755 at the
    /// usual umask, and a file written into it then lands 0644 — so restricting individual files
    /// is not enough when a directory holds several, and it is no help at all for the files a
    /// CHILD PROCESS writes there. <c>clusterctl</c> and <c>kubectl</c> both drop kubeconfigs into
    /// a working directory, and nothing in this process chooses their mode.</para>
    ///
    /// <para>Restricting the directory covers every one of them at once, including the ones added
    /// later by someone who does not know this rule exists. Owner-only means read, write and
    /// EXECUTE: a directory without execute cannot be traversed, so its contents cannot be opened
    /// even by name.</para>
    /// </summary>
    public static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>
    /// Restricts an existing file to its owner. A no-op on Windows, where the equivalent is an
    /// ACL rather than a Unix mode and <see cref="File.SetUnixFileMode"/> is unsupported — the
    /// guard is what stops this throwing there rather than an oversight.
    /// </summary>
    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
