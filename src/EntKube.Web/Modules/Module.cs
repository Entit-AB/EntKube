namespace EntKube.Web.Modules;

/// <summary>
/// The twelve bounded contexts EntKube is being decomposed into. See
/// <c>docs/decomposition.md</c> for why these twelve and not others.
///
/// <para>This is a <em>logical</em> boundary first. Nothing moves process or database when a
/// module is declared; what a module buys immediately is a named owner for every table and a
/// test that notices when something reaches across. Deployables come later and there are fewer
/// of them than there are modules — several modules ship in one process on purpose.</para>
/// </summary>
public enum Module
{
    /// <summary>Users, tenants, customers, roles, tokens, JIT grants, Keycloak.</summary>
    Identity,

    /// <summary>Clusters, nodes, kubeconfigs, blueprints, bootstrap — the apply path.</summary>
    Fleet,

    /// <summary>Component definitions, installs, upgrades, drift, CA trust distribution.</summary>
    Catalog,

    /// <summary>Managed service lifecycle: Postgres, Mongo, Redis, Kafka, RabbitMQ, Elastic, LDAP, Harbor, object storage.</summary>
    DataServices,

    /// <summary>The Stalwart mail stack.</summary>
    Mail,

    /// <summary>Apps, environments, deployments, rollouts, policy, autoscaling, git, service bindings.</summary>
    Delivery,

    /// <summary>Routes, network policy, service mesh, mTLS, VPN.</summary>
    Connectivity,

    /// <summary>The vault and the secrets in it.</summary>
    Secrets,

    /// <summary>Logs, traces, metrics, dashboards, alerts, incidents and how they are delivered.</summary>
    Telemetry,

    /// <summary>Rates, usage, ledger, chargeback.</summary>
    Cost,

    /// <summary>The consultancy business layer: tickets, support mail, contracts, time, knowledge, on-call.</summary>
    Support,

    /// <summary>
    /// The cross-cutting findings feed. Reads from everywhere, owns almost nothing — which is
    /// what makes it the module that proves the contracts work. When Advisor can be written
    /// entirely against other modules' public APIs, the boundaries are real.
    /// </summary>
    Advisor,
}
