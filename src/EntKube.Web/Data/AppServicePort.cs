namespace EntKube.Web.Data;

/// <summary>
/// A structured record of a port an app exposes in a given environment — the
/// "destinations" side of the connectivity graph. Kubernetes Service objects
/// today live only as raw YAML inside <see cref="DeploymentManifest"/>; this
/// entity lifts each exposed port into a typed row so that connectivity rules,
/// routes, and the pre-apply analyzer can reference a real target instead of a
/// string that is assumed to exist.
///
/// Inferred rows are recreated from the Service manifests on every graph build.
/// Declared rows are authored by a user (e.g. to document a port that will be
/// added later) and survive a re-inference. Scoped per app + environment,
/// mirroring <see cref="AppNetworkPolicy"/>.
/// </summary>
public class AppServicePort
{
    public Guid Id { get; set; }

    public Guid AppId { get; set; }

    /// <summary>Which environment this exposure applies to. Ports are per-environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// The deployment whose Service exposes this port, when known. Plain scoping
    /// column (no relationship) so there is a single cascade path from App.
    /// </summary>
    public Guid? AppDeploymentId { get; set; }

    /// <summary>The namespace the Service lives in (derived at inference time).</summary>
    public string? Namespace { get; set; }

    /// <summary>The Kubernetes Service name that opens this port.</summary>
    public required string ServiceName { get; set; }

    /// <summary>The Service port (spec.ports[].port).</summary>
    public int Port { get; set; }

    /// <summary>The backing container port (spec.ports[].targetPort), when numeric.</summary>
    public int? TargetPort { get; set; }

    /// <summary>Transport protocol. Combined with Port forms the port's identity.</summary>
    public L4Protocol Protocol { get; set; } = L4Protocol.Tcp;

    /// <summary>Optional Service port name (spec.ports[].name), e.g. "http", "grpc".</summary>
    public string? PortName { get; set; }

    /// <summary>
    /// Application-layer protocol hint (spec.ports[].appProtocol or inferred from
    /// the port name): http, https, grpc, tcp… Drives whether an L7 Istio
    /// AuthorizationPolicy is meaningful for this port in later phases.
    /// </summary>
    public string? AppProtocol { get; set; }

    public ConnectivitySource Source { get; set; } = ConnectivitySource.Inferred;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public App App { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
