namespace EntKube.Installer;

/// <summary>
/// How Compose is invoked on this target.
///
/// Compose v2 ships in two forms and both are v2: a CLI plugin run as <c>docker compose</c>, and a
/// standalone binary named <c>docker-compose</c>. The hyphenated name is *not* a reliable signal of
/// version — it was v1's only name, and it is also what v2's standalone build and Docker Desktop's
/// compatibility shim are called. Only the reported version says which one is there.
/// </summary>
public sealed record ComposeInvocation(string File, IReadOnlyList<string> Prefix, string Version)
{
    /// <summary>How to name it in a message: "docker compose" or "docker-compose".</summary>
    public string Display => Prefix.Count > 0 ? $"{File} {string.Join(' ', Prefix)}" : File;

    public static ComposeInvocation Plugin(string version) => new("docker", ["compose"], version);

    public static ComposeInvocation Standalone(string version) => new("docker-compose", [], version);
}
