using System.Text.RegularExpressions;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Every command executed inside a pod now states whether it changes anything, and this file is
/// the audit surface that makes the claim worth having.
///
/// <para><b>What this is for.</b> Nine verbs on <c>IKubernetesClientFactory</c> carry the most
/// consequential writes in the product — <c>CREATE ROLE</c>, <c>DROP DATABASE</c>,
/// <c>ALTER ROLE … PASSWORD</c>, Mongo <c>createUser</c>/<c>dropUser</c>,
/// <c>rabbitmqctl import_definitions</c> — and none of it raised an acknowledgment, because the
/// factory gates what it can recognise and it cannot recognise these. See
/// docs/decomposition.md §5.4.2.</para>
///
/// <para><b>The caller declares, because nothing else can.</b> Reads and writes share the verbs:
/// <c>ExecuteSqlOnPodAsync</c> carries <c>CREATE ROLE … CREATE DATABASE</c> at one site and
/// <c>SELECT 1;</c> at another. A classifier over the SQL was measured and rejected — it left 9 of
/// 30 undecidable and mis-read the Mongo password rotation, which uses the
/// <em>output-returning</em> variant and so looks like a read from its signature alone.</para>
///
/// <para>⚠️ <b>This change is deliberately not yet gating.</b> The parameter forces every site to
/// state its intent and makes the statement auditable; wiring the gate to act on it — and
/// redacting SQL that carries a password before it reaches an operator's screen — is the next
/// change. So nothing here asserts that a prompt appears. What it asserts is that the claims exist,
/// that they are enumerable, and that a new site cannot avoid making one.</para>
/// </summary>
public class ExecChangeIntentTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// The nine verbs. Named rather than discovered, because the point is that this list and the
    /// interface agree — <see cref="Every_listed_verb_still_exists_on_the_factory"/> fails if a
    /// verb is renamed, instead of the scan silently finding nothing.
    /// </summary>
    private static readonly string[] Verbs =
    [
        "ExecuteSqlAsync",
        "ExecuteSqlOnPodAsync",
        "ExecuteSqlOnPodWithOutputAsync",
        "ExecuteSqlInCnpgDatabaseAsync",
        "ExecuteSqlInCnpgDatabaseWithOutputAsync",
        "ExecuteMongoAsync",
        "ExecuteMongoWithOutputAsync",
        "RunCommandOnPodAsync",
        "RunCommandOnPodWithStdinAsync",
    ];

    /// <summary>
    /// ⚠️ <b>Every site claiming <see cref="ChangeIntent.Read"/>, which is the list to actually
    /// read.</b> A wrong <c>Read</c> is the one way this design fails: it produces an unasked,
    /// unrecorded change while appearing to have been checked. Thirteen is short enough for a human
    /// to audit, which is the whole reason the declaration is explicit rather than inferred.
    ///
    /// <para>What each one is, verified by reading the statement at the call site:</para>
    /// <list type="bullet">
    ///   <item><c>RegisteredPostgresService</c> ×4 — <c>SELECT datname FROM pg_database</c>;
    ///     <c>pg_dump</c> twice (it reads and writes to stdout, taking a shared lock and modifying
    ///     nothing); and <c>SELECT 1;</c>, the credential probe.</item>
    ///   <item><c>CnpgService</c> ×3 — <c>SELECT</c> over <c>pg_database</c>, <c>pg_tables</c> and
    ///     <c>schema_migrations</c>.</item>
    ///   <item><c>MongoService</c> ×1 — <c>db.adminCommand({listDatabases:1})</c>.</item>
    ///   <item><c>RabbitMQService</c> ×2 — <c>export_definitions</c> (to stdout), and
    ///     <c>RunCtlListAsync</c>, which appends <c>--formatter json</c> and is a listing by
    ///     construction.</item>
    ///   <item><c>StalwartService</c>, <c>StalwartDnsService</c>, <c>ElasticsearchService</c> ×1
    ///     each — an <c>ldapsearch</c>, and two <c>curl</c> calls with no <c>-X</c>, so GETs.</item>
    /// </list>
    ///
    /// <para><b>The probe is the open question, not an oversight.</b>
    /// <c>TestConnectionCoreAsync</c> runs <c>SELECT 1;</c> to decide whether credentials work. It
    /// is a read by any reading, and it is also the cheapest possible place to hide something.
    /// Whether <c>Read</c> is a <em>trusted</em> declaration or merely a quieter one is a decision
    /// this file cannot make; what it does is keep the list short enough that the question can be
    /// asked of every member of it.</para>
    /// </summary>
    private const int BaselineReadClaims = 13;

    [Fact]
    public void Every_listed_verb_still_exists_on_the_factory()
    {
        // The health check for both measurements below: if a verb were renamed, the scan would
        // find no claims for it and an empty result would read as "nothing to audit".
        IReadOnlySet<string> actual = typeof(IKubernetesClientFactory)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string verb in Verbs)
        {
            actual.Should().Contain(verb,
                $"{verb} is one of the exec verbs this file audits; if it was renamed, rename it "
                + "here too rather than letting the scan go quiet");
        }
    }

    /// <summary>
    /// None of the nine may give <c>intent</c> a default value.
    ///
    /// <para>A defaulted parameter would let all 39 existing call sites compile unchanged while
    /// declaring nothing — which is precisely how the gap arose in the first place. It is also the
    /// only part of this design that cannot be restored by review: a missing declaration is
    /// invisible, where a wrong one is in the diff.</para>
    /// </summary>
    [Fact]
    public void The_intent_parameter_has_no_default_so_a_new_site_cannot_stay_silent()
    {
        List<string> defaulted = [];

        foreach (System.Reflection.MethodInfo m in typeof(IKubernetesClientFactory).GetMethods())
        {
            if (!Verbs.Contains(m.Name, StringComparer.Ordinal)) continue;

            System.Reflection.ParameterInfo intent = m.GetParameters()
                .SingleOrDefault(p => p.ParameterType == typeof(ChangeIntent))
                ?? throw new Xunit.Sdk.XunitException(
                    $"{m.Name} takes no ChangeIntent — every exec verb must make its caller declare");

            if (intent.HasDefaultValue) defaulted.Add(m.Name);
        }

        string.Join(", ", defaulted).Should().BeEmpty(
            "a default would let a new call site declare nothing and still compile");
    }

    [Fact]
    public void The_read_claims_are_enumerable_and_do_not_grow_unnoticed()
    {
        (int read, int change, IReadOnlyList<string> where) = Measure();

        // Health check, separate from the finding and able to survive it: the scan read something.
        // Unlike an offender list, these claims are expected to exist, so a zero here means the
        // scan broke rather than that the product got better.
        change.Should().BeGreaterThan(20,
            "if this stops finding Change claims the scan broke, and the Read count below would "
            + "mean nothing");

        read.Should().BeLessThanOrEqualTo(BaselineReadClaims,
            "a new site claiming Read is the one thing in this design that needs a human to look "
            + $"at it — say why in the baseline's doc comment. Claims: {string.Join(", ", where)}");
    }

    [Fact]
    public void The_read_baseline_does_not_carry_slack()
    {
        (int read, _, IReadOnlyList<string> where) = Measure();

        read.Should().Be(BaselineReadClaims,
            $"now {read} ({string.Join(", ", where)}) — bank it by lowering the baseline, or the "
            + "slack lets an unexamined Read claim in for free");
    }

    /// <summary>
    /// Counts <c>ChangeIntent.Read</c> and <c>ChangeIntent.Change</c> across the product, with
    /// comments stripped and the enum's own declaration excluded.
    /// </summary>
    private static (int Read, int Change, IReadOnlyList<string> Where) Measure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the product's source; if it moved, the test has to follow rather than "
            + "quietly stop checking anything");

        int read = 0, change = 0;
        List<string> where = [];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "ChangeIntent.cs") continue;

            foreach (string line in File.ReadAllLines(file))
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')) continue;

                int r = Regex.Matches(line, @"ChangeIntent\.Read\b").Count;
                int c = Regex.Matches(line, @"ChangeIntent\.Change\b").Count;

                read += r;
                change += c;

                if (r > 0) where.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return (read, change, where);
    }
}
