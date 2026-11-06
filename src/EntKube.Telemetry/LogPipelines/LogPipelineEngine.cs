using EntKube.Web.Services;

namespace EntKube.Telemetry.LogPipelines;

/// <summary>Where a query's lines come from. Delegates, so the engine knows nothing of any backend.</summary>
/// <param name="Query">Reads up to the given number of the newest lines matching a filter.</param>
/// <param name="ListNamespaces">The namespaces with lines in the window.</param>
public sealed record LogPipelineSource(
    Func<LogQueryFilter, int, CancellationToken, Task<KubernetesOperationResult<List<LokiLogStream>>>> Query,
    Func<CancellationToken, Task<KubernetesOperationResult<List<string>>>> ListNamespaces);

/// <summary>
/// Runs a <see cref="LogPipelineQuery"/>: pushes what it can into the backend's filter, reads a
/// bounded number of lines, and evaluates the whole pipeline over them.
///
/// <para><b>Why the filter is only an optimisation.</b> Every condition is evaluated here again,
/// including the ones pushed down, because the backends do not agree: Loki ignores a minimum level
/// and matches text case-sensitively, the segment store matches analysed tokens. So a pushdown must
/// only ever return a <em>superset</em> of what the condition means. That is why <c>contains</c> is
/// not pushed: an analysed token search for <c>time</c> cannot find <c>timeout</c>.</para>
///
/// <para><b>Partial results are stated.</b> Reading stops at <c>maxLines</c>, newest first, and on a
/// busy cluster a count over that is a lower bound. <see cref="LogPipelineResult.Partial"/> carries
/// that to whoever displays or alerts on it.</para>
/// </summary>
public static class LogPipelineEngine
{
    public const int DefaultMaxLines = 20_000;

    public static async Task<KubernetesOperationResult<LogPipelineResult>> RunAsync(
        LogPipelineQuery query, LogPipelineSource source, int maxLines = DefaultMaxLines, CancellationToken ct = default)
    {
        LogQueryFilter filter = Pushdown(query);

        if (filter.Namespaces.Count == 0)
        {
            KubernetesOperationResult<List<string>> namespaces = await source.ListNamespaces(ct);

            // A failed listing is not an empty cluster: answering "no rows" here would tell an alert
            // that all is well because the store could not be reached.
            if (!namespaces.IsSuccess)
                return KubernetesOperationResult<LogPipelineResult>.Failure(namespaces.Error ?? "The namespaces could not be listed.");

            if (namespaces.Data is null || namespaces.Data.Count == 0)
                return KubernetesOperationResult<LogPipelineResult>.Success(Evaluate(query, [], partial: false));

            filter = filter with { Namespaces = namespaces.Data };
        }

        KubernetesOperationResult<List<LokiLogStream>> streams = await source.Query(filter, maxLines, ct);

        if (!streams.IsSuccess)
            return KubernetesOperationResult<LogPipelineResult>.Failure(streams.Error ?? "The logs could not be read.");

        List<object?[]> lines = [.. Flatten(streams.Data ?? [])];

        return KubernetesOperationResult<LogPipelineResult>.Success(Evaluate(query, lines, partial: lines.Count >= maxLines));
    }

    /// <summary>
    /// What can safely go into the backend's filter: the conditions every row must meet, from the
    /// leading <c>where</c> stages only. After a summarize the columns mean something else, and an
    /// <c>or</c> or <c>not</c> cannot be narrowed by one side of it.
    /// </summary>
    public static LogQueryFilter Pushdown(LogPipelineQuery query)
    {
        LogQueryFilter filter = new() { Namespaces = [] };

        foreach (LogStage stage in query.Stages)
        {
            if (stage is not WhereStage where) break;

            foreach (LogComparison c in Conjuncts(where.Condition))
            {
                filter = Narrow(filter, c);
            }
        }

        return filter;
    }

    private static IEnumerable<LogComparison> Conjuncts(LogExpression e) => e switch
    {
        LogAnd a => Conjuncts(a.Left).Concat(Conjuncts(a.Right)),
        LogComparison c => [c],
        _ => [],
    };

