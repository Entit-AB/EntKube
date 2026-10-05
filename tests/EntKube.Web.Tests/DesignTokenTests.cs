using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The ways the design-token layer breaks silently.
///
/// <para><b>Why this reads the stylesheets.</b> CSS is not compiled, so none of the failures
/// below is a build error, and none is visible on the page you happen to be looking at. Each
/// one has already happened once: a token with no dark value, a triplet handed to a colour
/// property, and a <c>:global()</c> selector Blazor emits verbatim for the browser to discard.
/// The token layer is only worth having if it holds everywhere.</para>
/// </summary>
public class DesignTokenTests
{
    private const string Web = "../../../../../src/EntKube.Web";
    private const string AppCss = Web + "/wwwroot/app.css";

    private static string Read(string relative)
    {
        string path = Path.Combine(AppContext.BaseDirectory, relative);

        File.Exists(path).Should().BeTrue(
            $"{relative} is where the design tokens live; if it moved, this test has to "
            + "follow it rather than quietly stop checking anything");

        return Strip(File.ReadAllText(path));
    }

    private static IEnumerable<string> Stylesheets()
    {
        string root = Path.Combine(AppContext.BaseDirectory, Web);

        yield return AppCss;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.razor.css", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                     .Order(StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    /// <summary>Comments document values; only declarations are the subject here.</summary>
    private static string Strip(string css) => Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static string Body(string css, string selector)
    {
        Match m = Regex.Match(css, Regex.Escape(selector) + @"\s*\{(.*?)\n\}", RegexOptions.Singleline);
        m.Success.Should().BeTrue($"app.css must declare a {selector} block");
        return m.Groups[1].Value;
    }

    private static Dictionary<string, string> Declarations(string body) =>
        Regex.Matches(body, @"(--[a-z0-9-]+)\s*:\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());

    /// <summary>An HSL triplet — shadcn's convention, meaningless until wrapped in hsl().</summary>
    private static bool IsTriplet(string value) =>
        Regex.IsMatch(value, @"^\d+(\.\d+)?\s+\d+(\.\d+)?%\s+\d+(\.\d+)?%$");

    /// <summary>
    /// Deliberately absent from the dark block, with the reason stated in app.css beside
    /// it. Anything else missing is an oversight, which is the whole point of this test:
    /// a half-filled dark theme looks exactly like a finished one until you switch.
    /// </summary>
    private static readonly string[] LightOnly =
    [
        "--destructive-foreground",   // white on a red fill in either theme
        "--entit-navy",               // the signed-out hero's gradient, which must not invert
    ];

    /// <summary>
    /// Whole families that are theme-invariant by design: a log pane is a terminal in either
    /// theme, and a chart series that changed colour with the theme would be a series you
    /// could not talk about. Adding a family here is a decision, not a convenience — it opts
    /// those tokens out of the dark-coverage check below.
    /// </summary>
    private static readonly string[] ThemeInvariantPrefixes = ["--console-", "--chart-"];

    private static bool IsThemeInvariant(string token) =>
        LightOnly.Contains(token)
        || token == "--console"
        || ThemeInvariantPrefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal));

    [Fact]
    public void Every_colour_token_has_a_dark_counterpart()
    {
        string css = Read(AppCss);

        Dictionary<string, string> light = Declarations(Body(css, ":root"));
        Dictionary<string, string> dark = Declarations(Body(css, "[data-bs-theme=\"dark\"]"));

        light.Should().NotBeEmpty("the :root block is where the tokens are declared");

        List<string> colours = [.. light.Where(kv => IsTriplet(kv.Value)).Select(kv => kv.Key)];

        colours.Should().HaveCountGreaterThan(15,
            "matching almost no colour tokens means the HSL-triplet convention has changed "
            + "and this test has stopped checking anything");

        string.Join(", ", colours.Except(dark.Keys).Where(t => !IsThemeInvariant(t)).Order())
            .Should().BeEmpty(
                "a token with no dark value falls back to its light one, so dark mode would "
                + "render a light surface under light text — and nothing says which omissions "
                + "were meant (see LightOnly and ThemeInvariantPrefixes in this test, and the "
                + "note beside the dark block in app.css)");
    }

