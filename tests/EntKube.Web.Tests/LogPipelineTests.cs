using System.Globalization;
using System.Net;
using System.Text.Json;
using EntKube.Telemetry.LogPipelines;
using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>The language: what it accepts, and that what it refuses points at the mistake.</summary>
public class LogPipelineParserTests
{
    private static LogPipelineQuery Valid(string text)
    {
        (LogPipelineQuery? query, LogPipelineError? error) = LogPipelineParser.Parse(text);
        error.Should().BeNull();
        return query!;
    }

    private static LogPipelineError Invalid(string text)
    {
        (LogPipelineQuery? query, LogPipelineError? error) = LogPipelineParser.Parse(text);
        query.Should().BeNull();
        return error!;
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("logs | take 5")]
    [InlineData("| take 5")]
    [InlineData("take 5")]
    [InlineData("LOGS | WHERE level >= ERROR")]
    [InlineData("logs | where body has 'single quotes'")]
    [InlineData("logs | order by timestamp asc")]
    [InlineData("logs | limit 5")]
    public void The_forms_a_kusto_user_would_type_are_accepted(string text) => Valid(text);

    [Fact]
    public void Top_is_a_sort_and_a_take()
    {
        Valid("logs | summarize n = count() by pod | top 3 by n").Stages.Skip(1).Should().Equal(
            new SortStage("n", Descending: true), new TakeStage(3));
    }

    [Fact]
    public void A_misspelt_column_is_refused_where_it_was_written_and_the_real_ones_are_listed()
    {
        LogPipelineError error = Invalid("logs | where podd == \"x\"");

        error.Position.Should().Be(13);
        error.Message.Should().Contain("podd").And.Contain("namespace");
    }

    [Fact]
    public void A_summarize_replaces_the_columns_so_the_old_ones_are_gone_after_it()
    {
        Invalid("logs | summarize count() by pod | where body has \"x\"").Message
            .Should().Contain("no column 'body'").And.Contain("count_");
    }

    [Fact]
    public void A_project_replaces_the_columns_too()
    {
        Invalid("logs | project pod | sort by body").Message.Should().Contain("no column 'body'");
    }

    [Theory]
    [InlineData("logs | where body has \"open", "never closed")]
    [InlineData("logs | where level >= loud", "not a level")]
    [InlineData("logs | where timestamp == \"x\"", "time range")]
    [InlineData("logs | where level has \"x\"", "does not apply")]
    [InlineData("logs | where body > \"x\"", "does not apply")]
    [InlineData("logs | where body == 5", "quoted string")]
    [InlineData("logs | summarize count() by bin(pod, 5m)", "time column")]
    [InlineData("logs | summarize count() by bin(timestamp, 5y)", "not a duration")]
    [InlineData("logs | summarize count() by bin(timestamp, 0s)", "at least one second")]
    [InlineData("logs | take 0", "from 1 to")]
    [InlineData("logs | take 1.5", "from 1 to")]
    [InlineData("logs | take 99999999", "from 1 to")]
    [InlineData("logs | delete", "Expected where")]
    [InlineData("logs |", "the query ended")]
    [InlineData("logs | where", "the query ended")]
    [InlineData("logs garbage", "Expected where")]
    [InlineData("logs | where body ~ \"x\"", "not part of the language")]
    [InlineData("logs | summarize pod = count() by pod", "appear twice")]
    [InlineData("", "empty")]
    public void A_bad_query_says_what_is_wrong(string text, string expected)
    {
        Invalid(text).Message.Should().Contain(expected);
    }

    [Fact]
    public void An_oversized_query_is_refused_before_any_work()
    {
        Invalid(new string('x', LogPipelineParser.MaxQueryLength + 1)).Message.Should().Contain("longer than");
    }

    /// <summary>The parser recurses, and this text arrives from an API token.</summary>
    [Fact]
    public void Nesting_is_bounded_so_a_query_cannot_exhaust_the_stack()
    {
        string deep = "logs | where " + string.Concat(Enumerable.Repeat("(", 500)) + "body has \"x\""
            + string.Concat(Enumerable.Repeat(")", 500));

        Invalid(deep).Message.Should().Contain("nested");

        string deepNots = "logs | where " + string.Concat(Enumerable.Repeat("not ", 300)) + "body has \"x\"";
        Invalid(deepNots).Message.Should().Contain("nested");
    }

