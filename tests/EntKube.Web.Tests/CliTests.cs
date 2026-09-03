using System.Text;
using System.Text.Json;
using EntKube.Cli;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the CLI's argument parsing, command resolution and table rendering —
/// the parts that decide whether an operator's invocation does what they meant.
/// </summary>
public class CliTests
{
    // ── Argument parsing ──

    [Fact]
    public void Parses_an_option_and_its_value()
    {
        CliArgs.Parse(["--cluster", "abc"]).Required("cluster").Should().Be("abc");
    }

    [Fact]
    public void Accepts_the_equals_form_as_well()
    {
        // Both forms are common enough that rejecting either would just look broken.
        CliArgs.Parse(["--cluster=abc"]).Required("cluster").Should().Be("abc");
    }

    [Fact]
    public void An_option_followed_by_another_option_is_a_flag_not_a_value()
    {
        CliArgs args = CliArgs.Parse(["--open", "--json"]);

        args.Flag("open").Should().BeTrue();
        args.IsBareFlag("open").Should().BeTrue();
        args.Optional("open").Should().BeNull();
        args.Flag("json").Should().BeTrue();
    }

    [Fact]
    public void A_trailing_flag_is_recognised()
    {
        CliArgs.Parse(["--id", "x", "--json"]).Flag("json").Should().BeTrue();
    }

    [Fact]
    public void Option_names_are_case_insensitive()
    {
        CliArgs.Parse(["--Cluster", "abc"]).Required("cluster").Should().Be("abc");
    }

    [Fact]
    public void Positional_arguments_are_kept_separate_from_options()
    {
        CliArgs.Parse(["thing", "--id", "x", "other"]).Positional
            .Should().BeEquivalentTo(["thing", "other"]);
    }

    [Fact]
    public void A_missing_required_option_is_a_usage_error_not_a_failed_request()
    {
        // Catching it here means the operator is told what they forgot, rather than
        // getting a 404 from the API for a URL with a hole in it.
        Action act = () => CliArgs.Parse([]).Required("id");
        act.Should().Throw<CliUsageException>().WithMessage("*--id is required*");
    }

    [Fact]
    public void An_option_given_with_an_empty_value_still_counts_as_missing()
    {
        Action act = () => CliArgs.Parse(["--id="]).Required("id");
        act.Should().Throw<CliUsageException>();
    }

    // ── Command resolution ──

    [Fact]
    public void Resolves_a_single_word_command()
    {
        (CliCommand? command, string[] remaining) = CliCommands.Resolve(["advisor"]);

        command!.Usage.Should().Be("advisor");
        remaining.Should().BeEmpty();
    }

    [Fact]
    public void Resolves_a_two_word_command_and_returns_the_rest()
    {
        (CliCommand? command, string[] remaining) =
            CliCommands.Resolve(["deployments", "sync", "--id", "abc"]);

        command!.Usage.Should().Be("deployments sync");
        remaining.Should().BeEquivalentTo(["--id", "abc"]);
    }

    [Fact]
    public void The_longest_matching_command_wins()
    {
        // "deployments list" must not resolve to a shorter "deployments" prefix.
        CliCommands.Resolve(["deployments", "list"]).Command!.Usage.Should().Be("deployments list");
        CliCommands.Resolve(["deployments", "restart"]).Command!.Usage.Should().Be("deployments restart");
    }

    [Fact]
    public void An_unknown_command_resolves_to_nothing()
    {
        CliCommands.Resolve(["nonsense"]).Command.Should().BeNull();
        CliCommands.Resolve(["deployments", "nonsense"]).Command.Should().BeNull();
    }

    [Fact]
    public void Command_matching_is_case_insensitive()
    {
        CliCommands.Resolve(["Clusters", "LIST"]).Command!.Usage.Should().Be("clusters list");
    }

