namespace EntKube.Web.Data;

public enum TenantFeature
{
    Clusters,
    Environments,
    Customers,
    Apps,
    Deployments,
    Groups,
    Storage,
    Databases,
    GitRepositories,
    Keycloak,
    ContainerRegistry,
    Messaging,
    Cache,
    VPN,
    Monitoring,
    Audit,

    /// <summary>
    /// Approving just-in-time cluster access for customers.
    ///
    /// Its own feature rather than folded into Deployments, because the two are not the same
    /// authority: deploying an app changes what runs in a namespace, while this hands a person
    /// outside the organisation a shell into it. Appended last — these serialize as their numeric
    /// value, so inserting a member anywhere else silently re-points every permission already
    /// stored.
    /// </summary>
    JitAccess,
}
