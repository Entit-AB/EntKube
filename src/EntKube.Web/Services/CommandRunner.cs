using System.Diagnostics;

namespace EntKube.Web.Services;

/// <summary>The outcome of one external command. <see cref="Success"/> is exit code zero.</summary>
public sealed record CliResult(bool Success, int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs the external tools provisioning is built on — <c>clusterctl</c>, <c>kubectl</c>, <c>ssh</c>,
/// <c>helm</c> — with a timeout, an isolated HOME, and every line handed to a log callback so a
/// long-running provisioning run reads as a transcript rather than as silence.
///
/// <para>Shared rather than duplicated: cluster provisioning and image building drive the same
/// tools the same way, and two copies of a process runner drift in exactly the details (timeout
/// handling, killing the process tree) that only matter when something has already gone wrong.</para>
/// </summary>
public class CommandRunner
{
    public async Task<CliResult> RunAsync(
        string program,
        string arguments,
        string workDir,
        Dictionary<string, string> env,
        Action<string> log,
        CancellationToken ct,
        TimeSpan? timeout = null,
        bool quiet = false)
    {
        ProcessStartInfo psi = new()
        {
            FileName = program,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["HOME"] = workDir;
        foreach ((string k, string v) in env) psi.EnvironmentVariables[k] = v;

        using Process process = new() { StartInfo = psi };
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

        try
        {
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (!quiet)
            {
                string tail = (stdout.Trim() + "\n" + stderr.Trim()).Trim();
                if (tail.Length > 0) log($"$ {program} {Redact(arguments)}\n{tail}");
                else log($"$ {program} {Redact(arguments)}");
            }

            return new CliResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"'{program} {Redact(arguments)}' timed out.");
        }
    }

    /// <summary>
    /// Arguments here never carry secrets — credentials go through files and environment variables —
    /// but a transcript that scrolls for pages helps nobody, so long ones are cut.
    /// </summary>
    public static string Redact(string arguments) =>
        arguments.Length > 400 ? arguments[..400] + "…" : arguments;

    /// <summary>Standard non-interactive ssh options for a host whose key we have never seen before.</summary>
    public static string SshOptions(string keyPath) =>
        $"-i {keyPath} -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null " +
        "-o ConnectTimeout=15 -o BatchMode=yes";
}
