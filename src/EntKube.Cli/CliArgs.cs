namespace EntKube.Cli;

/// <summary>
/// Parsed command-line options.
///
/// Deliberately tiny and hand-rolled: the surface is `--name value` and `--flag`,
/// and a parsing library would be a dependency carried into every published binary
/// to handle two forms.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Positional arguments left after the command words and options are removed.</summary>
    public IReadOnlyList<string> Positional { get; }

    private CliArgs(Dictionary<string, string?> options, List<string> positional)
    {
        this.options = options;
        Positional = positional;
    }

    public static CliArgs Parse(IEnumerable<string> args)
    {
        Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
        List<string> positional = [];

        string[] tokens = [.. args];
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(token);
                continue;
            }

            string name = token[2..];

            // Support --name=value as well as --name value; both are common enough
            // that rejecting either would just look broken.
            int equals = name.IndexOf('=');
            if (equals > 0)
            {
                options[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            // A following token that is itself an option means this was a flag.
            bool hasValue = i + 1 < tokens.Length
                            && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal);

            options[name] = hasValue ? tokens[++i] : null;
        }

        return new CliArgs(options, positional);
    }

    /// <summary>A required option value. Throws a usage error rather than failing later at the API.</summary>
    public string Required(string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CliUsageException($"--{name} is required.");

    public string? Optional(string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>True when the flag was given at all, with or without a value.</summary>
    public bool Flag(string name) => options.ContainsKey(name);

    /// <summary>True when an option was supplied but carries no value — i.e. used as a flag.</summary>
    public bool IsBareFlag(string name) => options.TryGetValue(name, out string? value) && value is null;
}
