namespace EntKube.Cli;

/// <summary>One CLI command: how it is invoked, what it calls, and how its output is shaped.</summary>
public sealed record CliCommand
{
    /// <summary>Words that invoke it, e.g. ["deployments", "sync"].</summary>
    public required string[] Path { get; init; }

    public required string Description { get; init; }

    /// <summary>Builds the API path from the parsed arguments. Throws <see cref="CliUsageException"/> on bad input.</summary>
    public required Func<CliArgs, string> BuildPath { get; init; }

    /// <summary>
    /// Which verb to send. A bool was enough while POST was the only write; DELETE made it two
    /// bits pretending to be one, which is how a third verb gets added as a second bool.
    /// </summary>
    public CliVerb Verb { get; init; } = CliVerb.Get;

    /// <summary>Kept so existing definitions read the same; projects onto <see cref="Verb"/>.</summary>
    public bool IsPost
    {
        get => Verb == CliVerb.Post;
        init => Verb = value ? CliVerb.Post : CliVerb.Get;
    }

    /// <summary>True for anything that is not a read — what an operator wants warned about.</summary>
    public bool Mutates => Verb is CliVerb.Post or CliVerb.Delete;

    /// <summary>Columns for the default table view: header → JSON property path.</summary>
    public (string Header, string Path)[] Columns { get; init; } = [];

    /// <summary>
    /// For endpoints that return an object wrapping a list, the property holding the rows.
    /// Null when the response is already an array.
    /// </summary>
    public string? RowsProperty { get; init; }

    /// <summary>Usage line shown in help and on a usage error.</summary>
    public string Usage => string.Join(' ', Path);
}

/// <summary>Thrown for a mistake in how the command was invoked, as opposed to a failure from the API.</summary>
public enum CliVerb { Get, Post, Delete }

public sealed class CliUsageException(string message) : Exception(message);