    private static LogQueryFilter Narrow(LogQueryFilter f, LogComparison c)
    {
        switch (c.Column, c.Op)
        {
            case ("namespace", LogCompareOp.Equal) when f.Namespaces.Count == 0:
                return f with { Namespaces = [(string)c.Value] };

            case ("container", LogCompareOp.Equal) when f.Container is null:
                return f with { Container = (string)c.Value };

            // A pod filter is a prefix, so both are supersets. Only a plain name goes down, because
            // the filter reads "(a|b)" as an alternation.
            case ("pod", LogCompareOp.Equal or LogCompareOp.StartsWith) when f.Pod is null && IsPlainName((string)c.Value):
                return f with { Pod = (string)c.Value };

            case ("body", LogCompareOp.Has) when f.Text is null:
                return f with { Text = (string)c.Value };

            case ("level", LogCompareOp.GreaterOrEqual or LogCompareOp.Equal):
                return f with { MinLevel = Max(f.MinLevel, (LogLevel)c.Value) };

            case ("level", LogCompareOp.Greater) when (LogLevel)c.Value < LogLevel.Fatal:
                return f with { MinLevel = Max(f.MinLevel, (LogLevel)c.Value + 1) };

            default:
                return f;
        }
    }

    private static LogLevel Max(LogLevel a, LogLevel b) => a > b ? a : b;

    private static bool IsPlainName(string value) =>
        value.Length > 0 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' or '_');

    private static IEnumerable<object?[]> Flatten(List<LokiLogStream> streams)
    {
        foreach (LokiLogStream stream in streams)
        {
            string ns = stream.Labels.GetValueOrDefault("namespace", "");
            string pod = stream.Labels.GetValueOrDefault("pod", "");
            string container = stream.Labels.GetValueOrDefault("container", "");

            foreach (LokiLogEntry entry in stream.Entries)
            {
                yield return [entry.Timestamp, entry.DetectedLevel, ns, pod, container, entry.Line, entry.TraceId];
            }
        }
    }

    /// <summary>Runs the stages over rows shaped like <see cref="LogPipelineParser.LogColumns"/>.</summary>
    public static LogPipelineResult Evaluate(LogPipelineQuery query, List<object?[]> lines, bool partial)
    {
        // Newest first unless a stage says otherwise, whichever order the streams arrived in.
        List<object?[]> rows = [.. lines.OrderByDescending(r => (DateTime)r[0]!)];
        List<LogColumn> columns = [.. LogPipelineParser.LogColumns];

        foreach (LogStage stage in query.Stages)
        {
            switch (stage)
            {
                case WhereStage where:
                    List<LogColumn> current = columns;
                    rows = [.. rows.Where(r => Matches(where.Condition, r, current))];
                    break;

                case ProjectStage project:
                    int[] kept = [.. project.Columns.Select(name => IndexOf(columns, name))];
                    rows = [.. rows.Select(r => kept.Select(i => r[i]).ToArray())];
                    columns = [.. kept.Select(i => columns[i])];
                    break;

                case SortStage sort:
                    int key = IndexOf(columns, sort.Column);
                    rows = sort.Descending
                        ? [.. rows.OrderByDescending(r => r[key], ValueOrder.Instance)]
                        : [.. rows.OrderBy(r => r[key], ValueOrder.Instance)];
                    break;

                case TakeStage take:
                    rows = [.. rows.Take(take.Count)];
                    break;

                case SummarizeStage summarize:
                    (columns, rows) = Summarize(summarize, columns, rows);
                    break;
            }
        }

        return new LogPipelineResult(columns, rows, lines.Count, partial);
    }

    private static (List<LogColumn>, List<object?[]>) Summarize(
        SummarizeStage s, List<LogColumn> columns, List<object?[]> rows)
    {
        int[] keyIndex = [.. s.Keys.Select(k => IndexOf(columns, k.Column))];
        int?[] distinctIndex = [.. s.Aggregates.Select(a => a.Column is null ? (int?)null : IndexOf(columns, a.Column))];

        Dictionary<GroupKey, (long Count, HashSet<object?>[] Distinct)> groups = [];

        foreach (object?[] row in rows)
        {
            object?[] key = new object?[s.Keys.Count];
            for (int i = 0; i < key.Length; i++)
            {
                key[i] = s.Keys[i].Bin is { } width ? Floor((DateTime)row[keyIndex[i]]!, width) : row[keyIndex[i]];
            }

            GroupKey group = new(key);
            if (!groups.TryGetValue(group, out (long Count, HashSet<object?>[] Distinct) state))
            {
                state = (0, [.. s.Aggregates.Select(_ => new HashSet<object?>())]);
            }

            for (int i = 0; i < distinctIndex.Length; i++)
            {
                if (distinctIndex[i] is int d) state.Distinct[i].Add(row[d]);
            }

            groups[group] = (state.Count + 1, state.Distinct);
        }

        // Counting nothing is a row saying 0, as in Kusto. Grouped, there is simply no group.
        if (groups.Count == 0 && s.Keys.Count == 0)
        {
            groups[new GroupKey([])] = (0, [.. s.Aggregates.Select(_ => new HashSet<object?>())]);
        }

        List<LogColumn> output = [.. keyIndex.Select(i => columns[i])];
        output.AddRange(s.Aggregates.Select(a => new LogColumn(a.Alias, LogColumnType.Number)));

        List<object?[]> result = [.. groups.Select(g =>
        {
            object?[] row = new object?[output.Count];
            g.Key.Values.CopyTo(row, 0);
            for (int i = 0; i < s.Aggregates.Count; i++)
            {
                row[keyIndex.Length + i] = s.Aggregates[i].Column is null
                    ? (double)g.Value.Count
                    : (double)g.Value.Distinct[i].Count;
            }
            return row;
        })];

        return (output, result);
    }

