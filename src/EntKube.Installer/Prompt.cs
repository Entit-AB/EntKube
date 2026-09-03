using System.Text;

namespace EntKube.Installer;

/// <summary>
/// The console interface the wizard is built from.
///
/// Deliberately a terminal UI rather than a windowed one. The management plane is installed on a
/// server, and a server is reached over SSH far more often than it is sat in front of — a graphical
/// installer would be unusable in the most common case, and unavailable in the second most common
/// (a headless host with no display server at all). A terminal wizard works over SSH, in a
/// container, in a serial console, and on all three desktop platforms.
///
/// Every prompt has to behave when there is no human attached, because the same binary is used for
/// scripted installs. When <see cref="Interactive"/> is false nothing is asked: a value that has a
/// default takes it, and a value that does not is a fatal error naming the flag that would have
/// supplied it. Silently guessing a domain name or an admin password would be worse than stopping.
/// </summary>
public sealed class Prompt
{
    private readonly TextWriter _out;

    public Prompt(bool interactive, TextWriter? output = null)
    {
        _out = output ?? Console.Out;

        // Redirected input means a pipe or a here-doc, which cannot answer a question. Honour the
        // caller's request for interactivity only when there is genuinely a terminal to interact with.
        Interactive = interactive && !Console.IsInputRedirected;
    }

    public bool Interactive { get; }

    // ── Layout ───────────────────────────────────────────────────────────────────────────────────

    public void Blank() => _out.WriteLine();

    public void Heading(string text)
    {
        _out.WriteLine();
        _out.WriteLine(text);
        _out.WriteLine(new string('─', Math.Min(text.Length, 72)));
    }

    public void Info(string text) => _out.WriteLine(text);

    /// <summary>Indented explanatory text under a question. Wrapped, because these run long.</summary>
    public void Note(string text)
    {
        foreach (string line in Wrap(text, 74))
        {
            _out.WriteLine("  " + line);
        }
    }

    public void Step(string label, string outcome) => _out.WriteLine($"  {label,-38} {outcome}");

    /// <summary>
    /// A heading over a run of questions, and the guidance under it. Both are silent when nothing is
    /// being asked — a scripted run would otherwise print a page of headings with no content beneath
    /// them, which reads like something failed.
    /// </summary>
    public void Section(string text)
    {
        if (Interactive)
        {
            Heading(text);
        }
    }

    public void Guidance(string text)
    {
        if (Interactive)
        {
            Note(text);
        }
    }

    public void Warn(string text) => _out.WriteLine($"  ! {text}");

    // ── Questions ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Free text. <paramref name="validate"/> returns null when the answer is acceptable, or the
    /// reason it is not — which is re-asked rather than accepted, so a typo in a domain name is
    /// caught here instead of by a failed ACME challenge ten minutes later.
    /// </summary>
    public string Text(
        string question,
        string? @default = null,
        string? note = null,
        Func<string, string?>? validate = null)
    {
        while (true)
        {
            if (!Interactive)
            {
                string? unattended = @default
                    ?? throw new InstallAbortedException(
                        $"{question} has no default and cannot be asked for in a non-interactive run.");

                string? complaint = validate?.Invoke(unattended);
                if (complaint is not null)
                {
                    throw new InstallAbortedException($"{question}: {complaint}");
                }

                return unattended;
            }

            Ask(question, note, @default);
            string answer = (Console.ReadLine() ?? string.Empty).Trim();

            if (answer.Length == 0 && @default is not null)
            {
                answer = @default;
            }

            if (answer.Length == 0)
            {
                Warn("A value is required.");
                continue;
            }

            string? problem = validate?.Invoke(answer);
            if (problem is not null)
            {
                Warn(problem);
                continue;
            }

            return answer;
        }
    }

    /// <summary>
    /// Free text that may be left empty — an optional setting rather than one with a default.
    /// </summary>
    public string? OptionalText(string question, string? @default = null, string? note = null)
    {
        if (!Interactive)
        {
            return string.IsNullOrWhiteSpace(@default) ? null : @default;
        }

        Ask(question, note, @default ?? "(none)");
        string answer = (Console.ReadLine() ?? string.Empty).Trim();

        if (answer.Length == 0)
        {
            return string.IsNullOrWhiteSpace(@default) ? null : @default;
        }

        // An explicit "-" clears a value that a previous install left behind. Without it there is no
        // way to *remove* an existing setting: an empty line means "keep the default", and the
        // default is the existing value.
        return answer == "-" ? null : answer;
    }

    /// <summary>
    /// A secret. Echo is suppressed where the terminal allows it. Where it does not — a console
    /// with no key-reading support — the input is read normally and the operator is told plainly
    /// that it is visible, rather than being left to assume it was hidden.
    /// </summary>
    public string Secret(string question, string? note = null)
    {
        if (!Interactive)
        {
            throw new InstallAbortedException(
                $"{question} cannot be asked for in a non-interactive run; pass it as a flag.");
        }

        while (true)
        {
            Ask(question, note, null);

            string value;
            try
            {
                value = ReadHidden();
            }
            catch (InvalidOperationException)
            {
                Warn("This terminal cannot hide input — what you type will be visible.");
                value = (Console.ReadLine() ?? string.Empty).Trim();
            }

            if (value.Length > 0)
            {
                return value;
            }

            Warn("A value is required.");
        }
    }

    private static string ReadHidden()
    {
        StringBuilder buffer = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString().Trim();

                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Length--;
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }

    public bool YesNo(string question, bool @default, string? note = null)
    {
        if (!Interactive)
        {
            return @default;
        }

        while (true)
        {
            Ask(question, note, @default ? "Y/n" : "y/N");
            string answer = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();

            switch (answer)
            {
                case "": return @default;
                case "y" or "yes": return true;
                case "n" or "no": return false;
                default: Warn("Answer y or n."); break;
            }
        }
    }

    /// <summary>
    /// One of a fixed set. Returns the chosen option's key.
    /// </summary>
    public string Choice(string question, IReadOnlyList<Option> options, string @default, string? note = null)
    {
        if (options.All(o => o.Key != @default))
        {
            throw new ArgumentException($"Default '{@default}' is not one of the options.", nameof(@default));
        }

        if (!Interactive)
        {
            return @default;
        }

        _out.WriteLine();
        _out.WriteLine(question);

        if (note is not null)
        {
            Note(note);
        }

        for (int i = 0; i < options.Count; i++)
        {
            string marker = options[i].Key == @default ? "*" : " ";
            _out.WriteLine($"  {marker} {i + 1}) {options[i].Label}");

            foreach (string line in Wrap(options[i].Description, 66))
            {
                _out.WriteLine($"       {line}");
            }
        }

        while (true)
        {
            _out.Write($"  [1-{options.Count}, default {options.First(o => o.Key == @default).Label}]: ");
            string answer = (Console.ReadLine() ?? string.Empty).Trim();

            if (answer.Length == 0)
            {
                return @default;
            }

            if (int.TryParse(answer, out int index) && index >= 1 && index <= options.Count)
            {
                return options[index - 1].Key;
            }

            Warn($"Enter a number from 1 to {options.Count}.");
        }
    }

    private void Ask(string question, string? note, string? @default)
    {
        _out.WriteLine();
        _out.WriteLine(question);

        if (note is not null)
        {
            Note(note);
        }

        _out.Write(@default is null ? "  > " : $"  [{@default}] > ");
    }

    internal static IEnumerable<string> Wrap(string text, int width) => TextWrap.Wrap(text, width);

    public sealed record Option(string Key, string Label, string Description);
}
