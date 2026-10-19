using System.Text;
using System.Text.RegularExpressions;

namespace LegacyLens.Api.Pdf;

/// <summary>
/// The Markdown the report writer emits, read back into blocks.
///
/// The PDF is typeset from the same text the command prints rather than from
/// the assessment a second time. Two writers over one assessment would drift
/// apart the first time a sentence changed in one of them, and the reader
/// holding the PDF would be reading a different report from the one in CI.
/// Only the subset ReportWriter produces is understood: headings, paragraphs,
/// lists, tables, fenced code, rules, and inline code, bold, italic and links.
/// </summary>
internal static partial class Markdown
{
    public abstract record Block;

    public sealed record Heading(int Level, string Text) : Block;

    public sealed record Paragraph(string Text) : Block;

    public sealed record ListBlock(bool Ordered, IReadOnlyList<string> Items) : Block;

    public sealed record Table(
        IReadOnlyList<string> Head,
        IReadOnlyList<bool> RightAligned,
        IReadOnlyList<IReadOnlyList<string>> Rows) : Block;

    public sealed record Code(string Language, string Body) : Block;

    public sealed record Rule : Block;

    [Flags]
    public enum Style { None = 0, Bold = 1, Italic = 2, Code = 4, Link = 8 }

    public sealed record Run(string Text, Style Style, string? Url = null);

    public static IReadOnlyList<Block> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<Block>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var language = line[3..].Trim();
                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                    body.Add(lines[i++]);
                i++;
                blocks.Add(new Code(language, string.Join('\n', body)));
                continue;
            }

            var heading = HeadingLine().Match(line);
            if (heading.Success)
            {
                blocks.Add(new Heading(heading.Groups[1].Length, heading.Groups[2].Value.Trim()));
                i++;
                continue;
            }

            if (line.Trim() == "---")
            {
                blocks.Add(new Rule());
                i++;
                continue;
            }

            if (line.StartsWith('|'))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].StartsWith('|'))
                    rows.Add(lines[i++]);

                var head = Cells(rows[0]);
                var rule = rows.Count > 1 ? Cells(rows[1]) : [];
                var right = head.Select((_, k) => k < rule.Count && rule[k].EndsWith(':')).ToList();
                var body = rows.Skip(2).Select(r => (IReadOnlyList<string>)Cells(r)).ToList();
                blocks.Add(new Table(head, right, body));
                continue;
            }

            if (ListItem().IsMatch(line))
            {
                var ordered = OrderedItem().IsMatch(line);
                var items = new List<string>();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    if (ListItem().IsMatch(lines[i]))
                        items.Add(ListItem().Replace(lines[i], "", 1));
                    else if (items.Count > 0)
                        items[^1] += " " + lines[i].Trim();
                    i++;
                }
                blocks.Add(new ListBlock(ordered, items));
                continue;
            }

            var paragraph = new List<string>();
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !BlockStart().IsMatch(lines[i]))
            {
                paragraph.Add(lines[i].Trim());
                i++;
            }

            if (paragraph.Count == 0)
            {
                // A line that looks like the start of a block and was not one
                // of them. Kept as text rather than looping on it forever.
                paragraph.Add(lines[i].Trim());
                i++;
            }

            blocks.Add(new Paragraph(string.Join(' ', paragraph)));
        }

        return blocks;
    }

    /// <summary>
    /// Splits a row on unescaped pipes. ReportWriter escapes a pipe inside a
    /// cell, and splitting on every pipe would shift the columns after it.
    /// </summary>
    private static List<string> Cells(string row)
    {
        var trimmed = row.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal))
            trimmed = trimmed[..^1];

        var cells = new List<string>();
        var current = new StringBuilder();
        for (var k = 0; k < trimmed.Length; k++)
        {
            if (trimmed[k] == '\\' && k + 1 < trimmed.Length && trimmed[k + 1] == '|')
            {
                current.Append('|');
                k++;
            }
            else if (trimmed[k] == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(trimmed[k]);
            }
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    /// <summary>Inline formatting, as runs of text that share a style.</summary>
    public static IReadOnlyList<Run> Inline(string text)
    {
        var runs = new List<Run>();
        var position = 0;

        foreach (Match match in InlineToken().Matches(text))
        {
            if (match.Index > position)
                runs.Add(new Run(text[position..match.Index], Style.None));

            if (match.Groups["code"].Success)
                runs.Add(new Run(match.Groups["code"].Value, Style.Code));
            else if (match.Groups["label"].Success)
                runs.Add(new Run(match.Groups["label"].Value, Style.Link, match.Groups["url"].Value));
            else if (match.Groups["bold"].Success)
                runs.AddRange(Inline(match.Groups["bold"].Value)
                    .Select(r => r with { Style = r.Style | Style.Bold }));
            else if (match.Groups["italic"].Success)
                runs.AddRange(Inline(match.Groups["italic"].Value)
                    .Select(r => r with { Style = r.Style | Style.Italic }));

            position = match.Index + match.Length;
        }

        if (position < text.Length)
            runs.Add(new Run(text[position..], Style.None));

        return runs;
    }

    /// <summary>The text of a line with its formatting taken out.</summary>
    public static string Plain(string text) => string.Concat(Inline(text).Select(r => r.Text));

    [GeneratedRegex(@"^(#{1,6}) (.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*([-*]|\d+\.) ")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\d+\. ")]
    private static partial Regex OrderedItem();

    [GeneratedRegex(@"^(#{1,6} |```|\||---$)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(
        @"`(?<code>[^`]+)`"
      + @"|\[(?<label>[^\]]+)\]\((?<url>https?://[^)\s]+)\)"
      + @"|\*\*(?<bold>[^*]+?)\*\*"
      + @"|(?<![*\w])\*(?<italic>[^*\s][^*]*?)\*(?![*\w])")]
    private static partial Regex InlineToken();
}