/// <summary>The CLI's command table.</summary>
public static class CliCommands
{
    public static IReadOnlyList<CliCommand> All { get; } =
    [
        new CliCommand
        {
            Path = ["whoami"],
            Description = "Show the tenant and scopes this token has",
            BuildPath = _ => "/api/v1/whoami",
        },
        new CliCommand
        {
            Path = ["clusters", "list"],
            Description = "List registered clusters",
            BuildPath = _ => "/api/v1/clusters",
            Columns = [("NAME", "name"), ("ENVIRONMENT", "environment"),
                       ("STATUS", "provisioningStatus"), ("COMPONENTS", "componentCount"), ("ID", "id")],
        },
        new CliCommand
        {
            Path = ["components", "list"],
            Description = "List components on a cluster (--cluster <id>)",
            BuildPath = a => $"/api/v1/clusters/{a.Required("cluster")}/components",
            Columns = [("NAME", "name"), ("CHART", "chart"), ("VERSION", "version"),
                       ("STATUS", "status"), ("NAMESPACE", "ns")],
        },
        new CliCommand
        {
            Path = ["apps", "list"],
            Description = "List applications",
            BuildPath = _ => "/api/v1/apps",
            Columns = [("NAME", "name"), ("CUSTOMER", "customer"),
                       ("DEPLOYMENTS", "deploymentCount"), ("ID", "id")],
        },
        new CliCommand
        {
            Path = ["deployments", "list"],
            Description = "List deployments (optionally --app <id>)",
            BuildPath = a => "/api/v1/deployments" + Query(("appId", a.Optional("app"))),
            Columns = [("APP", "app"), ("NAME", "name"), ("ENVIRONMENT", "environment"),
                       ("CLUSTER", "cluster"), ("SYNC", "syncStatus"), ("HEALTH", "healthStatus"), ("ID", "id")],
        },
        new CliCommand
        {
            Path = ["deployments", "sync"],
            Description = "Apply a deployment's manifests (--id <id>)",
            BuildPath = a => $"/api/v1/deployments/{a.Required("id")}/sync",
            IsPost = true,
        },
        new CliCommand
        {
            Path = ["deployments", "restart"],
            Description = "Restart one workload (--id <id> --workload <name>)",
            BuildPath = a => $"/api/v1/deployments/{a.Required("id")}/restart"
                             + Query(("workload", a.Required("workload"))),
            IsPost = true,
        },
        new CliCommand
        {
            Path = ["advisor"],
            Description = "Operations Advisor findings",
            BuildPath = _ => "/api/v1/advisor/findings",
            Columns = [("SEVERITY", "severity"), ("WHEN", "horizon"), ("CATEGORY", "category"),
                       ("TITLE", "title"), ("SCOPE", "scope")],
        },
        new CliCommand
        {
            Path = ["incidents"],
            Description = "Alert incidents (--open for unresolved only)",
            BuildPath = a => "/api/v1/incidents" + Query(("open", a.Flag("open") ? "true" : null)),
            Columns = [("SEVERITY", "severity"), ("STATUS", "status"), ("ALERT", "alertName"),
                       ("CLUSTER", "cluster"), ("STARTED", "startsAt")],
        },
        new CliCommand
        {
            Path = ["upgrades"],
            Description = "Components behind their published chart versions",
            BuildPath = _ => "/api/v1/upgrades",
            RowsProperty = "components",
            Columns = [("CLUSTER", "cluster"), ("COMPONENT", "component"), ("INSTALLED", "installed"),
                       ("LATEST", "latest"), ("STATUS", "status"), ("LAG", "lag")],
        },
        new CliCommand
        {
            Path = ["drift"],
            Description = "Deployments changed outside EntKube",
            BuildPath = _ => "/api/v1/drift",
            RowsProperty = "results",
            Columns = [("APP", "app"), ("DEPLOYMENT", "deployment"), ("CLUSTER", "cluster"),
                       ("STATE", "state"), ("CHANGES", "changedLines")],
        },
        new CliCommand
        {
            Path = ["supply-chain"],
            Description = "Running images joined to their vulnerability scans",
            BuildPath = _ => "/api/v1/supply-chain",
            RowsProperty = "images",
            Columns = [("STATE", "state"), ("IMAGE", "image"), ("CLUSTER", "cluster"),
                       ("CRITICAL", "critical"), ("HIGH", "high"), ("FIXABLE", "fixable")],
        },
        new CliCommand
        {
            Path = ["cost"],
            Description = "Cost run rate by namespace",
            BuildPath = _ => "/api/v1/cost",
            RowsProperty = "namespaces",
            Columns = [("NAMESPACE", "ns"), ("CLUSTER", "cluster"), ("CUSTOMER", "customer"),
                       ("CPU", "cpuCores"), ("MEM GiB", "memoryGiB"), ("MONTHLY", "monthlyCost")],
        },
        new CliCommand
        {
            Path = ["cost", "history"],
            Description = "Cost actually incurred over a period "
                        + "(--from yyyy-MM-dd --to yyyy-MM-dd --group-by customer|app|cluster|environment|namespace)",
            BuildPath = a => "/api/v1/cost/history" + Query(
                ("from", a.Optional("from")),
                ("to", a.Optional("to")),
                ("groupBy", a.Optional("group-by")),
                ("customerId", a.Optional("customer")),
                ("appId", a.Optional("app"))),
            RowsProperty = "groups",
            Columns = [("NAME", "label"), ("COST", "cost"), ("PREVIOUS", "previousCost"),
                       ("CHANGE", "change"), ("CPU CORE-H", "cpuCoreHours")],
        },
        new CliCommand
        {
            Path = ["rollouts"],
            Description = "Recent release watches and their verdicts",
            BuildPath = _ => "/api/v1/rollouts",
            Columns = [("STATUS", "status"), ("APP", "app"), ("DEPLOYMENT", "deployment"),
                       ("STARTED", "startedAt"), ("VERDICT", "verdict")],
        },
        new CliCommand
        {
            Path = ["dr"],
            Description = "Backup and restore posture per cluster",
            BuildPath = _ => "/api/v1/disaster-recovery",
            Columns = [("CLUSTER", "cluster"), ("VELERO", "veleroInstalled"), ("RESTORABLE", "restorable"),
                       ("LAST BACKUP", "lastUsableBackupAt"), ("SCHEDULES", "scheduleCount")],
        },

        // ── Everything below exists because the API had routes the CLI could not reach ──
        //
        // ⚠️ The gap was found by comparing the two surfaces, not by anyone noticing: seven of
        // twenty-three endpoints had no command, and one of them was added the same week by the
        // same author who then did not add its command. `CliEndpointParityTests` now fails when
        // that happens again, which is the actual fix — this list is just today's backlog.

        new CliCommand
        {
            Path = ["cost", "months"],
            Description = "List the months that have a cost ledger",
            BuildPath = _ => "/api/v1/cost/months",
            Columns = [("MONTH", "month"), ("CLUSTERS", "clusters"), ("TOTAL", "total")],
        },
        new CliCommand
        {
            Path = ["cost", "rates", "list"],
            Description = "List per-cluster cost rates",
            BuildPath = _ => "/api/v1/cost-rates",
            Columns = [("CLUSTER", "clusterName"), ("CPU", "cpuCorePerMonth"),
                       ("MEMORY", "memoryGiBPerMonth"), ("STORAGE", "storageGiBPerMonth"),
                       ("IDLE", "chargeIdleCapacity"), ("CLUSTER ID", "clusterId")],
        },
        new CliCommand
        {
            Path = ["cost", "rates", "show"],
            Description = "Show one cluster's cost rate (--cluster <id>)",
            BuildPath = a => $"/api/v1/cost-rates/{a.Required("cluster")}",
        },
        new CliCommand
        {
            Path = ["cost", "rates", "clear"],
            Description = "Remove a cluster's cost rate, falling back to the defaults (--cluster <id>)",
            BuildPath = a => $"/api/v1/cost-rates/{a.Required("cluster")}",
            Verb = CliVerb.Delete,
        },
        new CliCommand
        {
            Path = ["advisor", "ack"],
            Description = "Acknowledge an advisor finding (--id <finding-id>)",
            BuildPath = a => $"/api/v1/advisor/findings/{a.Required("id")}/acknowledge",
            Verb = CliVerb.Post,
        },
        new CliCommand
        {
            // ⚠️ Partial success is normal here: one binding failing does not stop its siblings,
            // so the endpoint answers 207 with per-binding outcomes rather than a bare ok. The
            // table shows each binding, because "it worked" is not a truthful summary of a sync
            // where two of nine secrets did not reach their cluster.
            Path = ["secrets", "sync"],
            Description = "Re-sync an app's bound credentials into its clusters "
                          + "(--app <id> [--environment <id>])",
            BuildPath = a => $"/api/v1/apps/{a.Required("app")}/secrets/sync"
                             + Query(("environmentId", a.Optional("environment"))),
            Verb = CliVerb.Post,
            Columns = [("KIND", "kind"), ("SECRET", "secret"), ("ERROR", "error")],
            RowsProperty = "bindings",
        },
    ];

    /// <summary>
    /// Resolves the longest matching command, so "deployments sync" wins over a
    /// hypothetical "deployments". Returns the remaining arguments alongside it.
    /// </summary>
    public static (CliCommand? Command, string[] Remaining) Resolve(string[] args)
    {
        CliCommand? best = null;
        int bestLength = 0;

        foreach (CliCommand command in All)
        {
            if (args.Length < command.Path.Length || command.Path.Length <= bestLength)
            {
                continue;
            }

            bool matches = !command.Path.Where((word, i) =>
                !string.Equals(args[i], word, StringComparison.OrdinalIgnoreCase)).Any();

            if (matches)
            {
                best = command;
                bestLength = command.Path.Length;
            }
        }

        return best is null ? (null, args) : (best, args[bestLength..]);
    }

    private static string Query(params (string Key, string? Value)[] parameters)
    {
        List<string> parts = [];
        foreach ((string key, string? value) in parameters)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }
}
