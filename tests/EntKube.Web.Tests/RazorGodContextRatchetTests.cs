using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// A shape the service-level god-context ratchet structurally cannot see: a <b>Razor component</b>
/// that injects <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> and queries the database from
/// the page.
///
/// <para><c>ModuleContractTests</c> measures SERVICES — by constructor, by scope, and (since #327)
/// by static helpers taking the context as a parameter. A component is none of those: it is not in
/// <c>ModuleServiceMap</c>, has no constructor reflection can read, and is compiled from a
/// <c>.razor</c> file. So a page could read every table in the product while every existing count
/// read unchanged, which is exactly what 31 of them were doing.</para>
///
/// <para><b>Why a page on the god context matters even when its current query is innocent.</b> Half
/// of these only looked up a cluster or a customer — tables every module context exposes read-only
/// as hubs anyway. The cost is not what the page reads today, it is what the type system permits
/// tomorrow: on <c>ApplicationDbContext</c> the next edit can join anything, and nothing fails.
/// On the module's own context it does not compile.</para>
/// </summary>
public class RazorGodContextRatchetTests
{
    /// <summary>
    /// The components still reading the all-seeing context, and why each one is still here.
    ///
    /// <para>⚠️ <b>A ratchet constant has no correct merge</b> — see the warning on
    /// <c>ModuleContractTests.Baseline</c>, which has now been merged wrongly three times. If two
    /// branches both touch this list, re-run the scan on the MERGE RESULT; a number here is a fact
    /// about the tree, and a merge produces a tree neither author measured.</para>
    /// </summary>
    private static readonly string[] Remaining =
    [
        // No single module context exposes every table these two touch, so they are the genuine
        // contract work — the only two of the original 31.
        "SlaTargetManager.razor",          // SlaTargets (Support) + Delivery's deployments + Customers

        // Hub-only pages whose SUBJECT does not derive. These touch nothing but Tenant, Customer,
        // App and KubernetesCluster, which every module context exposes, so the tables cannot pick
        // one — and their injected services either split across modules or are absent entirely.
        // Choosing for them is an architecture decision about which module owns the page, so it is
        // stated in a change of its own rather than guessed here.
        "SupportInbox.razor",              // SupportMailService (Support) + UserAccessService (Identity)
        "TenantMonitoringOverview.razor",  // Telemetry ×2 + OnCall (Support) + OperationsAdvisor (Advisor)
        "TicketDetail.razor",              // Support ×2 + UserAccessService (Identity)
        "UpgradesTab.razor",               // ComponentUpgrade (Catalog) + NodeManagement (Fleet)
        "TenantAlertRulesTab.razor",       // no injected service to derive a subject from
        "TenantInfraMetricsTab.razor",     // ditto
        "TenantMetricsTab.razor",          // ditto
        "TenantTracesTab.razor",           // ditto
        "TicketBridgeTab.razor",           // ditto
    ];

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "charts")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static List<string> ComponentFiles() =>
        [.. Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src", "EntKube.Web", "Components"), "*.razor",
            SearchOption.AllDirectories)];

    /// <summary>
    /// The components injecting the god context, by file name.
    ///
    /// <para>⚠️ Matched on the <c>@inject</c> DIRECTIVE, not on the type name appearing anywhere in
    /// the file. The first version of this scan counted any mention and reported 33 — two of which
    /// were a code COMMENT about <c>IDbContextFactory</c> in a page that injects nothing. A grep
    /// that matches its own subject's name matches prose about it too.</para>
    /// </summary>
    private static List<string> GodContextComponents() =>
        [.. ComponentFiles()
            .Where(f => Regex.IsMatch(
                File.ReadAllText(f), @"@inject\s+IDbContextFactory<ApplicationDbContext>"))
            .Select(Path.GetFileName)
            .Select(n => n!)
            .Order()];

    [Fact]
    public void No_new_razor_component_reaches_for_the_all_seeing_context()
    {
        List<string> files = ComponentFiles();
        List<string> actual = GodContextComponents();

        // The health check, separated from the finding, and it must survive this list emptying:
        // an empty offender set is the goal, so it cannot double as evidence that the scan ran.
        // Asserting on the files READ is the only part the scan can vouch for itself.
        files.Count.Should().BeGreaterThan(100,
            "if this stops finding .razor files the scan broke, and an empty offender list would "
            + "mean nothing");

        List<string> added = [.. actual.Except(Remaining).Order()];

        string.Join(", ", added).Should().BeEmpty(
            "a page on ApplicationDbContext can query every table in the product, and no service-"
            + "level count can see it. Inject the module's own context — the model is identical, so "
            + "behaviour does not change, only what the next edit is allowed to join to. If the page "
            + "genuinely needs two modules' tables, that is what a contract is for.");
    }

    [Fact]
    public void The_ratchet_does_not_carry_slack()
    {
        List<string> actual = GodContextComponents();

        List<string> fixedAlready = [.. Remaining.Except(actual).Order()];

        string.Join(", ", fixedAlready).Should().BeEmpty(
            "this component has moved off the all-seeing context — bank it by deleting its line "
            + "from Remaining, or the slack lets it come back for free");
    }

    /// <summary>
    /// Nothing converted was converted by weakening the boundary: a page must not inject a module
    /// context AND the god context side by side, which would satisfy the ratchet while keeping
    /// every table reachable.
    /// </summary>
    [Fact]
    public void No_component_injects_a_module_context_alongside_the_god_one()
    {
        List<string> both =
        [
            .. ComponentFiles()
                .Where(f =>
                {
                    string t = File.ReadAllText(f);
                    return Regex.IsMatch(t, @"@inject\s+IDbContextFactory<ApplicationDbContext>")
                        && Regex.IsMatch(t, @"@inject\s+IDbContextFactory<(?!ApplicationDbContext)\w+>");
                })
                .Select(f => Path.GetFileName(f)!)
                .Order()
        ];

        string.Join(", ", both).Should().BeEmpty(
            "injecting both leaves every table reachable while looking converted");
    }
}
