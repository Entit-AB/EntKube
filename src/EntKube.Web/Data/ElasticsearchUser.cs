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
/// Whether this account can sign in to Kibana, and with how much of it.
///
/// <para>Kibana access is granted as application privileges on the user's own role rather than
/// through the built-in <c>viewer</c> and <c>editor</c> roles, because those two carry read (or
/// write) on <em>every</em> index — which would quietly undo the index scoping this user exists
/// for.</para>
/// </summary>
public enum ElasticsearchKibanaAccess
{
    /// <summary>No Kibana. The account is for an application, not a person.</summary>
    None,

    /// <summary>Can open Kibana, search its own indices and read dashboards, but save nothing.</summary>
    Read,

    /// <summary>Can also create data views, dashboards and saved searches.</summary>
    All
}

/// <summary>
/// A native-realm Elasticsearch user scoped to one index pattern — an application, or a person who
/// signs in to Kibana with it.
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

    /// <summary>
    /// Whether a person can sign in to Kibana with this account. Default None: most of these are
    /// applications, and an application has no use for a UI it will never open.
    /// </summary>
    public ElasticsearchKibanaAccess KibanaAccess { get; set; } = ElasticsearchKibanaAccess.None;

    /// <summary>
    /// The Kibana space this account is confined to, by <see cref="ElasticsearchKibanaSpace.SpaceId"/>.
    /// Null means every space, which is the right answer for the only team on a cluster and the
    /// wrong one the moment there are two.
    /// </summary>
    public string? KibanaSpaceId { get; set; }

    /// <summary>When the password was last generated — for the account, and for whoever holds it.</summary>
    public DateTime? PasswordSetAt { get; set; }

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
