namespace EntKube.Contracts.Fleet;

/// <summary>One KubeVirt virtual machine, as it stands on the cluster right now.</summary>
/// <param name="Status">KubeVirt's own word for the state: "Running", "Stopped", "Paused", "Provisioning", "ErrorUnschedulable"…</param>
/// <param name="RunStrategy">Always, Halted, Manual or RerunOnFailure.</param>
/// <param name="LiveMigratable">KubeVirt's verdict on the running instance; null when nothing is running.</param>
public sealed record VirtualMachineSummary(
    string Namespace,
    string Name,
    string Status,
    string RunStrategy,
    int? Cores,
    string? Memory,
    string? Node,
    string? IpAddress,
    bool? LiveMigratable,
    DateTime? CreatedAt);

/// <summary>A read of one cluster's VMs. <see cref="Error"/> is set when nothing could be read.</summary>
public sealed record VirtualMachineList(
    IReadOnlyList<VirtualMachineSummary> Machines,
    IReadOnlyList<string> Warnings,
    string? Error);

/// <summary>
/// What can be done to a running VM through the contract. Deleting is deliberately not here: it
/// destroys the disks the VM imported, and the page refuses it while the VM has published ports — a
/// rule that lives with the routes, not in this module.
/// </summary>
public enum VirtualMachineAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Unpause,
    Migrate
}

/// <summary>Whether an action reached the cluster, and why not when it did not.</summary>
public sealed record VirtualMachineActionOutcome(bool Succeeded, string? Error);
