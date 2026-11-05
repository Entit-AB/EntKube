using System.Net;
using System.Text;
using EntKube.ApiClient;
using EntKube.Cli;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// A partly-failed operation reaches the exit code.
///
/// <para><b>What was wrong.</b> <c>POST /api/v1/apps/{appId}/secrets/sync</c> answers
/// <b>207 Multi-Status</b> when any binding failed — the status exists to say exactly that, and
/// the body carries <c>failed: true</c> with a per-binding <c>error</c>. But 207 is a 2xx, so
/// <c>HttpResponseMessage.IsSuccessStatusCode</c> is true, <c>ApiResult.Success</c> was true, and
/// the CLI printed its table and exited <b>0</b>. `entkube secrets sync` in a pipeline reported
/// success while some of an app's credentials had never reached its cluster.</para>
///
/// <para>⚠️ <b>A person watching would have seen it; a pipeline cannot.</b> The table's columns
/// are <c>kind</c>, <c>secret</c> and <c>error</c>, so the failures were on screen. The defect is
/// entirely in the machine-readable half of the answer — which is the half CI gates on, and the
/// half nobody was asserting.</para>
///
/// <para>This is the same shape as the dead CLI columns: the API was made to answer precisely,
/// and the client did not listen. The server side of that 207 is deliberate and stays as it
/// is.</para>
/// </summary>
public class CliPartialFailureTests
{
    /// <summary>The body the secrets-sync route sends with its 207, shortened but shaped the same.</summary>
    private const string PartialBody =
        """
        {"failed":true,"output":"2 of 3 delivered","bindings":[
        {"kind":"Database","secret":"billing-db","error":null},
        {"kind":"OAuthClient","secret":"billing-sso","error":"the namespace does not exist"}],
        "unlistedFamilies":[],"fatalError":null}
        """;

    private static CliCommand SecretsSync() =>
        CliCommands.All.Single(c => c.Path.SequenceEqual(new[] { "secrets", "sync" }));

    private sealed record Rendered(int Exit, string Out, string Err);

    private static Rendered Render(ApiResult result, params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();

        // ⚠️ Fully qualified. `Program` unqualified in this namespace resolves to the WEB
        // app's top-level Program — the one WebApplicationFactory<Program> uses elsewhere in
        // this suite — because the global namespace is searched before the usings.
        int exit = EntKube.Cli.Program.Render(
            result, CliArgs.Parse(args), SecretsSync(), output, error);

        return new(exit, output.ToString(), error.ToString());
    }

    /// <summary>
    /// ⚠️ The measure. Every assertion below is about the secrets-sync command's rendering, and
    /// each would still pass if that command had quietly lost the columns that show WHICH binding
    /// failed — the exit code would be right and the output empty.
    /// </summary>
    [Fact]
    public void The_command_under_test_still_renders_the_bindings_and_their_errors()
    {
        CliCommand command = SecretsSync();

        command.RowsProperty.Should().Be("bindings");
        command.Columns.Select(c => c.Path).Should().Contain("error",
            "the exit code says that something failed; this column is what says which thing");
    }

    [Fact]
    public void A_207_exits_one_and_says_so_while_still_printing_the_table()
    {
        Rendered r = Render(new ApiResult(true, PartialBody, 207));

        r.Exit.Should().Be(1, "a pipeline reads the exit code and nothing else");
        r.Err.Should().Contain("part of this operation failed");
        r.Err.Should().Contain("207");

        // The table is still printed, because the exit code says THAT something failed and only
        // the table says WHICH.
        r.Out.Should().Contain("billing-sso");
        r.Out.Should().Contain("the namespace does not exist");
    }

    /// <summary>
    /// ⚠️ The other direction, which is what stops the fix from being "always exit 1". A fully
    /// successful sync sends the SAME body shape under a 200.
    /// </summary>
    [Fact]
    public void The_same_body_under_a_200_exits_zero_and_says_nothing_on_stderr()
    {
        string allDelivered = PartialBody.Replace("\"failed\":true", "\"failed\":false");

        Rendered r = Render(new ApiResult(true, allDelivered, 200));

        r.Exit.Should().Be(0);
        r.Err.Should().BeEmpty();
        r.Out.Should().Contain("billing-db");
    }

