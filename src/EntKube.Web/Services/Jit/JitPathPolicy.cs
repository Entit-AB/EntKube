namespace EntKube.Web.Services.Jit;

/// <summary>The verdict on one proxied request path.</summary>
public sealed record JitPathVerdict(bool Allowed, string? Reason)
{
    public static readonly JitPathVerdict Allow = new(true, null);
    public static JitPathVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether a Kubernetes API path is inside a grant's namespace.
///
/// This is the proxy's half of the confinement, and it is deliberately redundant with the
/// RoleBinding: the RBAC would already refuse a cross-namespace read, so nothing here is the only
/// thing standing between a customer and another tenant. Two independent checks means a mistake in
/// either one is not sufficient on its own — and this one fails closed, which the RBAC cannot,
/// because a Role that was never applied grants nothing but also blocks nothing.
///
/// The list of allowed shapes is an allow-list rather than a deny-list. The Kubernetes API surface
/// grows every release, and a deny-list would silently admit whatever was added.
/// </summary>
public static class JitPathPolicy
{
    /// <summary>
    /// Paths kubectl needs before it can do anything at all: negotiating API versions, fetching
    /// the OpenAPI schema, checking the server is up.
    ///
    /// None of them is namespaced and none reveals anything about another tenant — they describe
    /// what the API server can do, not what is running on it. Refusing them would leave a
    /// kubeconfig that cannot complete a single command.
    /// </summary>
    private static readonly HashSet<string> DiscoveryRoots = new(StringComparer.Ordinal)
    {
        "version", "api", "apis", "openapi", "healthz", "livez", "readyz",
    };

    /// <summary>
    /// Checks one path against a grant's namespace. <paramref name="path"/> is the part after
    /// <c>/jit/{grantId}</c>, with or without a leading slash, and without the query string.
    /// </summary>
    public static JitPathVerdict Check(string? path, string grantNamespace)
    {
        if (string.IsNullOrWhiteSpace(grantNamespace))
            return JitPathVerdict.Deny("The grant has no namespace.");

        string[] segments = (path ?? "")
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return JitPathVerdict.Deny("An empty path is not a Kubernetes API request.");

        // A traversal segment cannot appear in a legitimate API path, and normalising it away
        // rather than refusing would mean the check ran against a different path than the one
        // forwarded.
        if (segments.Any(s => s is "." or ".."))
            return JitPathVerdict.Deny("Path traversal is not allowed.");

        // Discovery: the root itself, or one or two segments below it. `apis/apps/v1` lists the
        // apps group's resources; `apis/apps/v1/deployments` is a cluster-wide deployment list
        // and is not discovery at all, which is why the depth matters.
        if (DiscoveryRoots.Contains(segments[0]))
        {
            if (segments[0] == "openapi") return JitPathVerdict.Allow;

            if (IsDiscoveryDepth(segments))
                return JitPathVerdict.Allow;
        }

        // Everything else has to be namespaced, and namespaced at the right place. The `namespaces`
        // segment sits at a fixed index: 2 for the core group (/api/v1/namespaces/…) and 3 for a
        // named group (/apis/{group}/{version}/namespaces/…).
        int nsIndex = segments[0] switch
        {
            "api" => 2,
            "apis" => 3,
            _ => -1,
        };

        if (nsIndex < 0)
            return JitPathVerdict.Deny($"'{segments[0]}' is not part of the Kubernetes API.");

        if (segments.Length <= nsIndex || segments[nsIndex] != "namespaces")
        {
            return JitPathVerdict.Deny(
                "This grant can only reach resources inside its own namespace, and this request is "
                + "cluster-scoped or spans all namespaces.");
        }

        if (segments.Length <= nsIndex + 1)
            return JitPathVerdict.Deny("The request names no namespace.");

        string requested = segments[nsIndex + 1];
        if (!string.Equals(requested, grantNamespace, StringComparison.Ordinal))
        {
            return JitPathVerdict.Deny(
                $"This grant covers namespace '{grantNamespace}', not '{requested}'.");
        }

        // A second `namespaces` segment further down would mean a path shape this check has not
        // reasoned about. Refuse rather than assume the first one governs.
        if (segments.Skip(nsIndex + 2).Contains("namespaces"))
            return JitPathVerdict.Deny("Unexpected path shape.");

        return JitPathVerdict.Allow;
    }

    /// <summary>
    /// Whether the segments are a discovery listing rather than a resource request.
    /// <c>api</c>, <c>api/v1</c>, <c>apis</c>, <c>apis/{group}</c>, <c>apis/{group}/{version}</c>
    /// — and nothing deeper, because one more segment names a resource.
    /// </summary>
    private static bool IsDiscoveryDepth(string[] segments) => segments[0] switch
    {
        "api" => segments.Length <= 2,
        "apis" => segments.Length <= 3,
        _ => segments.Length == 1,
    };
}
