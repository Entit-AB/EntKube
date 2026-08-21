using System.Text.Json;

namespace EntKube.Cli;

/// <summary>
/// Renders API responses as aligned text tables.
///
/// Column-aligned rather than JSON by default because the common use is a person
/// reading terminal output; `--json` gives the raw response for anything that needs
/// to be piped into jq.
/// </summary>
public static class TableWriter
{
    /// <summary>Cap on a rendered cell, so one long verdict cannot destroy the layout.</summary>
    private const int MaxCellWidth = 60;

    /// <summary>
    /// Writes rows as a table. Returns the number of rows written, which callers use
    /// to decide the exit code.
    /// </summary>
    public static int Write(TextWriter output, JsonElement root,
        (string Header, string Path)[] columns, string? rowsProperty)
    {
        JsonElement rows = root;

        if (rowsProperty is not null)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(rowsProperty, out rows))
            {
                output.WriteLine("(no rows in response)");
                return 0;
            }
        }

        if (rows.ValueKind != JsonValueKind.Array)
        {
            // Not a list — print the object as key/value pairs, which is what the
            // single-object endpoints like whoami return.
            WriteObject(output, rows);
            return 1;
        }

        List<JsonElement> items = [.. rows.EnumerateArray()];
        if (items.Count == 0)
        {
            output.WriteLine("No results.");
            return 0;
        }

        if (columns.Length == 0)
        {
            foreach (JsonElement item in items)
            {
                WriteObject(output, item);
                output.WriteLine();
            }

            return items.Count;
        }

        string[][] cells =
        [
            [.. columns.Select(c => c.Header)],
            .. items.Select(item => columns.Select(c => Cell(item, c.Path)).ToArray()),
        ];

        int[] widths = new int[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            widths[c] = cells.Max(row => row[c].Length);
        }

        foreach (string[] row in cells)
        {
            List<string> parts = [];
            for (int c = 0; c < columns.Length; c++)
            {
                // The last column is not padded — trailing whitespace serves nobody
                // and makes copy-paste worse.
                parts.Add(c == columns.Length - 1 ? row[c] : row[c].PadRight(widths[c]));
            }

            output.WriteLine(string.Join("  ", parts).TrimEnd());
        }

        return items.Count;
    }

    private static void WriteObject(TextWriter output, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            output.WriteLine(Render(element));
            return;
        }

        int width = element.EnumerateObject().Select(p => p.Name.Length).DefaultIfEmpty(0).Max();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            output.WriteLine($"{property.Name.PadRight(width)}  {Render(property.Value)}");
        }
    }

    /// <summary>Reads a dotted path out of an object and renders it for display.</summary>
    public static string Cell(JsonElement item, string path)
    {
        JsonElement current = item;
        foreach (string segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return "-";
            }
        }

        string text = Render(current);
        return text.Length > MaxCellWidth ? text[..(MaxCellWidth - 1)] + "…" : text;
    }

    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => RenderString(value.GetString()),
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        // Null and absent both render as "-": for a CLI reader they mean the same
        // thing, and distinguishing them would add noise without adding information.
        JsonValueKind.Null or JsonValueKind.Undefined => "-",
        // Short arrays of scalars are spelled out — `entkube whoami` exists to show which
        // scopes a token holds, and rendering that as "[2]" answers nothing. Longer or
        // nested arrays fall back to a count, which is all a table cell can carry.
        JsonValueKind.Array => RenderArray(value),
        _ => value.ToString(),
    };

    private static string RenderArray(JsonElement array)
    {
        List<JsonElement> items = [.. array.EnumerateArray()];

        if (items.Count == 0)
        {
            return "-";
        }

        bool allScalar = items.All(i => i.ValueKind
            is JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False);

        if (!allScalar || items.Count > 8)
        {
            return $"[{items.Count}]";
        }

        return string.Join(", ", items.Select(i =>
            i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : i.ToString()));
    }

    private static string RenderString(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "-";
        }

        // Timestamps are shortened to minutes: seconds and the timezone suffix are
        // never what someone scanning a table needs.
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed)
            && text.Length >= 19)
        {
            return parsed.ToString("yyyy-MM-dd HH:mm");
        }

        // Newlines would break row alignment.
        return text.Replace("\r", "").Replace("\n", " ");
    }
}
