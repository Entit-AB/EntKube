namespace EntKube.Web.Services.PublicApi;

/// <summary>
/// The scope vocabulary for public API tokens.
///
/// Deliberately small and resource-shaped rather than one scope per endpoint: a
/// vocabulary an operator cannot hold in their head gets granted wholesale, which
/// defeats the point. Read and write are always separate — the common case (a
/// dashboard, a monitoring integration) needs no write access at all.
/// </summary>
public static class ApiScopes
{
    /// <summary>Clusters, nodes, components, workloads.</summary>
    public const string FleetRead = "fleet:read";

    /// <summary>Apps, environments, deployments and their status.</summary>
    public const string AppsRead = "apps:read";

    /// <summary>Trigger a deployment sync or restart.</summary>
    public const string AppsWrite = "apps:write";

    /// <summary>Advisor findings, incidents, drift, upgrades, supply-chain reports.</summary>
    public const string OpsRead = "ops:read";

    /// <summary>Acknowledge/snooze findings, open and resolve incidents.</summary>
    public const string OpsWrite = "ops:write";

    /// <summary>Every scope, in the order they should be presented.</summary>
    // Declared before All: static field initializers run in declaration order, so
    // assigning All from a field declared below it would leave All null at runtime.
    private static readonly string[] AllScopes =
        [FleetRead, AppsRead, AppsWrite, OpsRead, OpsWrite];

    public static readonly IReadOnlyList<string> All = AllScopes;

    /// <summary>Human-readable description for the token-creation UI.</summary>
    public static string Describe(string scope) => scope switch
    {
        FleetRead => "Read clusters, nodes, components and workloads",
        AppsRead => "Read apps, environments and deployments",
        AppsWrite => "Trigger deployment syncs and restarts",
        OpsRead => "Read advisor findings, incidents, drift and vulnerability reports",
        OpsWrite => "Acknowledge findings and manage incidents",
        _ => scope,
    };

    /// <summary>True when the scope is one this build understands.</summary>
    public static bool IsKnown(string scope) => All.Contains(scope);

    /// <summary>
    /// Parses a stored scope string. Unknown scopes are dropped rather than kept:
    /// a scope this build does not understand cannot be enforced, so treating it as
    /// held would grant access no check governs.
    /// </summary>
    public static HashSet<string> Parse(string? scopes) =>
        scopes is null
            ? []
            : [.. scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Where(IsKnown)];

    public static string Serialize(IEnumerable<string> scopes) =>
        string.Join(' ', scopes.Where(IsKnown).Distinct().OrderBy(s => Array.IndexOf(AllScopes, s)));
}