    [Fact]
    public void Every_command_has_a_description_and_a_path()
    {
        foreach (CliCommand command in CliCommands.All)
        {
            command.Path.Should().NotBeEmpty();
            command.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Only_the_deliberately_mutating_commands_use_post()
    {
        // A read command issued as POST would be a surprising side effect from something
        // an operator expects to be safe.
        CliCommands.All.Where(c => c.IsPost).Select(c => c.Usage)
            .Should().BeEquivalentTo(["deployments sync", "deployments restart"]);
    }

    // ── Table rendering ──

    private static (string Output, int Rows) Render(
        string json, (string Header, string Path)[] columns, string? rowsProperty = null)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        StringBuilder sb = new();
        using StringWriter writer = new(sb);
        int rows = TableWriter.Write(writer, doc.RootElement, columns, rowsProperty);
        return (sb.ToString(), rows);
    }

    [Fact]
    public void Renders_an_array_as_an_aligned_table()
    {
        (string output, int rows) = Render(
            """[{"name":"a","n":1},{"name":"longer-name","n":22}]""",
            [("NAME", "name"), ("N", "n")]);

        rows.Should().Be(2);
        output.Should().Contain("NAME");
        output.Should().Contain("longer-name  22");
    }

    [Fact]
    public void Reads_rows_out_of_a_wrapping_object()
    {
        (string _, int rows) = Render(
            """{"total":2,"items":[{"name":"a"},{"name":"b"}]}""",
            [("NAME", "name")], rowsProperty: "items");

        rows.Should().Be(2);
    }

    [Fact]
    public void An_empty_result_set_says_so_and_counts_zero()
    {
        (string output, int rows) = Render("""{"items":[]}""", [("NAME", "name")], "items");

        rows.Should().Be(0);
        output.Should().Contain("No results.");
    }

    [Fact]
    public void A_single_object_renders_as_key_value_pairs()
    {
        (string output, int rows) = Render("""{"tenantId":"t","token":"CI"}""", []);

        rows.Should().Be(1);
        output.Should().Contain("tenantId").And.Contain("token");
    }

    [Fact]
    public void A_missing_field_renders_as_a_dash_rather_than_breaking_the_row()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"a":1}""");
        TableWriter.Cell(doc.RootElement, "missing").Should().Be("-");
        TableWriter.Cell(doc.RootElement, "a.nested.deep").Should().Be("-");
    }

    [Fact]
    public void Null_renders_as_a_dash()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"a":null}""");
        TableWriter.Cell(doc.RootElement, "a").Should().Be("-");
    }

    [Fact]
    public void Booleans_render_as_yes_and_no()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"t":true,"f":false}""");
        TableWriter.Cell(doc.RootElement, "t").Should().Be("yes");
        TableWriter.Cell(doc.RootElement, "f").Should().Be("no");
    }

    [Fact]
    public void Short_scalar_arrays_are_spelled_out()
    {
        // `entkube whoami` exists to show which scopes a token holds; rendering that
        // as "[2]" would answer nothing.
        using JsonDocument doc = JsonDocument.Parse("""{"scopes":["fleet:read","ops:read"]}""");
        TableWriter.Cell(doc.RootElement, "scopes").Should().Be("fleet:read, ops:read");
    }

    [Fact]
    public void Long_arrays_fall_back_to_a_count()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"x":[1,2,3,4,5,6,7,8,9,10]}""");
        TableWriter.Cell(doc.RootElement, "x").Should().Be("[10]");
    }

    [Fact]
    public void Timestamps_are_shortened_to_the_minute()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"at":"2026-08-21T09:14:33.123Z"}""");
        TableWriter.Cell(doc.RootElement, "at").Should().Be("2026-08-21 09:14");
    }

    [Fact]
    public void Long_text_is_truncated_so_one_cell_cannot_destroy_the_layout()
    {
        using JsonDocument doc = JsonDocument.Parse($$"""{"v":"{{new string('x', 200)}}"}""");
        string cell = TableWriter.Cell(doc.RootElement, "v");

        cell.Length.Should().BeLessThan(70);
        cell.Should().EndWith("…");
    }

    [Fact]
    public void Newlines_are_flattened_so_rows_stay_aligned()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"v":"line one\nline two"}""");
        TableWriter.Cell(doc.RootElement, "v").Should().Be("line one line two");
    }
}
