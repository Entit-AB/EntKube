using System.Globalization;
using EntKube.Web.Services;

namespace EntKube.Telemetry.LogPipelines;

/// <summary>
/// Parses the log query language: a small, read-only subset of Kusto, so that anybody coming from
/// Azure Log Analytics already knows it.
///
/// <code>
/// logs
/// | where namespace == "shop" and level >= warn and body has "timeout"
/// | summarize failures = count() by pod, bin(timestamp, 5m)
/// | top 10 by failures
/// </code>
///
/// <para>Columns are checked as the stages are walked, because a <c>summarize</c> replaces them: a
/// misspelt column is an error at its position rather than a query that quietly matches nothing.</para>
///
/// <para>⚠️ There is deliberately no regular-expression operator. A query runs on the management
/// plane against every line in its window, and a backtracking pattern is a way to spend that
/// process's CPU. The bounds below exist for the same reason: this text arrives from an API token.</para>
/// </summary>
public static class LogPipelineParser
{
    public const int MaxQueryLength = 2000;
    public const int MaxStages = 20;
    public const int MaxDepth = 32;
    public const int MaxTake = 10_000;

    /// <summary>The columns every query starts with.</summary>
    public static readonly IReadOnlyList<LogColumn> LogColumns =
    [
        new("timestamp", LogColumnType.Time),
        new("level", LogColumnType.Level),
        new("namespace", LogColumnType.Text),
        new("pod", LogColumnType.Text),
        new("container", LogColumnType.Text),
        new("body", LogColumnType.Text),
        new("traceId", LogColumnType.Text),
    ];

    public static (LogPipelineQuery? Query, LogPipelineError? Error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, new LogPipelineError("The query is empty.", 0));
        if (text.Length > MaxQueryLength)
            return (null, new LogPipelineError($"The query is longer than {MaxQueryLength} characters.", MaxQueryLength));

