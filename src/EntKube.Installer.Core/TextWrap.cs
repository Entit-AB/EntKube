using System.Text;

namespace EntKube.Installer;

/// <summary>
/// Word wrapping, shared by the console prompts and the comments written into the generated .env.
/// </summary>
public static class TextWrap
{
    /// <summary>
    /// Wraps on whitespace at <paramref name="width"/>. A word longer than the width is emitted
    /// whole rather than split — the long words here are URLs and connection strings, and breaking
    /// one across lines makes it uncopyable, which is worse than a line that overflows.
    /// </summary>
    public static IEnumerable<string> Wrap(string text, int width)
    {
        List<string> lines = [];
        StringBuilder line = new();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }
}
