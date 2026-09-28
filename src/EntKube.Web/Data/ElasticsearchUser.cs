namespace EntKube.Web.Data;

/// <summary>
/// What an application is allowed to do with the indices it was given.
/// </summary>
public enum ElasticsearchAccess
{
    /// <summary>Search and read. Cannot write, cannot create an index.</summary>
    Viewer,

    /// <summary>Read and write, including creating the indices and data streams it writes to.</summary>
    Writer,

    /// <summary>Everything on its own indices, including deleting them. Still nothing outside the pattern.</summary>
    Manager
}

/// <summary>
/// A native-realm Elasticsearch user scoped to one index pattern, created for an application rather
/// than for a person.
///
/// <para><b>Why this exists at all.</b> Without it the only account on the cluster is the
/// operator-generated <c>elastic</c> superuser, so every application that needs to write a log line
/// gets the credential that can also delete every index. The role here names exactly one index
/// pattern and one level of access, so a compromised application reaches its own data and no
/// further.</para>
///
/// <para>The password is generated once, written to a Kubernetes Secret in the Elasticsearch
/// namespace, and read back from there when a binding needs it — the same shape Strimzi uses for
/// Kafka users. It is never stored in the management plane's database.</para>
/// </summary>
public class ElasticsearchUser
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ElasticsearchClusterId { get; set; }

    /// <summary>The Elasticsearch username. Lowercase, no spaces.</summary>
    public required string Username { get; set; }

    /// <summary>Indices this user may touch, e.g. "logs-app-*". Everything else is invisible to it.</summary>
    public required string IndexPattern { get; set; }

    public ElasticsearchAccess Access { get; set; } = ElasticsearchAccess.Writer;

    /// <summary>When the user was last successfully applied to the cluster.</summary>
    public DateTime? LastAppliedAt { get; set; }

    /// <summary>What the last apply said, when it failed.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ElasticsearchCluster ElasticsearchCluster { get; set; } = null!;

    /// <summary>The Secret in the Elasticsearch namespace holding this user's password (key: "password").</summary>
    public string CredentialsSecretName => $"es-user-{Username}";

    /// <summary>The role created alongside the user. One role per user keeps the scoping obvious.</summary>
    public string RoleName => $"entkube-{Username}";
}
