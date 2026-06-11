using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Implements IKubernetesClientFactory using kubectl commands.
/// Writes manifests to temporary files and applies/deletes them via kubectl,
/// using the provided kubeconfig for authentication.
/// </summary>
public class KubernetesClientFactory : IKubernetesClientFactory
{
    /// <summary>
    /// Applies a YAML manifest by writing it to a temp file and running kubectl apply.
    /// The kubeconfig is written to a separate temp file for authentication.
    /// </summary>
    public async Task ApplyManifestAsync(string manifest, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();
        string manifestPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);
            await File.WriteAllTextAsync(manifestPath, manifest, ct);

            string result = await RunKubectlAsync(
                $"apply -f {manifestPath} --kubeconfig={kubeconfigPath}", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
            File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// Deletes a specific Kubernetes resource by kind, name, and namespace.
    /// </summary>
    public async Task DeleteManifestAsync(
        string kind, string name, string ns, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            await RunKubectlAsync(
                $"delete {kind} {name} -n {ns} --kubeconfig={kubeconfigPath} --ignore-not-found", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// Applies a JSON merge patch to a specific Kubernetes resource.
    /// Safer than ApplyManifestAsync for partial updates because only the specified fields change;
    /// all other fields (including spec.users lists on CRDs) are left untouched.
    /// </summary>
    public async Task PatchJsonAsync(
        string resource, string name, string ns, string jsonPatch, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            await RunKubectlAsync(
                $"patch {resource} {name} -n {ns} --type=merge -p {jsonPatch} --kubeconfig={kubeconfigPath}", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// Creates a namespace if it doesn't already exist.
    /// </summary>
    public async Task EnsureNamespaceAsync(string ns, string kubeconfig, CancellationToken ct = default)
    {
        string manifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n";
        await ApplyManifestAsync(manifest, kubeconfig, ct);
    }

    /// <summary>
    /// Gets JSON output from kubectl for a given resource type in a namespace.
    /// Supports optional label selectors for filtering.
    /// </summary>
    public async Task<string> GetJsonAsync(
        string resource, string ns, string kubeconfig, string labelSelector = "", CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            string selector = string.IsNullOrEmpty(labelSelector) ? "" : $" -l {labelSelector}";
            return await RunKubectlAsync(
                $"get {resource} -n {ns} --kubeconfig={kubeconfigPath}{selector} -o json", ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    /// <summary>
    /// Executes SQL on the CNPG primary pod via kubectl exec + psql.
    /// The primary pod is identified by the CNPG naming convention: {cluster}-1.
    /// </summary>
    public async Task ExecuteSqlAsync(
        string clusterName, string ns, string sql, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            // CNPG primary pod follows the naming pattern: {cluster}-1
            string primaryPod = $"{clusterName}-1";

            // Pipe SQL via stdin to avoid shell argument splitting issues.
            // The -i flag tells kubectl exec to pass stdin to the container.

            await RunKubectlWithStdinAsync(
                $"exec -i {primaryPod} -n {ns} --kubeconfig={kubeconfigPath} -- psql -U postgres",
                sql, ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public Task ExecuteMongoAsync(
        string clusterName, string ns, string script, string kubeconfig,
        string? username = null, string? password = null, CancellationToken ct = default) =>
        ExecuteMongoWithOutputAsync(clusterName, ns, script, kubeconfig, username, password, ct);

    public async Task<string> ExecuteMongoWithOutputAsync(
        string clusterName, string ns, string script, string kubeconfig,
        string? username = null, string? password = null, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            // MongoDB Community Operator names StatefulSet pods {cluster}-0, {cluster}-1, ...
            string primaryPod = $"{clusterName}-0";

            string mongoshArgs = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                ? $"--username {username} --password {password} --authenticationDatabase admin --quiet"
                : "--quiet";

            return await RunKubectlWithStdinAsync(
                $"exec -i {primaryPod} -n {ns} --kubeconfig={kubeconfigPath} -- mongosh {mongoshArgs}",
                script, ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<string?> GetSecretValueAsync(
        string secretName, string key, string ns, string kubeconfig, CancellationToken ct = default)
    {
        string json = await GetJsonAsync($"secret/{secretName}", ns, kubeconfig, ct: ct);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty(key, out JsonElement valEl))
            {
                string? b64 = valEl.GetString();
                if (string.IsNullOrEmpty(b64)) return null;
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            return null;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            return null;
        }
    }

    public Task ExecuteSqlOnPodAsync(
        string podName, string ns, string sql, string kubeconfig,
        string username = "postgres", string? password = null, CancellationToken ct = default) =>
        ExecuteSqlOnPodCoreAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public async Task<string> ExecuteSqlOnPodWithOutputAsync(
        string podName, string ns, string sql, string kubeconfig,
        string username = "postgres", string? password = null, CancellationToken ct = default) =>
        await ExecuteSqlOnPodCoreAsync(podName, ns, sql, kubeconfig, username, password, ct);

    public async Task<string> RunCommandOnPodAsync(
        string podName, string ns, IReadOnlyList<string> command, string kubeconfig,
        IReadOnlyDictionary<string, string>? envVars = null, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            ProcessStartInfo startInfo = new()
            {
                FileName = "kubectl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(podName);
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(ns);
            startInfo.ArgumentList.Add($"--kubeconfig={kubeconfigPath}");
            startInfo.ArgumentList.Add("--");

            if (envVars is { Count: > 0 })
            {
                startInfo.ArgumentList.Add("env");
                foreach (KeyValuePair<string, string> kv in envVars)
                    startInfo.ArgumentList.Add($"{kv.Key}={kv.Value}");
            }

            foreach (string arg in command)
                startInfo.ArgumentList.Add(arg);

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"kubectl exec failed (exit {process.ExitCode}): {error}");

            return output;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task ExecuteSqlInCnpgDatabaseAsync(
        string clusterName, string ns, string database, string sql, string kubeconfig, CancellationToken ct = default)
    {
        await ExecuteSqlInCnpgDatabaseWithOutputAsync(clusterName, ns, database, sql, kubeconfig, ct);
    }

    public async Task<string> ExecuteSqlInCnpgDatabaseWithOutputAsync(
        string clusterName, string ns, string database, string sql, string kubeconfig, CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            string primaryPod = $"{clusterName}-1";

            return await RunKubectlWithStdinAsync(
                $"exec -i {primaryPod} -n {ns} --kubeconfig={kubeconfigPath} -- psql -U postgres -d \"{database}\" -t -A",
                sql, ct);
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    public async Task<string> RunCommandOnPodWithStdinAsync(
        string podName, string ns, IReadOnlyList<string> command, string stdin, string kubeconfig,
        CancellationToken ct = default)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            ProcessStartInfo startInfo = new()
            {
                FileName = "kubectl",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(podName);
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(ns);
            startInfo.ArgumentList.Add($"--kubeconfig={kubeconfigPath}");
            startInfo.ArgumentList.Add("--");

            foreach (string arg in command)
                startInfo.ArgumentList.Add(arg);

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"kubectl exec failed (exit {process.ExitCode}): {error}");

            return output;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    private async Task<string> ExecuteSqlOnPodCoreAsync(
        string podName, string ns, string sql, string kubeconfig,
        string username, string? password, CancellationToken ct)
    {
        string kubeconfigPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(kubeconfigPath, kubeconfig, ct);

            // Use ArgumentList (not Arguments string) so passwords with special
            // characters are never mangled by shell quoting or .NET arg splitting.
            ProcessStartInfo startInfo = new()
            {
                FileName = "kubectl",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(podName);
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(ns);
            startInfo.ArgumentList.Add($"--kubeconfig={kubeconfigPath}");
            startInfo.ArgumentList.Add("--");

            if (!string.IsNullOrEmpty(password))
            {
                startInfo.ArgumentList.Add("env");
                startInfo.ArgumentList.Add($"PGPASSWORD={password}");
            }

            startInfo.ArgumentList.Add("psql");
            startInfo.ArgumentList.Add("-U");
            startInfo.ArgumentList.Add(username);
            // Force TCP so pg_hba host rules apply (not peer/local socket rules).
            startInfo.ArgumentList.Add("-h");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("-t");

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            await process.StandardInput.WriteAsync(sql.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"kubectl failed (exit {process.ExitCode}): {error}");
            }

            return output;
        }
        finally
        {
            File.Delete(kubeconfigPath);
        }
    }

    private static async Task<string> RunKubectlAsync(string arguments, CancellationToken ct)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        StringBuilder output = new();
        StringBuilder error = new();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        output.Append(await outputTask);
        error.Append(await errorTask);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"kubectl failed (exit {process.ExitCode}): {error}");
        }

        return output.ToString();
    }

    /// <summary>
    /// Runs kubectl with input piped via stdin. Used for executing SQL/scripts
    /// where passing the content as command-line arguments would break due to
    /// shell escaping and argument splitting in Process.
    /// </summary>
    private static async Task<string> RunKubectlWithStdinAsync(
        string arguments, string stdinContent, CancellationToken ct)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        // Write the script/SQL to stdin and close it so the process knows input is done.

        await process.StandardInput.WriteAsync(stdinContent.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"kubectl failed (exit {process.ExitCode}): {error}");
        }

        return output;
    }
}
