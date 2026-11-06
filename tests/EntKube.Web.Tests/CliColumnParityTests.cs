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
/// <see cref="Unverifiable"/> below is the same debt seen from the client's side.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class CliColumnParityTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Commands whose route does not describe its success body, so there is no schema to check
    /// their columns against. Named, not counted.
    ///
    /// <para>This is debt in the same direction as the response-typing ratchet: every route that
    /// gains a response type removes a name from here. A name that is still listed once its route
    /// is typed fails <see cref="The_banked_list_names_only_commands_that_are_still_unverifiable"/>
    /// rather than sitting here being trusted.</para>
    ///
    /// <para>⚠️ <b>This was an integer, and an integer was the wrong shape.</b> Two changes in
    /// opposite directions net to zero: type one route in the same change that reverts another,
    /// and the count stays 13 while a command's columns silently stop being checked. The sibling
    /// ratchet already solved this — <c>CliEndpointParityTests.NoCommandYet</c> is a named set
    /// with a staleness assertion beside it — and this started life as a count anyway.</para>
    ///
    /// <para>⚠️ The thirteen this list began with were read by hand on 2026-11-05 and their columns matched the
    /// anonymous objects their handlers return. That is an observation on one day, <b>not an
    /// assertion</b>, and it stops being true the moment one of those handlers is edited. It is
    /// recorded against the names rather than against a number so a later reader can tell which
    /// commands it was about.</para>
    /// </summary>
    private static readonly HashSet<string> Unverifiable = new(StringComparer.Ordinal)
    {
        "advisor",
        "error-budgets",
        "incidents",
        "upgrades",
        "drift",
        "supply-chain",
        "rollouts",
        "dr",
        "secrets sync",
    };

    /// <summary>Commands that render a table — the only ones with a column list to check.</summary>
    private const int CommandsWithColumns = 19;

    /// <summary>
    /// Whether this command is banked as unverifiable. Exposed so
    /// <see cref="CliRenderedColumnTests"/> can assert that its rendered coverage keeps step with
    /// this class's schema coverage, rather than the two drifting apart silently.
    /// </summary>
    internal static bool BankedAsUnverifiable(string commandName) => Unverifiable.Contains(commandName);

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

        // ⚠️ Collected rather than assigned, because the loop below cannot assume one match.
        // Flattening {clusterId} and a future {slug} to the same "{}" makes two document paths
        // one key, and an unconditional assignment would take the LAST match — silently checking
        // the columns against another operation, or resetting a found body to null and reporting
        // the command as debt.
        List<IOpenApiSchema?> matched = [];

        foreach ((string path, IOpenApiPathItem item) in fixture.Document!.Paths ?? [])
        {
            string flat = Regex.Replace(path, @"\{[^}]+\}", "{}");

            foreach ((HttpMethod verb, OpenApiOperation operation) in item.Operations ?? [])
            {
                if ($"{verb.Method.ToUpperInvariant()} {flat}" != route) continue;

                // ⚠️ Every 2xx that carries a body, not the first one. POST
                // /apps/{appId}/secrets/sync answers the SAME body under 200 and 207, and once it
                // is typed it will declare both — at which point "whichever came first in the
                // dictionary" decides what the columns are checked against. Two DIFFERENT 2xx
                // bodies mean the question has no single answer, so that is reported rather than
                // resolved by picking one.
                List<IOpenApiSchema> bodies = SuccessBodies(operation);

                if (bodies.Count > 1)
                {
                    return new(command, route, null,
                        $"its route describes {bodies.Count} different 2xx bodies, so there is no "
                        + "single shape its columns can be checked against");
                }

                matched.Add(bodies.FirstOrDefault());
            }
        }

        if (matched.Count > 1)
        {
            return new(command, route, null,
                $"{matched.Count} documented operations flatten to '{route}', so which one this "
                + "command reaches is ambiguous");
        }

        IOpenApiSchema? body = matched.FirstOrDefault();

        if (body is null) return new(command, route, null, "its success body is not described");

        IOpenApiSchema rows = body;

        // An envelope wrapping a list. ⚠️ A RowsProperty the body does not carry is its own
        // defect: TableWriter prints "(no rows in response)" and reports zero rows, which also
        // takes --fail-on-results' exit code 3 off the table.
        if (command.RowsProperty is not null)
        {
            if (IsArray(body))
            {
                return new(command, route, null,
                    $"it names rows property '{command.RowsProperty}' but the body is an array");
            }

            if (body.Properties?.TryGetValue(command.RowsProperty, out IOpenApiSchema? wrapped) != true)
            {
                return new(command, route, null,
                    $"it names rows property '{command.RowsProperty}', which the body does not have");
            }

            // ⚠️ And it must BE an array. The assertion below is named after the array and did
            // not check for one: a rows property pointed at a scalar — `RowsProperty = "currency"`
            // — resolved, so nothing complained, while TableWriter found a string, fell through to
            // its key/value path, printed `SEK` and reported one row.
            if (!IsArray(wrapped))
            {
                return new(command, route, null,
                    $"its rows property '{command.RowsProperty}' is not an array, so TableWriter "
                    + "prints the value as a single row instead of a table");
            }

            rows = wrapped!;
        }

        // ⚠️ The comment here used to say "TableWriter renders both", which is true of the BODY
        // and false of the columns: for anything that is not an array TableWriter calls
        // WriteObject and never consults the column list at all. So a column list against an
        // object body is inert — printed nowhere, checked by nothing — which is the same defect
        // as a dashed column wearing a different hat.
        if (!IsArray(rows))
        {
            return new(command, route, null,
                "its route answers a single object, and TableWriter renders those as key/value "
                + "pairs without consulting the columns — so this column list is never used");
        }

        return new(command, route, rows.Items, null);
    }

    /// <summary>
    /// The distinct shapes an operation says it can answer with a 2xx.
    ///
    /// <para>⚠️ <b>Deduplicated by schema REFERENCE, not by object identity.</b> An operation that
    /// answers the same record under two statuses — which <c>POST /apps/{appId}/secrets/sync</c>
    /// does, 200 when every binding succeeded and 207 when one did not — produces two separate
    /// <c>OpenApiSchemaReference</c> instances pointing at one component. <c>Distinct()</c> over
    /// those compares references and calls them two bodies, so the caller would report "describes
    /// 2 different 2xx bodies" about an operation that describes one shape twice. That is a FALSE
    /// failure on the first route to use the pattern, and a reviewer clearing it would reach for
    /// the assertion rather than for this line.</para>
    ///
    /// <para>An inline schema has no reference to compare, so each stays distinct — two inline
    /// 2xx bodies genuinely are two shapes as far as anything here can tell, and
    /// <c>A_described_success_body_is_a_named_schema_the_document_defines</c> already refuses
    /// inline bodies outright.</para>
    /// </summary>
    private static List<IOpenApiSchema> SuccessBodies(OpenApiOperation operation)
    {
        List<IOpenApiSchema> bodies = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach ((string code, IOpenApiResponse response) in operation.Responses ?? [])
        {
            if (!code.StartsWith("2", StringComparison.Ordinal)) continue;

            foreach (OpenApiMediaType content in response.Content?.Values ?? [])
            {
                if (content.Schema is not IOpenApiSchema schema) continue;

                if (schema is OpenApiSchemaReference { Reference.Id: { Length: > 0 } id }
                    && !seen.Add(id))
                {
                    continue;
                }

                bodies.Add(schema);
            }
        }

        return bodies;
    }

    /// <summary>
    /// ⚠️ <c>HasFlag</c>, not <c>==</c>. <c>JsonSchemaType</c> is a <c>[Flags]</c> enum — Array is
    /// 64, Null is 1 — and OpenAPI 3.1 writes a nullable array as <c>type: ["array","null"]</c>,
    /// which is 65 and does not equal Array. With an exact comparison a nullable collection
    /// response would be handed to the column walk as the ARRAY schema rather than its element,
    /// and every column of a perfectly correct command would be reported missing. A ratchet's
    /// false failures are as expensive as its blind spots: the next reader loosens it.
    /// </summary>
    private static bool IsArray(IOpenApiSchema? schema) =>
        schema?.Type?.HasFlag(JsonSchemaType.Array) == true;

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
    /// list is how much of the CLI's rendering is still unverifiable.
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

        // ⚠️ Set equality, both directions, not a count. A count is satisfied by a change that
        // types one route and reverts another, and the command that stopped being checked is
        // invisible in the arithmetic.
        unverifiable.Select(c => c.Name).Should().BeEquivalentTo(
            Unverifiable,
            "REMOVE a name from Unverifiable when its route gains a response type — that is the "
            + "point of typing them. A name appearing here that is not in the set means a command "
            + "was added against a route whose answer nothing can describe.");
    }

    /// <summary>
    /// The banked list must not outlive its entries — the failure mode of every allow-list in this
    /// repo. A name left here after its route is typed is a command nobody checks, sitting in a
    /// list that says it cannot be checked.
    ///
    /// <para>This is the half a bare count could not express at all, and it is why the sibling
    /// ratchet's <c>NoCommandYet</c> has always been a named set.</para>
    /// </summary>
    [Fact]
    public void The_banked_list_names_only_commands_that_are_still_unverifiable()
    {
        List<string> nowCheckable =
            [.. CliCommands.All
                .Where(c => c.Columns.Length > 0 && Unverifiable.Contains(string.Join(' ', c.Path)))
                .Select(Resolve)
                .Where(c => c.Row is not null)
                .Select(c => c.Name)];

        nowCheckable.Should().BeEmpty(
            "these routes now describe their success body, so their columns ARE checked — delete "
            + "them from Unverifiable rather than leaving a list that lies about its own coverage: "
            + string.Join(", ", nowCheckable));

        List<string> notCommands =
            [.. Unverifiable.Except(CliCommands.All.Select(c => string.Join(' ', c.Path)))];

        notCommands.Should().BeEmpty(
            "and these are not commands at all any more: " + string.Join(", ", notCommands));
    }

    /// <summary>
    /// ⚠️ Pins the flag arithmetic <see cref="IsArray"/> exists for, because no route answers a
    /// nullable collection today — so nothing else in the suite would notice the predicate going
    /// back to <c>==</c>, and the damage would be a FALSE failure on the first route that does.
    ///
    /// <para><c>JsonSchemaType</c> is <c>[Flags]</c>: Array is 64 and Null is 1, so OpenAPI 3.1's
    /// <c>type: ["array","null"]</c> is 65. An exact comparison against Array is false for it.</para>
    /// </summary>
    private static IOpenApiResponse JsonResponse(IOpenApiSchema schema) =>
        new OpenApiResponse
        {
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new() { Schema = schema },
            },
        };

    /// <summary>
    /// ⚠️ The same record under two statuses is ONE shape.
    ///
    /// <para>No route declares two 2xx bodies today, so nothing else in the suite would catch
    /// <see cref="SuccessBodies"/> regressing to <c>Distinct()</c> — and the first route to use the
    /// pattern is already known: <c>POST /apps/{appId}/secrets/sync</c> answers the same body under
    /// 200 and 207. Reference equality would call that two bodies and refuse to check a correct
    /// command's columns.</para>
    /// </summary>
    [Fact]
    public void The_same_schema_under_two_statuses_is_one_body()
    {
        OpenApiOperation twoStatusesOneShape = new()
        {
            Responses = new OpenApiResponses
            {
                ["200"] = JsonResponse(new OpenApiSchemaReference("AppSecretSyncResponse")),
                ["207"] = JsonResponse(new OpenApiSchemaReference("AppSecretSyncResponse")),
            },
        };

        SuccessBodies(twoStatusesOneShape).Should().HaveCount(
            1,
            "two references to one component are two objects and one shape, and the caller reports "
            + "a count greater than 1 as 'no single shape its columns can be checked against'");

        OpenApiOperation genuinelyTwoShapes = new()
        {
            Responses = new OpenApiResponses
            {
                ["200"] = JsonResponse(new OpenApiSchemaReference("AppSecretSyncResponse")),
                ["202"] = JsonResponse(new OpenApiSchemaReference("OkResponse")),
            },
        };

        SuccessBodies(genuinelyTwoShapes).Should().HaveCount(
            2, "different components are different shapes and the question has no single answer");

        // A 4xx carries a body too, and it is not what the columns render.
        OpenApiOperation successPlusProblem = new()
        {
            Responses = new OpenApiResponses
            {
                ["200"] = JsonResponse(new OpenApiSchemaReference("OkResponse")),
                ["404"] = JsonResponse(new OpenApiSchemaReference("ProblemDetails")),
            },
        };

        SuccessBodies(successPlusProblem).Should().HaveCount(1, "only 2xx answers carry rows");
    }

    [Fact]
    public void A_nullable_array_is_still_an_array()
    {
        IsArray(new OpenApiSchema { Type = JsonSchemaType.Array }).Should().BeTrue();

        IsArray(new OpenApiSchema { Type = JsonSchemaType.Array | JsonSchemaType.Null })
            .Should().BeTrue("a nullable array is the shape an exact == comparison misses");

        IsArray(new OpenApiSchema { Type = JsonSchemaType.Object }).Should().BeFalse();
        IsArray(new OpenApiSchema { Type = null }).Should().BeFalse("an untyped schema is not an array");
        IsArray(null).Should().BeFalse();
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
