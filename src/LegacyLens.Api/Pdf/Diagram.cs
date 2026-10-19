using System.Text.RegularExpressions;
using PdfSharp.Drawing;

namespace LegacyLens.Api.Pdf;

/// <summary>
/// The dependency diagram, drawn on the page instead of left as Mermaid source.
///
/// Mermaid needs a browser to become a picture, and a browser is the one thing
/// a single executable on someone's laptop cannot be assumed to have. What the
/// report emits is a narrow subset, though: boxes, folders and arrows, at most
/// a dozen of them. That is small enough to lay out here, in layers, which is
/// how a reader of a dependency graph wants it anyway: what everything rests on
/// at the bottom, what nothing rests on at the top.
///
/// Anything outside that subset is not guessed at: <see cref="Parse"/> returns
/// nothing and the caller prints the source as text.
/// </summary>
internal sealed partial class Diagram
{
    private const double Gap = 7;
    private const double RowGap = 30;
    private const double DummyWidth = 1;
    private const double MaxBox = 112;
    private const double MinBox = 64;
    private const double LegendHeight = 22;
    private const double NameSize = 6.8;
    private const double SmallestName = 5.6;
    private const double DetailSize = 6.2;
    private const double GroupSize = 5.2;

    private sealed class Node
    {
        public required string Id { get; init; }
        public string Name { get; init; } = "";
        public string? Detail { get; init; }
        public string Group { get; init; } = "";
        public string Kind { get; set; } = "";
        public bool Dummy { get; init; }
        public int Rank { get; set; }
        public int Index { get; init; }
        public double X { get; set; }
        public List<string> Lines { get; set; } = [];
    }

    private readonly List<Node> _nodes = [];
    private readonly List<(Node From, Node To)> _edges = [];
    private readonly List<List<Node>> _rows = [];
    private readonly List<List<Node>> _paths = [];
    private double _box;
    private double _boxHeight;
    private double _nameSize = NameSize;

    public double Width { get; private set; }

    public double Height { get; private set; }

    /// <summary>
    /// Reads the subset MermaidWriter emits. Null for anything else, so that a
    /// diagram this cannot read is printed as source rather than drawn wrong.
    /// </summary>
    public static Diagram? Parse(string source, double width)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("%%", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0 || !lines[0].StartsWith("graph ", StringComparison.Ordinal)) return null;

        var diagram = new Diagram { Width = width };
        var byId = new Dictionary<string, Node>(StringComparer.Ordinal);
        var group = "";

