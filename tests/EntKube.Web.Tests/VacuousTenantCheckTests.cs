using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Where a seam call gets its tenant from — because <c>IClusterClientFactory.ForAsync</c> enforces
/// <c>c.TenantId == tenantId</c>, and a tenant read off the cluster row itself makes that predicate
/// compare the row to itself.
///
/// <para><b>Why this test exists.</b> The custody programme counts credential sites disappearing:
/// 508 down to 223. That number says the kubeconfig no longer travels, which is true and worth
/// having. It does not say the call became authorized, and 19 of the conversions did not — they
/// are of the form</para>
///
/// <code>await clusterClients.ForAsync(cluster.TenantId, cluster.Id, ct)</code>
///
/// <para>which asks the factory to fetch the cluster whose tenant equals its own tenant. The
/// wrong-tenant branch cannot fire. At the call site it is indistinguishable from a real check, it
/// satisfies the custody ratchet, and it reads like diligence.</para>
///
/// <para><b>It is not carelessness.</b> Every one of them is in a method with no tenant in its
/// signature, so the cluster row was the only tenant in scope — the author had nothing else to
/// pass. One said so out loud: <c>ComponentLifecycleService</c>'s secret sync carried the comment
/// "the tenant check inside ForAsync is vacuous here" — and that site was the <b>last</b> one,
/// closed by giving the method the tenant its five callers all had. That makes this a measurement
/// of the authorization debt
/// <see cref="CustodyTriageTests"/> describes, taken at the one place it is visible in a single
/// expression instead of a call graph.</para>
///
/// <para>⚠️ <b>This test reported zero while thirteen sites carried the pattern, and the reason is
/// worth more than the sites.</b> It only inspected lines containing <c>ForAsync(</c> — and six
/// services wrap the seam in a one-line forwarder of their own
/// (<c>ClusterFor(Guid tenantId, Guid clusterId, …) =&gt; await clusters.ForAsync(…) ?? throw …</c>)
/// so they can attach their own refusal message. One level of indirection hid the pattern
/// completely: <c>MongoService</c> 5, <c>RegisteredPostgresService</c> 5, <c>RabbitMQService</c> 2,
/// <c>ElasticsearchService</c> 1, plus the four create/register paths found first by hand.</para>
///
/// <para>The forwarder's name is now <b>derived per file</b> (see <see cref="Forwarder"/>) rather
/// than listed, for the same reason <c>CatalogContractAdoptionTests</c> derives its navigation
/// names from the entity model: a list only ever covers the spelling whoever wrote it had seen.
/// That is the fourth ratchet in this programme found to be counting one spelling of the thing it
/// names — a DbSet but not a navigation, a credential read but not a log line about one, a call but
/// not the wrapper around it.</para>
///
/// <para><b>What the thirteen were worth, which differs by site.</b> In most of them the row had
/// itself been loaded tenant-scoped, so the check was redundant rather than unsound. In the
/// create/register paths — Mongo, Kafka, RabbitMQ, Postgres and Elasticsearch — the target cluster
/// was loaded <em>by id with no tenant predicate</em>, so a foreign cluster id would provision a
/// managed database or broker onto another tenant's cluster while the vault secrets were written
/// under the caller's own tenant. <c>ElasticsearchService.CreateClusterAsync</c> took no tenant at
/// all, only the entity the caller had built, so the tenant arrived as data; it takes one now and
/// refuses a mismatch.</para>
///
/// <para><b>Three kinds of first argument.</b></para>
/// <list type="bullet">
///   <item><b>Independent</b> — a <c>tenantId</c> parameter, so it came from the caller, which is
///     ultimately a signed-in user or a sweep that chose a tenant. This is the only kind that
///     authorizes anything.</item>
///   <item><b>Cross-row</b> — a tenant column on a different row that references the cluster, e.g.
///     <c>ForAsync(cnpg.TenantId, cnpg.KubernetesClusterId)</c>. The predicate compares two tables
///     and will catch a row pointing at another tenant's cluster, so it is not vacuous. It is an
///     integrity check, not an authorization one: it is only as good as how that row was fetched.
///     </item>
///   <item><b>Tautological</b> — the tenant of the very cluster row being named. No check.</item>
/// </list>
/// </summary>
public class VacuousTenantCheckTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Measured 2026-10-11, after deleting <c>DatabaseService</c>'s two dead discovery methods —
    /// which were the only two tautological sites in a method that <em>had</em> a tenant, and so
    /// the only two that were a one-word fix. They had no callers at all: the portal's database
    /// panels were rebuilt as per-app views and left 336 lines of live-cluster querying behind,
    /// including a <c>DatabaseClusterInfo</c> DTO referenced from nowhere but its own file.
    ///
    /// <para>They are concentrated: <c>KubernetesOperationsService</c> has 11,
    /// <c>ComponentLifecycleService</c> 4, and one each in <c>CertificateDistributionService</c>,
    /// <c>TrustBundleService</c>, <c>ElasticsearchService</c> and <c>KedaScalerService</c>. The
    /// other two buckets stand at 22 independent and 7 cross-row, 48 sites in all.</para>
    ///
    /// <para>None of the 19 can be fixed here. They need a tenant threaded down from their
    /// callers, which is the authorization question in docs/decomposition.md §4.0.1 — and the
    /// concentration is the useful part of the figure: eleven of them are one service's.
    /// <em>(Those eleven are done; the concentration was indeed the useful part.)</em></para>
    ///
    /// <para>This number may not grow. A new tenant-less method reaching a cluster is a new place
    /// where any cluster in the installation is reachable by id, and going through the seam does
    /// not change that.</para>
    ///
    /// <para><b>18 — <c>ElasticsearchService.CheckCapacityAsync</c>.</b> It took a cluster id,
    /// loaded that row, and handed the tenant it found back to the seam. Its page caller had a
    /// <c>TenantId</c> all along; its two internal callers hold an <c>ElasticsearchCluster</c>,
    /// whose own <c>TenantId</c> makes those two <em>cross-row</em> — an integrity check that can
    /// actually fail, because an ES cluster row pointing at another tenant's Kubernetes cluster is
    /// now refused rather than served. The cluster lookup is scoped as well, so the row cannot be
    /// read at all without the tenant matching.</para>
    ///
    /// <para>Picked because <c>ElasticsearchService</c> holds <b>zero</b> credential sites — it was
    /// converted in full earlier — so fixing a vacuous check there could not spill into the
    /// custody count while that work was in flight on another branch. The other eight non-Fleet
    /// tautologies all sit in files that still hold credentials, where threading a tenant in makes
    /// those reads newly inexcusable in the same step (the trap #185 walked into deliberately).</para>
    /// </summary>
    /// <para><b>7 — <c>KubernetesOperationsService</c>'s eleven, which were the concentration
    /// this figure was pointing at.</b> They are the apply path: install, upgrade, delete,
    /// the HTTP and L4 route applies and deletes, mesh and outbound mTLS, and the L4 gateway
    /// address. Each took an id, loaded the row, and handed the row's own tenant back to the seam
    /// — so the most consequential writes in the product were reachable by id alone.</para>
    ///
    /// <para>Every caller had a tenant to give: five pages that already held one, the public API's
    /// token principal, and two sweeps (<c>GitSyncService</c>, <c>AppL4RouteHealthService</c>)
    /// that became tenant loops. <c>GitSyncService</c>'s on-demand queue now carries the tenant
    /// with each entry rather than recovering it when the queue drains — the webhook that fills it
    /// had already resolved one in order to find the deployment at all.</para>
    ///
    /// <para>What remains is <b>7</b>: four in <c>ComponentLifecycleService</c> and one each in
    /// <c>CertificateDistributionService</c>, <c>TrustBundleService</c> and
    /// <c>KedaScalerService</c>. All four files still hold credential sites, so threading a tenant
    /// in makes those reads newly inexcusable in the same step — which is why they belong with
    /// the custody work rather than beside it.</para>
    /// </summary>
    /// <para><b>5.</b> <c>KedaScalerService</c> and <c>TrustBundleService</c>, converted together
    /// with their credential sites — which is the only way these four files can move, since a
    /// tenant parameter makes the reads inside them newly inexcusable in the same step.</para>
    ///
    /// <para><b>4.</b> <c>CertificateDistributionService</c> followed, the same way: its target
    /// resolution returns seam clients rather than cluster rows, so there is no row left to read a
    /// tenant off.</para>
    ///
    /// <para><b>1.</b> <c>ComponentLifecycleService</c>'s three went with its credential sites —
    /// the install path now takes the tenant from <c>ComponentInstallOrchestrator</c> (which had
    /// one all along) and from <c>ClusterDetail</c>.</para>
    ///
    /// <para><b>0 — and the last one was hidden by an alias.</b> <c>SyncComponentSecretsAsync</c>
    /// read <c>Guid tenantId = component.Cluster.TenantId;</c> and then called
    /// <c>ForAsync(tenantId, component.ClusterId)</c>, so it <em>read</em> like every converted
    /// site while being the opposite. Resolving method-scoped aliases is the only reason this test
    /// could see it, which is worth remembering about every grep-shaped measurement: the spelling
    /// that hides a thing is usually the spelling everyone else uses.</para>
    ///
    /// <para>Its comment explained that the check had to be vacuous because the method "is
    /// reached by component id by design". All five callers — two pages, the install
    /// orchestrator twice, and <c>StalwartService</c>'s apply — had a tenant. The design was in
    /// the signature. That is the third such claim this programme has opened and found wrong,
    /// after #189's exemption list and #193's "cannot move".</para>
    ///
    /// <para><b>This number may now only ever be zero.</b> The two halves of the ratchet say the
    /// same thing from here: a new tautological site is both a regression and un-banked slack.</para>
    /// </summary>
    private const int BaselineTautological = 0;

    [Fact]
    public void A_tenant_read_off_the_cluster_row_checks_nothing_and_may_not_spread()
    {
        Classified sites = Measure();

        sites.Tautological.Count.Should().BeLessThanOrEqualTo(BaselineTautological,
            "ForAsync(x.TenantId, x.Id) asks for the cluster whose tenant is its own tenant — the "
            + "wrong-tenant branch cannot fire. New ones here:\n  "
            + string.Join("\n  ", sites.Tautological.Skip(BaselineTautological)));
    }

    [Fact]
    public void The_tautological_baseline_does_not_carry_slack()
    {
        Classified sites = Measure();

        sites.Tautological.Count.Should().Be(BaselineTautological,
            $"now {sites.Tautological.Count} — bank it, or the slack lets them come back for free");
    }

    /// <summary>
    /// The seam is worth having even where the tenant is derived: the credential stops travelling
    /// through the caller either way. So this asserts the independent group is the larger one and
    /// does not shrink, which is what would happen if a real check were replaced by a derived one
    /// during some later refactor.
    /// </summary>
    [Fact]
    public void Most_seam_calls_still_get_their_tenant_from_the_caller()
    {
        Classified sites = Measure();

        sites.Independent.Count.Should().BeGreaterThanOrEqualTo(22,
            "a tenant that came from the caller is the only kind that authorizes anything");
        sites.Independent.Count.Should().BeGreaterThan(sites.Tautological.Count);
    }

    /// <summary>
    /// Every site lands in exactly one bucket. Without this, a classifier that silently stopped
    /// recognising a shape would report the tautological count falling and look like progress —
    /// the same failure as a ratchet whose regex matches nothing.
    /// </summary>
    [Fact]
    public void Every_seam_call_is_classified()
    {
        Classified sites = Measure();

        sites.Total.Should().BeGreaterThan(40, "the seam has ~50 call sites; a collapse means the "
            + "pattern stopped matching rather than the code improving");
        (sites.Independent.Count + sites.CrossRow.Count + sites.Tautological.Count)
            .Should().Be(sites.Total,
                $"unclassified: {string.Join(", ", sites.Unclassified)}");
    }

    // ════════════════════════════════════════════════════════════════

    private sealed record Classified(
        List<string> Independent, List<string> CrossRow,
        List<string> Tautological, List<string> Unclassified)
    {
        public int Total => Independent.Count + CrossRow.Count
                          + Tautological.Count + Unclassified.Count;
    }

    /// <summary>
    /// The two arguments of a <c>ForAsync</c> call, allowing the call to wrap — which it usually
    /// does, because the result is assigned to a long nullable type.
    /// </summary>
    private static readonly Regex Call = new(
        @"ForAsync\(\s*([A-Za-z_][\w\.]*)\s*,\s*([A-Za-z_][\w\.]*)\s*[,)]", RegexOptions.Compiled);

    /// <summary>
    /// A private one-line forwarder onto the seam:
    /// <c>private async Task&lt;IClusterClient&gt; ClusterFor(Guid tenantId, Guid clusterId, …)
    /// =&gt; await clusters.ForAsync(tenantId, clusterId, ct) ?? throw …</c>
    ///
    /// <para><b>Why this is here.</b> Six services wrap <c>ForAsync</c> in exactly that shape to
    /// attach their own refusal message, and this test only looked at lines containing
    /// <c>ForAsync(</c> — so <b>one level of indirection hid the pattern completely</b>. It
    /// reported zero while four create/register paths passed a cluster row's own tenant through a
    /// wrapper: `ClusterFor(k8sCluster.TenantId, k8sCluster.Id)`, the row checked against itself,
    /// in methods that had the asking tenant in scope. A foreign cluster id provisioned a managed
    /// database or broker onto another tenant's cluster.</para>
    ///
    /// <para>The wrapper's name is <em>derived per file</em> rather than listed, for the same
    /// reason the Catalog ratchet now derives its navigation names: a list only ever covers the
    /// spelling whoever wrote it had seen. A wrapper counts only when its two <c>Guid</c>
    /// parameters reach <c>ForAsync</c> in that order, so a helper that does something else with
    /// them is not mistaken for one.</para>
    /// </summary>
    private static readonly Regex Forwarder = new(
        @"(?:private|internal)\s+(?:static\s+)?(?:async\s+)?Task<[^>]*IClusterClient[^>]*>\s+(\w+)\s*\(\s*Guid\s+(\w+)\s*,\s*Guid\s+(\w+)",
        RegexOptions.Compiled);

    /// <summary>A method declaration, which is where a tenant local's scope begins.</summary>
    private static readonly Regex Declaration = new(
        @"^\s+(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>?,\.\s\[\]\(\)]+?\s\w+\(",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>Guid tenantId = component.Cluster.TenantId;</c> — a tenant lifted out of a row into a
    /// local, which the call below then passes as if it were a parameter.
    /// </summary>
    private static readonly Regex TenantLocal = new(
        @"\bGuid\s+(\w+)\s*=\s*([\w\.]+\.TenantId)\s*;", RegexOptions.Compiled);

    private static Classified Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        Classified found = new([], [], [], []);

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".cs", StringComparison.Ordinal)
                              || p.EndsWith(".razor", StringComparison.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(path);

            // The factory's own declaration and implementation are not call sites.
            if (text.Contains("Task<IClusterClient?> ForAsync(", StringComparison.Ordinal))
                continue;

            // Line comments go first, so prose mentioning a call is not counted as one. None does
            // today; three comments name ForAsync without arguments, and one of them says in so
            // many words that the check below it is vacuous.
            string[] lines = [.. text.Split('\n').Select(l => LineComment.Replace(l, ""))];

            // Which names in this file forward straight into the seam. Matched on the body
            // rather than the signature alone: the two Guids have to reach ForAsync in order.
            HashSet<string> forwarders = [];
            foreach (Match w in Forwarder.Matches(text))
            {
                string body = Whitespace.Replace(
                    text[(w.Index + w.Length)..Math.Min(text.Length, w.Index + w.Length + 400)], " ");
                if (body.Contains($"ForAsync({w.Groups[2].Value}, {w.Groups[3].Value}", StringComparison.Ordinal))
                {
                    forwarders.Add(w.Groups[1].Value);
                }
            }

            Dictionary<string, string> locals = [];

            for (int i = 0; i < lines.Length; i++)
            {
                // A new declaration ends the previous method's locals. Without this the map is
                // file-wide, and a `Guid tenantId = cluster.TenantId;` in one method rewrites
                // another method's tenantId PARAMETER -- which moved RegisteredPostgresService's
                // one honest site into the wrong bucket when this was first measured.
                if (Declaration.IsMatch(lines[i]))
                    locals.Clear();

                foreach (Match a in TenantLocal.Matches(lines[i]))
                    locals[a.Groups[1].Value] = a.Groups[2].Value;

                // A call to one of this file's forwarders is a call to the seam. Its own
                // declaration line is not a call site, so the name has to be followed by an
                // argument that is not a `Guid` parameter.
                bool viaWrapper = forwarders.Any(w =>
                    lines[i].Contains($"{w}(", StringComparison.Ordinal)
                    && !Forwarder.IsMatch(lines[i]));

                if (!lines[i].Contains("ForAsync(", StringComparison.Ordinal) && !viaWrapper)
                    continue;

                // The call usually wraps, because the result is a long nullable type.
                string window = Whitespace.Replace(
                    string.Join(' ', lines.Skip(i).Take(3)), " ");

                Match m = Call.Match(window);

                if (!m.Success && viaWrapper)
                {
                    foreach (string w in forwarders)
                    {
                        Match viaCall = Regex.Match(
                            window,
                            $@"\b{Regex.Escape(w)}\(\s*([A-Za-z_][\w\.]*)\s*,\s*([A-Za-z_][\w\.]*)\s*[,)]");
                        if (viaCall.Success) { m = viaCall; break; }
                    }
                }

                if (!m.Success)
                    continue;

                string tenant = m.Groups[1].Value, cluster = m.Groups[2].Value;

                // A tenant laundered through a local is still the row's tenant. Resolving it is
                // not pedantry: without it, the cheapest way to satisfy this ratchet would be to
                // assign the expression to a variable first.
                if (!tenant.Contains('.') && locals.TryGetValue(tenant, out string? origin))
                    tenant = origin;

                string site = $"{Path.GetFileName(path)}:{i + 1} ForAsync({tenant}, {cluster})";

                // "tenantId" does not end with "TenantId" -- the capital T cost a run where every
                // independent site landed in Unclassified while the two sound buckets passed.
                if (!tenant.EndsWith("enantId", StringComparison.Ordinal))
                    found.Unclassified.Add(site);
                else if (!tenant.Contains('.'))
                    found.Independent.Add(site);
                else if (IsTheSameClusterRow(tenant, cluster))
                    found.Tautological.Add(site);
                else
                    found.CrossRow.Add(site);
            }
        }

        return found;
    }

    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Whether the tenant was read off the very cluster row the second argument names.
    ///
    /// <para>The discriminator is not the owner's name — <c>cluster.TenantId</c> appears both for a
    /// <c>KubernetesCluster</c> and for an <c>ElasticsearchCluster</c> that merely holds a foreign
    /// key to one. It is what the second argument asks for:</para>
    /// <list type="bullet">
    ///   <item><c>owner.Id</c> — the row supplies its own id, so it <em>is</em> the cluster.</item>
    ///   <item><c>x.ClusterId</c> with the tenant from <c>x.Cluster.TenantId</c> — a navigation and
    ///     its own foreign key, which is one row reached two ways.</item>
    ///   <item>a bare <c>clusterId</c> beside a local called <c>cluster</c> — that local was
    ///     fetched by that id.</item>
    ///   <item><c>owner.KubernetesClusterId</c> — a foreign key, so the owner is a different row
    ///     and the comparison spans two tables.</item>
    /// </list>
    /// </summary>
    private static bool IsTheSameClusterRow(string tenantExpr, string clusterExpr)
    {
        string owner = tenantExpr[..tenantExpr.LastIndexOf('.')];

        if (clusterExpr == $"{owner}.Id")
            return true;

        if (owner.EndsWith(".Cluster", StringComparison.Ordinal)
            && clusterExpr == $"{owner[..^".Cluster".Length]}.ClusterId")
            return true;

        return clusterExpr == "clusterId"
            && owner.Split('.')[^1].EndsWith("luster", StringComparison.Ordinal);
    }
}
