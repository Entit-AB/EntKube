using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Cluster mutations made through the <b>typed Kubernetes SDK</b> rather than through the CLI —
/// the dimension <see cref="ClusterChangeGateCoverageTests"/> cannot see, measured here for the
/// first time.
///
/// <para><b>Why a second file rather than a wider regex.</b> That one counts <c>"kubectl"</c> and
/// <c>"helm"</c> as the <em>program argument of a process call</em>. A mutation issued as
/// <c>client.AppsV1.PatchNamespacedDeploymentAsync(…)</c> spawns no process and names no program,
/// so it is invisible to that metric by construction, not by oversight. The two measure different
/// escapes from the same gate and neither subsumes the other: the CLI count is down to one
/// (a port-forward, which is a channel), while this one had five live sites when it was
/// written.</para>
///
/// <para><b>What it found.</b> 19 typed-SDK mutations in three files. Fourteen acknowledge:
/// <c>KubernetesOperationsService</c> ×11 and <c>NodeManagementService</c> ×3, each one preceded in
/// its own method by an <c>AcknowledgeAsync</c>/<c>RequireAckAsync</c>. The other five were
/// <c>RemediationService</c>, which deleted pods and restarted Deployments, StatefulSets and
/// DaemonSets <em>without calling the gate at all</em> — and that is worse than it reads, because
/// a flow that never calls the gate produces no record of any outcome. It was invisible rather
/// than ungated, which is the distinction docs/decomposition.md §5.4.1 exists to kill. All five now
/// go through <c>IClusterClient</c> inside a <c>DeclareConsequence</c> scope, so each is recorded
/// as <c>AppliedAsConsequence</c> and the operator is still not asked twice.</para>
///
/// <para><b>The standing capability this does not measure.</b>
/// <c>IClusterClient.CreateSdkClient()</c>/<c>GetPooledSdkClient()</c> hand out a live
/// <c>k8s.Kubernetes</c> at 36 call sites in 12 files, and the accessor's own doc-comment admits
/// the credential stays reachable. Those sites are overwhelmingly legitimate reads — EntKube has to
/// read clusters — so counting them would be a census, not a ratchet: a number that cannot reach
/// zero and whose members are all defensible teaches nothing. What is worth guarding is that none
/// of them <em>mutates</em> outside an acknowledgment, and that is the number below. Any of the 36
/// is one line from becoming a site this file catches, which is the point of catching it here
/// rather than in review.</para>
///
/// <para><b>Unlike an offender list, this one is expected to be empty</b>, so its health check
/// cannot be "we found something". See <see cref="MinimumSdkCallsSeen"/>.</para>
/// </summary>
public class TypedSdkMutationTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Typed-SDK mutations whose own method never acknowledges. Zero, and it may only stay zero.
    ///
    /// <para>This is a stronger containment test than the CLI metric's, which asks only whether
    /// the <em>file</em> acknowledges anywhere and so scores zero for a file that gates some paths
    /// and not others — it says so itself, and calls undercounting the right error for a ratchet to
    /// make. Here the enclosing method is resolved per site, so removing the
    /// <c>AcknowledgeAsync</c> from one of <c>KubernetesOperationsService</c>'s five gated methods
    /// fails this test while the other four keep theirs.</para>
    /// </summary>
    private const int BaselineUnacknowledged = 0;

    /// <summary>
    /// The health check, and it has to be one that <b>survives the problem being fixed</b>.
    ///
    /// <para>A ratchet whose finding is an empty list cannot prove it looked by finding something —
    /// if the receiver pattern stopped matching, every count would read zero and every assertion
    /// would pass. So the assertion is on what the scan <em>read</em>: the same API-group receiver
    /// shape must still find SDK calls at all. 64 when written, nearly all of them reads, which is
    /// why this is a floor well below that rather than a banked count — <see cref="Measure"/>
    /// deliberately does not care whether a call reads or writes when answering "does this scan
    /// still work".</para>
    /// </summary>
    private const int MinimumSdkCallsSeen = 40;

    /// <summary>
    /// Every acknowledged typed-SDK mutation, named by the method that makes it, with how many it
    /// makes.
    ///
    /// <para>Named rather than totalled, for the reason <see cref="ExecChangeIntentTests"/> names
    /// its verbs: a total of 14 is satisfied by any 14, so a method that stopped acknowledging
    /// while another gained a site would net out to zero. Each entry was verified by reading the
    /// method, not by trusting the line numbers to be adjacent — <c>DeleteResourceAsync</c>'s three
    /// deletes sit in a switch 14 lines below the one acknowledgment that covers them.</para>
    ///
    /// <para>⚠️ Changing an entry here is a claim about a security property. A site that moves onto
    /// the seam should <em>leave</em> this list (the factory acknowledges for it), not be re-banked
    /// with a different count.</para>
    /// </summary>
    private static readonly Dictionary<string, int> Acknowledged = new(StringComparer.Ordinal)
    {
        ["KubernetesOperationsService.RestartDeploymentAsync"] = 1,
        ["KubernetesOperationsService.ScaleWorkloadAsync"] = 3,
        ["KubernetesOperationsService.RestartWorkloadAsync"] = 3,
        ["KubernetesOperationsService.DeleteResourceAsync"] = 3,
        ["KubernetesOperationsService.SetCronJobSuspendedAsync"] = 1,
        ["NodeManagementService.SetNodeSchedulableAsync"] = 1,
        ["NodeManagementService.SetNodeLabelAsync"] = 1,
        ["NodeManagementService.SetNodeTaintsAsync"] = 1,
    };

    /// <summary>
    /// A mutation on a k8s API-group receiver: <c>CoreV1</c>, <c>AppsV1</c>, <c>BatchV1</c>,
    /// <c>NetworkingV1</c>, <c>ApiextensionsV1</c>, <c>AutoscalingV2</c>.
    ///
    /// <para><b>Matched by shape, not by a list of group names, and that is deliberate.</b> The
    /// generated client names every group <c>&lt;Name&gt;V&lt;n&gt;[suffix]</c>, so a seventh group
    /// arriving — <c>PolicyV1</c>, <c>RbacAuthorizationV1</c> — is caught without this file being
    /// edited. A whitelist would have gone quiet instead, which is the failure mode a ratchet can
    /// least afford.</para>
    ///
    /// <para>⚠️ <b>The receiver is what identifies these, never the method name.</b> "Cluster" and
    /// "Node" are EntKube's own words: <c>TenantService.CreateClusterAsync</c>,
    /// <c>RedisService.DeleteClusterAsync</c> and <c>HeadscaleService.DeleteNodeAsync</c> all match
    /// the k8s naming shape and none of them is an SDK call. A first pass at this scan keyed on
    /// <c>(Create|Delete|…)(Namespaced|Node|Cluster)\w*Async</c> and returned 39 sites, twenty of
    /// them EntKube service methods and Razor click handlers.</para>
    /// </summary>
    private static readonly Regex Mutation = new(
        @"\.(?:[A-Z][A-Za-z]*V[0-9][A-Za-z0-9]*)\.(?:Create|Delete|Patch|Replace)[A-Za-z0-9]*Async\s*\(",
        RegexOptions.Compiled);

    /// <summary>Any call on an API-group receiver, read or write — the health check's subject.</summary>
    private static readonly Regex AnySdkCall = new(
        @"\.(?:[A-Z][A-Za-z]*V[0-9][A-Za-z0-9]*)\.[A-Za-z0-9]+Async\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Every spelling of "this scope was declared to the gate". <c>BeginOperation</c> counts: it
    /// suppresses the second and later questions of one operation, and the first still asked.
    ///
    /// <para>⚠️ <c>\b</c> on both ends, and it is load-bearing. Verifying this ratchet meant
    /// removing a real acknowledgment to watch it fail, and the first attempt renamed
    /// <c>AcknowledgeAsync</c> to <c>AcknowledgeAsyncSTOLEN</c> — which still <em>contains</em> the
    /// symbol, so the unanchored pattern matched and the test passed, reporting a gated method
    /// that no longer called the gate. The removal check is what found it; the anchors are what
    /// stop an identifier that merely embeds one of these names from reading as a declaration.</para>
    /// </summary>
    private static readonly Regex GateCall = new(
        @"\b(?:AcknowledgeAsync|RequireAckAsync|RequireNodeAckAsync"
        + @"|DeclareUnattended|DeclareConsequence|BeginOperation)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// A member declaration at class or nested-class indent. Both depths are accepted because
    /// <c>NodeManagementService</c> has nested types, and the <em>nearest</em> preceding
    /// declaration is the enclosing one at either depth.
    ///
    /// <para><c>[^;{=]*?</c> is what keeps a field initialiser — <c>private readonly Mock&lt;T&gt;
    /// x = new();</c> — from reading as a method called <c>new</c>.</para>
    /// </summary>
    private static readonly Regex MemberDecl = new(
        @"^(?:    ){1,2}(?:public|private|internal|protected)\s[^;{=]*?\b(?<name>\w+)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void No_typed_sdk_mutation_runs_outside_its_own_acknowledgment()
    {
        Scan scan = Measure();

        // Health check first, and able to survive the finding being empty: the pattern still
        // matches SDK calls, so a zero below means there is nothing rather than that the scan
        // broke. Asserting on what was READ, never on the offender list being non-empty.
        scan.SdkCallsSeen.Should().BeGreaterThanOrEqualTo(MinimumSdkCallsSeen,
            "if this stops finding SDK calls the API-group receiver pattern has broken, and the "
            + "empty offender list below would mean nothing at all");

        scan.Unplaceable.Should().BeEmpty(
            "the scan could not find the enclosing member for these sites, so it cannot say "
            + "whether they acknowledge — a step that cannot answer must fail rather than skip");

        scan.Unacknowledged.Should().HaveCount(BaselineUnacknowledged,
            "a typed-SDK mutation whose method never acknowledges changes a cluster with nobody "
            + "asked AND no outcome recorded, because a flow that never calls the gate gives "
            + $"IClusterChangeRecorder nothing to record. Sites: {Describe(scan.Unacknowledged)}");
    }

    /// <summary>
    /// The other direction of the ratchet. Without this, deleting an <c>AcknowledgeAsync</c>
    /// <em>and</em> its mutation together would pass the test above by making both disappear, and
    /// so would moving a mutation into a method that acknowledges for an unrelated reason.
    /// </summary>
    [Fact]
    public void The_acknowledged_sdk_mutations_are_the_ones_this_file_names()
    {
        Scan scan = Measure();

        scan.Acknowledged.Should().BeEquivalentTo(Acknowledged,
            "a method that gained or lost a typed-SDK mutation, or stopped acknowledging one, is a "
            + "change to where this product writes to clusters — say which in the doc comment "
            + "rather than re-banking a total");
    }

    /// <summary>
    /// The shapes this scan is blind to, asserted <b>empty</b> so that starting to use one fails
    /// here instead of quietly adding an unmeasured mutation path.
    ///
    /// <para>Each would be invisible for a different reason, and all four are unoccupied today:</para>
    /// <list type="bullet">
    ///   <item><c>GenericClient</c> — mutates through a constructed wrapper
    ///     (<c>new GenericClient(client, group, version, plural).CreateAsync(…)</c>), so there is
    ///     no API-group receiver in the expression at all.</item>
    ///   <item><c>…WithHttpMessagesAsync</c> — the generated low-level overload. It <em>does</em>
    ///     carry the receiver, so <see cref="Mutation"/> would match it; it is named here because
    ///     its absence is worth keeping, not because the scan would miss it.</item>
    ///   <item><c>DeleteCollection…</c> — a bulk delete. Matched by <see cref="Mutation"/> as
    ///     written, and listed so that stays true if the verb alternation is ever narrowed.</item>
    ///   <item><c>CustomObjects</c> mutations — CNPG clusters, Istio policies and cert-manager
    ///     Certificates live here. <c>CustomObjects</c> has no <c>V&lt;n&gt;</c> in its name, so
    ///     the receiver shape does <b>not</b> match it and a mutation there would be genuinely
    ///     invisible. Today every CRD write goes through <c>ApplyManifestAsync</c> and only
    ///     <c>ListNamespacedCustomObjectAsync</c>/<c>ReadCustomResourceDefinitionAsync</c> are
    ///     called, which are reads.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_shapes_this_scan_cannot_see_are_still_unoccupied()
    {
        // Join each file's STRIPPED LINES into text, then the files together. Spelled out
        // because Strip returns string[]: Select(Strip) would have produced a sequence of arrays
        // and string.Join would have rendered each as "System.String[]", leaving all four
        // absences below vacuously true. The health check immediately after is what caught it.
        string all = string.Join('\n',
            Sources().Select(p => string.Join('\n', Strip(File.ReadAllLines(p)))));

        // Control: the corpus is non-empty and contains the SDK, or the four zeros below are free.
        AnySdkCall.Matches(all).Count.Should().BeGreaterThanOrEqualTo(MinimumSdkCallsSeen,
            "same health check as above — four absences prove nothing about a corpus that was "
            + "never read");

        Regex.Matches(all, @"\bGenericClient\b").Should().BeEmpty(
            "a GenericClient mutation carries no API-group receiver and this scan would not see "
            + "it; route the write through IClusterClient instead");

        Regex.Matches(all, @"(?:Create|Delete|Patch|Replace)[A-Za-z0-9]*WithHttpMessagesAsync\s*\(")
            .Should().BeEmpty("the low-level overload bypasses nothing, but nothing needs it");

        Regex.Matches(all, @"\.DeleteCollection[A-Za-z0-9]*Async\s*\(").Should().BeEmpty(
            "a bulk delete is the one shape where a single ungated call can empty a namespace");

        Regex.Matches(all, @"\.CustomObjects\.(?:Create|Delete|Patch|Replace)[A-Za-z0-9]*Async\s*\(")
            .Should().BeEmpty(
                "CustomObjects has no V<n> in its name, so the receiver shape cannot match it — a "
                + "CRD mutation here would be invisible to this ratchet. CNPG clusters and Istio "
                + "policies are written with ApplyManifestAsync, which acknowledges");
    }

    private sealed record Scan(
        Dictionary<string, int> Acknowledged,
        List<string> Unacknowledged,
        List<string> Unplaceable,
        int SdkCallsSeen);

    private static string Describe(List<string> sites)
        => sites.Count == 0 ? "none" : string.Join("; ", sites);

    private static IEnumerable<string> Sources()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        return Directory
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                     || p.EndsWith(".razor", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    /// <summary>
    /// Blanks whole-line comments while keeping every line's position, so a site's line number
    /// still means something and prose cannot be mistaken for a call.
    ///
    /// <para>That matters in both directions here. A comment naming <c>AcknowledgeAsync</c> must
    /// not make a method look gated — <c>RemediationService</c> carried a four-line comment about
    /// the change gate precisely where it was not calling it. And the comment on
    /// <see cref="Mutation"/> quotes an SDK call, which would otherwise count as one.</para>
    /// </summary>
    private static string[] Strip(string[] lines) =>
        [.. lines.Select(l =>
        {
            string t = l.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith("*", StringComparison.Ordinal)
                || t.StartsWith("/*", StringComparison.Ordinal) ? "" : l;
        })];

    /// <summary>
    /// Resolves, per mutation site, whether its own method declared anything to the gate before it.
    /// </summary>
    private static Scan Measure()
    {
        Dictionary<string, int> acknowledged = new(StringComparer.Ordinal);
        List<string> unacknowledged = [], unplaceable = [];
        int seen = 0;

        foreach (string path in Sources())
        {
            string[] code = Strip(File.ReadAllLines(path));
            string file = Path.GetFileNameWithoutExtension(path);

            for (int i = 0; i < code.Length; i++)
            {
                seen += AnySdkCall.Matches(code[i]).Count;

                int mutations = Mutation.Matches(code[i]).Count;
                if (mutations == 0) continue;

                // The enclosing member, found by walking back to the nearest declaration.
                int decl = -1;
                string name = "";
                for (int j = i; j >= 0; j--)
                {
                    Match m = MemberDecl.Match(code[j]);
                    if (!m.Success) continue;
                    decl = j;
                    name = m.Groups["name"].Value;
                    break;
                }

                if (decl < 0)
                {
                    unplaceable.Add($"{file}:{i + 1}");
                    continue;
                }

                bool declared = false;
                for (int j = decl; j < i && !declared; j++)
                    declared = GateCall.IsMatch(code[j]);

                string where = $"{file}.{name}";

                if (declared)
                    acknowledged[where] = acknowledged.GetValueOrDefault(where) + mutations;
                else
                    for (int k = 0; k < mutations; k++)
                        unacknowledged.Add($"{where} (line {i + 1})");
            }
        }

        return new Scan(acknowledged, unacknowledged, unplaceable, seen);
    }
}
