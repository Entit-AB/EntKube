using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace EntKube.Installer;

/// <summary>
/// Installs onto the machine the installer is running on.
///
/// Output is captured rather than inherited so a front-end can present it, and every failure carries
/// the command's own stderr — a build or a pull that fails behind a swallowed stream produces "it did
/// not work", which is not a diagnosis.
/// </summary>
public sealed class LocalExecutor(string workingDirectory) : IExecutor
{
    private readonly string _workingDirectory = workingDirectory;

    public string Target => "this host";

    public bool IsLocal => true;

    /// <summary>A local install runs as whoever started the installer; there is no elevation step.</summary>
    public bool ElevationInUse => false;

    public ExecResult RunUnelevated(string file, IReadOnlyList<string> args, TimeSpan? timeout = null) =>
        Run(file, args, timeout);

    public void Dispose()
    {
        // Nothing to release: no connection, no temporary state.
    }

    public ExecResult Run(
        string file,
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        Action<string>? onLine = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = new() { StartInfo = psi };

        StringBuilder stdout = new();
        StringBuilder stderr = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // A missing executable is an ordinary outcome here — preflight uses it to decide whether
            // docker is installed — so it is reported as a failed result rather than thrown.
            return new ExecResult(127, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(30)).TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the timeout and the kill. Nothing to do.
            }

            return new ExecResult(124, stdout.ToString(), $"'{file}' timed out.");
        }

        // WaitForExit(int) can return before the redirected streams are drained; the parameterless
        // overload is what guarantees both readers have finished. Without it a fast-failing command
        // reports an empty stderr, which is exactly when its stderr is most wanted.
        process.WaitForExit();

        return new ExecResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public void WriteFile(string path, string content, bool secret = false)
    {
        if (File.Exists(path) && File.ReadAllText(path) != content)
        {
            File.Copy(path, $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);
        }

        File.WriteAllText(path, content);

        if (secret && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public bool FileExists(string path) => File.Exists(path);

    public void EnsureWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            string probe = Path.Combine(path, ".entkube-install-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InstallAbortedException(
                $"Cannot write to {path}:\n\n  {ex.Message}\n\n"
                + "Choose a different directory, or run this with the rights to create it.\n"
                + "On Linux /opt/entkube usually needs root.");
        }
    }

    public bool IsPortFree(int port)
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // Without this the bind succeeds on Linux against a socket in TIME_WAIT and reports a
            // busy port as free. Windows gives SO_REUSEADDR the opposite meaning — it allows
            // stealing a live socket — so it is set only where it means what is wanted here.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public int? ProbeHttp(string url)
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage response = http.GetAsync(url).GetAwaiter().GetResult();

            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
