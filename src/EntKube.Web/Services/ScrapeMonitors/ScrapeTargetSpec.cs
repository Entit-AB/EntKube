namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>The two kinds of object that tell Prometheus what to scrape.</summary>
public enum MonitorKind
{
    /// <summary>Selects pods, and scrapes a port their containers declare.</summary>
    PodMonitor,

    /// <summary>Selects Services, and scrapes the pods behind one of their ports.</summary>
    ServiceMonitor,
}

/// <summary>Which monitor: there is at most one of a kind with a name in a namespace.</summary>
public sealed record MonitorIdentity(MonitorKind Kind, string Namespace, string Name)
{
    public override string ToString() => $"{Kind} {Namespace}/{Name}";
}

/// <summary>One relabelling rule on an endpoint. Only what a target sets is written.</summary>
public sealed record ScrapeRelabeling(
    IReadOnlyList<string>? SourceLabels = null,
    string? TargetLabel = null,
    string? Regex = null,
    string? Action = null);

/// <summary>One port of a target, and how it is scraped.</summary>
/// <param name="Port">
/// The port's name: the container's for a PodMonitor, the Service's for a ServiceMonitor. Names,
/// because charts renumber ports between versions and keep the names (the RabbitMQ operator moved
/// from 9782 to 8080 and stayed "metrics").
/// </param>
/// <param name="ContainerPort">
/// The container port's number, for the one target whose port has no name (the descheduler).
/// </param>
/// <param name="Path">The HTTP path the metrics are served on.</param>
/// <param name="Https">
/// Scraped over TLS without verifying the certificate: every such target serves one it signed
/// itself, or one from an operator's own CA.
/// </param>
/// <param name="HttpsWhenContainerArgument">
/// Scraped over TLS only where a live pod's command line carries this argument. CNPG serves its
/// metrics in plain text unless the Cluster asks for TLS, and the pod says which.
/// </param>
/// <param name="HonorLabels">
/// Whether the series' own namespace and pod labels win over the target's. True for exporters that
/// report about other workloads (KEDA, Tetragon, DCGM, KubeVirt), where the target's labels would
/// push the interesting ones to <c>exported_*</c>.
/// </param>
/// <param name="BearerTokenFile">A token Prometheus presents, as a file in its own pod.</param>
/// <param name="Relabelings">Labels added to the target before it is scraped.</param>
public sealed record ScrapeEndpointSpec(
    string? Port,
    int? ContainerPort = null,
    string Path = "/metrics",
    bool Https = false,
    string? HttpsWhenContainerArgument = null,
    bool HonorLabels = false,
    string? BearerTokenFile = null,
    IReadOnlyList<ScrapeRelabeling>? Relabelings = null)
{
    /// <summary>How a sentence names the port.</summary>
    public string PortLabel => Port ?? ContainerPort?.ToString() ?? "";
}

/// <summary>The monitor a target should have: what it is called, what it selects and which ports.</summary>
public sealed record MonitorSpec(
    MonitorIdentity Identity,
    LabelSelector Selector,
    IReadOnlyList<ScrapeEndpointSpec> Endpoints)
{
    /// <summary>
    /// True where EntKube wrote this monitor before it labelled what it writes, so an unlabelled one
    /// of this name is taken over as long as it still selects exactly <see cref="Selector"/>. Only
    /// RabbitMQ has one: the old "Enable metrics" button's <c>{name}-metrics</c>.
    /// </summary>
    public bool TakesOverUnlabelled { get; init; }
}

/// <summary>
/// One thing on a cluster that has, or could have, metrics: a data service, or a part of an
/// installed component.
///
/// <para>Either <see cref="Monitor"/> says which monitor EntKube gives it, or <see cref="Info"/>
/// says why there is none to give. Exactly one is set.</para>
/// </summary>
/// <param name="Key">
/// What it is, by kind, namespace and name. Not by row id: a CNPG major upgrade replaces the row and
/// keeps the database, and a monitor keyed by the row would be deleted and created again.
/// </param>
/// <param name="Title">Its name, for a person.</param>
/// <param name="ServiceKind">What sort of thing it is, for a person: "PostgreSQL", "Tetragon agent".</param>
/// <param name="Namespace">Where it runs. The monitor goes in the same namespace.</param>
/// <param name="Monitor">The monitor it should have.</param>
/// <param name="Info">Why EntKube gives it none, as a full sentence.</param>
public sealed record ScrapeTargetSpec(
    string Key,
    string Title,
    string ServiceKind,
    string Namespace,
    MonitorSpec? Monitor = null,
    string? Info = null);