        foreach (var line in lines.Skip(1))
        {
            Match m;
            if ((m = Subgraph().Match(line)).Success)
            {
                group = Unescape(m.Groups[2].Value);
            }
            else if (line == "end")
            {
                group = "";
            }
            else if ((m = NodeLine().Match(line)).Success)
            {
                var parts = Unescape(m.Groups[2].Value).Split("<br/>");
                var node = new Node
                {
                    Id = m.Groups[1].Value,
                    Name = parts[0],
                    Detail = parts.Length > 1 ? parts[1] : null,
                    Group = group,
                    Index = diagram._nodes.Count,
                };
                byId[node.Id] = node;
                diagram._nodes.Add(node);
            }
            else if ((m = Edge().Match(line)).Success)
            {
                if (byId.TryGetValue(m.Groups[1].Value, out var from)
                    && byId.TryGetValue(m.Groups[2].Value, out var to)
                    && from != to)
                {
                    diagram._edges.Add((from, to));
                }
            }
            else if ((m = ClassLine().Match(line)).Success)
            {
                foreach (var id in m.Groups[1].Value.Split(','))
                    if (byId.TryGetValue(id, out var node)) node.Kind = m.Groups[2].Value;
            }
            else if (!line.StartsWith("classDef ", StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (diagram._nodes.Count == 0) return null;

        diagram.Layout();
        return diagram;
    }

    private void Layout()
    {
        AssignRanks();
        SplitCrowdedRows();
        BuildPaths();
        Order();
        Size();
        Place();
    }

    /// <summary>
    /// A project sits one row above the highest thing it depends on, so what
    /// everything rests on ends at the bottom. A cycle, which the findings
    /// report separately, is broken where it is first met.
    /// </summary>
    private void AssignRanks()
    {
        var height = new Dictionary<Node, int>();
        var visiting = new HashSet<Node>();

        int Height(Node node)
        {
            if (height.TryGetValue(node, out var known)) return known;
            if (!visiting.Add(node)) return 0;

            var h = _edges.Where(e => e.From == node).Select(e => Height(e.To) + 1).DefaultIfEmpty(0).Max();
            visiting.Remove(node);
            height[node] = h;
            return h;
        }

        foreach (var node in _nodes) Height(node);
        var top = height.Values.Max();
        foreach (var node in _nodes) node.Rank = top - height[node];

        for (var r = 0; r <= top; r++)
            _rows.Add(_nodes.Where(n => n.Rank == r).ToList());
    }

    /// <summary>
    /// A row with more boxes than the page is wide gives up its extra boxes to
    /// a new row above it. Nothing in a row depends on anything else in it, so
    /// moving some of them up keeps every arrow pointing down.
    /// </summary>
    private void SplitCrowdedRows()
    {
        var capacity = Math.Max(1, (int)Math.Floor((Width + Gap) / (MinBox + Gap)));

        for (var r = 0; r < _rows.Count; r++)
        {
            if (_rows[r].Count <= capacity) continue;

            var moved = _rows[r].Skip(capacity).ToList();
            _rows[r] = _rows[r].Take(capacity).ToList();
            _rows.Insert(r, moved);
            r++;
        }

        for (var r = 0; r < _rows.Count; r++)
            foreach (var node in _rows[r]) node.Rank = r;
    }

    /// <summary>
    /// An arrow that skips rows runs through a narrow slot in each row it
    /// crosses, so it passes between boxes instead of behind them. Arrows
    /// heading for the same box share their slots, which turns ten arrows into
    /// Orchard.Framework into one trunk instead of ten parallel lines.
    /// </summary>
    private void BuildPaths()
    {
        var dummies = new Dictionary<(Node Target, int Row), Node>();

        foreach (var (from, to) in _edges)
        {
            var path = new List<Node> { from };
            if (to.Rank > from.Rank + 1)
            {
                for (var r = from.Rank + 1; r < to.Rank; r++)
                {
                    if (!dummies.TryGetValue((to, r), out var dummy))
                    {
                        dummy = new Node { Id = $"{to.Id}@{r}", Dummy = true, Rank = r, Index = int.MaxValue };
                        dummies[(to, r)] = dummy;
                        _rows[r].Add(dummy);
                    }
                    path.Add(dummy);
                }
            }
            path.Add(to);
            _paths.Add(path);
        }
    }

    private IEnumerable<(Node Upper, Node Lower)> Segments() =>
        _paths.SelectMany(p => p.Zip(p.Skip(1))).Distinct();

    /// <summary>
    /// Order within each row by the average position of what it is joined to,
    /// swept down and up a few times: the usual way of keeping arrows from
    /// crossing without searching every arrangement.
    /// </summary>
    private void Order()
    {
        foreach (var row in _rows)
        {
            var sorted = row.OrderBy(n => n.Dummy ? 1 : 0).ThenBy(n => n.Group, StringComparer.Ordinal)
                .ThenBy(n => n.Index).ToList();
            row.Clear();
            row.AddRange(sorted);
        }

        var segments = Segments().Where(s => s.Lower.Rank == s.Upper.Rank + 1).ToList();

        for (var sweep = 0; sweep < 8; sweep++)
        {
            var down = sweep % 2 == 0;
            var range = down
                ? Enumerable.Range(1, Math.Max(0, _rows.Count - 1))
                : Enumerable.Range(0, Math.Max(0, _rows.Count - 1)).Reverse();

            foreach (var r in range)
            {
                var row = _rows[r];
                var neighbours = down ? _rows[r - 1] : _rows[r + 1];
                var position = neighbours.Select((n, k) => (n, k)).ToDictionary(t => t.n, t => (double)t.k);
                var current = row.Select((n, k) => (n, k)).ToDictionary(t => t.n, t => (double)t.k);

                double Key(Node node)
                {
                    var joined = down
                        ? segments.Where(s => s.Lower == node && position.ContainsKey(s.Upper)).Select(s => position[s.Upper])
                        : segments.Where(s => s.Upper == node && position.ContainsKey(s.Lower)).Select(s => position[s.Lower]);
                    var list = joined.ToList();
                    if (list.Count == 0) return current[node] * neighbours.Count / Math.Max(1.0, row.Count);
                    return list.Average();
                }

                var keyed = row.Select(n => (n, key: Key(n), was: current[n])).ToList();
                row.Clear();
                row.AddRange(keyed.OrderBy(t => t.key).ThenBy(t => t.was).Select(t => t.n));
            }
        }
    }

    private void Size()
    {
        _box = MaxBox;
        foreach (var row in _rows)
        {
            var real = row.Count(n => !n.Dummy);
            var dummies = row.Count - real;
            if (real == 0) continue;
            var available = Width - dummies * (DummyWidth + Gap) - (real - 1) * Gap;
            _box = Math.Min(_box, available / real);
        }
        _box = Math.Max(_box, 40);

        var measure = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var textWidth = _box - 8;

        // One size for every name, small enough that each dotted part fits on
        // a line of its own. A name broken in the middle of a word reads as two
        // names; a slightly smaller face does not.
        var pieces = _nodes.SelectMany(n => Regex.Split(n.Name, @"(?<=[.\-_])")).Where(p => p.Length > 0).ToList();
        while (_nameSize > SmallestName
               && pieces.Any(p => measure.MeasureString(p, new XFont(Typefaces.Sans, _nameSize, XFontStyleEx.Bold)).Width > textWidth))
            _nameSize -= 0.2;
        var nameFont = new XFont(Typefaces.Sans, _nameSize, XFontStyleEx.Bold);

        foreach (var node in _nodes)
            node.Lines = WrapName(measure, node.Name, nameFont, textWidth);

        var maxLines = _nodes.Max(n => n.Lines.Count);
        var hasGroups = _nodes.Any(n => n.Group.Length > 0);
        var hasDetail = _nodes.Any(n => n.Detail is not null);

        _boxHeight = 6 + (hasGroups ? GroupSize + 3.5 : 0) + maxLines * (_nameSize + 1.8)
                   + (hasDetail ? DetailSize + 2.5 : 0) + 4;

        Height = _rows.Count * _boxHeight + (_rows.Count - 1) * RowGap + LegendHeight;
    }

    /// <summary>
    /// Project names are dotted, and a dot is where a reader expects one to
    /// break: Orchard. / MediaLibrary rather than Orchard.MediaLi / brary.
    /// </summary>
    private static List<string> WrapName(XGraphics measure, string name, XFont font, double width)
    {
        var pieces = Regex.Split(name, @"(?<=[.\-_])").Where(p => p.Length > 0).ToList();
        var lines = new List<string>();
        var current = "";

        foreach (var piece in pieces)
        {
            if (current.Length > 0 && measure.MeasureString(current + piece, font).Width > width)
            {
                lines.Add(current);
                current = "";
            }

            var rest = piece;
            while (measure.MeasureString(rest, font).Width > width && rest.Length > 1)
            {
                var take = rest.Length - 1;
                while (take > 1 && measure.MeasureString(rest[..take], font).Width > width) take--;
                if (current.Length > 0) { lines.Add(current); current = ""; }
                lines.Add(rest[..take]);
                rest = rest[take..];
            }
            current += rest;
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private void Place()
    {
        foreach (var row in _rows)
        {
            var total = row.Sum(n => n.Dummy ? DummyWidth : _box) + (row.Count - 1) * Gap;
            var x = (Width - total) / 2;
            foreach (var node in row)
            {
                var w = node.Dummy ? DummyWidth : _box;
                node.X = x + w / 2;
                x += w + Gap;
            }
        }
    }

    private double Top(int row) => row * (_boxHeight + RowGap);

    private double Bottom(int row) => Top(row) + _boxHeight;

    public void Draw(XGraphics g, double left, double top)
    {
        var state = g.Save();
        g.TranslateTransform(left, top);

        DrawEdges(g);
        foreach (var node in _nodes) DrawBox(g, node);
        DrawLegend(g);

        g.Restore(state);
    }

    private void DrawEdges(XGraphics g)
    {
        var pen = new XPen(Palette.X(0x8a8a8a), 0.6);
        var arrow = new XSolidBrush(Palette.X(0x6b6a67));
        var segments = Segments().ToList();

        // Where each arrow leaves its box and where it arrives, spread along
        // the edge in the order of the other end, so they fan out instead of
        // all leaving from one point.
        double Port(Node node, Node other, bool leaving)
        {
            if (node.Dummy) return node.X;
            var siblings = leaving
                ? segments.Where(s => s.Upper == node).Select(s => s.Lower)
                : segments.Where(s => s.Lower == node).Select(s => s.Upper);
            var ordered = siblings.Distinct().OrderBy(n => n.X).ToList();
            var k = ordered.IndexOf(other);
            var span = Math.Min(_box - 16, ordered.Count * 9.0);
            return ordered.Count <= 1 ? node.X : node.X - span / 2 + span * k / (ordered.Count - 1);
        }

        foreach (var (upper, lower) in segments)
        {
            var x1 = Port(upper, lower, leaving: true);
            var y1 = Bottom(upper.Rank);
            var x2 = Port(lower, upper, leaving: false);
            var y2 = Top(lower.Rank);

            if (lower.Rank <= upper.Rank)
            {
                // Only a cycle produces this, and it is drawn as a plain line.
                g.DrawLine(pen, upper.X, Bottom(upper.Rank), lower.X, Top(lower.Rank));
                continue;
            }

            if (upper.Dummy) g.DrawLine(pen, upper.X, Top(upper.Rank), upper.X, y1);

            var bend = (y2 - y1) / 2;
            g.DrawBezier(pen, x1, y1, x1, y1 + bend, x2, y2 - bend, x2, y2);

            if (!lower.Dummy)
            {
                g.DrawPolygon(arrow, [
                    new XPoint(x2, y2),
                    new XPoint(x2 - 2.2, y2 - 4.2),
                    new XPoint(x2 + 2.2, y2 - 4.2),
                ], XFillMode.Winding);
            }
        }
    }

    private static uint Fill(string kind) => kind switch
    {
        "web" => Palette.Slab2,
        "console" => Palette.Slab,
        "wpf" or "winforms" => 0xd6d4cf,
        _ => Palette.Paper,
    };

    private static string Describe(string kind) => kind switch
    {
        "web" => "Web application",
        "library" => "Class library",
        "console" => "Console application",
        "wpf" => "WPF application",
        "winforms" => "Windows Forms application",
        "test" => "Test project",
        "broken" => "Unreadable project file",
        _ => "Other",
    };

    private void DrawBox(XGraphics g, Node node)
    {
        if (node.Dummy) return;

        var x = node.X - _box / 2;
        var y = Top(node.Rank);
        var rect = new XRect(x, y, _box, _boxHeight);
        g.DrawRectangle(new XPen(Palette.X(Palette.Ink), 0.75), new XSolidBrush(Palette.X(Fill(node.Kind))), rect);

        var ink = new XSolidBrush(Palette.X(Palette.Ink));
        var muted = new XSolidBrush(Palette.X(Palette.Muted));
        var faint = new XSolidBrush(Palette.X(Palette.Faint));
        var center = XStringFormats.TopCenter;

        var cursor = y + 5;
        if (_nodes.Any(n => n.Group.Length > 0))
        {
            if (node.Group.Length > 0)
                g.DrawString(node.Group.ToUpperInvariant(), new XFont(Typefaces.Sans, GroupSize, XFontStyleEx.Bold),
                    faint, new XRect(x + 2, cursor, _box - 4, GroupSize + 2), center);
            cursor += GroupSize + 3.5;
        }

        var nameFont = new XFont(Typefaces.Sans, _nameSize, XFontStyleEx.Bold);
        var maxLines = _nodes.Max(n => n.Lines.Count);
        var blockTop = cursor + (maxLines - node.Lines.Count) * (_nameSize + 1.8) / 2;
        foreach (var line in node.Lines)
        {
            g.DrawString(line, nameFont, ink, new XRect(x + 2, blockTop, _box - 4, _nameSize + 2), center);
            blockTop += _nameSize + 1.8;
        }
        cursor += maxLines * (_nameSize + 1.8);

        if (node.Detail is not null)
            g.DrawString(node.Detail, new XFont(Typefaces.Sans, DetailSize), muted,
                new XRect(x + 2, cursor + 1.5, _box - 4, DetailSize + 2), center);
    }

    private void DrawLegend(XGraphics g)
    {
        var y = Height - LegendHeight + 10;
        var font = new XFont(Typefaces.Sans, 6.5);
        var brush = new XSolidBrush(Palette.X(Palette.Muted));
        var x = 0.0;

        foreach (var kind in _nodes.Select(n => n.Kind).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            g.DrawRectangle(new XPen(Palette.X(Palette.Ink), 0.6), new XSolidBrush(Palette.X(Fill(kind))),
                new XRect(x, y, 8, 8));
            var label = Describe(kind);
            g.DrawString(label, font, brush, new XPoint(x + 12, y + 6.5));
            x += 12 + g.MeasureString(label, font).Width + 16;
        }

        var pen = new XPen(Palette.X(0x8a8a8a), 0.6);
        g.DrawLine(pen, x, y + 4, x + 16, y + 4);
        g.DrawPolygon(new XSolidBrush(Palette.X(0x6b6a67)), [
            new XPoint(x + 18, y + 4), new XPoint(x + 13.8, y + 1.8), new XPoint(x + 13.8, y + 6.2),
        ], XFillMode.Winding);
        g.DrawString("depends on", font, brush, new XPoint(x + 22, y + 6.5));
    }

    private static string Unescape(string text) => text.Replace("&quot;", "\"");

    [GeneratedRegex(@"^subgraph\s+(\S+)\[""(.*)""\]$")]
    private static partial Regex Subgraph();

    [GeneratedRegex(@"^(\w+)\[""(.*)""\]$")]
    private static partial Regex NodeLine();

    [GeneratedRegex(@"^(\w+)\s*-->\s*(\w+)$")]
    private static partial Regex Edge();

    [GeneratedRegex(@"^class\s+([\w,]+)\s+(\w+)$")]
    private static partial Regex ClassLine();
}
