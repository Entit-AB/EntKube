namespace EntKube.Web.Data;

/// <summary>
/// A named, per-blueprint variable that can be referenced inside a
/// <see cref="BlueprintStep"/>'s parameters as <c>${Name}</c>. Each variable
/// carries a value per environment (<see cref="BlueprintVariableValue"/>), so a
/// single blueprint can target dev and prod cleanly: cluster-specific values
/// (domains, cluster issuers, storage links, database references) are factored
/// out here and resolved at bootstrap time from the target cluster's environment.
/// </summary>
public class BlueprintVariable
{
    public Guid Id { get; set; }

    public Guid BlueprintId { get; set; }

    /// <summary>The token name referenced as <c>${Name}</c> in step parameters (e.g. "domain").</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Fallback value used when the target environment has no explicit value.</summary>
    public string? DefaultValue { get; set; }

    // Navigation
    public ClusterBlueprint Blueprint { get; set; } = null!;
    public ICollection<BlueprintVariableValue> Values { get; set; } = [];
}

/// <summary>
/// The value of a <see cref="BlueprintVariable"/> for a specific environment.
/// At bootstrap the target cluster's <see cref="KubernetesCluster.EnvironmentId"/>
/// selects which value substitutes the <c>${var}</c> token.
/// </summary>
public class BlueprintVariableValue
{
    public Guid Id { get; set; }

    public Guid VariableId { get; set; }

    public Guid EnvironmentId { get; set; }

    public required string Value { get; set; }

    // Navigation
    public BlueprintVariable Variable { get; set; } = null!;
}
