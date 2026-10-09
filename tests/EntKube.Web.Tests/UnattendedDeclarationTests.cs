using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Every background scope declares what it is — a list that may only shrink, and that a new
/// background service cannot quietly stay off.
///
/// <para><b>Why this is a test and not a convention.</b> The gate refuses an undeclared scope, so
/// a background service that forgets the declaration does not silently bypass anything any more —
/// it stops working. That is the right failure, but it is a failure discovered at 3am by whoever
/// notices the reconcile has not run. The check belongs here, where a missing declaration is a
/// red build instead.</para>
///
/// <para><b>What the list is, exactly.</b> Every <c>CreateScope()</c> in a class implementing
/// <c>BackgroundService</c> or <c>IHostedService</c>. Not every one of them changes a cluster —
/// a declaration is never read unless a mutation arrives with no sink — so this is deliberately
/// an <em>upper bound</em>: the set of contexts that <em>could</em> act unattended. The
/// gate-coverage ratchet next door is a lower bound, and the two are useful for opposite reasons.
/// Narrowing this one means proving a service cannot reach a mutation, which is worth doing only
/// if the list ever gets long enough to stop being readable.</para>
/// </summary>
public class UnattendedDeclarationTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Background scopes and the reason each declares, measured 2026-10-09. The reasons are the
    /// strings that reach the audit trail, so they are asserted rather than counted — a renamed
    /// service that keeps a stale reason makes its own audit rows unfindable.
    /// </summary>
    private static readonly string[] Baseline =
    [
        "AdvisorScanService:advisor-scan",
        "AlertEscalationService:alert-escalation",
        "AlertSyncService:alert-sync",
        "AppL4RouteHealthService:app-l4-route-health",
        "BootstrapRunnerService:bootstrap-runner",
        "CertificateDistributionReconcileService:certificate-distribution-reconcile",
        "CostScanService:cost-scan",
        "DeploymentSyncService:deployment-sync",
        "DrScanService:dr-scan",
        "DriftScanService:drift-scan",
        "ExternalRouteHealthService:external-route-health",
        "GitSyncService:git-sync",
        "HeadscaleCertSyncService:headscale-cert-sync",
        "JitGrantReaperService:jit-grant-reaper",
        "KeycloakBackupSchedulerService:keycloak-backup-scheduler",
        "MessagingStatusPollingService:messaging-status-polling",
        "ObservedSecretRefreshService:observed-secret-refresh",
        "ResourceUsageCollectorService:resource-usage-collector",
        "RolloutWatcherService:rollout-watcher",
        "SearchStatusPollingService:search-status-polling",
        "SecretExpiryNotificationService:secret-expiry-notification",
        "SupplyChainScanService:supply-chain-scan",
        "SupportMailPoller:support-mail-poller",
        "TelemetryAlertEvaluator:telemetry-alert-evaluator",
        "UptimeTrackingService:uptime-tracking",
    ];

    [Fact]
    public void Every_background_scope_declares_what_it_is()
    {
        (List<string> declared, List<string> undeclared) = Measure();

        undeclared.Should().BeEmpty(
            "a background scope with no declaration is refused by ClusterChangeGate, so this is "
            + "not a style point — the service will stop changing anything. Declare it where the "
            + "scope is created: scope.DeclareUnattended(\"<what the work is>\")");

        declared.Should().BeEquivalentTo(Baseline,
            "a new background context that may change clusters unattended is a decision, not a "
            + "detail. Adding one means adding it here, where the whole list can be read at once");
    }

    /// <summary>
    /// Scopes with a declaration, as "Service:reason", and those without.
    ///
    /// <para>A declaration counts when it is on one of the few lines after the scope is created.
    /// Deliberately positional rather than anywhere-in-the-file: a declaration made in a different
    /// method does not cover this scope, and a check that accepted one would pass a service that
    /// declares on its happy path and not on its retry.</para>
    /// </summary>
    private static (List<string> Declared, List<string> Undeclared) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        List<string> declared = [];
        List<string> undeclared = [];

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string[] lines = File.ReadAllLines(path);
            string body = string.Join('\n', lines);

            bool hosted = Regex.IsMatch(body, @":\s*BackgroundService\b")
                       || Regex.IsMatch(body, @"\bIHostedService\b");
            if (!hosted) continue;

            string service = Path.GetFileNameWithoutExtension(path);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"\bCreateScope\(\)")) continue;

                string window = string.Join('\n', lines.Skip(i + 1).Take(3));
                Match declaration = Regex.Match(window, @"DeclareUnattended\(""([^""]+)""\)");

                if (declaration.Success)
                {
                    string entry = $"{service}:{declaration.Groups[1].Value}";
                    if (!declared.Contains(entry)) declared.Add(entry);
                }
                else
                {
                    undeclared.Add($"{service}:{i + 1}");
                }
            }
        }

        declared.Sort(StringComparer.Ordinal);
        return (declared, undeclared);
    }
}
