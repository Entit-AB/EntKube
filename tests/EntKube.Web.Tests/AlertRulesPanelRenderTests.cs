using System.Reflection;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What an operator reads off the alert-rules panel.
///
/// <para>Every defect pinned here is a <em>display</em> defect — the panel is read-only over
/// Prometheus's rules API — and all three share a shape: the panel answered a question it had
/// not been asked. "I have no state for this rule" came out as a green "OK"; a 90-minute
/// pre-fire window came out as "1h"; and a click that the markup clearly expected came out as
/// nothing at all. None of them fails, logs, or looks broken, which is why none of them was
/// going to be noticed from the outside.</para>
///
/// <para>⚠️ <b>Why the panel's state is set by reflection.</b> <c>AlertRulesPanel</c> injects
/// the concrete <see cref="PrometheusService"/> and loads its rules in
/// <c>OnParametersSetAsync</c>; the service has no interface and no virtual member, so there is
/// nothing to substitute, and with no Prometheus in the test the load can only fail. These
/// tests therefore let the load fail, then seed <c>rules</c>/<c>filtered</c> directly and
/// re-render — which is honest about what is under test: the panel as a pure function from a
/// rule list to markup. Nothing here covers the fetch or the parse.</para>
/// </summary>
public class AlertRulesPanelRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;

    public AlertRulesPanelRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);

        Services.AddSingleton(new PrometheusService(
            new EntKube.Web.Modules.Api.DeliveryApi(factory),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(
                factory, new Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.CatalogApi(factory),
            new PromQueryCache(),
            NullLogger<PrometheusService>.Instance));
    }

    public new void Dispose()
    {
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────── The "OK" badge ────────

    /// <summary>
    /// ⭐ A rule Prometheus gave no state for must not be reported as fine.
    ///
    /// <para><c>StateIndicator</c> ended in a bare <c>_ =></c> onto the green check labelled
    /// "OK", so any state string that was not <c>firing</c> or <c>pending</c> landed there —
    /// including the <c>""</c> that <c>ParseAlertRules</c> leaves when the rules API omits the
    /// field. The absence of information rendered as a positive reassurance, and the green one
    /// is the badge nobody investigates.</para>
    ///
    /// <para>⚠️ The <c>inactive</c> half of this test is not decoration. Asserting only that an
    /// unknown state is <em>not</em> "OK" would also pass if the badge were deleted outright, or
    /// if the panel rendered no rows at all; the first assertion is what makes the second mean
    /// something.</para>
    /// </summary>
    [Fact]
    public void An_unknown_state_is_not_painted_as_a_green_OK()
    {
        IRenderedComponent<AlertRulesPanel> cut = Seeded(
            Rule("KubePodCrashLooping", state: "inactive"),
            Rule("KubeletDown", state: ""));

        // Positive control first: the one state Prometheus really calls quiet does say OK.
        cut.Markup.Should().Contain(">OK<",
            "an inactive rule is genuinely quiet and should read OK — if this fails the rest of "
            + "this test is vacuous");

        // And the rule with no state is not given the same answer.
        cut.Markup.Should().Contain("Unknown",
            "a rule whose state Prometheus did not report must say so rather than borrow the "
            + "green badge from the rules that are actually quiet");
        System.Text.RegularExpressions.Regex.Matches(cut.Markup, ">OK<").Should().HaveCount(1,
            "only the inactive rule may read OK; the stateless one used to fall through the "
            + "catch-all arm onto the same green check");
    }

    /// <summary>
    /// A state string Prometheus adds later is shown verbatim rather than being guessed at.
    ///
    /// <para>Same defect by a second route: the catch-all swallowed anything unrecognised into
    /// "OK". Showing the raw value is the conservative answer — it tells whoever is reading that
    /// this panel does not know the word, instead of deciding on their behalf that it is good
    /// news.</para>
    /// </summary>
    [Fact]
    public void An_unrecognised_state_is_shown_rather_than_assumed_to_be_fine()
    {
        IRenderedComponent<AlertRulesPanel> cut = Seeded(Rule("FutureRule", state: "silenced"));

        cut.Markup.Should().Contain("silenced");
        cut.Markup.Should().NotContain(">OK<");
    }

    // ──────── The "for" column ────────

    /// <summary>
    /// ⭐ The <c>for:</c> window keeps its remainder.
    ///
    /// <para><c>FormatDuration</c> collapsed to the largest unit and truncated the rest, so
    /// <c>for: 90m</c> (5400s) read "1h" and <c>for: 2m30s</c> (150s) read "2m". The column is
    /// tooltipped "Fires after this duration", and this panel is the only place in EntKube that
    /// window appears, so there was nowhere to catch the 30 minutes that went missing.</para>
    ///
    /// <para>⚠️ <c>1h 30m</c> contains <c>1h</c>, so "the fix is present" cannot be asserted by
    /// containment alone — the exact rendered text is pinned instead, including the collapsed
    /// cases where a bare single unit is still right.</para>
    /// </summary>
    [Theory]
    [InlineData(5400, "1h 30m")]   // for: 90m — the defect
    [InlineData(150, "2m 30s")]    // for: 2m30s
    [InlineData(90, "1m 30s")]
    [InlineData(3600, "1h")]       // exact — collapse to one unit, as IdentityTab does
    [InlineData(300, "5m")]
    [InlineData(45, "45s")]
    public void The_for_window_is_rendered_whole(double seconds, string expected)
    {
        IRenderedComponent<AlertRulesPanel> cut = Seeded(Rule("Window", duration: seconds));

        cut.Markup.Should().Contain($">{expected}</span>",
            $"a rule configured for {seconds}s must read exactly \"{expected}\"");
    }

    // ──────── The expand row ────────

    /// <summary>
    /// ⭐ Clicking a rule shows its query. It used to do nothing whatsoever.
    ///
    /// <para><c>expandedRule</c> was read by the expand row's <c>@if</c> and assigned by no code
    /// path at all — the compiler said so: <c>warning CS0649: Field
    /// 'AlertRulesPanel.expandedRule' is never assigned to, and will always have its default
    /// value null</c>. No row carried a click handler, so the PromQL beside every rule was
    /// unreachable markup.</para>
    ///
    /// <para>The query text is asserted absent first and present after the click, so this
    /// cannot pass by rendering the query unconditionally.</para>
    /// </summary>
    [Fact]
    public void Clicking_a_rule_reveals_its_query()
    {
        // ⚠️ No `>` or `<` in the query: the rendered markup escapes them, so an assertion
        // written with the raw operator fails against a row that is in fact expanded.
        const string promql = "rate(kube_pod_container_status_restarts_total[5m])";
        IRenderedComponent<AlertRulesPanel> cut = Seeded(
            Rule("KubePodCrashLooping", query: promql));

        cut.Markup.Should().NotContain(promql, "the query starts collapsed");

        cut.Find("tbody tr").Click();
        cut.Markup.Should().Contain(promql, "clicking the row is the only way to read the PromQL");

        cut.Find("tbody tr").Click();
        cut.Markup.Should().NotContain(promql, "clicking the open row again closes it");
    }

    /// <summary>
    /// ⚠️ The expand key is <c>group/name</c>, not <c>Name + GroupName</c>.
    ///
    /// <para>Bare concatenation of two free-form strings collides: group <c>node</c> + rule
    /// <c>down</c> and group <c>no</c> + rule <c>dedown</c> both produce <c>nodedown</c>, so
    /// expanding one rule would also expand the other. These two rules are built to collide
    /// under the old key and not under the new one.</para>
    /// </summary>
    [Fact]
    public void Two_rules_whose_names_concatenate_alike_expand_independently()
    {
        IRenderedComponent<AlertRulesPanel> cut = Seeded(
            Rule("down", group: "node", query: "up{job=\"node\"} == 0"),
            Rule("dedown", group: "no", query: "up{job=\"other\"} == 0"));

        // Groups render sorted by name: "no" before "node", so "dedown" is the first row.
        cut.FindAll("tbody tr")[0].Click();

        cut.Markup.Should().Contain("up{job=\"other\"} == 0", "the clicked rule opened");
        cut.Markup.Should().NotContain("up{job=\"node\"} == 0",
            "the other rule's key only looked identical under the old Name + GroupName key");
    }

    // ──────── Harness ────────

    private static AlertRule Rule(
        string name,
        string state = "inactive",
        string group = "kubernetes-apps",
        double duration = 0,
        string query = "vector(1)") => new()
        {
            Name = name,
            GroupName = group,
            State = state,
            DurationSeconds = duration,
            Query = query
        };

    /// <summary>
    /// Renders the panel, lets its real load fail (there is no Prometheus here), then puts the
    /// given rules in place of what the load would have returned and re-renders.
    ///
    /// <para>⚠️ The wait is not optional: <c>Load</c> is async, and seeding before its
    /// continuation runs would have the failure path overwrite the seeded rules with
    /// <c>[]</c>.</para>
    /// </summary>
    private IRenderedComponent<AlertRulesPanel> Seeded(params AlertRule[] seed)
    {
        IRenderedComponent<AlertRulesPanel> cut = Render<AlertRulesPanel>(p => p
            .Add(c => c.TenantId, Guid.NewGuid())
            .Add(c => c.ClusterId, Guid.NewGuid()));

        AlertRulesPanel panel = cut.Instance;
        cut.WaitForState(() => !(bool)Field("isLoading").GetValue(panel)!);

        Field("rules").SetValue(panel, seed.ToList());
        Field("filtered").SetValue(panel, seed.ToList());
        Field("errorMessage").SetValue(panel, null);

        cut.InvokeAsync(() => typeof(ComponentBase)
            .GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(panel, null)).GetAwaiter().GetResult();

        return cut;
    }

    private static FieldInfo Field(string name) =>
        typeof(AlertRulesPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            $"AlertRulesPanel has no private field '{name}' — this harness reaches into the "
            + "component's own state and has to be updated with it.");
}