    [Fact]
    public void Stages_are_bounded()
    {
        Invalid("logs" + string.Concat(Enumerable.Repeat(" | take 1", LogPipelineParser.MaxStages + 1)))
            .Message.Should().Contain("at most");
    }

    [Fact]
    public void There_is_no_regular_expression_operator_to_backtrack_in()
    {
        Invalid("logs | where body matches regex \"(a+)+$\"").Message.Should().Contain("Expected a comparison");
    }
}

/// <summary>
/// The answers. Numbers are chosen so that a sum, a count of the wrong thing or an off-by-one bucket
/// gives a different figure, because a query language that runs and counts wrongly fails silently.
/// </summary>
public class LogPipelineEngineTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private static object?[] Line(
        int minute, LogLevel level, string pod, string body, string ns = "shop", string container = "app") =>
        [T0.AddMinutes(minute), level, ns, pod, container, body, null];

    private static readonly List<object?[]> Lines =
    [
        Line(0, LogLevel.Info, "api-1", "request ok"),
        Line(1, LogLevel.Error, "api-1", "connection timeout to db"),
        Line(2, LogLevel.Error, "api-2", "Connection Timeout to cache"),
        Line(7, LogLevel.Warn, "api-2", "slow timeouts"),
        Line(8, LogLevel.Fatal, "worker-1", "out of memory", ns: "jobs"),
        Line(11, LogLevel.None, "worker-1", "unparsed line", ns: "jobs"),
    ];

    private static LogPipelineResult Run(string text, List<object?[]>? lines = null)
    {
        (LogPipelineQuery? query, LogPipelineError? error) = LogPipelineParser.Parse(text);
        error.Should().BeNull();
        return LogPipelineEngine.Evaluate(query!, lines ?? Lines, partial: false);
    }

    private static string[] Cells(LogPipelineResult result, int column) =>
        [.. result.Rows.Select(r => LogPipelineFormat.Cell(r[column]) ?? "")];

    private static Dictionary<string, string> ByFirstColumn(LogPipelineResult result) =>
        result.Rows.ToDictionary(r => LogPipelineFormat.Cell(r[0])!, r => LogPipelineFormat.Cell(r[^1])!);

    [Fact]
    public void A_bare_query_returns_every_line_newest_first()
    {
        LogPipelineResult result = Run("logs");

        result.Rows.Should().HaveCount(6);
        Cells(result, 5)[0].Should().Be("unparsed line");
        Cells(result, 5)[^1].Should().Be("request ok");
    }

    [Theory]
    [InlineData("level >= error", 3)]
    [InlineData("level > error", 1)]
    [InlineData("level == error", 2)]
    [InlineData("level != error", 4)]
    [InlineData("level <= info", 2)]
    [InlineData("level < info", 1)]
    public void A_level_comparison_follows_severity_and_an_unknown_level_sorts_lowest(string condition, int rows)
    {
        Run($"logs | where {condition}").Rows.Should().HaveCount(rows);
    }

    [Theory]
    [InlineData("body has \"timeout\"", 2)]        // "timeouts" is another word
    [InlineData("body has \"TIMEOUT\"", 2)]        // and case does not matter
    [InlineData("body !has \"timeout\"", 4)]
    [InlineData("body contains \"timeout\"", 3)]   // a substring finds "timeouts"
    [InlineData("body !contains \"timeout\"", 3)]
    [InlineData("body == \"request ok\"", 1)]
    [InlineData("body == \"REQUEST OK\"", 0)]      // == is exact
    [InlineData("body =~ \"REQUEST OK\"", 1)]      // =~ is not
    [InlineData("body != \"request ok\"", 5)]
    [InlineData("pod startswith \"API\"", 4)]
    [InlineData("body endswith \"MEMORY\"", 1)]
    public void Text_operators_mean_what_they_mean_in_kusto(string condition, int rows)
    {
        Run($"logs | where {condition}").Rows.Should().HaveCount(rows);
    }

    [Theory]
    [InlineData("a b c", "b", true)]
    [InlineData("b", "b", true)]
    [InlineData("a.b.c", "b", true)]
    [InlineData("abc", "b", false)]
    [InlineData("ab b", "b", true)]       // the first hit is inside a word, the second is not
    [InlineData("connection refused", "connection refused", true)]
    [InlineData("", "b", false)]
    public void Has_needs_a_whole_term(string text, string term, bool expected)
    {
        LogPipelineEngine.HasTerm(text, term).Should().Be(expected);
    }

    [Fact]
    public void And_binds_tighter_than_or_and_parentheses_change_it()
    {
        Run("logs | where pod == \"worker-1\" or pod == \"api-2\" and level == error").Rows.Should().HaveCount(3);
        Run("logs | where (pod == \"worker-1\" or pod == \"api-2\") and level == error").Rows.Should().HaveCount(1);
    }

    [Fact]
    public void Not_inverts_a_group()
    {
        Run("logs | where not (level >= error or pod == \"api-1\")").Rows.Should().HaveCount(2);
    }

    [Fact]
    public void Count_by_a_column_counts_each_group()
    {
        LogPipelineResult result = Run("logs | summarize count() by pod");

        result.Columns.Select(c => c.Name).Should().Equal("pod", "count_");
        ByFirstColumn(result).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["api-1"] = "2", ["api-2"] = "2", ["worker-1"] = "2",
        });
    }

    [Fact]
    public void Count_runs_after_the_filter_before_it()
    {
        ByFirstColumn(Run("logs | where level >= error | summarize count() by namespace"))
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["shop"] = "2", ["jobs"] = "1" });
    }

    [Fact]
    public void Dcount_counts_distinct_values_rather_than_lines()
    {
        ByFirstColumn(Run("logs | summarize pods = dcount(pod) by namespace"))
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["shop"] = "2", ["jobs"] = "1" });
    }

    [Fact]
    public void Two_keys_make_a_group_of_each_pair()
    {
        Run("logs | summarize n = count() by namespace, level").Rows.Should().HaveCount(5);
    }

    [Fact]
    public void A_bin_floors_to_its_width_and_keeps_edges_in_the_later_bucket()
    {
        List<object?[]> lines =
        [
            Line(0, LogLevel.Info, "a", "x"),
            Line(4, LogLevel.Info, "a", "x"),
            Line(5, LogLevel.Info, "a", "x"),   // exactly on the boundary
            Line(9, LogLevel.Info, "a", "x"),
            Line(10, LogLevel.Info, "a", "x"),
        ];

        ByFirstColumn(Run("logs | summarize count() by bin(timestamp, 5m)", lines))
            .Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["2026-11-06T10:00:00.000Z"] = "2",
                ["2026-11-06T10:05:00.000Z"] = "2",
                ["2026-11-06T10:10:00.000Z"] = "1",
            });
    }

    [Fact]
    public void Counting_nothing_is_a_row_saying_zero_but_grouping_nothing_is_no_rows()
    {
        Cells(Run("logs | where pod == \"none\" | summarize count()"), 0).Should().Equal("0");
        Run("logs | where pod == \"none\" | summarize count() by pod").Rows.Should().BeEmpty();
    }

    [Fact]
    public void Top_orders_by_the_number_and_not_by_its_text()
    {
        List<object?[]> lines = [.. Enumerable.Range(0, 10).Select(_ => Line(0, LogLevel.Info, "ten", "x"))];
        lines.Add(Line(0, LogLevel.Info, "one", "x"));
        lines.AddRange(Enumerable.Range(0, 9).Select(_ => Line(0, LogLevel.Info, "nine", "x")));

        LogPipelineResult result = Run("logs | summarize n = count() by pod | top 2 by n", lines);

        Cells(result, 0).Should().Equal("ten", "nine");
    }

    [Fact]
    public void A_where_after_a_summarize_filters_the_aggregates()
    {
        List<object?[]> lines = [.. Enumerable.Range(0, 3).Select(_ => Line(0, LogLevel.Info, "busy", "x"))];
        lines.Add(Line(0, LogLevel.Info, "quiet", "x"));

        Cells(Run("logs | summarize n = count() by pod | where n >= 2", lines), 0).Should().Equal("busy");
    }

    [Fact]
    public void Project_keeps_the_named_columns_in_the_named_order()
    {
        LogPipelineResult result = Run("logs | project pod, level | take 1");

        result.Columns.Select(c => c.Name).Should().Equal("pod", "level");
        result.Rows[0].Should().Equal("worker-1", LogLevel.None);
    }

    [Fact]
    public void Sort_ascending_and_descending_are_not_the_same_order()
    {
        Cells(Run("logs | sort by timestamp asc | take 1"), 5).Should().Equal("request ok");
        Cells(Run("logs | sort by timestamp desc | take 1"), 5).Should().Equal("unparsed line");
    }

    /// <summary>A Swedish server formats 1.5 as "1,5"; that must not reach the API or a chart.</summary>
    [Fact]
    public void A_cell_reads_the_same_under_any_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            LogPipelineFormat.Cell(1.5).Should().Be("1.5");
            LogPipelineFormat.Cell(T0).Should().Be("2026-11-06T10:00:00.000Z");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}

