namespace EntKube.Web.Data;

/// <summary>
/// Well-known OCI / Docker registry providers. Drives server URL defaults and
/// display labels in the UI. Use <see cref="Generic"/> for any registry not
/// listed here.
/// </summary>
public enum DockerRegistryType
{
    Generic,
    DockerHub,
    AzureContainerRegistry,
    Harbor,
    Quay,
    GitHubContainerRegistry
}
