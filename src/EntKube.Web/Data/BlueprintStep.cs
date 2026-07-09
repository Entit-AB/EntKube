namespace EntKube.Web.Data;

/// <summary>
/// The kind of thing a blueprint step installs.
/// </summary>
public enum BlueprintStepType
{
    /// <summary>A catalog component installed via Helm/manifest (ComponentCatalog entry).</summary>
    Component,
    /// <summary>An operator-backed service instance (CNPG/Redis/RabbitMQ cluster) applied as a CRD.</summary>
    Service,
    /// <summary>
    /// Provisions the underlying cluster on a cloud provider (Cluster API + CAPO) before
    /// any components are installed. Synthesized as the first step when a blueprint has a
    /// <see cref="ClusterBlueprint.ProvisioningProvider"/>; its <c>Key</c> is the provider
    /// ("openstack") and its parameters carry the resolved provisioning config.
    /// </summary>
    ProvisionCluster
}

/// <summary>
/// One ordered item in a <see cref="ClusterBlueprint"/>. Represents either a
/// catalog component (identified by its catalog <c>Key</c>) or a service kind
/// ("cnpg", "redis", "rabbitmq"). Parameters captured at authoring time are
/// stored in <see cref="ParametersJson"/>; they can be overridden per-bootstrap.
/// </summary>
public class BlueprintStep
{
    public Guid Id { get; set; }

    public Guid BlueprintId { get; set; }

    /// <summary>Zero-based position in the install order.</summary>
    public int Order { get; set; }

    public BlueprintStepType StepType { get; set; }

    /// <summary>
    /// For Component steps: the ComponentCatalog entry key (e.g. "cloudnative-pg").
    /// For Service steps: the service kind ("cnpg", "redis", "rabbitmq").
    /// </summary>
    public required string Key { get; set; }

    /// <summary>Resource/release name to create on the cluster (e.g. "cloudnative-pg", "app-db").</summary>
    public required string Name { get; set; }

    /// <summary>Target Kubernetes namespace.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// JSON parameters. For Component steps: a map of ComponentFormField.Key → value.
    /// For Service steps: the create arguments for that service kind.
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>
    /// Reserved: when true a failure of this step does not stop the run. The default
    /// flow is stop-on-failure with resume.
    /// </summary>
    public bool Optional { get; set; }

    // Navigation
    public ClusterBlueprint Blueprint { get; set; } = null!;
}
