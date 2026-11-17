using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Services.Support;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>A search the parser could not read, said so the person can fix it.</summary>
public sealed class TicketSearchException(string message) : Exception(message);

/// <summary>
/// Zammad's search syntax over tickets, compiled to a database query (docs/helpdesk.md, phase 3).
///
/// <para><b>Compiled to EF, not to a search engine.</b> EntKube runs on three databases and on
/// several instances at once; a node-local index would answer differently on each instance, and a
/// search service is one more thing to run. What a query here can say is therefore what portable
/// SQL can say: <c>field:value</c>, quoted phrases, <c>*</c> wildcards, ranges <c>[a TO b]</c> with
/// open ends, comparisons <c>&gt;now-1h</c> and <c>&lt;=2026-11-01</c>, <c>AND</c>, <c>OR</c>,
/// <c>NOT</c>/<c>!</c> and parentheses, and <c>field:(a OR b)</c>. Fuzzy <c>~</c> and regular
/// expressions are not supported, and say so.</para>
///
/// <para>Case-insensitive throughout, by comparing lower-cased text, which every provider
/// translates the same way. Dates are Swedish calendar days, like everything else here.</para>
/// </summary>
public static partial class TicketSearch
{
    /// <summary>The fields, with their aliases, for the help text and the error message.</summary>
    public static readonly IReadOnlyList<(string Field, string Aliases, string Takes)> Fields =
    [
        ("number", "#", "a ticket number, or a range"),
        ("title", "", "words"),
        ("text", "body, article.body", "words in the description or anywhere on the history"),
        ("status", "state, state.name", "new, open (in progress), waiting (pending), resolved, closed, rejected, merged"),
        ("priority", "priority.name", "P1 to P4"),
        ("customer", "customer.name, organization, organization.name", "words in the customer's name"),
        ("app", "application", "words in the application's name"),
        ("assignee", "owner", "words in the name of whoever has it; nobody for unassigned"),
        ("requester", "customer.email, reporter", "words in the reporter's name or address"),
        ("tag", "tags", "a tag"),
        ("channel", "create_article_type.name", "email, portal, phone (conversation), monitoring, integration, feedback"),
        ("reported", "created, created_at", "a date, a range or a comparison"),
        ("updated", "updated_at", "a date, a range or a comparison"),
        ("closed", "close_at, closed_at", "a date, a range or a comparison"),
        ("reminder", "pending_time, reminder_at", "a date, a range or a comparison"),
    ];

    private static readonly Dictionary<string, string> Canonical = BuildCanonical();

