using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// A <c>string</c> component parameter is never handed a C# expression as a literal.
///
/// <para><b>Why this needs a test.</b> Razor parses an attribute value as an expression when the
/// parameter's type is <em>not</em> string — so <c>ClusterId="Cluster.Id"</c> (a <c>Guid</c>) works
/// — and as literal text when it is. <c>ClusterName="Cluster.Name"</c> therefore compiles, renders,
/// and puts the words "Cluster.Name" on the page. Five of these were live, including
/// "Distribute trusted CA certificates to all workloads on <b>Cluster.Name</b>", and nothing
/// failed: no warning, no exception, only wrong text in front of an operator.</para>
///
/// <para>The remedy is one character — <c>="@Cluster.Name"</c> — which is exactly why it is worth a
/// guard rather than a fixed instance. Nothing else in the build can see it.</para>
///
/// <para>⚠️ A literal that <em>looks</em> like an expression is sometimes correct: on
/// <c>&lt;PasskeySubmit EmailName="Input.Email"&gt;</c> the value is the name of an HTML form
/// field, not a value to read. Those are listed below by name, because an exemption nobody states
/// is indistinguishable from a bug — the lesson this repo keeps relearning.</para>
/// </summary>
public class RazorStringParameterTests
{
    private const string Components = "../../../../../src/EntKube.Web/Components";

    /// <summary>
    /// Deliberate literals: the value names something (a form field) rather than supplying it.
    /// Keyed "Component.Parameter".
    /// </summary>
    private static readonly HashSet<string> IntentionalLiterals = new(StringComparer.Ordinal)
    {
        // Rendered into email-name="…"/name="…" so the passkey script can find the input.
        "PasskeySubmit.EmailName",
        "PasskeySubmit.Name",
    };

    [Fact]
    public void No_string_parameter_is_given_an_expression_as_a_literal()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Components));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the Razor components; if they moved, it has to follow rather than "
            + "quietly stop checking anything");

        string[] files = [.. Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)];
        files.Should().NotBeEmpty();

        // Which components declare which string parameters, keyed by component (file) name.
        Dictionary<string, HashSet<string>> stringParameters = new(StringComparer.Ordinal);
        foreach (string path in files)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(path),
                         @"\[Parameter[^\]]*\]\s*public\s+string\??\s+(\w+)"))
            {
                (stringParameters.TryGetValue(Path.GetFileNameWithoutExtension(path), out HashSet<string>? set)
                    ? set
                    : stringParameters[Path.GetFileNameWithoutExtension(path)] = new(StringComparer.Ordinal))
                    .Add(m.Groups[1].Value);
            }
        }

        stringParameters.Should().NotBeEmpty("the parameter declarations are what this test reads");

        // A value shaped like `Thing.Member` — what somebody meant to be an expression.
        Regex looksLikeExpression = new(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$");

        // The leading (?<![\w@-]) keeps `@bind-Value="x.y"` out: a bind directive IS an expression.
        Regex attribute = new(@"(?<![\w@-])([A-Z]\w*)\s*=\s*""([^""]*)""");

        List<string> offenders = [];

        foreach (string path in files)
        {
            string source = File.ReadAllText(path);

            foreach (Match tag in Regex.Matches(source, @"<([A-Z]\w*)\b((?:[^>""]|""[^""]*"")*)/?>", RegexOptions.Singleline))
            {
                string component = tag.Groups[1].Value;

                if (!stringParameters.TryGetValue(component, out HashSet<string>? declared)) continue;

                foreach (Match attr in attribute.Matches(tag.Groups[2].Value))
                {
                    string name = attr.Groups[1].Value;
                    string value = attr.Groups[2].Value.Trim();

                    if (!declared.Contains(name)) continue;
                    if (!looksLikeExpression.IsMatch(value)) continue;
                    if (IntentionalLiterals.Contains($"{component}.{name}")) continue;

                    int line = source[..tag.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(path)}:{line} <{component} {name}=\"{value}\"");
                }
            }
        }

        string.Join("; ", offenders.Order()).Should().BeEmpty(
            "a string parameter takes the attribute as literal text, so this renders the source "
            + "code to the page instead of the value. Prefix the value with @ — and if the literal "
            + "is deliberate, name it in IntentionalLiterals with the reason");
    }
}