    /// <summary>
    /// <b>The one that bites.</b> A token holding <c>227 86% 58%</c> is not a colour until
    /// <c>hsl()</c> wraps it, and <c>color: var(--x)</c> where --x holds a triplet is
    /// invalid — so the browser drops the declaration and falls back to whatever it
    /// inherited. Worse, a fallback in <c>var(--x, #3A63F0)</c> does NOT rescue it:
    /// fallbacks apply when a property is undefined, not when its value is unusable.
    ///
    /// <para>This is how the brand colour variables broke when they became triplets: about
    /// twenty declarations across the top nav lost their colour and nothing failed.</para>
    /// </summary>
    [Fact]
    public void No_stylesheet_hands_a_raw_triplet_to_a_colour_property()
    {
        string tokens = Read(AppCss);
        HashSet<string> triplets =
        [
            .. Declarations(Body(tokens, ":root")).Where(kv => IsTriplet(kv.Value)).Select(kv => kv.Key)
        ];

        const string ColourProp = @"(?:color|background|background-color|border-color|border-top-color|border-bottom-color|border-left-color|border-right-color|outline-color|fill|stroke)";

        List<string> offences = [];

        foreach (string sheet in Stylesheets())
        {
            string css = Strip(File.ReadAllText(
                Path.IsPathRooted(sheet) ? sheet : Path.Combine(AppContext.BaseDirectory, sheet)));
            string name = Path.GetFileName(sheet);

            // A literal triplet written straight into a colour property.
            foreach (Match m in Regex.Matches(css,
                         $@"(?:^|[;{{])\s*{ColourProp}\s*:\s*\d+\s+\d+%\s+\d+%\s*[;}}]",
                         RegexOptions.Multiline))
            {
                offences.Add($"{name}: {m.Value.Trim().TrimEnd(';', '}')}");
            }

            // A triplet-valued token referenced outside hsl(): var(--border) rather than
            // hsl(var(--border)). Matched by looking at what precedes the var().
            foreach (Match m in Regex.Matches(css, @"(hsl\(\s*)?var\(\s*(--[a-z0-9-]+)"))
            {
                string token = m.Groups[2].Value;
                if (!triplets.Contains(token) || m.Groups[1].Success) continue;
                offences.Add($"{name}: var({token}) outside hsl() — the token holds a triplet");
            }
        }

        string.Join("\n  ", offences).Should().BeEmpty(
            "a triplet used as a colour makes the browser drop the declaration silently, "
            + "and a var() fallback does not save it");
    }

    /// <summary>
    /// <c>:global()</c> is a CSS Modules feature. Blazor's scoped CSS does not implement it and
    /// emits the selector verbatim, so the browser reads something invalid and drops the whole
    /// rule — no build error, no console warning, just styling that quietly does not apply. The
    /// theme toggle shipped like this for one build: it kept offering to turn on the dark mode
    /// it was already in.
    ///
    /// <para>When a rule needs an ancestor a scoped sheet cannot reach — <c>data-bs-theme</c>
    /// lives on <c>&lt;html&gt;</c> — it belongs in <c>app.css</c> instead.</para>
    /// </summary>
    [Fact]
    public void No_scoped_stylesheet_uses_the_css_modules_global_selector()
    {
        List<string> offences = [];

        foreach (string sheet in Stylesheets().Skip(1))   // app.css is not scoped
        {
            string path = Path.IsPathRooted(sheet) ? sheet : Path.Combine(AppContext.BaseDirectory, sheet);
            if (Strip(File.ReadAllText(path)).Contains(":global(", StringComparison.Ordinal))
            {
                offences.Add(Path.GetFileName(path));
            }
        }

        string.Join(", ", offences).Should().BeEmpty(
            "Blazor passes :global() straight through, so the browser drops the rule and the "
            + "styling silently does not apply; a rule needing an ancestor belongs in app.css");
    }
}
