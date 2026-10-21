using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// One operator action should raise one acknowledgment.
///
/// <para><c>IClusterChangeGate.BeginOperation</c> exists for this: the first gated change inside
/// asks, every later one is recorded as a consequence, and the window closes with the action so
/// the next button press asks again. It was used in <b>five</b> places in the whole codebase, and
/// the operator who reported this found one of the gaps by clicking "Re-apply to cluster" and
/// being asked four times.</para>
///
/// <para>This ratchet counts what is left: public methods a Razor component calls directly which
/// make two or more gated cluster changes without opening an operation. Each is a button that
/// asks more than once. The count may only go down.</para>
///
/// <para><b>Why a source scan.</b> The property is about a <em>missing wrapper</em>, which has no
/// behaviour to observe until someone clicks the button — and the gate is reached through the real
/// <c>KubernetesClientFactory</c>, so a test with a mocked seam sees nothing at all. The
/// behavioural counterpart lives in <c>RoutingClusterCallTests</c>, where the mock raises the
/// acknowledgment the real factory would and the count is asserted at exactly one.</para>
/// </summary>
public class OneAckPerActionRatchetTests
{
    /// <summary>
    /// What is left after the Kafka conversion — four actions (create, update, delete, sync a
    /// binding), measured on this branch.
    ///
    /// <para>Each remaining service needs a constructor parameter and every test that builds it
    /// updated, so they are a change of their own rather than a silent widening of this one.
    /// Driving this to zero is the work; the ratchet is here so it cannot grow while that
    /// waits.</para>
    ///
    /// <para>⚠️ <b>Re-measure on the merge result, not here.</b> This number has no correct value
    /// at the moment two conversions merge — each is right against the tree it was measured on.
    /// See <see cref="AllowedDrift"/>, which exists because that turned <c>main</c> red four
    /// times.</para>
    /// </summary>
    private const int Baseline = 10;

    /// <summary>
    /// How far below <see cref="Baseline"/> the count may sit before this test asks to be
    /// re-banked.
    ///
    /// <para><b>Why there is slack at all, which is a deviation from the other ratchets here.</b>
    /// They assert the count <em>exactly</em>, so that a conversion has to bank its own number
    /// and cannot leave room for the next regression to slip in free. That is right when one
    /// change lands at a time, and it has now made <c>main</c> red <b>four</b> times when two did:
    /// each PR is correct against the tree it was measured on, both lower the count, and whichever
    /// merges second leaves a constant that no longer matches. The number has no correct value at
    /// the moment of the merge — only after it.</para>
    ///
    /// <para>So growth is still refused outright, and drift is bounded: ten is small enough that
    /// a forgotten re-bank cannot quietly become headroom for ten new offenders, and large enough
    /// to absorb the two or three conversions that are usually in flight together.</para>
    /// </summary>
    private const int AllowedDrift = 10;

    private const string Web = "../../../../../src/EntKube.Web";

