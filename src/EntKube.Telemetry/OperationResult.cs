namespace EntKube.Web.Services;

// NOTE ON THE NAMESPACE: this file lives in the EntKube.Telemetry assembly but keeps the
// EntKube.Web.Services namespace on purpose. The type is returned by every engine query surface
// (ILogBackend, ITraceQueryService, IRumQueryService) AND used by several hundred call sites across
// EntKube.Web. Moving the assembly is the change worth making now; renaming the type — it is not
// Kubernetes-specific and never was — is a purely mechanical follow-up that would otherwise bury this
// diff. See docs/telemetry-in-cluster.md §5.1.

/// <summary>
/// A simple result type for Kubernetes operations. Operations can fail for
/// many reasons (no kubeconfig, cluster unreachable, pod not found), so we
/// use Result rather than exceptions for expected failures.
/// </summary>
public class KubernetesOperationResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }

    public static KubernetesOperationResult Success() => new() { IsSuccess = true };
    public static KubernetesOperationResult Failure(string error) => new() { IsSuccess = false, Error = error };
}

/// <summary>
/// Result type with a data payload for operations that return information
/// (e.g. pod lists, log content).
/// </summary>
public class KubernetesOperationResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }

    public static KubernetesOperationResult<T> Success(T data) => new() { IsSuccess = true, Data = data };
    public static KubernetesOperationResult<T> Failure(string error) => new() { IsSuccess = false, Error = error };
}