    private static DateTime Floor(DateTime value, TimeSpan width) =>
        new(value.Ticks - (value.Ticks % width.Ticks), DateTimeKind.Utc);

    private static int IndexOf(List<LogColumn> columns, string name) =>
        columns.FindIndex(c => c.Name == name);

    private static bool Matches(LogExpression e, object?[] row, List<LogColumn> columns) => e switch
    {
        LogAnd a => Matches(a.Left, row, columns) && Matches(a.Right, row, columns),
        LogOr o => Matches(o.Left, row, columns) || Matches(o.Right, row, columns),
        LogNot n => !Matches(n.Inner, row, columns),
        LogComparison c => Compare(c, row[IndexOf(columns, c.Column)]),
        _ => false,
    };

    private static bool Compare(LogComparison c, object? actual)
    {
        if (c.Value is string text) return CompareText((actual as string) ?? "", c.Op, text);

        int order = c.Value is LogLevel level
            ? ((LogLevel)actual!).CompareTo(level)
            : ((double)actual!).CompareTo((double)c.Value);

        return c.Op switch
        {
            LogCompareOp.Equal => order == 0,
            LogCompareOp.NotEqual => order != 0,
            LogCompareOp.GreaterOrEqual => order >= 0,
            LogCompareOp.Greater => order > 0,
            LogCompareOp.LessOrEqual => order <= 0,
            LogCompareOp.Less => order < 0,
            _ => false,
        };
    }

    private static bool CompareText(string actual, LogCompareOp op, string value) => op switch
    {
        LogCompareOp.Equal => actual == value,
        LogCompareOp.NotEqual => actual != value,
        LogCompareOp.EqualIgnoreCase => actual.Equals(value, StringComparison.OrdinalIgnoreCase),
        LogCompareOp.Has => HasTerm(actual, value),
        LogCompareOp.NotHas => !HasTerm(actual, value),
        LogCompareOp.Contains => actual.Contains(value, StringComparison.OrdinalIgnoreCase),
        LogCompareOp.NotContains => !actual.Contains(value, StringComparison.OrdinalIgnoreCase),
        LogCompareOp.StartsWith => actual.StartsWith(value, StringComparison.OrdinalIgnoreCase),
        LogCompareOp.EndsWith => actual.EndsWith(value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// Whole-term and case-insensitive, as in Kusto: <c>has "time"</c> does not match "timeout".
    /// Hand-written rather than a regular expression so the cost is linear in the line.
    /// </summary>
    public static bool HasTerm(string text, string term)
    {
        if (term.Length == 0) return true;

        int from = 0;
        while (from <= text.Length - term.Length)
        {
            int at = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            bool startsClean = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            bool endsClean = at + term.Length == text.Length || !char.IsLetterOrDigit(text[at + term.Length]);
            if (startsClean && endsClean) return true;

            from = at + 1;
        }

        return false;
    }

    /// <summary>Array-keyed group identity. Values compare by equality, in order.</summary>
    private sealed class GroupKey(object?[] values) : IEquatable<GroupKey>
    {
        public object?[] Values { get; } = values;

        public bool Equals(GroupKey? other) => other is not null && Values.SequenceEqual(other.Values);

        public override bool Equals(object? obj) => Equals(obj as GroupKey);

        public override int GetHashCode()
        {
            HashCode hash = new();
            foreach (object? value in Values) hash.Add(value);
            return hash.ToHashCode();
        }
    }

    /// <summary>Orders any one column's values, with nulls first.</summary>
    private sealed class ValueOrder : IComparer<object?>
    {
        public static readonly ValueOrder Instance = new();

        public int Compare(object? a, object? b)
        {
            if (a is null) return b is null ? 0 : -1;
            if (b is null) return 1;
            return a is string s ? string.CompareOrdinal(s, (string)b) : ((IComparable)a).CompareTo(b);
        }
    }
}
