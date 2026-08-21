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
///   1  the request failed, or the token is not permitted
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

        ApiResult result = command.IsPost
            ? await api.PostAsync(path)
            : await api.GetAsync(path);

        if (!result.Success)
        {
            Console.Error.WriteLine($"entkube: {result.Body}");
            return ExitFailure;
        }

        if (parsed.Flag("json"))
        {
            Console.Out.WriteLine(result.Body);
            return ExitOk;
        }

        int rows;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(result.Body);
            rows = TableWriter.Write(Console.Out, doc.RootElement, command.Columns, command.RowsProperty);
        }
        catch (JsonException)
        {
            // A non-JSON success body is unexpected but not worth failing over — print
            // it rather than hiding a response the operator might need to see.
            Console.Out.WriteLine(result.Body);
            return ExitOk;
        }

        // Lets a pipeline gate on "is anything wrong": `entkube drift --fail-on-results`
        // exits non-zero when a deployment has drifted, without parsing the output.
        return parsed.Flag("fail-on-results") && rows > 0 ? ExitResults : ExitOk;
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
              0 success · 1 request failed · 2 bad usage · 3 rows returned with --fail-on-results

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
