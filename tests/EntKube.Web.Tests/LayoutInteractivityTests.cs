using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// A component in the layout that attaches itself to an application service must be interactive.
///
/// <para><b>Why this is a test and not a comment.</b> <c>App.razor</c> mounts the router as a
/// bare <c>&lt;Routes /&gt;</c>, so interactivity is declared per page. A render mode applies
/// down a subtree and never up, which means a page that declares one becomes an interactive
/// island <em>inside a statically rendered layout</em>. Anything in that layout therefore runs
/// in the HTTP request's render and the HTTP request's DI scope — not the circuit's.</para>
///
/// <para>That is invisible in review and fatal to two kinds of component: one whose buttons do
/// something (a statically rendered button has no handler), and one that subscribes to a
/// <b>scoped</b> service in <c>OnInitialized</c> (the subscription lands on the instance that is
/// disposed with the response). <c>ClusterChangeAckDialog</c> was both at once: its sink went
/// into the request scope's <c>IClusterChangeGate</c>, so the circuit's gate had no sink and
/// every gated cluster change from an interactive page was refused with "There is no operator to
/// acknowledge it". <c>ToastHost</c> was the same defect, quieter: no toast ever appeared.</para>
///
/// <para>⚠️ bUnit cannot catch this. It renders a component tree in one scope with no render-mode
/// semantics, so the dialog tests pass either way. The hosting model is the thing that is wrong,
/// and the only place it is written down is the .razor file — hence a source test.</para>
/// </summary>
public class LayoutInteractivityTests
{
    private const string Components = "../../../../../src/EntKube.Web/Components";

    /// <summary>
    /// Elements in the layout that are framework components rather than ones in this project.
    /// </summary>
    private static readonly HashSet<string> NotOurs = new(StringComparer.Ordinal)
    {
        "ReconnectModal", "HeadOutlet", "ImportMap", "Routes", "App",
    };

    /// <summary>
    /// ⚠️ The rule is not "everything in the layout must be interactive" — my first draft said
    /// that and <c>TopNav</c> failed it, correctly. TopNav is static on purpose: its one
    /// clickable element carries a plain <c>onclick</c> rather than <c>@onclick</c> (there is a
    /// comment in the file saying so), and its only subscription is to the framework's
    /// <c>NavigationManager</c>, which is harmless because static SSR re-renders on every
    /// navigation anyway.
    ///
    /// <para>What actually breaks is narrower: <b>handing yourself to a scoped application
    /// service</b> — <c>Gate.RegisterSink(this)</c>, <c>Toasts.OnChange += …</c>. In the request
    /// scope that registration is thrown away with the response, and the circuit's instance of
    /// the same service never hears about it. So that, and only that, is what this requires a
    /// render mode for.</para>
    /// </summary>
    private static readonly Regex AttachesSelf = new(
        @"\.Register\w*\(\s*this\s*\)|\.On\w+\s*\+=|\.\w+Changed\s*\+=",
        RegexOptions.Compiled);

    /// <summary>Framework services whose subscriptions survive being request-scoped.</summary>
    private static readonly string[] FrameworkServices =
        ["NavigationManager", "AuthenticationStateProvider", "IJSRuntime"];

    [Fact]
    public void A_layout_component_that_attaches_itself_to_a_service_is_interactive()
    {
        string layoutPath = Path.Combine(Components, "Layout", "MainLayout.razor");
        File.Exists(layoutPath).Should().BeTrue($"{layoutPath} should exist");

        string layout = File.ReadAllText(layoutPath);

        // Self-closing PascalCase tags: <ToastHost />, <ClusterChangeAckDialog />, <TopNav />.
        List<string> hosted = Regex.Matches(layout, @"<([A-Z][A-Za-z0-9]*)\s*/>")
            .Select(m => m.Groups[1].Value)
            .Where(n => !NotOurs.Contains(n))
            .Distinct()
            .ToList();

        hosted.Should().NotBeEmpty("the layout hosts components, so this test has something to check");

        List<string> missing = [];

        foreach (string name in hosted)
        {
            string[] found = Directory.GetFiles(Components, $"{name}.razor", SearchOption.AllDirectories);
            if (found.Length == 0) continue;

            string source = File.ReadAllText(found[0]);
            if (source.Contains("@rendermode", StringComparison.Ordinal)) continue;

            // Only the registrations that die with the request scope count. A subscription to a
            // framework service is not one of them.
            foreach (Match m in AttachesSelf.Matches(source))
            {
                string line = source[..m.Index].Split('\n')[^1] + m.Value;
                if (FrameworkServices.Any(f => source.Contains($"{f} ", StringComparison.Ordinal)
                                               && line.Contains("Navigation", StringComparison.Ordinal)))
                {
                    continue;
                }

                missing.Add($"{name} ({m.Value.Trim()})");
                break;
            }
        }

        missing.Should().BeEmpty(
            "a component in the static layout runs in the request's scope, not the circuit's — "
            + "its buttons do nothing and anything it subscribes to on a scoped service is "
            + "subscribed on an instance that is about to be disposed. Declare "
            + "@rendermode @(new InteractiveServerRenderMode(prerender: false)) on it, or move it "
            + "inside a page that is already interactive");
    }
}