    /// <summary>
    /// ⚠️ <c>--json</c> changes the output FORMAT. It is not a request to be told less about what
    /// happened, and it returned early before the fix — so the one flag a pipeline is most likely
    /// to use was the one that laundered the failure.
    /// </summary>
    [Fact]
    public void Json_mode_prints_the_body_verbatim_and_still_exits_one()
    {
        Rendered r = Render(new ApiResult(true, PartialBody, 207), "--json");

        r.Exit.Should().Be(1);
        r.Out.Trim().Should().Be(PartialBody.Trim(), "verbatim, so jq sees exactly what the API sent");
        r.Err.Should().Contain("part of this operation failed");
    }

    /// <summary>
    /// A 207 outranks <c>--fail-on-results</c>: both are non-zero, but exit 3 means "rows came
    /// back, as you asked to be told", and reporting that for a run where work did not happen
    /// would send a pipeline down the wrong branch.
    /// </summary>
    [Fact]
    public void A_partial_failure_outranks_the_fail_on_results_gate()
    {
        Render(new ApiResult(true, PartialBody, 207), "--fail-on-results").Exit.Should().Be(1);

        // And the gate still works on its own.
        string allDelivered = PartialBody.Replace("\"failed\":true", "\"failed\":false");
        Render(new ApiResult(true, allDelivered, 200), "--fail-on-results").Exit.Should().Be(3);
    }

    /// <summary>
    /// A 204 has no body. <c>cost rates clear</c> is the command that answers one, and before this
    /// was explicit the empty string reached <c>JsonDocument.Parse</c>, threw, and was rescued by
    /// the catch for non-JSON bodies — which printed a blank line and returned success. The
    /// outcome was right entirely by accident, and the catch's comment called the body
    /// "unexpected" when for that command it is the contract.
    /// </summary>
    [Fact]
    public void A_204_with_no_body_exits_zero_and_prints_nothing()
    {
        Rendered r = Render(new ApiResult(true, "", 204));

        r.Exit.Should().Be(0);
        r.Out.Should().BeEmpty("a blank line is what the accidental path produced");
        r.Err.Should().BeEmpty();
    }

    // ── The client, through its own transport ──

    private sealed class Answers(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// ⚠️ Through a real <see cref="EntKubeApiClient"/>, not by constructing an
    /// <see cref="ApiResult"/> by hand. The whole defect was that <c>IsSuccessStatusCode</c> is
    /// true for a 207, so a test that asserts <c>new ApiResult(true, …, 207).Partial</c> asserts
    /// my own arithmetic and never touches the line that made the mistake.
    /// </summary>
    [Fact]
    public async Task The_client_reports_a_207_as_successful_AND_partial()
    {
        using EntKubeApiClient client = new(
            "https://entkube.example", "ekp_x", transport: new Answers((HttpStatusCode)207, PartialBody));

        ApiResult result = await client.PostAsync("/api/v1/apps/" + Guid.NewGuid() + "/secrets/sync");

        result.Success.Should().BeTrue("the request was answered, and the body is the answer");
        result.Partial.Should().BeTrue();
        result.StatusCode.Should().Be(207);
        result.Body.Should().Be(PartialBody, "the body is passed through unprefixed on a 2xx");
    }

    [Fact]
    public async Task A_200_is_not_partial_and_a_503_is_a_failure_with_advice()
    {
        using EntKubeApiClient ok = new(
            "https://entkube.example", "ekp_x", transport: new Answers(HttpStatusCode.OK, "{}"));

        ApiResult fine = await ok.GetAsync("/api/v1/whoami");
        fine.Success.Should().BeTrue();
        fine.Partial.Should().BeFalse();

        using EntKubeApiClient notYet = new(
            "https://entkube.example", "ekp_x",
            transport: new Answers(HttpStatusCode.ServiceUnavailable, "{\"detail\":\"no sweep yet\"}"));

        ApiResult sweep = await notYet.GetAsync("/api/v1/drift");
        sweep.Success.Should().BeFalse();
        sweep.Partial.Should().BeFalse("503 is not a partial result; nothing happened at all");
        sweep.Body.Should().Contain("has not completed a background sweep");
    }
}
