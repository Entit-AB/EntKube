using EntKube.Web.Services;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The argument vectors EntKube hands <c>kubectl exec</c>, and which of them still carry a secret.
///
/// <para><b>Why this exists.</b> Two database admin paths put a password on the command line.
/// <c>ps</c> and <c>/proc/&lt;pid&gt;/cmdline</c> expose it on the EntKube host for the life of
/// the call, and process command lines are routinely collected by security and observability
/// agents — EntKube ships its own — so the value can leave the host and be indexed. The repository
/// already knows this: <c>SyncComponentSecretsAsync</c> carries a <c>"NEVER --from-literal"</c>
/// comment whose three reasons are all about the argument list.</para>
///
/// <para>⚠️ <b>The interface's own doc comment claimed the opposite.</b> It said the password
/// "never appears in the process argument list" while delivering it as <c>env PGPASSWORD=…</c> —
/// which is an argument. A false security comment is worse than none, because it answers the
/// question a reader came to ask. It is corrected; this file is what would have caught it.</para>
///
/// <para><b>What this file is for.</b> Fixing the mechanism means getting a secret to an in-pod
/// process without an argument, which changes how admin scripts execute and cannot be verified
/// without a cluster. Until that is decided, the exposure is at least <em>bounded</em>: every
/// builder is probed with a sentinel, and one that leaks must be named in
/// <see cref="StillCarryASecret"/> with a reason. A new one fails instead of joining quietly.</para>
/// </summary>
public class KubectlExecArgvTests
{
    private const string Sentinel = "s3nt1nel-PASSWORD-do-not-leak";

    /// <summary>
    /// The invocations whose argument vector still contains a secret value, each with why it has
    /// not been fixed. ⚠️ Named, not counted: two changes in opposite directions net to zero.
    /// </summary>
    private static readonly Dictionary<string, string> StillCarryASecret = new(StringComparer.Ordinal)
    {
        ["psql"] =
            "delivered as `env PGPASSWORD=…`, where env is the in-pod command, so the value is an "
            + "argument of both kubectl and env. psql reads no password file we control and its "
            + "own options are worse; the fix needs a different delivery mechanism.",
        ["mongosh"] =
            "mongosh takes --password as an argument and reads no password environment variable. "
            + "Its only argument-free route is its interactive prompt, which would share stdin "
            + "with the script this call is streaming.",
    };

    /// <summary>
    /// ⚠️ The probe. Each builder is given the sentinel as its password; whether the sentinel
    /// comes back in the vector is the whole question, and the answer must match the banked set.
    /// </summary>
    [Theory]
    [InlineData("psql")]
    [InlineData("mongosh")]
    [InlineData("pod-command-without-env")]
    public void An_invocation_leaks_a_secret_only_if_it_is_banked_as_doing_so(string invocation)
    {
        IReadOnlyList<string> args = Build(invocation);

        bool leaks = args.Any(a => a.Contains(Sentinel, StringComparison.Ordinal));

        if (StillCarryASecret.TryGetValue(invocation, out string? why))
        {
            leaks.Should().BeTrue(
                "`{0}` is banked as still carrying a secret on its argument vector ({1}) — if it "
                + "has been fixed, REMOVE it from StillCarryASecret rather than leaving a stale "
                + "exemption that would hide a regression",
                invocation, why);
        }
        else
        {
            leaks.Should().BeFalse(
                "`{0}` is not banked as carrying a secret, so nothing it builds may contain one. "
                + "If a secret genuinely has to go on this vector, add it to StillCarryASecret "
                + "WITH the reason — do not delete this assertion",
                invocation);
        }
    }

    /// <summary>
    /// ⚠️ The staleness half, which is what stops the set above from being trusted forever: a
    /// name listed here must correspond to a builder this file actually probes, or the exemption
    /// is about code that no longer exists.
    /// </summary>
    [Fact]
    public void Every_banked_name_is_one_this_file_probes()
    {
        string[] probed = ["psql", "mongosh", "pod-command-without-env"];

        StillCarryASecret.Keys.Should().BeSubsetOf(
            probed, "an exemption for an unprobed builder asserts nothing");

        StillCarryASecret.Values.Should().OnlyContain(
            why => why.Length > 40,
            "the reason is the point — a bare name is a TODO, and this file is meant to carry why");
    }

    /// <summary>
    /// ⚠️ <b>The defect this change actually fixes.</b> mongosh's credentials were interpolated
    /// into an <c>Arguments</c> STRING, which .NET splits on whitespace — so a password containing
    /// a space arrived as two arguments and the real password was whatever came first. It could
    /// not fire today, because <c>GeneratePassword</c> returns base64 with <c>+</c> and <c>/</c>
    /// substituted, i.e. <c>[A-Za-z0-9xy]{32}</c>; it was one generator change, or one
    /// operator-settable field, away.
    ///
    /// <para>⚠️ And the failure mode was the bad kind: a mis-split password does not necessarily
    /// error. It can authenticate as nobody, or — with the fragments landing on other flags — run
    /// the script against different credentials than intended. The repository has seen this shape
    /// before: a Secret that "applies cleanly with the wrong password".</para>
    /// </summary>
    [Theory]
    [InlineData("has a space")]
    [InlineData("has\"a\"quote")]
    [InlineData("has'a'single")]
    [InlineData("--looks-like-a-flag")]
    [InlineData("trailing ")]
    public void A_mongosh_password_is_one_argument_however_it_is_spelled(string password)
    {
        IReadOnlyList<string> args = KubernetesClientFactory.MongoshExecArgs(
            "rs0-0", "data", "/tmp/kc", "admin", password);

        int at = args.ToList().IndexOf("--password");
        at.Should().BeGreaterThanOrEqualTo(0, "the flag must be present when credentials are given");

        args[at + 1].Should().Be(
            password,
            "the whole value, as ONE element — the previous string form would have split this");

        args.Count(a => a == password).Should().Be(1, "and it appears exactly once");
    }

