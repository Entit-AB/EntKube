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

    /// <summary>
    /// Editing the support knowledge base (helpdesk phase 10) — Zammad's "KB editor". Opt-in: until
    /// some role in a tenant grants it at Edit or above, every member may edit, as before; once one
    /// does, only members of such roles may. Appended last, for the reason given on JitAccess.
    /// </summary>
    KnowledgeBase,
}
