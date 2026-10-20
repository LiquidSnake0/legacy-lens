using PdfSharp.Drawing;

namespace LegacyLens.Api.Pdf;

/// <summary>
/// The order of work as one line across the page: a numbered mark per step,
/// and under it what the step is, what kind of work, and how much of it.
///
/// Drawn from the table the report prints at the top of "In what order", so
/// the picture and the text cannot say different things. It carries no dates
/// and no durations, because nothing in the analysis measured them.
/// </summary>
internal sealed class Timeline
{
    public static readonly string[] Head = ["Step", "What", "Kind", "How much"];

    private const double Mark = 15;
    private const double TitleSize = 6.9;
    private const double DetailSize = 6.1;
    private const double Pad = 4;

    private readonly List<(string Number, List<string> Title, List<string> Kind, List<string> Size)> _steps = [];
    private readonly double _column;

    public double Height { get; }

    private Timeline(IReadOnlyList<IReadOnlyList<string>> rows, double width)
    {
        _column = width / rows.Count;
        var measure = XGraphics.CreateMeasureContext(new XSize(4000, 4000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var title = new XFont(Typefaces.Sans, TitleSize, XFontStyleEx.Bold);
        var detail = new XFont(Typefaces.Sans, DetailSize);
        var text = _column - 2 * Pad;

        foreach (var row in rows)
            _steps.Add((row[0], Wrap(measure, row[1], title, text), Wrap(measure, row[2], detail, text),
                Wrap(measure, row[3], title, text)));

        var lines = _steps.Max(s => s.Title.Count * (TitleSize + 1.6) + 4 + s.Size.Count * (TitleSize + 1.6)
                                    + 3 + s.Kind.Count * (DetailSize + 1.5));
        Height = Mark + 10 + lines + 4;
    }

    /// <summary>The timeline for a table with the expected head, or null for any other table.</summary>
    public static Timeline? From(Markdown.Table table, double width) =>
        table.Head.SequenceEqual(Head) && table.Rows.Count is > 0 and <= 9 && table.Rows.All(r => r.Count == 4)
            ? new Timeline(table.Rows.Select(r => (IReadOnlyList<string>)r.Select(Markdown.Plain).ToList()).ToList(), width)
            : null;

    public void Draw(XGraphics g, double left, double top)
    {
        var ink = new XSolidBrush(Palette.X(Palette.Ink));
        var muted = new XSolidBrush(Palette.X(Palette.Muted));
        var accent = new XSolidBrush(Palette.X(Palette.Accent));
        var onBlock = new XSolidBrush(Palette.X(Palette.OnBlock));
        var title = new XFont(Typefaces.Sans, TitleSize, XFontStyleEx.Bold);
        var detail = new XFont(Typefaces.Sans, DetailSize);
        var number = new XFont(Typefaces.Heavy, 7.5);

        // The line the marks sit on, from the first mark to the last.
        var centre = top + Mark / 2;
        g.DrawLine(new XPen(Palette.X(Palette.Line), 1.2),
            left + _column / 2, centre, left + _column * (_steps.Count - 0.5), centre);

        for (var k = 0; k < _steps.Count; k++)
        {
            var (n, titleLines, kindLines, sizeLines) = _steps[k];
            var x = left + k * _column;
            var cx = x + _column / 2;

            g.DrawEllipse(accent, cx - Mark / 2, top, Mark, Mark);
            g.DrawString(n, number, onBlock, new XRect(cx - Mark / 2, top, Mark, Mark), XStringFormats.Center);

            var y = top + Mark + 10;
            foreach (var line in titleLines)
            {
                g.DrawString(line, title, ink, new XRect(x + Pad, y, _column - 2 * Pad, TitleSize), XStringFormats.TopCenter);
                y += TitleSize + 1.6;
            }
            y += 4;
            foreach (var line in sizeLines)
            {
                g.DrawString(line, title, accent, new XRect(x + Pad, y, _column - 2 * Pad, TitleSize), XStringFormats.TopCenter);
                y += TitleSize + 1.6;
            }
            y += 3;
            foreach (var line in kindLines)
            {
                g.DrawString(line, detail, muted, new XRect(x + Pad, y, _column - 2 * Pad, DetailSize), XStringFormats.TopCenter);
                y += DetailSize + 1.5;
            }
        }
    }

    private static List<string> Wrap(XGraphics g, string text, XFont font, double width)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && g.MeasureString(candidate, font).Width > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }
}