    /// <summary>
    /// ⚠️ Without credentials neither flag appears at all — so "always pass --username" is not a
    /// silent way to satisfy the fact above, and an unauthenticated replica set is still reachable.
    /// </summary>
    [Fact]
    public void Mongosh_omits_the_credential_flags_entirely_when_there_are_none()
    {
        IReadOnlyList<string> args =
            KubernetesClientFactory.MongoshExecArgs("rs0-0", "data", "/tmp/kc", null, null);

        args.Should().NotContain("--username").And.NotContain("--password");
        args.Should().EndWith(["--quiet"], "the quiet flag is unconditional, as it was before");
        args.Should().ContainInOrder("exec", "-i", "rs0-0", "-n", "data", "--", "mongosh");
    }

    /// <summary>
    /// ⚠️ The pod-command builder leaks only because a CALLER hands it a secret envVar — the
    /// builder itself has no notion of one. Both halves are asserted, because "it never leaks" and
    /// "it leaks whatever you give it" are different claims and only the second is true.
    /// </summary>
    [Fact]
    public void A_pod_command_carries_whatever_env_vars_its_caller_supplies()
    {
        IReadOnlyList<string> clean = KubernetesClientFactory.PodCommandArgs(
            "db-0", "data", "/tmp/kc", ["pg_dump", "-Fc"], envVars: null, verbose: false);

        clean.Should().NotContain("env", "no env prefix when there are no variables");
        clean.Should().ContainInOrder("exec", "db-0", "-n", "data", "--", "pg_dump", "-Fc");

        // ⚠️ EMPTY and null are different inputs, and only this case pins the `Count: > 0` part of
        // the guard. A mutation relaxing it to `is not null` passed every other fact here: an
        // empty dictionary then prepends a bare `env`, which happens to be harmless — `env` with
        // no assignments just execs its argument — but the assertion above CLAIMED to cover "no
        // variables" while only ever testing null. Found by the mutation, not by reading.
        IReadOnlyList<string> empty = KubernetesClientFactory.PodCommandArgs(
            "db-0", "data", "/tmp/kc", ["pg_dump", "-Fc"],
            envVars: new Dictionary<string, string>(), verbose: false);

        empty.Should().NotContain(
            "env", "an empty dictionary is 'no variables' too, not 'variables I cannot name'");
        empty.Should().BeEquivalentTo(
            clean, "and an empty dictionary must build exactly what null builds");

        IReadOnlyList<string> withSecret = KubernetesClientFactory.PodCommandArgs(
            "db-0", "data", "/tmp/kc", ["pg_dump"],
            envVars: new Dictionary<string, string> { ["PGPASSWORD"] = Sentinel },
            verbose: false);

        withSecret.Should().Contain(
            $"PGPASSWORD={Sentinel}",
            "which is the pg_dump path's live exposure — stated here rather than left implicit");
    }

    /// <summary>
    /// The psql vector's shape, including the TCP forcing that makes pg_hba host rules apply
    /// rather than peer rules. ⚠️ Asserted because it is a one-line behaviour a reader could take
    /// for redundant: dropping <c>-h 127.0.0.1</c> switches which authentication rules the
    /// connection is subject to, and the password would then be unnecessary — and ignored.
    /// </summary>
    [Fact]
    public void Psql_connects_over_tcp_so_host_authentication_rules_apply()
    {
        IReadOnlyList<string> args =
            KubernetesClientFactory.PsqlExecArgs("db-0", "data", "/tmp/kc", "postgres", Sentinel);

        args.Should().ContainInOrder("psql", "-U", "postgres", "-h", "127.0.0.1", "-t");
        args.Should().ContainInOrder("--", "env", $"PGPASSWORD={Sentinel}", "psql");
    }

    [Fact]
    public void Psql_omits_the_env_prefix_when_there_is_no_password()
    {
        IReadOnlyList<string> args =
            KubernetesClientFactory.PsqlExecArgs("db-0", "data", "/tmp/kc", "postgres", null);

        args.Should().NotContain("env");
        args.Should().ContainInOrder("--", "psql", "-U", "postgres");
    }

    private static IReadOnlyList<string> Build(string invocation) => invocation switch
    {
        "psql" => KubernetesClientFactory.PsqlExecArgs("db-0", "data", "/tmp/kc", "postgres", Sentinel),
        "mongosh" => KubernetesClientFactory.MongoshExecArgs("rs0-0", "data", "/tmp/kc", "admin", Sentinel),
        "pod-command-without-env" => KubernetesClientFactory.PodCommandArgs(
            "db-0", "data", "/tmp/kc", ["pg_dump", "-Fc"], envVars: null, verbose: false),
        _ => throw new ArgumentOutOfRangeException(nameof(invocation), invocation, "unprobed"),
    };
}