        try
        {
            return (new Parser(Tokenize(text), text.Length).ParseQuery(), null);
        }
        catch (SyntaxError e)
        {
            return (null, new LogPipelineError(e.Message, e.Position));
        }
    }

    private enum TokenKind { Word, Text, Number, Duration, Symbol, End }

    private sealed record Token(TokenKind Kind, string Value, int Position)
    {
        public double Number => double.Parse(Value, CultureInfo.InvariantCulture);
    }

    private sealed class SyntaxError(string message, int position) : Exception(message)
    {
        public int Position { get; } = position;
    }

    private static List<Token> Tokenize(string s)
    {
        List<Token> tokens = [];
        int i = 0;

        while (i < s.Length)
        {
            char c = s[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c is '"' or '\'')
            {
                int start = i++;
                System.Text.StringBuilder value = new();
                while (true)
                {
                    if (i >= s.Length) throw new SyntaxError("This string is never closed.", start);
                    if (s[i] == '\\' && i + 1 < s.Length) { value.Append(s[i + 1]); i += 2; continue; }
                    if (s[i] == c) { i++; break; }
                    value.Append(s[i++]);
                }
                tokens.Add(new Token(TokenKind.Text, value.ToString(), start));
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                string digits = s[start..i];
                if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new SyntaxError($"'{digits}' is not a number.", start);

                int unit = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                tokens.Add(i > unit
                    ? new Token(TokenKind.Duration, s[start..i], start)
                    : new Token(TokenKind.Number, digits, start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Word, s[start..i], start));
                continue;
            }

            string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "==" or "!=" or "=~" or ">=" or "<=")
            {
                tokens.Add(new Token(TokenKind.Symbol, two, i));
                i += 2;
                continue;
            }

            // !has and !contains are one operator, as in Kusto.
            if (c == '!' && i + 1 < s.Length && char.IsLetter(s[i + 1]))
            {
                int start = i++;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                tokens.Add(new Token(TokenKind.Word, s[start..i], start));
                continue;
            }

            if (c is '|' or ',' or '(' or ')' or '>' or '<' or '=')
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString(), i++));
                continue;
            }

            throw new SyntaxError($"'{c}' is not part of the language.", i);
        }

        tokens.Add(new Token(TokenKind.End, "", s.Length));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens, int length)
    {
        private int at;
        private int depth;
        private List<LogColumn> columns = [.. LogColumns];

        private Token Current => tokens[at];

        private Token Next() => tokens[at++];

        private bool IsWord(string word) =>
            Current.Kind == TokenKind.Word && Current.Value.Equals(word, StringComparison.OrdinalIgnoreCase);

        private bool IsSymbol(string symbol) => Current.Kind == TokenKind.Symbol && Current.Value == symbol;

        private bool TakeWord(string word)
        {
            if (!IsWord(word)) return false;
            at++;
            return true;
        }

        private bool TakeSymbol(string symbol)
        {
            if (!IsSymbol(symbol)) return false;
            at++;
            return true;
        }

        private void ExpectSymbol(string symbol)
        {
            if (!TakeSymbol(symbol)) throw Expected($"'{symbol}'");
        }

        private SyntaxError Expected(string what) => new(
            Current.Kind == TokenKind.End
                ? $"Expected {what}, but the query ended."
                : $"Expected {what}, but found '{Current.Value}'.",
            Current.Kind == TokenKind.End ? length : Current.Position);

        public LogPipelineQuery ParseQuery()
        {
            TakeWord("logs");

            // A query may start straight at a stage, with or without the leading pipe.
            List<LogStage> stages = [];

            if (!IsSymbol("|") && Current.Kind != TokenKind.End) AddStage(stages);

            while (TakeSymbol("|"))
            {
                if (stages.Count >= MaxStages)
                    throw new SyntaxError($"A query can have at most {MaxStages} stages.", Current.Position);
                AddStage(stages);
            }

            if (Current.Kind != TokenKind.End) throw Expected("'|' or the end of the query");

            return new LogPipelineQuery(stages);
        }

        private void AddStage(List<LogStage> stages)
        {
            if (TakeWord("where")) stages.Add(new WhereStage(ParseOr()));
            else if (TakeWord("project")) stages.Add(ParseProject());
            else if (TakeWord("summarize")) stages.Add(ParseSummarize());
            else if (TakeWord("take") || TakeWord("limit")) stages.Add(new TakeStage(ParseCount()));
            else if (TakeWord("sort") || TakeWord("order")) stages.Add(ParseSort());
            else if (TakeWord("top"))
            {
                // top is a sort and a take, which is all the engine needs to know about it.
                int count = ParseCount();
                if (!TakeWord("by")) throw Expected("'by'");
                (string column, bool descending) = ParseSortKey();
                stages.Add(new SortStage(column, descending));
                stages.Add(new TakeStage(count));
            }
            else
            {
                throw Expected("where, project, summarize, sort, top or take");
            }
        }

        private int ParseCount()
        {
            if (Current.Kind != TokenKind.Number) throw Expected("a number of rows");
            Token token = Next();
            double value = token.Number;

            if (value != Math.Floor(value) || value < 1 || value > MaxTake)
                throw new SyntaxError($"The number of rows must be a whole number from 1 to {MaxTake}.", token.Position);

            return (int)value;
        }

        private LogStage ParseSort()
        {
            if (!TakeWord("by")) throw Expected("'by'");
            (string column, bool descending) = ParseSortKey();
            return new SortStage(column, descending);
        }

        private (string Column, bool Descending) ParseSortKey()
        {
            string column = ParseColumn().Name;
            if (TakeWord("asc")) return (column, false);
            TakeWord("desc");
            return (column, true);
        }

        private LogStage ParseProject()
        {
            List<LogColumn> kept = [ParseColumn()];
            while (TakeSymbol(",")) kept.Add(ParseColumn());

            columns = [.. kept.DistinctBy(c => c.Name)];
            return new ProjectStage([.. columns.Select(c => c.Name)]);
        }

        private LogStage ParseSummarize()
        {
            List<LogAggregate> aggregates = [ParseAggregate()];
            while (TakeSymbol(",")) aggregates.Add(ParseAggregate());

            List<LogGroupKey> keys = [];
            if (TakeWord("by"))
            {
                keys.Add(ParseGroupKey());
                while (TakeSymbol(",")) keys.Add(ParseGroupKey());
            }

            List<LogColumn> output = [];
            foreach (LogGroupKey key in keys)
            {
                output.Add(key.Pattern
                    ? new LogColumn(key.OutputName, LogColumnType.Text)
                    : columns.First(c => c.Name == key.Column));
            }
            output.AddRange(aggregates.Select(a => new LogColumn(a.Alias, LogColumnType.Number)));

            string? duplicate = output.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1)?.Key;
            if (duplicate is not null)
                throw new SyntaxError($"'{duplicate}' would appear twice in the result. Give one a name.", Current.Position);

            columns = output;
            return new SummarizeStage(aggregates, keys);
        }

        private LogAggregate ParseAggregate()
        {
            string? alias = null;

            if (Current.Kind == TokenKind.Word && tokens[at + 1] is { Kind: TokenKind.Symbol, Value: "=" })
            {
                alias = Next().Value;
                at++;
            }

            if (TakeWord("count"))
            {
                ExpectSymbol("(");
                ExpectSymbol(")");
                return new LogAggregate(alias ?? "count_", null);
            }

            if (TakeWord("dcount"))
            {
                ExpectSymbol("(");
                LogColumn column = ParseColumn();
                ExpectSymbol(")");
                return new LogAggregate(alias ?? "dcount_" + column.Name, column.Name);
            }

            throw Expected("count() or dcount(column)");
        }

        private LogGroupKey ParseGroupKey()
        {
            if (TakeWord("pattern"))
            {
                ExpectSymbol("(");
                int at = Current.Position;
                LogColumn text = ParseColumn();
                if (text.Type != LogColumnType.Text)
                    throw new SyntaxError($"pattern() needs a text column, and '{text.Name}' is not one.", at);

                ExpectSymbol(")");
                return new LogGroupKey(text.Name, null, Pattern: true);
            }

            if (!TakeWord("bin")) return new LogGroupKey(ParseColumn().Name, null);

            ExpectSymbol("(");
            int position = Current.Position;
            LogColumn column = ParseColumn();
            if (column.Type != LogColumnType.Time)
                throw new SyntaxError($"bin() needs a time column, and '{column.Name}' is not one.", position);

            ExpectSymbol(",");
            TimeSpan width = ParseDuration();
            ExpectSymbol(")");
            return new LogGroupKey(column.Name, width);
        }

        private TimeSpan ParseDuration()
        {
            if (Current.Kind != TokenKind.Duration) throw Expected("a duration such as 5m");
            Token token = Next();

            int unitAt = token.Value.TakeWhile(c => char.IsDigit(c) || c == '.').Count();
            double amount = double.Parse(token.Value[..unitAt], CultureInfo.InvariantCulture);

            TimeSpan width = token.Value[unitAt..].ToLowerInvariant() switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => throw new SyntaxError($"'{token.Value}' is not a duration. Use s, m, h or d.", token.Position),
            };

            if (width < TimeSpan.FromSeconds(1))
                throw new SyntaxError("A bin must be at least one second wide.", token.Position);

            return width;
        }

        private LogColumn ParseColumn()
        {
            if (Current.Kind != TokenKind.Word) throw Expected("a column");

            Token token = Next();
            LogColumn? column = columns.FirstOrDefault(c => c.Name.Equals(token.Value, StringComparison.OrdinalIgnoreCase));

            return column ?? throw new SyntaxError(
                $"There is no column '{token.Value}' here. The columns are: {string.Join(", ", columns.Select(c => c.Name))}.",
                token.Position);
        }

        private LogExpression ParseOr()
        {
            LogExpression left = ParseAnd();
            while (TakeWord("or")) left = new LogOr(left, ParseAnd());
            return left;
        }

        private LogExpression ParseAnd()
        {
            LogExpression left = ParseUnary();
            while (TakeWord("and")) left = new LogAnd(left, ParseUnary());
            return left;
        }

        private LogExpression ParseUnary()
        {
            if (++depth > MaxDepth)
                throw new SyntaxError($"The condition is nested more than {MaxDepth} levels deep.", Current.Position);

            try
            {
                if (TakeWord("not")) return new LogNot(ParseUnary());

                if (TakeSymbol("("))
                {
                    LogExpression inner = ParseOr();
                    ExpectSymbol(")");
                    return inner;
                }

                return ParseComparison();
            }
            finally
            {
                depth--;
            }
        }

        private LogExpression ParseComparison()
        {
            int position = Current.Position;
            LogColumn column = ParseColumn();

            Token opToken = Current;
            LogCompareOp op = ParseOperator();

            switch (column.Type)
            {
                case LogColumnType.Time:
                    throw new SyntaxError("The time range is set beside the query, so timestamp cannot be compared here.", position);

                case LogColumnType.Number:
                    if (op is not (LogCompareOp.Equal or LogCompareOp.NotEqual or LogCompareOp.GreaterOrEqual
                        or LogCompareOp.Greater or LogCompareOp.LessOrEqual or LogCompareOp.Less))
                    {
                        throw new SyntaxError($"'{opToken.Value}' does not apply to the number '{column.Name}'.", opToken.Position);
                    }
                    if (Current.Kind != TokenKind.Number) throw Expected("a number");
                    return new LogComparison(column.Name, op, Next().Number);

                case LogColumnType.Level:
                    if (op is not (LogCompareOp.Equal or LogCompareOp.NotEqual or LogCompareOp.GreaterOrEqual
                        or LogCompareOp.Greater or LogCompareOp.LessOrEqual or LogCompareOp.Less))
                    {
                        throw new SyntaxError($"'{opToken.Value}' does not apply to level.", opToken.Position);
                    }
                    if (Current.Kind is not (TokenKind.Word or TokenKind.Text)) throw Expected("a level such as error");
                    Token word = Next();
                    LogLevel level = LogLevelMap.FromText(word.Value)
                        ?? throw new SyntaxError($"'{word.Value}' is not a level. Use debug, info, warn, error or fatal.", word.Position);
                    return new LogComparison(column.Name, op, level);

                default:
                    if (op is LogCompareOp.GreaterOrEqual or LogCompareOp.Greater or LogCompareOp.LessOrEqual or LogCompareOp.Less)
                        throw new SyntaxError($"'{opToken.Value}' does not apply to the text '{column.Name}'.", opToken.Position);
                    if (Current.Kind != TokenKind.Text) throw Expected("a quoted string");
                    return new LogComparison(column.Name, op, Next().Value);
            }
        }

        private LogCompareOp ParseOperator()
        {
            Token token = Current;

            LogCompareOp? op = (token.Kind, token.Value.ToLowerInvariant()) switch
            {
                (TokenKind.Symbol, "==") => LogCompareOp.Equal,
                (TokenKind.Symbol, "!=") => LogCompareOp.NotEqual,
                (TokenKind.Symbol, "=~") => LogCompareOp.EqualIgnoreCase,
                (TokenKind.Symbol, ">=") => LogCompareOp.GreaterOrEqual,
                (TokenKind.Symbol, ">") => LogCompareOp.Greater,
                (TokenKind.Symbol, "<=") => LogCompareOp.LessOrEqual,
                (TokenKind.Symbol, "<") => LogCompareOp.Less,
                (TokenKind.Word, "has") => LogCompareOp.Has,
                (TokenKind.Word, "!has") => LogCompareOp.NotHas,
                (TokenKind.Word, "contains") => LogCompareOp.Contains,
                (TokenKind.Word, "!contains") => LogCompareOp.NotContains,
                (TokenKind.Word, "startswith") => LogCompareOp.StartsWith,
                (TokenKind.Word, "endswith") => LogCompareOp.EndsWith,
                _ => null,
            };

            if (op is null) throw Expected("a comparison such as ==, has or contains");

            at++;
            return op.Value;
        }
    }
}