    private static readonly string ServicesRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web, "Services"));

    private static readonly string ComponentsRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web, "Components"));

    /// <summary>
    /// The seam verbs that raise an acknowledgment. Taken from the factory rather than guessed:
    /// each of these calls <c>_gate.AcknowledgeAsync</c> before it touches the cluster.
    /// </summary>
    /// <summary>
    /// ⚠️ <b>First-party helpers that reach a gated verb one level down.</b> Omitting these is
    /// how the first version of this scan missed nine methods — every binding writer, because
    /// they all write through <c>SharedSecretKeys</c> since #305, and
    /// <c>VaultService.SyncAppSecretsToKubernetesAsync</c> among them, which was the first thing
    /// an operator reported about this gate. A scan for calls measures one hop; the property is
    /// transitive, so each wrapper has to be named here or counted by following it.
    /// </summary>
    private static readonly string[] GatedThroughHelpers =
    [
        @"SharedSecretKeys\.WriteAsync\(",
        @"SharedSecretKeys\.RemoveAsync\(",
    ];

    private static readonly string[] GatedVerbs =
    [
        "ApplyManifestAsync", "ApplyManifestInNamespaceAsync", "ReplaceSecretAsync",
        "MergeSecretKeysAsync", "DeleteManifestAsync", "DeleteManifestSetAsync",
        "PatchJsonAsync", "PatchStrategicAsync", "ScaleDeploymentAsync", "DrainNodeAsync",
        "RunHelmAsync", "CreateJobFromCronJobAsync", "UndoDeploymentsAsync",
        "ApplyManifestFromUrlAsync", "EnsureNamespaceAsync",
    ];

    /// <summary>
    /// ⚠️ Matches a <b>call</b> on something — never a declaration. A first version counted the
    /// method's own name too, so every one-line delegate in <c>ClusterClient</c> looked like two
    /// gated changes and the published figure was wrong by three files. <c>ApplyRawYamlAsync</c>
    /// is the one private helper called bare inside its own class, so it is matched without a dot
    /// but with a guard against the declaration.
    /// </summary>
    private static readonly Regex GatedCall = new(
        @"\.(?:" + string.Join("|", GatedVerbs) + @")\(|(?<![.\w])ApplyRawYamlAsync\(|"
        + string.Join("|", GatedThroughHelpers),
        RegexOptions.Compiled);

    /// <summary>
    /// ⚠️ Splits on <b>every</b> member, public or private. A first version split only on public
    /// ones, so each private helper's calls were attributed to the public method above it —
    /// which reported four gated changes for <c>GetL4EndpointAddressAsync</c>, a method that
    /// makes none.
    /// </summary>
    private static readonly Regex AnyMember = new(
        @"\n    (?:public|private|internal|protected)"
        + @"(?:[ \t]+(?:static|virtual|override|sealed|async|readonly))*"
        + @"[ \t]+[\w<>?,\[\] .]+[ \t]+(\w+)\(",
        RegexOptions.Compiled);

    private static List<string> FindActionsThatAskTwice()
    {
        string razor = string.Join("\n", Directory
            .EnumerateFiles(ComponentsRoot, "*.razor", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        List<string> found = [];

        foreach (string path in Directory.EnumerateFiles(
                     ServicesRoot, "*.cs", SearchOption.AllDirectories))
        {
            string src = File.ReadAllText(path);
            List<Match> members = AnyMember.Matches(src).ToList();

            for (int i = 0; i < members.Count; i++)
            {
                Match m = members[i];
                string name = m.Groups[1].Value;
                int end = i + 1 < members.Count ? members[i + 1].Index : src.Length;

                string body = Regex.Replace(
                    src[m.Index..end], @"^\s*//.*$", "", RegexOptions.Multiline);

                // Declarations only: a private helper is free to make one change at a time.
                if (!src.Substring(m.Index, Math.Min(50, src.Length - m.Index)).Contains("public"))
                    continue;

                // Builders name these verbs in their doc comments, not in code.
                if (Regex.IsMatch(name, "^(Build|Generate|Render)"))
                    continue;

                // Only what an operator can actually press. A background sweep needs
                // DeclareUnattended, which is a different question with a different answer.
                if (!razor.Contains($".{name}(", StringComparison.Ordinal))
                    continue;

                if (GatedCall.Matches(body).Count >= 2 && !body.Contains("BeginOperation("))
                    found.Add($"{Path.GetFileName(path)}:{name}");
            }
        }

        return found;
    }

    [Fact]
    public void No_new_operator_action_asks_more_than_once()
    {
        List<string> found = FindActionsThatAskTwice();

        found.Count.Should().BeLessThanOrEqualTo(Baseline,
            "an operator action that raises several acknowledgments teaches the operator to click "
            + "through dialogs without reading them — wrap it in gate.BeginOperation(what), before "
            + "its first gated change:\n  "
            + string.Join("\n  ", found.Skip(Baseline)));
    }

    /// <summary>
    /// The banked number has to be re-measured when it falls — but not so strictly that two
    /// conversions landing together turn <c>main</c> red. See <see cref="AllowedDrift"/>.
    /// </summary>
    [Fact]
    public void The_ratchet_does_not_carry_unbounded_slack()
    {
        int found = FindActionsThatAskTwice().Count;

        (Baseline - found).Should().BeLessThanOrEqualTo(AllowedDrift,
            $"now {found}, banked {Baseline} — re-bank it, or the gap becomes headroom for "
            + "offenders nobody converted");
    }

    /// <summary>
    /// And the scan still finds the four that were converted, so a rewrite that moved them out of
    /// reach cannot leave this passing against nothing.
    /// </summary>
    [Fact]
    public void The_scan_still_sees_the_converted_actions()
    {
        string ops = File.ReadAllText(
            Path.Combine(ServicesRoot, "KubernetesOperationsService.cs"));

        Regex.Matches(ops, @"gate\.BeginOperation\(").Count.Should().BeGreaterThanOrEqualTo(8,
            "four pre-existing operations plus the four this change added");
    }
}
