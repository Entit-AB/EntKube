using EntKube.Web.Services;

namespace EntKube.Telemetry.LogPipelines;

/// <summary>What a column holds, which decides the operators that make sense on it.</summary>
public enum LogColumnType { Text, Level, Time, Number }

public sealed record LogColumn(string Name, LogColumnType Type);

public enum LogCompareOp
{
    Equal, NotEqual, EqualIgnoreCase,
    Has, NotHas, Contains, NotContains, StartsWith, EndsWith,
    GreaterOrEqual, Greater, LessOrEqual, Less,
}

public abstract record LogExpression;
public sealed record LogAnd(LogExpression Left, LogExpression Right) : LogExpression;
public sealed record LogOr(LogExpression Left, LogExpression Right) : LogExpression;
public sealed record LogNot(LogExpression Inner) : LogExpression;

/// <param name="Column">The column, spelled as the schema spells it.</param>
/// <param name="Op">The comparison.</param>
/// <param name="Value">A string, a <see cref="double"/>, or a <c>LogLevel</c> for the level column.</param>
public sealed record LogComparison(string Column, LogCompareOp Op, object Value) : LogExpression;

public abstract record LogStage;
public sealed record WhereStage(LogExpression Condition) : LogStage;
public sealed record ProjectStage(IReadOnlyList<string> Columns) : LogStage;
public sealed record SortStage(string Column, bool Descending) : LogStage;
public sealed record TakeStage(int Count) : LogStage;
public sealed record SummarizeStage(IReadOnlyList<LogAggregate> Aggregates, IReadOnlyList<LogGroupKey> Keys) : LogStage;

/// <param name="Alias">The output column.</param>
/// <param name="Column">The column counted distinct, or null for a plain count.</param>
public sealed record LogAggregate(string Alias, string? Column);

/// <param name="Column">The grouped column.</param>
/// <param name="Bin">The bucket width when grouping time, else null.</param>
public sealed record LogGroupKey(string Column, TimeSpan? Bin);

/// <summary>A parsed, type-checked query. Every column it names exists where it is used.</summary>
public sealed record LogPipelineQuery(IReadOnlyList<LogStage> Stages)
{
    /// <summary>
    /// True when the query ends in one aggregate row by construction, so its answer is a number
    /// rather than a list. That is what an alert compares with its threshold.
    /// </summary>
    public bool EndsInSingleValue =>
        Stages.LastOrDefault(s => s is not SortStage and not TakeStage and not ProjectStage)
            is SummarizeStage { Keys.Count: 0 };
}

/// <summary>Why a query was refused, and where. <paramref name="Position"/> is a 0-based offset.</summary>
public sealed record LogPipelineError(string Message, int Position);

/// <summary>What a query produced.</summary>
/// <param name="Columns">The output columns.</param>
/// <param name="Rows">Rows, each aligned with <paramref name="Columns"/>.</param>
/// <param name="LinesScanned">How many log lines were read to produce this.</param>
/// <param name="Partial">
/// True when the scan stopped at its limit, so this describes only the newest lines in the window.
/// A count is then a lower bound, and nothing here may present it as the total.
/// </param>
public sealed record LogPipelineResult(
    IReadOnlyList<LogColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    int LinesScanned,
    bool Partial);

/// <summary>What a pipeline's values look like outside the engine, in the UI and on the wire.</summary>
public static class LogPipelineFormat
{
    /// <summary>Invariant, so a number or a time reads the same whatever the server's culture is.</summary>
    public static string? Cell(object? value) => value switch
    {
        null => null,
        DateTime time => time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
        double number => number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

/// <summary>What a query means to an alert rule.</summary>
public static class LogPipelineAlert
{
    /// <summary>
    /// The number a rule compares with its threshold: the value when the query ends in a single
    /// aggregate, as in <c>summarize count()</c>, otherwise how many rows it returned.
    /// </summary>
    public static double Measure(LogPipelineQuery query, LogPipelineResult result)
    {
        if (query.EndsInSingleValue && result.Rows.Count == 1)
        {
            int number = result.Columns.ToList().FindIndex(c => c.Type == LogColumnType.Number);
            if (number >= 0) return (double)result.Rows[0][number]!;
        }

        return result.Rows.Count;
    }

    /// <summary>
    /// Evaluates a rule's query. Anything that leaves the answer unknown holds the rule where it is:
    /// a query that no longer parses, a store that cannot be read, or a partial result under the
    /// threshold. A partial result only ever undercounts, so over the threshold it stands, and under
    /// it proves nothing.
    /// </summary>
    public static async Task<LogAlertVerdict> CheckAsync(
        string? queryText, LogPipelineSource source, double threshold, int maxLines, CancellationToken ct = default)
    {
        (LogPipelineQuery? query, _) = LogPipelineParser.Parse(queryText);
        if (query is null) return new LogAlertVerdict(LogAlertState.Hold, 0);

        KubernetesOperationResult<LogPipelineResult> run = await LogPipelineEngine.RunAsync(query, source, maxLines, ct);
        if (!run.IsSuccess || run.Data is null) return new LogAlertVerdict(LogAlertState.Hold, 0);

        double value = Measure(query, run.Data);

        if (value > threshold) return new LogAlertVerdict(LogAlertState.Fire, value);

        return new LogAlertVerdict(run.Data.Partial ? LogAlertState.Hold : LogAlertState.Clear, value);
    }
}

public enum LogAlertState { Clear, Fire, Hold }

/// <param name="State">What the rule should do.</param>
/// <param name="Value">The measure compared with the threshold; 0 when there is none.</param>
public sealed record LogAlertVerdict(LogAlertState State, double Value);