    private static Dictionary<string, string> BuildCanonical()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string field, string aliases, _) in Fields)
        {
            map[field] = field;

            foreach (string alias in aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                map[alias] = field;
            }
        }

        return map;
    }

    // ───────────────────────────── The tree ─────────────────────────────

    public abstract record Node;
    public sealed record AndNode(Node Left, Node Right) : Node;
    public sealed record OrNode(Node Left, Node Right) : Node;
    public sealed record NotNode(Node Inner) : Node;

    /// <summary>One condition. <see cref="Field"/> null means "anywhere".</summary>
    public sealed record TermNode(string? Field, Value Value) : Node;

    public abstract record Value;
    public sealed record TextValue(string Text, bool Quoted) : Value;
    public sealed record RangeValue(string? From, string? To) : Value;
    public sealed record CompareValue(string Op, string Operand) : Value;

    // ───────────────────────────── Parsing ─────────────────────────────

    /// <summary>Reads a query into its tree. Throws <see cref="TicketSearchException"/> with what was wrong.</summary>
    public static Node Parse(string query)
    {
        Parser p = new(Tokenise(query ?? ""));
        Node node = p.ParseOr(defaultField: null);

        if (!p.AtEnd)
        {
            throw new TicketSearchException($"Did not expect “{p.Peek().Text}” there.");
        }

        return node;
    }

    private enum Kind { Word, Quoted, Range, Open, Close, And, Or, Not, Field }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenise(string q)
    {
        List<Token> tokens = [];
        int i = 0;

        while (i < q.Length)
        {
            char c = q[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { tokens.Add(new(Kind.Open, "(")); i++; continue; }
            if (c == ')') { tokens.Add(new(Kind.Close, ")")); i++; continue; }

            if (c == '!' || (c == '-' && (tokens.Count == 0 || tokens[^1].Kind is Kind.Open or Kind.And or Kind.Or or Kind.Not)
                             && i + 1 < q.Length && !char.IsWhiteSpace(q[i + 1])))
            {
                tokens.Add(new(Kind.Not, "!"));
                i++;
                continue;
            }

            if (c == '"')
            {
                int end = q.IndexOf('"', i + 1);
                if (end < 0) throw new TicketSearchException("A quotation mark is never closed.");
                tokens.Add(new(Kind.Quoted, q[(i + 1)..end]));
                i = end + 1;
                continue;
            }

            if (c == '[' || c == '{')
            {
                int end = q.IndexOfAny([']', '}'], i + 1);
                if (end < 0) throw new TicketSearchException("A range [a TO b] is never closed.");
                tokens.Add(new(Kind.Range, q[(i + 1)..end]));
                i = end + 1;
                continue;
            }

            // A word runs to whitespace or a bracket; "field:" ends at its colon.
            int start = i;
            StringBuilder word = new();

            while (i < q.Length && !char.IsWhiteSpace(q[i]) && q[i] is not ('(' or ')' or '"' or '[' or '{'))
            {
                if (q[i] == '\\' && i + 1 < q.Length)
                {
                    word.Append(q[i + 1]);
                    i += 2;
                    continue;
                }

                if (q[i] == ':' && word.Length > 0)
                {
                    tokens.Add(new(Kind.Field, word.ToString()));
                    i++;
                    word.Clear();

                    // "field: value" — Zammad tolerates the space.
                    while (i < q.Length && char.IsWhiteSpace(q[i])) i++;
                    start = i;
                    break;
                }

                word.Append(q[i]);
                i++;
            }

            if (word.Length > 0)
            {
                string w = word.ToString();
                tokens.Add(w switch
                {
                    "AND" or "&&" => new(Kind.And, w),
                    "OR" or "||" => new(Kind.Or, w),
                    "NOT" => new(Kind.Not, w),
                    _ => new(Kind.Word, w),
                });
            }
            else if (i == start && tokens.Count > 0 && tokens[^1].Kind == Kind.Field && i >= q.Length)
            {
                throw new TicketSearchException($"“{tokens[^1].Text}:” needs a value.");
            }
        }

        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int at;

        public bool AtEnd => at >= tokens.Count;

        public Token Peek() => tokens[at];

        public Node ParseOr(string? defaultField)
        {
            Node left = ParseAnd(defaultField);

            while (!AtEnd && Peek().Kind == Kind.Or)
            {
                at++;
                left = new OrNode(left, ParseAnd(defaultField));
            }

            return left;
        }

        private Node ParseAnd(string? defaultField)
        {
            Node left = ParseUnary(defaultField);

            while (!AtEnd && Peek().Kind is not (Kind.Or or Kind.Close))
            {
                if (Peek().Kind == Kind.And)
                {
                    at++;
                }

                left = new AndNode(left, ParseUnary(defaultField));
            }

            return left;
        }

        private Node ParseUnary(string? defaultField)
        {
            if (AtEnd)
            {
                throw new TicketSearchException("The search ends where a term was expected.");
            }

            if (Peek().Kind == Kind.Not)
            {
                at++;
                return new NotNode(ParseUnary(defaultField));
            }

            return ParsePrimary(defaultField);
        }

        private Node ParsePrimary(string? defaultField)
        {
            Token t = tokens[at++];

            switch (t.Kind)
            {
                case Kind.Open:
                {
                    Node inner = ParseOr(defaultField);
                    if (AtEnd || tokens[at].Kind != Kind.Close) throw new TicketSearchException("A parenthesis is never closed.");
                    at++;
                    return inner;
                }

                case Kind.Field:
                {
                    if (!Canonical.TryGetValue(t.Text, out string? field))
                    {
                        throw new TicketSearchException(
                            $"There is no field “{t.Text}”. Fields: {string.Join(", ", Fields.Select(f => f.Field))}.");
                    }

                    if (AtEnd) throw new TicketSearchException($"“{t.Text}:” needs a value.");

                    // field:(a OR b) — the field applies to everything inside.
                    if (Peek().Kind == Kind.Open)
                    {
                        at++;
                        Node inner = ParseOr(field);
                        if (AtEnd || tokens[at].Kind != Kind.Close) throw new TicketSearchException("A parenthesis is never closed.");
                        at++;
                        return inner;
                    }

                    return new TermNode(field, ValueOf(tokens[at++]));
                }

                case Kind.Word or Kind.Quoted or Kind.Range:
                    return new TermNode(defaultField, ValueOf(t));

                default:
                    throw new TicketSearchException($"Did not expect “{t.Text}” there.");
            }
        }

        private static Value ValueOf(Token t)
        {
            switch (t.Kind)
            {
                case Kind.Quoted:
                    return new TextValue(t.Text, Quoted: true);

                case Kind.Range:
                {
                    string[] ends = RangeTo().Split(t.Text);
                    if (ends.Length != 2) throw new TicketSearchException($"A range is written [from TO to], not [{t.Text}].");
                    string? from = ends[0].Trim() is "*" or "" ? null : ends[0].Trim();
                    string? to = ends[1].Trim() is "*" or "" ? null : ends[1].Trim();
                    return new RangeValue(from, to);
                }

                case Kind.Word:
                {
                    if (t.Text.Contains('~')) throw new TicketSearchException("Fuzzy search (~) is not supported here.");
                    if (t.Text.Length > 1 && t.Text.StartsWith('/') && t.Text.EndsWith('/'))
                        throw new TicketSearchException("Regular expressions are not supported here.");

                    Match m = Comparison().Match(t.Text);
                    return m.Success ? new CompareValue(m.Groups[1].Value, m.Groups[2].Value) : new TextValue(t.Text, Quoted: false);
                }

                default:
                    throw new TicketSearchException($"“{t.Text}” is not a value.");
            }
        }
    }

    // ───────────────────────────── Compiling ─────────────────────────────

    /// <summary>The tree as a condition on a ticket, evaluated in the database.</summary>
    public static Expression<Func<Ticket, bool>> Compile(Node node, DateTime now)
    {
        ParameterExpression t = Expression.Parameter(typeof(Ticket), "t");
        return Expression.Lambda<Func<Ticket, bool>>(Build(node, t, now), t);
    }

    private static Expression Build(Node node, ParameterExpression t, DateTime now) => node switch
    {
        AndNode a => Expression.AndAlso(Build(a.Left, t, now), Build(a.Right, t, now)),
        OrNode o => Expression.OrElse(Build(o.Left, t, now), Build(o.Right, t, now)),
        NotNode n => Expression.Not(Build(n.Inner, t, now)),
        TermNode term => Inline(Term(term, now), t),
        _ => throw new TicketSearchException("Unknown search."),
    };

    /// <summary>Substitutes the shared parameter into a term's own lambda.</summary>
    private static Expression Inline(Expression<Func<Ticket, bool>> lambda, ParameterExpression t) =>
        new Rebind(lambda.Parameters[0], t).Visit(lambda.Body);

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    private static Expression<Func<Ticket, bool>> Term(TermNode term, DateTime now)
    {
        Value v = term.Value;

        switch (term.Field)
        {
            case null:
            {
                string text = Text(v, "a search term");
                if (int.TryParse(text.TrimStart('#'), out int n))
                {
                    return t => t.Number == n;
                }

                string like = Like(text, ((TextValue)v).Quoted);
                return t => EF.Functions.Like(t.Title.ToLower(), like, Escape)
                            || EF.Functions.Like(t.Description.ToLower(), like, Escape)
                            || t.Events.Any(e => EF.Functions.Like(e.Detail.ToLower(), like, Escape));
            }

            case "number":
                return NumberTerm(v);

            case "title":
            {
                string like = LikeOf(v, "title");
                return t => EF.Functions.Like(t.Title.ToLower(), like, Escape);
            }

            case "text":
            {
                string like = LikeOf(v, "text");
                return t => EF.Functions.Like(t.Description.ToLower(), like, Escape)
                            || t.Events.Any(e => EF.Functions.Like(e.Detail.ToLower(), like, Escape));
            }

            case "customer":
            {
                string like = LikeOf(v, "customer");
                return t => EF.Functions.Like(t.Customer.Name.ToLower(), like, Escape);
            }

            case "app":
            {
                string like = LikeOf(v, "app");
                return t => t.App != null && EF.Functions.Like(t.App.Name.ToLower(), like, Escape);
            }

            case "assignee":
            {
                string text = Text(v, "assignee");
                if (text.Equals("nobody", StringComparison.OrdinalIgnoreCase) || text == "-")
                {
                    return t => t.Assignee == null;
                }

                string like = Like(text, ((TextValue)v).Quoted);
                return t => t.Assignee != null && EF.Functions.Like(t.Assignee.ToLower(), like, Escape);
            }

            case "requester":
            {
                string like = LikeOf(v, "requester");
                return t => (t.RequestedBy != null && EF.Functions.Like(t.RequestedBy.ToLower(), like, Escape))
                            || (t.RequestedByEmail != null && EF.Functions.Like(t.RequestedByEmail.ToLower(), like, Escape));
            }

            case "tag":
            {
                string text = Text(v, "tag");
                if (text.Contains('*'))
                {
                    string like = Like(text, quoted: false);
                    return t => t.Tags.Any(g => EF.Functions.Like(g.Name, like, Escape));
                }

                string name = TicketWorkService.NormaliseTag(text) ?? throw new TicketSearchException($"“{text}” cannot be a tag.");
                return t => t.Tags.Any(g => g.Name == name);
            }

            case "status":
                return EnumTerm<TicketStatus>(v, "status", StatusOf, s => t => t.Status == s);

            case "priority":
                return EnumTerm<TicketPriority>(v, "priority",
                    w => Enum.TryParse(w, true, out TicketPriority p) && Enum.IsDefined(p) ? [p] : [], p => t => t.Priority == p);

            case "channel":
                return EnumTerm<TicketChannel>(v, "channel", ChannelOf, c => t => t.Channel == c);

            case "reported":
                return DateTerm(v, now, "reported", t => t.ReportedAt);

            case "updated":
                return DateTerm(v, now, "updated", t => t.UpdatedAt);

            case "closed":
                return DateTermNullable(v, now, "closed", t => t.ClosedAt);

            case "reminder":
                return DateTermNullable(v, now, "reminder", t => t.ReminderAt);

            default:
                throw new TicketSearchException($"There is no field “{term.Field}”.");
        }
    }

    private static string Text(Value v, string what) => v is TextValue tv
        ? tv.Text
        : throw new TicketSearchException($"{what} takes words, not a range or a comparison.");

    private static string LikeOf(Value v, string what) => Like(Text(v, what), ((TextValue)v).Quoted);

    /// <summary>The escape character every LIKE here declares, so all three providers read patterns alike.</summary>
    private const string Escape = "\\";

    /// <summary>
    /// A LIKE pattern for lower-cased text: contains the words, or — with <c>*</c> — matches the
    /// wildcard. The characters LIKE treats specially are escaped out of what was typed. A quoted
    /// phrase is a plain contains, its <c>*</c> taken literally.
    /// </summary>
    private static string Like(string text, bool quoted)
    {
        string lower = text.ToLowerInvariant();

        // LIKE's own characters, escaped with an explicit escape character: the bracket form
        // ([%]) is SQL Server's alone, and SQLite and Postgres would read it literally.
        string escaped = lower.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        if (quoted || !lower.Contains('*'))
        {
            return $"%{escaped}%";
        }

        return escaped.Replace('*', '%');
    }

    private static Expression<Func<Ticket, bool>> NumberTerm(Value v)
    {
        static int N(string s) => int.TryParse(s.TrimStart('#'), out int n)
            ? n
            : throw new TicketSearchException($"“{s}” is not a ticket number.");

        switch (v)
        {
            case TextValue tv:
            {
                int n = N(tv.Text);
                return t => t.Number == n;
            }
            case RangeValue r:
            {
                int from = r.From is null ? int.MinValue : N(r.From);
                int to = r.To is null ? int.MaxValue : N(r.To);
                return t => t.Number >= from && t.Number <= to;
            }
            case CompareValue c:
            {
                int n = N(c.Operand);
                return c.Op switch
                {
                    ">" => t => t.Number > n,
                    ">=" => t => t.Number >= n,
                    "<" => t => t.Number < n,
                    _ => t => t.Number <= n,
                };
            }
            default:
                throw new TicketSearchException("number takes a number, a range or a comparison.");
        }
    }

    private static Expression<Func<Ticket, bool>> EnumTerm<TEnum>(
        Value v, string what, Func<string, IReadOnlyList<TEnum>> read, Func<TEnum, Expression<Func<Ticket, bool>>> one)
        where TEnum : struct, Enum
    {
        string text = Text(v, what);
        IReadOnlyList<TEnum> values = read(text);

        if (text.EndsWith('*'))
        {
            string prefix = text.TrimEnd('*');
            values = [.. Enum.GetValues<TEnum>().Where(e => Name(e).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                                       || read(prefix).Contains(e))];
        }

        if (values.Count == 0)
        {
            throw new TicketSearchException($"“{text}” is not a {what}.");
        }

        ParameterExpression t = Expression.Parameter(typeof(Ticket), "t");
        Expression body = values.Select(e => Inline(one(e), t)).Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<Ticket, bool>>(body, t);

        static string Name(TEnum e) => e.ToString();
    }

    /// <summary>Our statuses by our names, and by Zammad's.</summary>
    private static IReadOnlyList<TicketStatus> StatusOf(string word) => word.ToLowerInvariant() switch
    {
        "new" => [TicketStatus.New],
        "open" or "inprogress" or "in-progress" or "in_progress" => [TicketStatus.InProgress],
        "waiting" or "pending" or "paused" => [TicketStatus.Waiting],
        "resolved" or "pending close" => [TicketStatus.Resolved],
        "closed" => [TicketStatus.Closed],
        "rejected" => [TicketStatus.Rejected],
        "merged" => [TicketStatus.Merged],
        _ => [],
    };

    private static IReadOnlyList<TicketChannel> ChannelOf(string word) => word.ToLowerInvariant() switch
    {
        "email" or "mail" => [TicketChannel.Email],
        "portal" or "web" => [TicketChannel.Portal],
        "phone" or "call" or "conversation" or "teams" => [TicketChannel.Conversation],
        "monitoring" or "alert" => [TicketChannel.Monitoring],
        "integration" or "bridge" => [TicketChannel.Integration],
        "feedback" => [TicketChannel.Feedback],
        _ => [],
    };

    private static Expression<Func<Ticket, bool>> DateTerm(
        Value v, DateTime now, string what, Expression<Func<Ticket, DateTime>> field)
    {
        (DateTime? from, DateTime? to) = Window(v, now, what);
        ParameterExpression t = field.Parameters[0];
        Expression body = Expression.Constant(true);

        if (from is DateTime f) body = Expression.AndAlso(body, Expression.GreaterThanOrEqual(field.Body, Expression.Constant(f)));
        if (to is DateTime u) body = Expression.AndAlso(body, Expression.LessThan(field.Body, Expression.Constant(u)));

        return Expression.Lambda<Func<Ticket, bool>>(body, t);
    }

    private static Expression<Func<Ticket, bool>> DateTermNullable(
        Value v, DateTime now, string what, Expression<Func<Ticket, DateTime?>> field)
    {
        (DateTime? from, DateTime? to) = Window(v, now, what);
        ParameterExpression t = field.Parameters[0];
        Expression body = Expression.NotEqual(field.Body, Expression.Constant(null, typeof(DateTime?)));

        if (from is DateTime f) body = Expression.AndAlso(body, Expression.GreaterThanOrEqual(field.Body, Expression.Constant(f, typeof(DateTime?))));
        if (to is DateTime u) body = Expression.AndAlso(body, Expression.LessThan(field.Body, Expression.Constant(u, typeof(DateTime?))));

        return Expression.Lambda<Func<Ticket, bool>>(body, t);
    }

    /// <summary>
    /// A date value as a half-open UTC window [from, to). A plain day is that whole Swedish day;
    /// a range includes both ends' days; a comparison is exact to the instant.
    /// </summary>
    private static (DateTime? From, DateTime? To) Window(Value v, DateTime now, string what)
    {
        switch (v)
        {
            case TextValue tv:
            {
                (DateTime start, DateTime end) = Day(tv.Text, now, what);
                return (start, end);
            }
            case RangeValue r:
                return (r.From is null ? null : Day(r.From, now, what).Start, r.To is null ? null : Day(r.To, now, what).End);
            case CompareValue c:
            {
                (DateTime start, DateTime end) = Day(c.Operand, now, what);
                bool instant = c.Operand.StartsWith("now", StringComparison.OrdinalIgnoreCase);
                return c.Op switch
                {
                    ">" => (instant ? start : end, null),
                    ">=" => (start, null),
                    "<" => (null, start),
                    _ => (null, instant ? start : end),
                };
            }
            default:
                throw new TicketSearchException($"{what} takes a date.");
        }
    }

    /// <summary>
    /// A date as the UTC span of that Swedish day — or, for <c>now</c>, <c>now-2h</c>,
    /// <c>now-7d</c>, the instant itself (an empty span).
    /// </summary>
    private static (DateTime Start, DateTime End) Day(string text, DateTime now, string what)
    {
        Match rel = Relative().Match(text);

        if (rel.Success)
        {
            int amount = rel.Groups[2].Success ? int.Parse(rel.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            string unit = rel.Groups[3].Success ? rel.Groups[3].Value : "h";
            TimeSpan step = unit switch
            {
                "m" => TimeSpan.FromMinutes(1),
                "h" => TimeSpan.FromHours(1),
                "d" => TimeSpan.FromDays(1),
                "w" => TimeSpan.FromDays(7),
                _ => TimeSpan.FromDays(30),
            };
            DateTime at = rel.Groups[1].Value == "+" ? now + amount * step : now - amount * step;
            return (at, at);
        }

        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
        {
            throw new TicketSearchException($"{what} takes a date written YYYY-MM-DD, or now-2h, now-7d; not “{text}”.");
        }

        DateTime start = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), BusinessCalendar.SwedishTime);
        DateTime end = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), BusinessCalendar.SwedishTime);
        return (start, end);
    }

    [GeneratedRegex(@"^(>=|<=|>|<)(.+)$")]
    private static partial Regex Comparison();

    [GeneratedRegex(@"^now(?:([+-])(\d+)([mhdwM])?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Relative();

    [GeneratedRegex(@"\s+TO\s+")]
    private static partial Regex RangeTo();
}
