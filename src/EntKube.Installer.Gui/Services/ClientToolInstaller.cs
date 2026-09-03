using System.Text;

namespace EntKube.Installer.Gui.Services;

/// <summary>
/// Installs the client-side tools onto the machine running the GUI.
///
/// Deliberately limited to copying binaries and writing files that belong to EntKube. It does not
/// edit shell profiles, and it does not merge itself into an MCP client's configuration: those files
/// belong to the user and to other applications, and an installer that rewrites them is one that can
/// break something it did not create. Where configuration is needed, the snippet is produced for the
/// operator to paste, which is a smaller promise that is always safe to keep.
/// </summary>
public sealed class ClientToolInstaller
{
    /// <summary>
    /// Where tools go by default.
    ///
    /// A per-user directory rather than /usr/local/bin, because that one needs root and asking a
    /// desktop app for an administrator password to copy a CLI is a poor trade. Both are offered;
    /// this is the default.
    /// </summary>
    public static string DefaultDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EntKube", "bin")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    /// <summary>
    /// Whether the directory is on PATH. If it is not, the tools install correctly and then appear
    /// not to exist, which is a confusing enough outcome to be worth saying up front.
    /// </summary>
    public static bool IsOnPath(string directory)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        char separator = OperatingSystem.IsWindows() ? ';' : ':';
        string normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        return path.Split(separator, StringSplitOptions.RemoveEmptyEntries).Any(entry =>
        {
            try
            {
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Trim())),
                    normalised,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed PATH entry is not this installer's problem to solve; it just is not a
                // match.
                return false;
            }
        });
    }

    public sealed record Result(ClientTool Tool, bool Installed, string Message, string? Path = null);

    /// <summary>
    /// Copies the selected tools into <paramref name="directory"/>, returning one result per tool so
    /// the caller can report partial success — one missing binary should not fail the others.
    /// </summary>
    public IReadOnlyList<Result> Install(
        IReadOnlyList<ClientTool> tools,
        string directory,
        string? serverUrl)
    {
        List<Result> results = [];

        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [.. tools.Select(t => new Result(t, false, $"Cannot create {directory}: {ex.Message}"))];
        }

        foreach (ClientTool tool in tools)
        {
            string? source = ToolBundle.Locate(tool);

            if (source is null)
            {
                results.Add(new Result(tool, false,
                    $"Not bundled with this build. Run: scripts/release.sh {tool.Key} --rid {ToolBundle.CurrentRid}"));
                continue;
            }

            string destination = Path.Combine(directory, tool.PlatformFileName);

            try
            {
                File.Copy(source, destination, overwrite: true);

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destination,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

                // The agent is the one tool that cannot do anything without a file beside it, and its
                // allowlist is a security boundary the operator must set deliberately. Writing a
                // template — never overwriting a real one — is the difference between "edit this" and
                // "work out what this needs".
                if (tool.Key == "agent")
                {
                    WriteAgentTemplate(directory, serverUrl);
                }

                results.Add(new Result(tool, true, "installed", destination));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                results.Add(new Result(tool, false,
                    ex is UnauthorizedAccessException
                        ? $"No permission to write {destination}. Choose a directory you own."
                        : $"Could not copy to {destination}: {ex.Message}"));
            }
        }

        return results;
    }

    /// <summary>
    /// Writes agent.json.example rather than agent.json.
    ///
    /// The real file carries a token and a host allowlist that only the operator can decide, and
    /// overwriting an agent that is already configured would silently widen or narrow what it is
    /// permitted to reach.
    /// </summary>
    private static void WriteAgentTemplate(string directory, string? serverUrl)
    {
        string path = Path.Combine(directory, "agent.json.example");

        string json = $$"""
        {
          "ServerUrl": "{{serverUrl ?? "https://entkube.example.com"}}",
          "Token": "<EntKube: Storage -> Egress Agents -> Add Agent>",
          "AllowedHosts": ["identity.example.com", "*.citycloud.com"],
          "AllowedPorts": [443]
        }
        """;

        File.WriteAllText(path, json + "\n");
    }

    /// <summary>
    /// The configuration each installed tool still needs, as text to paste. Empty when a tool needs
    /// nothing.
    /// </summary>
    public static string NextSteps(IReadOnlyList<Result> results, string directory, string? serverUrl)
    {
        StringBuilder sb = new();
        string url = serverUrl ?? "https://entkube.example.com";

        if (!IsOnPath(directory))
        {
            sb.AppendLine($"{directory} is not on your PATH, so the tools will not be found by name.");
            sb.AppendLine(OperatingSystem.IsWindows()
                ? $"  Add it:  setx PATH \"%PATH%;{directory}\""
                : $"  Add it:  export PATH=\"{directory}:$PATH\"   (in ~/.zshrc or ~/.bashrc)");
            sb.AppendLine();
        }

        foreach (Result result in results.Where(r => r.Installed))
        {
            switch (result.Tool.Key)
            {
                case "cli":
                    sb.AppendLine("EntKube CLI — create a token under the tenant's API tokens tab, then:");
                    sb.AppendLine($"  export ENTKUBE_URL={url}");
                    sb.AppendLine("  export ENTKUBE_TOKEN=ekp_...");
                    sb.AppendLine("  entkube --help");
                    sb.AppendLine();
                    break;

                case "mcp":
                    sb.AppendLine("MCP server — add this to your MCP client's configuration:");
                    sb.AppendLine($$"""
                      {
                        "mcpServers": {
                          "entkube": {
                            "command": "{{result.Path}}",
                            "env": {
                              "ENTKUBE_URL": "{{url}}",
                              "ENTKUBE_TOKEN": "ekp_..."
                            }
                          }
                        }
                      }
                    """);
                    sb.AppendLine("  Add \"--allow-write\" to args to expose the cluster-changing tools.");
                    sb.AppendLine();
                    break;

                case "agent":
                    sb.AppendLine("Egress agent — a template was written next to the binary:");
                    sb.AppendLine($"  {Path.Combine(directory, "agent.json.example")}");
                    sb.AppendLine("  Copy it to agent.json, add the token from Storage -> Egress Agents,");
                    sb.AppendLine("  and set AllowedHosts. That allowlist is the security boundary and is");
                    sb.AppendLine("  local — EntKube cannot widen it.");
                    sb.AppendLine();
                    break;

                case "terraform":
                    sb.AppendLine("Terraform provider — for local use, point Terraform at it with a");
                    sb.AppendLine("  dev_overrides block in ~/.terraformrc:");
                    sb.AppendLine($$"""
                      provider_installation {
                        dev_overrides { "entkube/entkube" = "{{directory}}" }
                        direct {}
                      }
                    """);
                    sb.AppendLine();
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
