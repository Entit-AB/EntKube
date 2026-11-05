using System.Text.Json;
using EntKube.ApiClient;

namespace EntKube.Cli;

/// <summary>
/// The EntKube command-line client.
///
/// Talks to the same public /api/v1 surface as every other client, carrying an
/// ordinary scoped API token — there is no privileged path, so the CLI can do exactly
/// what the token permits and nothing more.
///
/// Exit codes are chosen for CI:
///   0  success
///   1  the request failed, the token is not permitted, or the API answered that PART of the
///      work failed (HTTP 207) — see <see cref="Render"/>
///   2  the command was invoked wrongly
///   3  --fail-on-results was given and rows came back
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;
    private const int ExitResults = 3;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage(Console.Out);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        (CliCommand? command, string[] remaining) = CliCommands.Resolve(args);
        if (command is null)
        {
            Console.Error.WriteLine($"entkube: unknown command '{string.Join(' ', args)}'.");
            PrintUsage(Console.Error);
            return ExitUsage;
        }

        CliArgs parsed = CliArgs.Parse(remaining);

        string? baseUrl = parsed.Optional("url") ?? Environment.GetEnvironmentVariable("ENTKUBE_URL");
        string? token = parsed.Optional("token") ?? Environment.GetEnvironmentVariable("ENTKUBE_TOKEN");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                "entkube: set ENTKUBE_URL and ENTKUBE_TOKEN (or pass --url and --token).\n"
                + "Create a token in EntKube under the tenant's API tokens tab.");
            return ExitUsage;
        }

        string path;
        try
        {
            path = command.BuildPath(parsed);
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"entkube {command.Usage}: {ex.Message}");
            return ExitUsage;
        }

        using EntKubeApiClient api = new(baseUrl, token);

        ApiResult result = command.Verb switch
        {
            CliVerb.Post => await api.PostAsync(path),
            CliVerb.Delete => await api.DeleteAsync(path),
            _ => await api.GetAsync(path),
        };

        return Render(result, parsed, command, Console.Out, Console.Error);
    }

    /// <summary>
    /// Turns one API answer into what the operator sees and the exit code CI reads.
    ///
    /// <para>⚠️ <b>Separated from <see cref="Main"/> so it can be tested.</b> <c>Main</c> reads
    /// environment variables and opens a real <c>HttpClient</c>; this method takes the answer and
    /// two writers, so every decision below can be asserted with a string. What is left in
    /// <c>Main</c> is one line of wiring, and that line is not covered.</para>
    ///
    /// <para>⚠️ <b>A 207 used to exit 0.</b> <c>POST /apps/{appId}/secrets/sync</c> answers
    /// 207 when any binding failed — the status exists to say so — and 207 is a 2xx, so the
    /// request counted as a success all the way out to the exit code. `entkube secrets sync` in a
    /// pipeline reported success while some of an app's credentials had not reached its cluster.
    /// The table showed the per-binding errors, so a person watching would see it; a pipeline
    /// cannot read a table.</para>
    /// </summary>
    internal static int Render(
        ApiResult result, CliArgs parsed, CliCommand command, TextWriter output, TextWriter error)
    {
        if (!result.Success)
        {
            error.WriteLine($"entkube: {result.Body}");
            return ExitFailure;
        }

        if (parsed.Flag("json"))
        {
            output.WriteLine(result.Body);

            // ⚠️ --json must not launder a partial failure. It changes the OUTPUT format; it is
            // not a request to be told less about what happened.
            return PartialFailure(result, error);
        }

        // A 204 has no body by design — `cost rates clear` is the one that answers it. Before
        // this was explicit it reached JsonDocument.Parse(""), threw, and was rescued by the
        // catch below, which printed an empty line and called it a success. Right outcome,
        // entirely by accident, and the comment in that catch said the body was "unexpected".
        if (string.IsNullOrWhiteSpace(result.Body))
        {
            return ExitOk;
        }

        int rows;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(result.Body);
            rows = TableWriter.Write(output, doc.RootElement, command.Columns, command.RowsProperty);
        }
        catch (JsonException)
        {
            // A non-empty, non-JSON success body is genuinely unexpected but not worth failing
            // over — print it rather than hiding a response the operator might need to see.
            output.WriteLine(result.Body);
            return PartialFailure(result, error);
        }

        if (PartialFailure(result, error) == ExitFailure)
        {
            // Reported before the gate below, because "part of this did not happen" outranks
            // "rows came back". The table has already been printed, so the operator sees which
            // part.
            return ExitFailure;
        }

        // Lets a pipeline gate on "is anything wrong": `entkube drift --fail-on-results`
        // exits non-zero when a deployment has drifted, without parsing the output.
        return parsed.Flag("fail-on-results") && rows > 0 ? ExitResults : ExitOk;
    }

    /// <summary>
    /// Exit 1 and a line on stderr when the API said part of the work failed; exit 0 otherwise.
    /// The advice names the body, because the body is where WHICH part failed is written.
    /// </summary>
    private static int PartialFailure(ApiResult result, TextWriter error)
    {
        if (!result.Partial) return ExitOk;

        error.WriteLine(
            "entkube: the API reported that part of this operation failed (HTTP 207). "
            + "The response above lists what did and did not succeed.");

        return ExitFailure;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            entkube — command-line client for EntKube

            Usage:
              entkube <command> [options]

            Connection (either form):
              ENTKUBE_URL / ENTKUBE_TOKEN environment variables
              --url <base-url> --token <ekp_...>

            Options:
              --json               Print the raw API response instead of a table
              --fail-on-results    Exit 3 when the command returns any rows (for CI gates)
              --help               Show this message

            Exit codes:
              0 success · 1 request failed or partly failed · 2 bad usage
              3 rows returned with --fail-on-results

            Commands:
            """);

        int width = CliCommands.All.Max(c => c.Usage.Length);
        foreach (CliCommand command in CliCommands.All)
        {
            output.WriteLine($"  {command.Usage.PadRight(width)}  {command.Description}");
        }

        output.WriteLine();
        output.WriteLine("Tokens are created in EntKube under the tenant's API tokens tab.");
        output.WriteLine("A command only works if the token carries the scope it needs; run");
        output.WriteLine("`entkube whoami` to see which scopes a token holds.");
    }
}
