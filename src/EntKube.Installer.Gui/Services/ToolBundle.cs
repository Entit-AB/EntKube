using System.Runtime.InteropServices;

namespace EntKube.Installer.Gui.Services;

/// <summary>One client-side tool the GUI can install onto the machine it is running on.</summary>
public sealed record ClientTool(
    string Key,
    string Name,
    string FileName,
    string Summary,
    string Documentation)
{
    /// <summary>The file as it is named on this platform.</summary>
    public string PlatformFileName => OperatingSystem.IsWindows() ? FileName + ".exe" : FileName;
}

/// <summary>
/// Finds the client tool binaries that were laid down beside the application.
///
/// They are bundled at build time rather than downloaded, because there is no release host for them
/// — docs/releasing.md is explicit that these are distributed by hand — and rather than embedded,
/// because four self-contained .NET binaries come to roughly 250 MB and putting that inside the
/// executable makes the build slow and the app unwieldy for a feature not everyone uses.
///
/// So <c>scripts/release.sh gui</c> builds them for the GUI's own platform and copies them into a
/// <c>tools/</c> folder next to it. A GUI built without that step still runs; the client-tools page
/// reports what is missing instead of failing.
/// </summary>
public static class ToolBundle
{
    public static readonly IReadOnlyList<ClientTool> All =
    [
        new("cli", "EntKube CLI", "entkube",
            "Query and control EntKube from a terminal or a CI job.",
            "docs/cli.md"),
        new("mcp", "MCP server", "entkube-mcp",
            "Exposes EntKube to an MCP client such as Claude.",
            "docs/mcp-server.md"),
        new("agent", "Egress agent", "entkube-agent",
            "Reaches an IP-allowlisted provider API from a network EntKube cannot.",
            "docs/egress-agent.md"),
        new("terraform", "Terraform provider", "terraform-provider-entkube",
            "Manage EntKube resources from Terraform.",
            "tools/terraform-provider-entkube/README.md"),
    ];

    /// <summary>
    /// Where the bundle is. Next to the executable in a shipped build; found by walking up to the
    /// repository root when running from a development build, so the GUI is usable straight after
    /// `scripts/release.sh binaries` without a packaging step.
    /// </summary>
    public static string? Directory
    {
        get
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "tools");

            if (System.IO.Directory.Exists(beside))
            {
                return beside;
            }

            // Development fallback: artifacts/<target>/Release/<rid>/ from the repository root.
            string? root = FindRepositoryRoot();

            return root is not null && System.IO.Directory.Exists(Path.Combine(root, "artifacts"))
                ? Path.Combine(root, "artifacts")
                : null;
        }
    }

    /// <summary>
    /// The path to a tool's binary, or null when it was not bundled.
    ///
    /// Checks the flat shipped layout first, then the artifacts tree a developer build produces, so
    /// the same code serves both without the caller knowing which it is looking at.
    /// </summary>
    public static string? Locate(ClientTool tool)
    {
        string? directory = Directory;

        if (directory is null)
        {
            return null;
        }

        string flat = Path.Combine(directory, tool.PlatformFileName);

        if (File.Exists(flat))
        {
            return flat;
        }

        string rid = CurrentRid;

        string[] candidates = tool.Key == "terraform"
            ? [Path.Combine(directory, "terraform-provider", GoPlatformDirectory, tool.PlatformFileName)]
            : [Path.Combine(directory, tool.Key, "Release", rid, tool.PlatformFileName),
               Path.Combine(directory, tool.Key, "Debug", rid, tool.PlatformFileName)];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The .NET runtime identifier for this machine, in the form the build scripts use.</summary>
    public static string CurrentRid
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : "linux";

            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };

            return $"{os}-{arch}";
        }
    }

    /// <summary>The Go provider is laid out by GOOS_GOARCH rather than by .NET RID.</summary>
    private static string GoPlatformDirectory
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";

            return $"{os}_{arch}";
        }
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EntKube.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
