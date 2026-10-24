using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Every background sweep, and whether it acts as each tenant in turn (docs/decomposition.md
/// §4.0.1.2) — classified by name, so a new sweep cannot join without someone deciding.
///
/// <para><b>Why a list and not just a count.</b> The decision this measures has been applied
/// thirteen times across #175–#206, and each time the hard part was knowing which sweeps were
/// left. The count was repeatedly wrong, in both directions, and always for the same reason: it
/// was inferred from a pattern instead of read. This file is the classification, so the next
/// conversion starts from a decision rather than a grep.</para>
///
/// <para><b>What the signal is worth.</b> <see cref="LoopsTenants"/> looks for the tenant table
/// and a loop in the same file. That is a <em>claim-checker, not a prover</em>: it cannot tell an
/// outer loop from an incidental one. It earns its place through
/// <see cref="Nothing_in_the_backlog_or_the_exemptions_quietly_loops_already"/>, which requires
/// the signal to be ABSENT from everything not claiming to loop — so a sweep that gets converted
/// and left in the backlog fails, which is the drift this would otherwise accumulate.</para>
///
/// <para>⚠️ <b>Two things this file exists because I got wrong.</b> Enumerating
/// <c>AddHostedService&lt;T&gt;</c> alone misses the lambda registrations —
/// <c>GitSyncService</c> and five <c>SegmentSealService</c>s — so a count built that way says 25
/// where the answer is 27. And <c>SupportMailPoller</c> reads as converted if you stop at
/// <c>foreach (Guid tenantId in due)</c>: <c>due</c> comes from <c>SupportMailboxes</c>, so the
/// tenants it acts as are taken from the work rows, which is the thing §4.0.1.2 forbids. <b>The
/// loop variable's name is not evidence of where its value came from.</b></para>
/// </summary>
public class SweepTenancyRatchetTests
{
    private const string Src = "../../../../../src";

    /// <summary>
    /// The management plane. Registrations are read from here only — <c>EntKube.TelemetryNode</c>
    /// registers two hosted services of its own (<c>ObjectStorageBudgetService</c>,
    /// <c>SegmentCacheTrimService</c>) and §4.0.1.2 does not reach them: the node serves exactly one
    /// cluster and authenticates with a token, so there is no tenant to act as. Named here so the
    /// next reader can see they were considered rather than missed.
    ///
    /// <para><c>VolumeGuardService</c> used to be a third. It moved into <c>EntKube.Telemetry</c>
    /// when telemetry came back to the management plane (docs/telemetry-central-again.md), which
    /// registers it too — so it is classified below like any other sweep here, and the node's copy
    /// is no longer the only one.</para>
    ///
    /// <para>Source files are still resolved across all of <c>src</c>, because the management plane
    /// registers two types that live elsewhere — <c>SegmentSealService</c> and
    /// <c>VolumeGuardService</c>, both in <c>EntKube.Telemetry</c>.</para>
    /// </summary>
    private const string ManagementPlane = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Sweeps whose outer loop is over the tenant table, so the tenant is an input to everything
    /// below it.
    /// </summary>
    private static readonly string[] ActsAsEachTenant =
    [
        "AdvisorScanService",
        "AlertEscalationService",
        "AlertSyncService",
        "AppL4RouteHealthService",
        "BootstrapRunnerService",
        "CostScanService",
        "DeploymentSyncService",
        "DrScanService",
        "DriftScanService",
        "GitSyncService",
        "HeadscaleCertSyncService",
        "JitGrantReaperService",
        "KeycloakBackupSchedulerService",
        "ExternalRouteHealthService",
        "ObservedSecretRefreshService",
        "ResourceUsageCollectorService",
        "RolloutWatcherService",
        "SecretExpiryNotificationService",
        "SupportMailPoller",
        "SupplyChainScanService",
        "TelemetryAlertEvaluator",
        "UptimeTrackingService",
    ];

    /// <summary>
    /// Thin hosts: they call one method on another service, and that service owns the loop. Named
    /// rather than waved through, because all four of these looked exempt for exactly this reason
    /// while none of their owners looped either (#202).
    /// </summary>
    private static readonly Dictionary<string, string[]> DelegatesTo = new(StringComparer.Ordinal)
    {
        ["CertificateDistributionReconcileService"] = ["CertificateDistributionService"],
        ["MessagingStatusPollingService"] = ["RabbitMQService", "KafkaService"],
        ["SearchStatusPollingService"] = ["ElasticsearchService"],
    };

    /// <summary>Sweeps with no tenant-scoped work to do, and why.</summary>
    private static readonly Dictionary<string, string> NoTenantScopedWork = new(StringComparer.Ordinal)
    {
        ["PresenceHeartbeatService"] =
            "refreshes an in-memory presence tracker; touches no rows and reaches no cluster",
        ["SegmentSealService"] =
            "iterates the live per-tenant segment managers for one signal and seals or expires that "
            + "tenant's own segments — per-tenant by construction, with no cross-tenant decision to make",
        ["VolumeGuardService"] =
            "measures ONE disk that every tenant shares, then acts through the same live per-tenant "
            + "managers, so the tenant is an input to every write exactly as for SegmentSealService. "
            + "Its one genuinely cross-tenant act is deliberate and is not a scoping gap to convert: "
            + "when the volume is full the oldest segments go, whoever wrote them, because the "
            + "alternative is that ingest fails for all of them at once. A shared disk cannot be "
            + "apportioned by asking each tenant in turn — there is one disk and one answer about it",
    };

    /// <summary>
    /// The backlog: sweeps that still read the installation, or take the tenant off the rows they
    /// then act as. Each needs the §4.0.1.2 conversion. <b>This list may only shrink.</b>
    /// </summary>
    private static readonly Dictionary<string, string> NotYetTenantScoped = new(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// <b>3.</b> Three left the backlog, and they were not equal in value — which is worth keeping
    /// separate rather than counting as a uniform win:
    ///
    /// <para><c>SupportMailPoller</c> and <c>SecretExpiryNotificationService</c> each took the
    /// tenant off the rows they then acted <em>as</em> — taking mail in as that tenant, notifying
    /// as that tenant — so a mis-stamped row would have filed one tenant's mail into another's
    /// tickets, or sent one tenant's expiring-secret list to another's channels.</para>
    ///
    /// <para><c>ExternalRouteHealthService</c> makes no tenant-scoped decision at all: it probes a
    /// hostname over HTTPS and writes history on the row it probed. Its conversion buys a cycle per
    /// tenant — isolation for the worker extraction — and nothing about safety. Said here because
    /// a backlog that counts those two kinds the same invites the wrong one being done first.</para>
    /// </summary>
    /// <para><b>1.</b> Two more left, and they are the two kinds again — which is why the pair is
    /// worth describing rather than counting.</para>
    ///
    /// <para><c>ObservedSecretRefreshService</c> is the one that buys <b>safety</b>.
    /// <c>GetObservedAppSecretIdsAsync</c> returned every observed secret in the installation, and
    /// the refresh then read each one from the cluster <em>its own row named</em>, resolving the
    /// credential with <c>secret.Vault.TenantId</c> — a check whose input came from the row being
    /// checked. Both now take the tenant: the id list is scoped through the secret's vault
    /// (<c>VaultService.ObservedAppSecretsFor</c>, public and static so the predicate is assertable
    /// without running the sweep), and <c>RefreshAppSecretFromClusterAsync(tenantId, secretId)</c>
    /// finds the secret only within that tenant before asking <c>ForAsync</c> for the cluster. A
    /// secret whose <c>KubernetesClusterId</c> points outside its tenant now resolves to no cluster
    /// at all instead of being read with that cluster's credential. Both pages that call it have a
    /// route <c>TenantId</c>, so nothing had to be derived.</para>
    ///
    /// <para><c>UptimeTrackingService</c> buys <b>isolation</b> only, and is <em>half</em> converted
    /// on purpose. The snapshot loop is per tenant now, so a tenant whose rows fail to write no
    /// longer ends the cycle for everyone. <b>The retention prune is deliberately still
    /// installation-wide</b>: it deletes by timestamp, names no tenant, decides nothing about one
    /// and reaches no cluster, so scoping it would turn one <c>ExecuteDeleteAsync</c> into one per
    /// tenant — each joining snapshot → deployment → app → customer to find a tenant it then
    /// ignores. More work, no authorization gain.</para>
    ///
    /// <para>⚠️ <b><see cref="LoopsTenants"/> would have passed a half-conversion without noticing
    /// it</b>, because it looks for <c>.Tenants</c> and <c>foreach</c> in the file — which this
    /// class's own remarks already call a claim-checker rather than proof. The honest record of
    /// what each conversion bought therefore has to live in this prose, and "it is in the
    /// ActsAsEachTenant list" is not the same claim as "every query in it is tenant-scoped".</para>
    /// </summary>
    /// <para><b>0 — the backlog is empty.</b> <c>JitGrantReaperService</c> was the last entry, and
    /// it was held back on purpose: its three credential reads meant it had to convert <em>with</em>
    /// the custody work rather than before it, so that two open branches would not both be lowering
    /// a constant. It did, and both numbers moved in one change.</para>
    ///
    /// <para>It is also the entry where acting as the wrong tenant was worst. The sweep's job is to
    /// <b>delete RBAC</b>: it selected every ended grant in the installation, and reached each
    /// cluster by reading <c>Kubeconfig</c> off the row, so there was no tenant anywhere in it.
    /// Both halves are per tenant now (<c>EndedFor</c> is public and static so the predicate is
    /// assertable), and the cluster comes from <c>ForAsync</c>.</para>
    ///
    /// <para>⚠️ One set is deliberately <em>not</em> narrowed: the "claimed" grant ids that protect
    /// live RBAC from the orphan sweep. It is a do-not-delete list, so narrowing it could only ever
    /// make the sweep delete more — and the single case where it would differ is RBAC on one
    /// tenant's cluster claimed by another's grant, which is the broken state the scoping now
    /// prevents. Reaping it is not worth being wrong about. Keeping that reasoning next to the code
    /// matters more than the symmetry.</para>
    ///
    /// <para>This list may only shrink, and it has reached zero — so what this ratchet now does is
    /// stop a NEW sweep being written against the installation. That is a different job from the
    /// one it was created for, and the classification test is what does it.</para>
    /// </summary>
    private const int BaselineNotYetTenantScoped = 0;

    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_hosted_service_is_classified_exactly_once()
    {
        IReadOnlySet<string> hosted = HostedServices();

        hosted.Count.Should().BeGreaterThan(20,
            "if the enumeration stops finding registrations, this whole file silently passes");

        List<string> unclassified = [.. hosted.Where(s => Classifications(s) == 0).Order()];
        List<string> twice = [.. hosted.Where(s => Classifications(s) > 1).Order()];
        List<string> ghosts =
        [
            .. ActsAsEachTenant.Concat(DelegatesTo.Keys).Concat(NoTenantScopedWork.Keys)
                .Concat(NotYetTenantScoped.Keys)
                .Where(s => !hosted.Contains(s)).Order()
        ];

        string.Join(", ", unclassified).Should().BeEmpty(
            "a new background sweep either acts as each tenant in turn (§4.0.1.2) or says here why "
            + "it does not. Deciding that when it is written costs a line; deciding it later means "
            + "re-reading a sweep nobody remembers");

        string.Join(", ", twice).Should().BeEmpty("each sweep belongs in one list");

        string.Join(", ", ghosts).Should().BeEmpty(
            "this names a service that is no longer a hosted service — the entry should go with it");
    }

    [Fact]
    public void Every_sweep_that_claims_to_loop_tenants_shows_it()
    {
        List<string> missing =
        [
            .. ActsAsEachTenant.Where(s => !LoopsTenants(s)).Order(),
            .. DelegatesTo.SelectMany(kv => kv.Value).Where(s => !LoopsTenants(s)).Order(),
        ];

        string.Join(", ", missing).Should().BeEmpty(
            "either the loop was removed or the owner named here is the wrong one. A thin host is "
            + "only exempt because something else does the work — if that something stopped, the "
            + "host is back in the backlog");
    }

    /// <summary>
    /// The half that keeps the lists honest. A sweep someone converts and forgets to move out of
    /// the backlog would otherwise sit there forever, making the number look like work remaining
    /// when it is work already done.
    /// </summary>
    [Fact]
    public void Nothing_in_the_backlog_or_the_exemptions_quietly_loops_already()
    {
        List<string> converted =
        [
            .. NotYetTenantScoped.Keys.Where(LoopsTenants).Order(),
            .. NoTenantScopedWork.Keys.Where(LoopsTenants).Order(),
        ];

        string.Join(", ", converted).Should().BeEmpty(
            "this sweep now reads the tenant table and loops — move it to ActsAsEachTenant and "
            + "lower the baseline, which is the whole point of having one");
    }

    [Fact]
    public void The_backlog_does_not_carry_slack()
    {
        NotYetTenantScoped.Count.Should().Be(BaselineNotYetTenantScoped,
            $"now {NotYetTenantScoped.Count} — bank it, or the slack lets a sweep come back");

        NotYetTenantScoped.Values.Should().OnlyContain(r => r.Length > 20,
            "a backlog entry without a reason is a name someone has to re-derive");
        NoTenantScopedWork.Values.Should().OnlyContain(r => r.Length > 20,
            "an exemption is a claim, and the claim is the reason");
    }

    // ──────── measurement ────────

    private static int Classifications(string service)
        => (ActsAsEachTenant.Contains(service) ? 1 : 0)
         + (DelegatesTo.ContainsKey(service) ? 1 : 0)
         + (NoTenantScopedWork.ContainsKey(service) ? 1 : 0)
         + (NotYetTenantScoped.ContainsKey(service) ? 1 : 0);

    /// <summary>
    /// Every type registered as a hosted service. <b>All three registration forms</b>, because the
    /// generic one alone misses six registrations — see this class's remarks.
    /// </summary>
    private static IReadOnlySet<string> HostedServices()
    {
        HashSet<string> found = new(StringComparer.Ordinal);

        foreach (string path in ManagementPlaneFiles())
        {
            // ⚠️ The seam itself registers `AddHostedService<SingletonWorkHost<T>>`, which is the
            // wrapper rather than a sweep. Counting it would add a service that does not exist and
            // then demand it be classified.
            if (Path.GetFileName(path) == "HostedWork.cs")
            {
                continue;
            }

            string code = File.ReadAllText(path);

            // ⚠️ Three verbs, not one. A hosted service now declares where it runs
            // (`AddSingletonWork`/`AddPerInstanceWork`, docs/decomposition.md §9.5) and the bare
            // `AddHostedService` is refused outside the seam by HostedWorkPlacementTests. Scanning
            // only the old spelling found ONE registration instead of 32 — caught by the count
            // guard below, which is the whole reason that guard exists.
            foreach (string pattern in new[]
                     {
                         @"Add(?:HostedService|SingletonWork|PerInstanceWork)<([\w.]+)>",
                         @"Add(?:HostedService|SingletonWork)\(sp => new ([\w.]+)\(",
                         @"Add(?:HostedService|SingletonWork)\(sp => sp\.GetRequiredService<([\w.]+)>",
                     })
            {
                foreach (Match m in Regex.Matches(code, pattern))
                {
                    found.Add(m.Groups[1].Value.Split('.')[^1]);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a type's source reads the tenant table and loops. A claim-checker: see this class's
    /// remarks for why that is enough here and not enough on its own.
    /// </summary>
    private static bool LoopsTenants(string typeName)
    {
        string? path = SourceFiles()
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == typeName);

        if (path is null) return false;

        string code = string.Join('\n', File.ReadLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && !l.TrimStart().StartsWith("*", StringComparison.Ordinal)));

        return code.Contains(".Tenants", StringComparison.Ordinal)
            && code.Contains("foreach", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ManagementPlaneFiles()
        => Files(ManagementPlane);

    private static IEnumerable<string> SourceFiles()
        => Files(Src);

    private static IEnumerable<string> Files(string relative)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the source tree; if it moved, the test has to follow rather than "
            + "quietly stop checking anything");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }
}
