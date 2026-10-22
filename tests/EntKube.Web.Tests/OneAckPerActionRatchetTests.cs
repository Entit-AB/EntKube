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
/// <para><b><c>DeclareConsequence</c> satisfies this as well as <c>BeginOperation</c>, and the
/// difference between the two is worth being exact about.</b> <c>BeginOperation</c> asks once and
/// records the rest as consequences; <c>DeclareConsequence</c> asks <em>nothing</em> and records
/// all of them. Under either one the operator cannot be asked twice, which is the property this
/// file is named after — so a method wrapped in either is not an offender here.</para>
///
/// <para>What follows from that is that this test does <b>not</b> police the opposite mistake: an
/// action that asks zero times where it should have asked once. That is a real failure mode and a
/// different question, answered by whether the caller already confirmed in the UI.
/// <c>RemediationService.ExecuteAsync</c> and <c>ReleaseVolumeGuard.ScaleDownAsync</c> both
/// declare a consequence for exactly that reason — the operator confirmed the action in a panel
/// naming the resource — and <c>ClusterChangeGate</c> is what keeps the declaration from being a
/// free pass: it refuses a consequence in a scope with no sink, so declaring one cannot conjure an
/// approver who was never there.</para>
///
/// <para>⚠️ <c>RemediationService.ExecuteAsync</c> is what prompted the clause, and shows the
/// standing limitation of measuring a missing wrapper by scanning source: its five gated calls sit
/// in a <c>switch</c> that runs exactly one of them per invocation, so the 5 this scan counts was
/// never a count of dialogs. A source scan cannot see control flow.</para>
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
    /// <b>Zero.</b> Measured on the merge result, which is the only tree where this number was
    /// ever meaningful.
    ///
    /// <para>It started at 53 and came down in nine changes: Kafka 4, Redis 2,
    /// <c>ComponentLifecycleService</c> 2, Storage and registered Postgres 2, then one each for
    /// the Docker registry, Keycloak, Stalwart and headscale. Every conversion was verified by
    /// deleting its <c>BeginOperation</c> and requiring a test to go red.</para>
    ///
    /// <para><b>What this test is now for.</b> While the backlog existed it was a bound on how
    /// bad things could get. With the backlog gone it refuses the FIRST new action that asks an
    /// operator twice — which is the job it was always meant to do, and the reason it stays after
    /// the work it tracked is finished.</para>
    ///
    /// <para>⚠️ Still re-measure on the merge result if this ever rises and falls again: a number
    /// here is a fact about a tree, and a merge produces a tree neither author measured.</para>
    /// </summary>
    private const int Baseline = 0;

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
    /// <para>So growth is still refused outright, and drift is bounded: ten was small enough that
    /// a forgotten re-bank could not quietly become headroom for ten new offenders, and large
    /// enough to absorb the two or three conversions usually in flight together.</para>
    ///
    /// <para><b>Back to zero now that the backlog is.</b> The slack existed because conversions
    /// landing together each lowered the count, so neither branch's constant survived the merge.
    /// Nothing is left to convert, so nothing can lower it any further — the count can only rise,
    /// and <see cref="No_new_operator_action_asks_more_than_once"/> refuses that outright. Keeping
    /// ten would be slack with no remaining purpose, which is what every other ratchet here
    /// refuses on principle; the deviation was for the backlog era and ends with it.</para>
    /// </summary>
    private const int AllowedDrift = 0;

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
        return [.. OperatorActions()
            .Where(a => GatedCall.Matches(a.Body).Count >= 2
                     && !a.Body.Contains("BeginOperation(")
                     && !a.Body.Contains("DeclareConsequence("))
            .Select(a => $"{a.File}:{a.Name}")];
    }

    /// <summary>
    /// The set both questions in this file are asked of: public service methods a Razor component
    /// calls by name. One enumerator rather than two scans, so the two cannot drift apart over
    /// which methods they consider an operator action.
    /// </summary>
    private static IEnumerable<(string File, string Name, string Body)> OperatorActions()
    {
        string razor = string.Join("\n", Directory
            .EnumerateFiles(ComponentsRoot, "*.razor", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

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

                // The gate is not an operator action: DeclareConsequence's own declaration
                // mentions itself, and asking it to justify itself is not coherent.
                if (Path.GetFileNameWithoutExtension(path) == "ClusterChangeGate")
                    continue;

                yield return (Path.GetFileName(path), name, body);
            }
        }
    }

    /// <summary>
    /// Operator actions that ask <b>zero</b> times, which is the mistake the count above does not
    /// police and which accepting <c>DeclareConsequence</c> there would otherwise have let in
    /// unnoticed.
    ///
    /// <para>Named with a reason each, not counted: there are two, which is few enough for a human
    /// to check, and a third appearing inside a total would say nothing about whether it was
    /// justified. The justification always has the same shape — <b>the UI already confirmed this
    /// specific act</b> — and it is the one thing a source scan cannot verify, so it is written
    /// down instead.</para>
    /// </summary>
    private static readonly Dictionary<string, string> AsksNothingBecauseTheUiAlreadyDid =
        new(StringComparer.Ordinal)
        {
            ["RemediationService.cs:ExecuteAsync"] =
                "TenantAlertsTab shows a confirm panel naming the resource and describing the "
                + "effect before this is reachable, and the switch runs exactly one change per call",

            ["ReleaseVolumeGuard.cs:RestoreAsync"] =
                "the upgrade dialog the operator acknowledged already named every workload taken "
                + "down and put back. ⚠️ This entry is also the scan's name-only matching being "
                + "coarse: the `.RestoreAsync(` it found in Razor is a backup page's own method, "
                + "not this one. Left in rather than special-cased, because the imprecision is the "
                + "existing heuristic's and hiding it here would be the wrong place to fix it",
        };

    /// <summary>
    /// ⚠️ A new entrant here is an operator action that changes a cluster <em>several times</em>
    /// with no dialog at all. That can be right — both current entries are — but it is never right
    /// by default, and it is strictly less visible than asking too often, which is what the rest
    /// of this file measures.
    /// </summary>
    [Fact]
    public void An_operator_action_that_asks_nothing_at_all_is_named_and_justified()
    {
        List<string> found = [.. OperatorActions()
            .Where(a => a.Body.Contains("DeclareConsequence("))
            .Select(a => $"{a.File}:{a.Name}")];

        // Health check that survives the list being correct: the enumerator still finds operator
        // actions at all. Without it, a broken AnyMember regex would empty both lists and pass.
        OperatorActions().Should().HaveCountGreaterThan(50,
            "if this stops finding operator actions, the member scan has broken and both "
            + "assertions in this file are vacuous");

        found.Should().BeEquivalentTo(AsksNothingBecauseTheUiAlreadyDid.Keys,
            "an operator action that suppresses every acknowledgment has to say why here — the "
            + "reason is that the UI already confirmed the act, and nothing in the source can "
            + "check that for you");
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
    /// The banked number has to be re-measured when it falls. With both at zero this cannot fire
    /// on a falling count — there is nothing left to fall — so what it now guards is a future
    /// re-opening of the backlog: convert something new, forget to re-bank, and this says so.
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