/// <summary>
/// What is pushed to the backend, and what a backend failure turns into.
///
/// <para>⚠️ A pushdown is only sound if it returns a superset of what the condition means, so every
/// refusal here is a query that would lose rows if it were pushed.</para>
/// </summary>
public class LogPipelinePushdownTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private static LogQueryFilter Filter(string text) =>
        LogPipelineEngine.Pushdown(LogPipelineParser.Parse(text).Query!);

    private static LokiLogStream Stream(string pod, params (LogLevel Level, string Line)[] lines) => new()
    {
        Labels = new() { ["namespace"] = "shop", ["pod"] = pod, ["container"] = "app" },
        Entries = [.. lines.Select(l => new LokiLogEntry { Timestamp = T0, Line = l.Line, DetectedLevel = l.Level })],
    };

    private static LogPipelineSource Source(
        List<LokiLogStream>? streams = null, List<string>? namespaces = null, List<LogQueryFilter>? seen = null) =>
        new(
            (filter, _, _) =>
            {
                seen?.Add(filter);
                return Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Success(streams ?? []));
            },
            _ => Task.FromResult(KubernetesOperationResult<List<string>>.Success(namespaces ?? ["shop", "jobs"])));

    [Fact]
    public void Every_leading_condition_that_can_narrow_the_read_does()
    {
        LogQueryFilter f = Filter(
            "logs | where namespace == \"shop\" and level >= warn | where body has \"timeout\" and container == \"app\" and pod startswith \"api\"");

        f.Namespaces.Should().Equal("shop");
        f.MinLevel.Should().Be(LogLevel.Warn);
        f.Text.Should().Be("timeout");
        f.Container.Should().Be("app");
        f.Pod.Should().Be("api");
    }

    [Theory]
    [InlineData("logs | where level > warn", LogLevel.Error)]
    [InlineData("logs | where level > fatal", LogLevel.None)]
    [InlineData("logs | where level == error", LogLevel.Error)]
    [InlineData("logs | where level >= info and level >= error", LogLevel.Error)]
    [InlineData("logs | where level <= warn", LogLevel.None)]
    public void A_level_condition_becomes_the_lowest_level_that_can_still_match(string text, LogLevel expected)
    {
        Filter(text).MinLevel.Should().Be(expected);
    }

    [Theory]
    [InlineData("logs | where namespace == \"a\" or level >= error")]
    [InlineData("logs | where not namespace == \"a\"")]
    [InlineData("logs | where not (level >= error)")]
    [InlineData("logs | where namespace != \"a\"")]
    [InlineData("logs | where namespace =~ \"A\"")]
    [InlineData("logs | where body contains \"time\"")]     // no token "time" in "timeout"
    [InlineData("logs | where body !has \"x\"")]
    [InlineData("logs | where pod endswith \"x\"")]
    [InlineData("logs | where pod startswith \"(a|b)\"")]   // the filter would read an alternation
    [InlineData("logs | take 5 | where level >= error")]    // after the read's limit has applied
    public void A_condition_a_pushdown_could_lose_rows_on_is_left_to_the_engine(string text)
    {
        LogQueryFilter f = Filter(text);

        f.Namespaces.Should().BeEmpty();
        f.MinLevel.Should().Be(LogLevel.None);
        f.Text.Should().BeNull();
        f.Pod.Should().BeNull();
    }

    [Fact]
    public async Task With_no_namespace_named_every_namespace_in_the_window_is_read()
    {
        List<LogQueryFilter> seen = [];

        // The backends return nothing for an empty namespace list, so this is not an optimisation.
        await LogPipelineEngine.RunAsync(LogPipelineParser.Parse("logs").Query!, Source(seen: seen));

        seen.Should().ContainSingle().Which.Namespaces.Should().Equal("shop", "jobs");
    }

    [Fact]
    public async Task A_namespace_in_the_query_is_not_listed_first()
    {
        bool listed = false;
        LogPipelineSource source = new(
            (_, _, _) => Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Success([])),
            _ =>
            {
                listed = true;
                return Task.FromResult(KubernetesOperationResult<List<string>>.Success([]));
            });

        await LogPipelineEngine.RunAsync(LogPipelineParser.Parse("logs | where namespace == \"a\"").Query!, source);

        listed.Should().BeFalse();
    }

    /// <summary>Loki ignores a minimum level, so conditions must hold after the read as well.</summary>
    [Fact]
    public async Task A_backend_that_ignores_the_filter_still_gets_the_right_answer()
    {
        LogPipelineSource source = Source([Stream("api", (LogLevel.Info, "noise"), (LogLevel.Error, "boom"))]);

        KubernetesOperationResult<LogPipelineResult> run = await LogPipelineEngine.RunAsync(
            LogPipelineParser.Parse("logs | where level >= error | summarize count()").Query!, source);

        run.Data!.Rows[0][0].Should().Be(1d);
    }

    [Fact]
    public async Task Stopping_at_the_limit_is_reported_as_partial_and_one_short_of_it_is_not()
    {
        LogPipelineQuery query = LogPipelineParser.Parse("logs | summarize count()").Query!;
        LogPipelineSource source = Source([Stream("api", (LogLevel.Info, "a"), (LogLevel.Info, "b"), (LogLevel.Info, "c"))]);

        (await LogPipelineEngine.RunAsync(query, source, maxLines: 3)).Data!.Partial.Should().BeTrue();
        (await LogPipelineEngine.RunAsync(query, source, maxLines: 4)).Data!.Partial.Should().BeFalse();
    }

    [Fact]
    public async Task The_limit_reaches_the_read_rather_than_only_being_checked_after_it()
    {
        int? limit = null;
        LogPipelineSource source = new(
            (_, l, _) =>
            {
                limit = l;
                return Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Success([]));
            },
            _ => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["shop"])));

        await LogPipelineEngine.RunAsync(LogPipelineParser.Parse("logs").Query!, source, maxLines: 123);

        limit.Should().Be(123);
    }

    [Fact]
    public async Task A_failed_read_is_a_failure_and_never_an_empty_answer()
    {
        LogPipelineSource source = new(
            (_, _, _) => Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Failure("store down")),
            _ => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["shop"])));

        KubernetesOperationResult<LogPipelineResult> run =
            await LogPipelineEngine.RunAsync(LogPipelineParser.Parse("logs").Query!, source);

        run.IsSuccess.Should().BeFalse();
        run.Error.Should().Be("store down");
    }

    [Fact]
    public async Task A_failed_namespace_listing_is_a_failure_and_never_an_empty_answer()
    {
        LogPipelineSource source = new(
            (_, _, _) => throw new InvalidOperationException("must not be reached"),
            _ => Task.FromResult(KubernetesOperationResult<List<string>>.Failure("store down")));

        (await LogPipelineEngine.RunAsync(LogPipelineParser.Parse("logs").Query!, source)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task A_cluster_with_no_namespaces_really_is_empty_and_still_shapes_the_answer()
    {
        KubernetesOperationResult<LogPipelineResult> run = await LogPipelineEngine.RunAsync(
            LogPipelineParser.Parse("logs | summarize count()").Query!, Source(namespaces: []));

        run.IsSuccess.Should().BeTrue();
        run.Data!.Rows.Should().ContainSingle().Which[0].Should().Be(0d);
    }

    // ── Alerts ──

    [Fact]
    public async Task An_aggregate_is_compared_by_its_value_and_a_list_by_its_length()
    {
        LogPipelineSource source = Source([Stream("api", (LogLevel.Error, "a"), (LogLevel.Error, "b"), (LogLevel.Error, "c"))]);

        (await LogPipelineAlert.CheckAsync("logs | summarize count()", source, 2, 100)).Value.Should().Be(3);
        (await LogPipelineAlert.CheckAsync("logs | summarize n = count() | project n", source, 2, 100)).Value.Should().Be(3);
        (await LogPipelineAlert.CheckAsync("logs | summarize count() by pod", source, 2, 100)).Value.Should().Be(1);
        (await LogPipelineAlert.CheckAsync("logs", source, 2, 100)).Value.Should().Be(3);
    }

    /// <summary>Strictly over, like every other kind of rule: equal to the threshold does not fire.</summary>
    [Theory]
    [InlineData(2, LogAlertState.Fire)]
    [InlineData(3, LogAlertState.Clear)]
    [InlineData(4, LogAlertState.Clear)]
    public async Task A_rule_fires_only_above_its_threshold(double threshold, LogAlertState expected)
    {
        LogPipelineSource source = Source([Stream("api", (LogLevel.Error, "a"), (LogLevel.Error, "b"), (LogLevel.Error, "c"))]);

        (await LogPipelineAlert.CheckAsync("logs | summarize count()", source, threshold, 100)).State.Should().Be(expected);
    }

    [Fact]
    public async Task A_partial_count_over_the_threshold_stands_because_it_only_undercounts()
    {
        LogPipelineSource source = Source([Stream("api", (LogLevel.Error, "a"), (LogLevel.Error, "b"))]);

        (await LogPipelineAlert.CheckAsync("logs | summarize count()", source, 1, maxLines: 2)).State
            .Should().Be(LogAlertState.Fire);
    }

    [Fact]
    public async Task A_partial_count_under_the_threshold_proves_nothing_so_the_rule_holds()
    {
        LogPipelineSource source = Source([Stream("api", (LogLevel.Error, "a"), (LogLevel.Error, "b"))]);

        (await LogPipelineAlert.CheckAsync("logs | summarize count()", source, 5, maxLines: 2)).State
            .Should().Be(LogAlertState.Hold);
    }

    [Fact]
    public async Task A_store_that_cannot_be_read_holds_the_rule_and_does_not_clear_it()
    {
        LogPipelineSource source = new(
            (_, _, _) => Task.FromResult(KubernetesOperationResult<List<LokiLogStream>>.Failure("down")),
            _ => Task.FromResult(KubernetesOperationResult<List<string>>.Success(["shop"])));

        (await LogPipelineAlert.CheckAsync("logs | summarize count()", source, 5, 100)).State
            .Should().Be(LogAlertState.Hold);
    }

    [Theory]
    [InlineData("logs | this does not parse")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_query_that_no_longer_parses_holds_the_rule_and_does_not_clear_it(string? query)
    {
        (await LogPipelineAlert.CheckAsync(query, Source(), 5, 100)).State.Should().Be(LogAlertState.Hold);
    }
}

public sealed class SavedLogQueryTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly SavedLogQueryService service;
    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid strangerId = Guid.NewGuid();

    public SavedLogQueryTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        factory = new TestDbContextFactory(connection);
        service = new SavedLogQueryService(factory);
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Saving_under_the_same_name_replaces_rather_than_duplicates()
    {
        (await service.SaveAsync(tenantId, "errors", "logs | take 1", "me")).Should().BeNull();
        (await service.SaveAsync(tenantId, "errors", "logs | take 2", "you")).Should().BeNull();

        SavedLogQuery saved = (await service.ListAsync(tenantId)).Should().ContainSingle().Subject;
        saved.Query.Should().Be("logs | take 2");
        saved.CreatedBy.Should().Be("me");
    }

    [Fact]
    public async Task A_query_that_would_not_run_is_not_saved()
    {
        (await service.SaveAsync(tenantId, "broken", "logs | nonsense", "me")).Should().Contain("Expected");

        (await service.ListAsync(tenantId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_name_is_refused()
    {
        (await service.SaveAsync(tenantId, "   ", "logs", "me")).Should().Contain("name");
    }

    [Fact]
    public async Task Two_tenants_can_use_the_same_name_and_never_see_or_delete_each_other_s()
    {
        await service.SaveAsync(tenantId, "errors", "logs | take 1", "me");
        await service.SaveAsync(strangerId, "errors", "logs | take 2", "them");

        SavedLogQuery mine = (await service.ListAsync(tenantId)).Single();
        mine.Query.Should().Be("logs | take 1");

        await service.DeleteAsync(strangerId, mine.Id);
        (await service.ListAsync(tenantId)).Should().ContainSingle("a stranger's delete must change nothing");

        await service.DeleteAsync(tenantId, mine.Id);
        (await service.ListAsync(tenantId)).Should().BeEmpty();
    }
}

/// <summary><see cref="TelemetryAlertRuleService"/> copies fields by hand, so a new one can be lost.</summary>
public sealed class LogQueryAlertRuleTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TelemetryAlertRuleService rules;
    private readonly Guid tenantId = Guid.NewGuid();

    public LogQueryAlertRuleTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        rules = new TelemetryAlertRuleService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(TelemetryAlertKind.LogQuery, true)]
    [InlineData(TelemetryAlertKind.LogErrorRate, true)]
    [InlineData(TelemetryAlertKind.NewErrorIssues, true)]
    [InlineData(TelemetryAlertKind.TraceErrorRate, false)]
    public void A_log_rule_looks_for_clusters_with_logs_rather_than_traces(TelemetryAlertKind kind, bool expected)
    {
        TelemetryAlertEvaluator.ReadsLogs(kind).Should().Be(expected);
    }

    [Fact]
    public async Task Editing_a_rule_keeps_the_new_query()
    {
        await rules.UpsertAsync(tenantId, new TelemetryAlertRule
        {
            Name = "errors", Kind = TelemetryAlertKind.LogQuery, Query = "logs | take 1", Threshold = 1,
        });

        TelemetryAlertRule rule = (await rules.ListAsync(tenantId)).Single();
        rule.Query.Should().Be("logs | take 1");

        rule.Query = "logs | take 2";
        await rules.UpsertAsync(tenantId, rule);

        (await rules.ListAsync(tenantId)).Single().Query.Should().Be("logs | take 2");
    }
}

/// <summary><c>GET /logs/query</c>, through real tokens.</summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class LogQueryApiTests(GeneratedOpenApiDocumentFixture fixture)
{
    private const string Scope = "logs:read";

    private static string Url(Guid cluster, string query, string? minutes = null) =>
        $"/api/v1/logs/query?clusterId={cluster}&query={Uri.EscapeDataString(query)}"
        + (minutes is null ? "" : $"&minutes={minutes}");

    private async Task<Guid> SeedClusterAsync(Guid tenantId)
    {
        Guid clusterId = Guid.NewGuid();
        Guid environmentId = Guid.NewGuid();

        await using ApplicationDbContext db = fixture.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Other", Slug = "other-" + tenantId.ToString("N")[..8] });
            await db.SaveChangesAsync();
        }

        db.Environments.Add(new EntKube.Web.Data.Environment { Id = environmentId, TenantId = tenantId, Name = "e-" + environmentId.ToString("N")[..8] });
        await db.SaveChangesAsync();
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "c-" + clusterId.ToString("N")[..8], ApiServerUrl = "https://k8s.example.com",
        });
        await db.SaveChangesAsync();
        return clusterId;
    }

    /// <summary>⚠️ The log layer does not refuse a foreign cluster, so this route is the check.</summary>
    [Fact]
    public async Task Another_tenant_s_cluster_is_a_404_and_the_token_s_own_is_not()
    {
        Guid foreign = await SeedClusterAsync(Guid.NewGuid());
        Guid own = await SeedClusterAsync(fixture.TokenTenantId);

        (HttpStatusCode refused, _, _) = await fixture.SendForResponseAsync("GET", Url(foreign, "logs"), Scope);
        refused.Should().Be(HttpStatusCode.NotFound);

        (HttpStatusCode answered, _, _) = await fixture.SendForResponseAsync("GET", Url(own, "logs | take 1"), Scope);
        answered.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_bad_query_is_a_400_that_says_what_is_wrong()
    {
        Guid own = await SeedClusterAsync(fixture.TokenTenantId);

        (HttpStatusCode status, _, string body) =
            await fixture.SendForResponseAsync("GET", Url(own, "logs | where podd == \"x\""), Scope);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("podd").And.Contain("at character 14");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10081")]
    public async Task A_window_outside_the_limit_is_refused(string minutes)
    {
        Guid own = await SeedClusterAsync(fixture.TokenTenantId);

        (HttpStatusCode status, _, _) = await fixture.SendForResponseAsync("GET", Url(own, "logs", minutes), Scope);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_ops_read_token_does_not_get_log_contents()
    {
        Guid own = await SeedClusterAsync(fixture.TokenTenantId);

        (HttpStatusCode status, _, _) = await fixture.SendForResponseAsync("GET", Url(own, "logs"), "ops:read");

        status.Should().Be(HttpStatusCode.Forbidden);
    }
}
