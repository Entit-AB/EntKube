using System.Text.RegularExpressions;
using EntKube.Cli;
using FluentAssertions;
using Microsoft.OpenApi;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Every column the CLI renders names a field the API actually sends.
///
/// <para><b>What was wrong.</b> <c>TableWriter.Cell</c> resolves a column's dotted path against
/// the row and returns <c>"-"</c> for anything absent — deliberately, because a null and a missing
/// field mean the same thing to someone reading a table. The consequence is that a column naming a
/// field the server has NEVER sent renders as a column of dashes, forever, and nothing fails.
/// <c>entkube cost rates list</c> asked for <c>clusterName</c>, <c>cpuCorePerMonth</c>,
/// <c>memoryGiBPerMonth</c> and <c>storageGiBPerMonth</c>; the endpoint sends none of those four,
/// so four of its six columns were dashes. <c>entkube cost months</c> asked for <c>clusters</c>
/// and <c>total</c>, and the endpoint sends neither.</para>
///
/// <para>⚠️ <b><c>CliEndpointParityTests</c> could not have caught this.</b> It compares which
/// ROUTES the CLI can reach — and both of these commands reach their route correctly, send a
/// valid request and get a 200 back. The defect is entirely in what the CLI does with the answer.
/// A route with a command that renders nothing is as useless to an operator as a route with no
/// command, and harder to notice, because it looks like an empty cluster rather than a broken
/// client.</para>
///
/// <para>⚠️ <b>Why this reads the generated document.</b> The oracle has to be what the server
/// sends, and the only thing that knows that for certain is the schema the framework derives from
/// the handler's own return type. A list of expected field names written in this file would be a
/// third copy of the contract, free to drift from both of the other two. The cost is that this
/// ratchet can only see routes whose success body IS described — which is exactly the debt
/// <c>ApiDocumentedResponsesTests.UndescribedSuccessBodies</c> counts, and
/// <see cref="Uncheckable"/> below is the same debt seen from the client's side.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class CliColumnParityTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Commands whose route does not describe its success body, so there is no schema to check
    /// their columns against.
    ///
    /// <para>This is a ratchet in the same direction as the response-typing one: it may only go
    /// DOWN, and every route that gains a response type moves a command from here into the checked
    /// set. It going UP means a command was added against a route nothing can describe.</para>
    ///
    /// <para>⚠️ <b>All thirteen were read by hand on 2026-11-05 and their columns matched the
    /// anonymous objects their handlers return.</b> That is an observation on one day, not an
    /// assertion — it is recorded here so this number is not mistaken for thirteen suspected
    /// defects, and it stops being true the moment one of those handlers is edited. Only the
    /// routes counted by the test below are actually checked on every run.</para>
    /// </summary>
    private const int Uncheckable = 13;

    /// <summary>Commands that render a table — the only ones with a column list to check.</summary>
    private const int CommandsWithColumns = 17;

    private sealed record Checked(
        CliCommand Command,
        string Route,
        IOpenApiSchema? Row,
        string? Problem)
    {
        public string Name => string.Join(' ', Command.Path);
    }

    /// <summary>
    /// ⚠️ The route is taken by RUNNING <c>BuildPath</c> with <c>{}</c> as every argument value,
    /// which is how <c>CliEndpointParityTests</c> does it and for the same reason: a regex over the
    /// source found three of sixteen paths, because interpolation holes contain quotes.
    /// </summary>
    private static string? RouteOf(CliCommand command)
    {
        string built;
        try
        {
            built = command.BuildPath(CliArgs.Parse(
            [
                "--cluster", "{}", "--id", "{}", "--app", "{}",
                "--workload", "{}", "--environment", "{}",
            ]));
        }
        catch (CliUsageException)
        {
            return null;
        }

        string verb = command.Verb switch
        {
            CliVerb.Get => "GET",
            CliVerb.Post => "POST",
            CliVerb.Delete => "DELETE",
            _ => throw new InvalidOperationException(
                $"CliVerb.{command.Verb} is not mapped here; add it rather than letting it read "
                + "as a GET"),
        };

        return $"{verb} {built.Split('?')[0]}";
    }

    /// <summary>The schema of one row of this command's table, or the reason there is none.</summary>
    private Checked Resolve(CliCommand command)
    {
        string? route = RouteOf(command);
        if (route is null) return new(command, "", null, "its stub arguments are not enough to build a path");

        IOpenApiSchema? body = null;

        foreach ((string path, IOpenApiPathItem item) in fixture.Document!.Paths ?? [])
        {
            string flat = Regex.Replace(path, @"\{[^}]+\}", "{}");

            foreach ((HttpMethod verb, OpenApiOperation operation) in item.Operations ?? [])
            {
                if ($"{verb.Method.ToUpperInvariant()} {flat}" != route) continue;

                body = operation.Responses?
                    .Where(r => r.Key.StartsWith("2", StringComparison.Ordinal))
                    .Select(r => r.Value.Content?.Values.Select(c => c.Schema).FirstOrDefault(s => s is not null))
                    .FirstOrDefault(s => s is not null);
            }
        }

        if (body is null) return new(command, route, null, "its success body is not described");

        IOpenApiSchema rows = body;

        // An envelope wrapping a list. ⚠️ A RowsProperty the body does not carry is its own
        // defect: TableWriter prints "(no rows in response)" and reports zero rows, which also
        // takes --fail-on-results' exit code 3 off the table.
        if (command.RowsProperty is not null)
        {
            if (body.Type == JsonSchemaType.Array)
            {
                return new(command, route, null,
                    $"it names rows property '{command.RowsProperty}' but the body is an array");
            }

            if (body.Properties?.TryGetValue(command.RowsProperty, out IOpenApiSchema? wrapped) != true)
            {
                return new(command, route, null,
                    $"it names rows property '{command.RowsProperty}', which the body does not have");
            }

            rows = wrapped!;
        }

        // The rows may be the array itself or, for a single-object endpoint like whoami, the
        // object — TableWriter renders both.
        return new(command, route, rows.Type == JsonSchemaType.Array ? rows.Items : rows, null);
    }

    private static string? Missing(IOpenApiSchema row, string columnPath)
    {
        IOpenApiSchema current = row;

        foreach (string segment in columnPath.Split('.'))
        {
            if (current.Properties?.TryGetValue(segment, out IOpenApiSchema? next) != true)
            {
                return segment;
            }

            current = next!;
        }

        return null;
    }

    /// <summary>
    /// ⚠️ The measure. Everything below walks a set that could silently go empty — if the route
    /// matching stopped resolving, or if <c>BuildPath</c> threw for every command, each assertion
    /// would pass over nothing and report a healthy client.
    /// </summary>
    [Fact]
    public void The_commands_and_their_schemas_were_actually_found()
    {
        fixture.GenerationFailure.Should().BeNull();

        List<CliCommand> withColumns = [.. CliCommands.All.Where(c => c.Columns.Length > 0)];

        // Projected to names before counting, so a failure prints a command list rather than
        // fifteen reflected CliCommand objects with their delegates.
        withColumns.Select(c => string.Join(' ', c.Path)).Should().HaveCount(
            CommandsWithColumns,
            "pinned rather than merely non-empty: a command that stops rendering a table leaves "
            + "this ratchet without noticing, and so does one added without columns");

        List<Checked> resolved = [.. withColumns.Select(Resolve)];

        resolved.Should().OnlyContain(
            c => c.Route.Length > 0,
            "a command whose path could not be built contributes nothing to the check below");

        resolved.Count(c => c.Row is not null).Should().BeGreaterThan(
            0, "otherwise every column trivially has nothing to be checked against");
    }

    /// <summary>
    /// ⚠️ The debt, banked and pointed the same way as the response-typing ratchet. A command
    /// whose route has no described success body cannot have its columns checked at all, so this
    /// number is how much of the CLI's rendering is still unverifiable.
    /// </summary>
    [Fact]
    public void The_commands_whose_columns_cannot_be_checked_are_the_ones_still_banked()
    {
        List<Checked> unverifiable =
            [.. CliCommands.All
                .Where(c => c.Columns.Length > 0)
                .Select(Resolve)
                .Where(c => c.Row is null)];

        unverifiable.Should().OnlyContain(
            c => c.Problem == "its success body is not described",
            "a command that cannot be checked for any OTHER reason is a defect, not debt: "
            + string.Join("; ", unverifiable.Select(c => $"{c.Name}: {c.Problem}")));

        unverifiable.Should().HaveCount(
            Uncheckable,
            "LOWER this when a route gains a response type — that is the point of typing them. If "
            + "it has risen, a command was added against a route whose answer nothing can "
            + "describe. Still unverifiable: {0}",
            string.Join("; ", unverifiable.Select(c => c.Name)));
    }

    [Fact]
    public void Every_column_names_a_field_the_api_sends()
    {
        List<string> broken = [];

        foreach (Checked c in CliCommands.All
            .Where(command => command.Columns.Length > 0)
            .Select(Resolve)
            .Where(c => c.Row is not null))
        {
            foreach ((string header, string path) in c.Command.Columns)
            {
                if (Missing(c.Row!, path) is string segment)
                {
                    broken.Add($"`{c.Name}` column {header} reads '{path}' but "
                        + $"{c.Route} sends no '{segment}'");
                }
            }
        }

        broken.Should().BeEmpty(
            "an absent field renders as '-', so these columns are dashes in every row and nothing "
            + "says so:\n  " + string.Join("\n  ", broken));
    }

    /// <summary>
    /// The rows property, checked separately because its failure is a different one: a column that
    /// does not resolve loses one column, while a rows property that does not resolve loses the
    /// whole table AND reports zero rows, which is what <c>--fail-on-results</c> reads for its
    /// exit code.
    /// </summary>
    [Fact]
    public void Every_rows_property_names_the_array_the_api_wraps_its_list_in()
    {
        List<string> broken = [];

        foreach (CliCommand command in CliCommands.All.Where(c => c.RowsProperty is not null))
        {
            Checked c = Resolve(command);

            if (c.Problem is string problem && problem.Contains("rows property", StringComparison.Ordinal))
            {
                broken.Add($"`{c.Name}`: {problem}");
            }
        }

        broken.Should().BeEmpty(
            "TableWriter prints '(no rows in response)' and reports zero rows for each of these:\n  "
            + string.Join("\n  ", broken));
    }
}
