using LegacyLens.Analysis;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace LegacyLens.Api.Pdf;

/// <summary>
/// The assessment as a PDF: one page for whoever reads one page, then the
/// whole report as the command prints it.
///
/// The body is typeset from the Markdown, block for block, so the PDF says
/// exactly what the text says: same sections, same numbers, the same list of
/// what was not seen. The cover is the only part composed here, and it holds
/// nothing the report does not: the name, the date, how the analysis ran,
/// figures read from the same assessment the report was written from, put as
/// "n of N" so each one carries its own scale, and the first three steps of
/// its order of work. The paragraph headed "In short" is not repeated on it:
/// the page after the cover opens with it, and saying it twice in two pages
/// taught readers to skip the second.
///
/// Everything happens in this process. No browser, no service, no font looked
/// up on the machine, which is what lets the single executable produce it on a
/// laptop with the network cable out.
/// </summary>
public static class PdfReport
{
    private static readonly XSize A4 = new(595.28, 841.89);
    private const double Side = 50;
    private const double TopMargin = 66;
    private const double BottomMargin = 62;
    private static double ContentWidth => A4.Width - 2 * Side;

    private const string Order = "In what order";

    public static byte[] Write(Assessment assessment, string markdown, DateTimeOffset generatedAt)
    {
        Typefaces.Install();

        var blocks = Markdown.Parse(markdown);
        var date = generatedAt.ToString("yyyy-MM-dd");

        var document = Body(blocks);
        var renderer = new DocumentRenderer(document);
        renderer.PrepareDocument();
        var bodyPages = renderer.FormattedDocument!.PageCount;
        var total = bodyPages + 1;

        var pdf = new PdfDocument();
        pdf.Info.Title = $"{assessment.Name}: structural assessment";
        pdf.Info.Author = "Legacy Lens";
        pdf.Info.Subject = $"A structural assessment of {assessment.Name}, generated on {date}";
        pdf.Info.Creator = "Legacy Lens";
        pdf.Info.CreationDate = generatedAt.LocalDateTime;
        pdf.Info.ModificationDate = generatedAt.LocalDateTime;
        pdf.PageLayout = PdfPageLayout.SinglePage;

        int? orderPage = null;
        var pages = new List<(PdfPage Page, int Number)>();

        var cover = NewPage(pdf);
        for (var n = 1; n <= bodyPages; n++)
        {
            var page = NewPage(pdf);
            using var g = XGraphics.FromPdfPage(page);
            renderer.RenderPage(g, n);

            foreach (var info in renderer.GetRenderInfoFromPage(n) ?? [])
            {
                switch (info.DocumentObject?.Tag)
                {
                    case Diagram diagram:
                        var area = info.LayoutInfo.ContentArea;
                        diagram.Draw(g, area.X.Point, area.Y.Point + 4);
                        break;
                    case string tag when tag == Order:
                        orderPage ??= n + 1;
                        break;
                }
            }

            Chrome(g, assessment.Name, date, n + 1, total);
        }

        using (var g = XGraphics.FromPdfPage(cover))
            Cover(g, assessment, blocks, date, orderPage);

        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static PdfPage NewPage(PdfDocument pdf)
    {
        var page = pdf.AddPage();
        page.Width = XUnit.FromPoint(A4.Width);
        page.Height = XUnit.FromPoint(A4.Height);
        return page;
    }

    // ---------------------------------------------------------------- body

    private static Document Body(IReadOnlyList<Markdown.Block> blocks)
    {
        var document = new Document();
        Styles(document);

        var section = document.AddSection();
        var setup = section.PageSetup;
        setup.PageWidth = Unit.FromPoint(A4.Width);
        setup.PageHeight = Unit.FromPoint(A4.Height);
        setup.LeftMargin = Unit.FromPoint(Side);
        setup.RightMargin = Unit.FromPoint(Side);
        setup.TopMargin = Unit.FromPoint(TopMargin);
        setup.BottomMargin = Unit.FromPoint(BottomMargin);

        Markdown.Heading? previous = null;
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var next = index + 1 < blocks.Count ? blocks[index + 1] : null;
            switch (block)
            {
                case Markdown.Heading h:
                    var heading = section.AddParagraph();
                    heading.Style = h.Level switch { 1 => "Heading1", 2 => "Heading2", _ => "Heading3" };
                    if (h.Level == 2 && h.Text == Order)
                    {
                        // The order of work is the part somebody acts on. It
                        // starts a page, so it can be handed over on its own.
                        heading.Format.PageBreakBefore = true;
                        heading.Tag = Order;
                    }
                    var numbered = System.Text.RegularExpressions.Regex.Match(h.Text, @"^(\d+\.) (.*)$");
                    if (h.Level == 3 && numbered.Success)
                    {
                        // A step of the order of work: the number in the one
                        // accent, as the cover has it.
                        var step = heading.AddFormattedText(numbered.Groups[1].Value + " ");
                        step.Color = Palette.M(Palette.Accent);
                        Inline(heading, numbered.Groups[2].Value);
                    }
                    else
                    {
                        Inline(heading, h.Text);
                    }
                    previous = h;
                    continue;

                case Markdown.Paragraph p:
                    var paragraph = section.AddParagraph();
                    if (previous is { Level: 1 }) paragraph.Style = "Lead";
                    // The sentence that introduces a diagram or a table stays
                    // on the same page as it.
                    // So does the short line under a heading, such as the kind
                    // and size of a step, with the paragraph that explains it.
                    if (next is Markdown.Code or Markdown.Table
                        || (previous is not null && p.Text.Length < 140))
                        paragraph.Format.KeepWithNext = true;
                    Inline(paragraph, p.Text);
                    break;

                case Markdown.ListBlock list:
                    List(section, list);
                    break;

                case Markdown.Table table:
                    Table(section, table);
                    break;

                case Markdown.Code code:
                    Code(section, code);
                    break;

                case Markdown.Rule:
                    var rule = section.AddParagraph();
                    rule.Format.Borders.Bottom.Width = Unit.FromPoint(0.5);
                    rule.Format.Borders.Bottom.Color = Palette.M(Palette.Line);
                    rule.Format.SpaceAfter = Unit.FromPoint(8);
                    rule.Format.LineSpacingRule = LineSpacingRule.Exactly;
                    rule.Format.LineSpacing = Unit.FromPoint(4);
                    break;
            }
            previous = null;
        }

        return document;
    }

    private static void Styles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = Typefaces.Sans;
        normal.Font.Size = Unit.FromPoint(8.8);
        normal.Font.Color = Palette.M(Palette.Ink);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.32;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        normal.ParagraphFormat.WidowControl = true;

        var h1 = document.Styles[StyleNames.Heading1]!;
        h1.Font.Name = Typefaces.Heavy;
        h1.Font.Size = Unit.FromPoint(26);
        h1.Font.Bold = false;
        h1.ParagraphFormat.LineSpacingRule = LineSpacingRule.Single;
        h1.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        h1.ParagraphFormat.KeepWithNext = true;

        var h2 = document.Styles[StyleNames.Heading2]!;
        h2.Font.Name = Typefaces.Heavy;
        h2.Font.Size = Unit.FromPoint(14.5);
        h2.Font.Bold = false;
        h2.ParagraphFormat.LineSpacingRule = LineSpacingRule.Single;
        h2.ParagraphFormat.SpaceBefore = Unit.FromPoint(18);
        h2.ParagraphFormat.SpaceAfter = Unit.FromPoint(8);
        h2.ParagraphFormat.KeepWithNext = true;
        h2.ParagraphFormat.Borders.Top.Width = Unit.FromPoint(2);
        h2.ParagraphFormat.Borders.Top.Color = Palette.M(Palette.Ink);
        h2.ParagraphFormat.Borders.DistanceFromTop = Unit.FromPoint(7);

        // Heading3 inherits from Heading2 in MigraDoc, rule included, so the
        // rule is taken off explicitly.
        var h3 = document.Styles[StyleNames.Heading3]!;
        h3.Font.Name = Typefaces.Heavy;
        h3.Font.Size = Unit.FromPoint(10.5);
        h3.Font.Bold = false;
        h3.ParagraphFormat.Borders.Top.Visible = false;
        h3.ParagraphFormat.Borders.Top.Width = 0;
        h3.ParagraphFormat.Borders.DistanceFromTop = 0;
        h3.ParagraphFormat.LineSpacingRule = LineSpacingRule.Single;
        h3.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
        h3.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        h3.ParagraphFormat.KeepWithNext = true;

        var lead = document.Styles.AddStyle("Lead", StyleNames.Normal);
        lead.Font.Color = Palette.M(Palette.Muted);
    }

    /// <summary>
    /// Inline Markdown into formatted text. Identifiers and paths are set in
    /// the monospace face and may break after a slash or a dot, because a
    /// repository path is often wider than the column it sits in.
    /// </summary>
    private static void Inline(Paragraph paragraph, string text, double? size = null, bool narrow = false)
    {
        foreach (var run in Markdown.Inline(text))
        {
            var bold = run.Style.HasFlag(Markdown.Style.Bold);
            var italic = run.Style.HasFlag(Markdown.Style.Italic);

            if (run.Style.HasFlag(Markdown.Style.Link) && run.Url is not null)
            {
                var link = paragraph.AddHyperlink(run.Url, HyperlinkType.Web);
                var label = link.AddFormattedText(run.Text);
                label.Color = Palette.M(Palette.Accent);
                label.Underline = Underline.Single;
                continue;
            }

            if (run.Style.HasFlag(Markdown.Style.Code))
            {
                var code = paragraph.AddFormattedText(Breakable(run.Text, narrow ? 24 : 90));
                code.Font.Name = Typefaces.Mono;
                code.Font.Size = Unit.FromPoint((size ?? 8.8) * 0.88);
                code.Bold = bold;
                code.Italic = italic;
                continue;
            }

            var formatted = paragraph.AddFormattedText(run.Text);
            formatted.Bold = bold;
            formatted.Italic = italic;
        }
    }

    /// <summary>
    /// A zero-width space after each separator in a long token: invisible, and
    /// an allowed break for the line breaker, which otherwise only breaks on
    /// spaces and lets a long path run out of its cell. Only past a length
    /// that could not fit, because the mark travels with a copied path.
    /// </summary>
    private static string Breakable(string text, int longest) =>
        text.Length <= longest ? text : text.Replace("/", "/\u200B").Replace(".", ".\u200B").Replace("\\", "\\\u200B");

    private static void List(Section section, Markdown.ListBlock list)
    {
        var number = 1;
        for (var k = 0; k < list.Items.Count; k++)
        {
            var item = section.AddParagraph();
            item.Format.LeftIndent = Unit.FromPoint(14);
            item.Format.FirstLineIndent = Unit.FromPoint(-14);
            item.Format.TabStops.AddTabStop(Unit.FromPoint(14));
            item.Format.SpaceAfter = Unit.FromPoint(k == list.Items.Count - 1 ? 7 : 1.5);
            var marker = item.AddFormattedText(list.Ordered ? $"{number++}." : "–");
            marker.Color = Palette.M(Palette.Faint);
            item.AddTab();
            Inline(item, list.Items[k]);
        }
    }

    private static void Code(Section section, Markdown.Code code)
    {
        if (code.Language == "mermaid")
        {
            var diagram = Diagram.Parse(code.Body, ContentWidth);
            if (diagram is not null)
            {
                var holder = section.AddParagraph();
                holder.Tag = diagram;
                holder.Format.LineSpacingRule = LineSpacingRule.Exactly;
                holder.Format.LineSpacing = Unit.FromPoint(diagram.Height + 8);
                holder.Format.SpaceAfter = Unit.FromPoint(2);
                holder.Format.KeepWithNext = true;
                holder.AddText("\u00A0");

                var caption = section.AddParagraph();
                caption.Format.Font.Size = Unit.FromPoint(7);
                caption.Format.Font.Color = Palette.M(Palette.Faint);
                caption.Format.SpaceAfter = Unit.FromPoint(10);
                caption.AddText("Drawn from the Mermaid source the Markdown report carries, "
                              + "with what each project depends on below it.");
                return;
            }
        }

        var block = section.AddParagraph();
        block.Format.Font.Name = Typefaces.Mono;
        block.Format.Font.Size = Unit.FromPoint(6.8);
        block.Format.LineSpacingRule = LineSpacingRule.Single;
        block.Format.Shading.Color = Palette.M(Palette.Wash);
        block.Format.Borders.Left.Width = Unit.FromPoint(2);
        block.Format.Borders.Left.Color = Palette.M(Palette.Ink);
        block.Format.Borders.DistanceFromLeft = Unit.FromPoint(6);
        block.Format.Borders.DistanceFromTop = Unit.FromPoint(4);
        block.Format.Borders.DistanceFromBottom = Unit.FromPoint(4);
        block.Format.LeftIndent = Unit.FromPoint(8);
        block.Format.SpaceAfter = Unit.FromPoint(10);

        var lines = code.Body.Split('\n');
        for (var k = 0; k < lines.Length; k++)
        {
            if (k > 0) block.AddLineBreak();
            block.AddText(lines[k].Length == 0 ? "\u00A0" : lines[k]);
        }

        if (code.Language == "mermaid")
        {
            var caption = section.AddParagraph();
            caption.Format.Font.Size = Unit.FromPoint(7);
            caption.Format.Font.Color = Palette.M(Palette.Faint);
            caption.AddText("Mermaid source, as the report carries it. GitHub and most Markdown "
                          + "viewers draw it as a diagram.");
        }
    }

    private static void Table(Section section, Markdown.Table source)
    {
        var columns = source.Head.Count;
        var size = columns >= 5 ? 7.0 : 7.8;
        var widths = ColumnWidths(source, size);

        var table = section.AddTable();
        table.Borders.Width = 0;
        // MigraDoc hangs the cell padding outside the margin; indenting by it
        // lines the table up with the text above.
        table.Rows.LeftIndent = Unit.FromPoint(4);
        table.LeftPadding = Unit.FromPoint(4);
        table.RightPadding = Unit.FromPoint(4);
        table.TopPadding = Unit.FromPoint(2.6);
        table.BottomPadding = Unit.FromPoint(2.6);
        table.Format.Font.Size = Unit.FromPoint(size);
        table.Format.LineSpacingRule = LineSpacingRule.Multiple;
        table.Format.LineSpacing = 1.15;
        table.Format.SpaceAfter = Unit.FromPoint(0);

        for (var c = 0; c < columns; c++)
        {
            var column = table.AddColumn(Unit.FromPoint(widths[c]));
            column.Format.Alignment = source.RightAligned[c] ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        }

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = Palette.M(Palette.Ink);
        head.Format.Font.Color = Palette.M(Palette.OnBlock);
        head.Format.Font.Bold = true;
        head.TopPadding = Unit.FromPoint(3.5);
        head.BottomPadding = Unit.FromPoint(3.5);
        for (var c = 0; c < columns; c++)
            Inline(head.Cells[c].AddParagraph(), source.Head[c], size, narrow: true);

        foreach (var cells in source.Rows)
        {
            var row = table.AddRow();
            row.Borders.Bottom.Width = Unit.FromPoint(0.4);
            row.Borders.Bottom.Color = Palette.M(Palette.Line);
            for (var c = 0; c < columns && c < cells.Count; c++)
                Inline(row.Cells[c].AddParagraph(), cells[c], size, narrow: true);
        }

        // A short table is read as one thing and should not be split across a
        // page; a long one repeats its header row on the next page instead.
        if (source.Rows.Count <= 12) table.Rows[0].KeepWith = source.Rows.Count;

        var after = section.AddParagraph();
        after.Format.LineSpacingRule = LineSpacingRule.Exactly;
        after.Format.LineSpacing = Unit.FromPoint(9);
        after.Format.SpaceAfter = 0;
    }

    /// <summary>
    /// Column widths from what is in them, the way a browser does it: every
    /// column gets at least its longest word, and what is left goes to the
    /// columns that have more to say. Tables take the full width, which reads
    /// as a block on the page rather than a scattering of numbers.
    /// </summary>
    private static double[] ColumnWidths(Markdown.Table table, double size)
    {
        var measure = XGraphics.CreateMeasureContext(new XSize(4000, 4000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var regular = new XFont(Typefaces.Sans, size);
        var bold = new XFont(Typefaces.Sans, size, XFontStyleEx.Bold);
        var mono = new XFont(Typefaces.Mono, size * 0.88);
        const double padding = 9;

        var columns = table.Head.Count;
        var minimum = new double[columns];
        var natural = new double[columns];

        foreach (var (cells, isHead) in table.Rows.Select(r => (r, false)).Prepend((table.Head, true)))
        {
            for (var c = 0; c < columns && c < cells.Count; c++)
            {
                var whole = 0.0;
                foreach (var run in Markdown.Inline(cells[c]))
                {
                    var font = run.Style.HasFlag(Markdown.Style.Code) ? mono
                             : isHead || run.Style.HasFlag(Markdown.Style.Bold) ? bold : regular;
                    whole += measure.MeasureString(run.Text, font).Width;

                    var pieces = run.Style.HasFlag(Markdown.Style.Code)
                        ? Breakable(run.Text, 24).Split(' ', '\u200B')
                        : run.Text.Split(' ');
                    foreach (var piece in pieces)
                        minimum[c] = Math.Max(minimum[c], measure.MeasureString(piece, font).Width + padding);
                }
                natural[c] = Math.Max(natural[c], whole + padding);
            }
        }

        var available = ContentWidth;
        for (var c = 0; c < columns; c++) minimum[c] = Math.Min(minimum[c], available * 0.45);

        var widths = new double[columns];
        var sumNatural = natural.Sum();
        var sumMinimum = minimum.Sum();

        if (sumNatural <= available)
        {
            for (var c = 0; c < columns; c++)
                widths[c] = natural[c] + (available - sumNatural) * natural[c] / sumNatural;
        }
        else if (sumMinimum >= available)
        {
            for (var c = 0; c < columns; c++) widths[c] = minimum[c] * available / sumMinimum;
        }
        else
        {
            var spare = available - sumMinimum;
            var wanted = natural.Zip(minimum, (n, m) => n - m).ToArray();
            var sumWanted = wanted.Sum();
            for (var c = 0; c < columns; c++)
                widths[c] = minimum[c] + (sumWanted > 0 ? spare * wanted[c] / sumWanted : 0);
        }

        return widths;
    }

    // ------------------------------------------------------------ chrome

    /// <summary>Header and footer, drawn on every page after the cover.</summary>
    private static void Chrome(XGraphics g, string name, string date, int page, int total)
    {
        var small = new XFont(Typefaces.Sans, 7);
        var muted = new XSolidBrush(Palette.X(Palette.Muted));
        var rule = new XPen(Palette.X(Palette.Line), 0.5);

        Brand(g, Side, 30, 6.5);
        g.DrawString($"{name}  ·  structural assessment  ·  {date}", small, muted,
            new XRect(Side, 30, ContentWidth, 13), XStringFormats.CenterRight);
        g.DrawLine(rule, Side, 49, A4.Width - Side, 49);

        var y = A4.Height - 36;
        g.DrawLine(rule, Side, y - 8, A4.Width - Side, y - 8);
        g.DrawString($"{name}  ·  {date}  ·  Legacy Lens", small, muted,
            new XRect(Side, y, ContentWidth, 10), XStringFormats.CenterLeft);
        g.DrawString($"Page {page} of {total}", small, muted,
            new XRect(Side, y, ContentWidth, 10), XStringFormats.CenterRight);
    }

    /// <summary>The black block with the name in it, as on the page.</summary>
    private static double Brand(XGraphics g, double x, double y, double size)
    {
        var font = new XFont(Typefaces.Heavy, size);
        const string text = "LEGACY LENS";
        var width = g.MeasureString(text, font).Width;
        var padX = size * 1.0;
        var height = size * 2.0;
        g.DrawRectangle(new XSolidBrush(Palette.X(Palette.Ink)), new XRect(x, y, width + 2 * padX, height));
        g.DrawString(text, font, new XSolidBrush(Palette.X(Palette.OnBlock)),
            new XRect(x + padX, y, width, height), XStringFormats.CenterLeft);
        return height;
    }

    // ------------------------------------------------------------- cover

    private sealed record Figure(string Value, string Label, string Detail);

    private static List<Figure> Figures(Assessment assessment)
    {
        var map = assessment.Map;
        var survey = assessment.Modernisation;
        var figures = new List<Figure>
        {
            new($"{map.TotalLines:N0}", "lines of code", $"in {Count(map.TotalFiles, "source file")}"),
            new($"{map.Projects.Count:N0}", Plural(map.Projects.Count, "project"), Targets(assessment)),
        };

        // A count alone does not say whether it is a lot. "45 of 78" does, and
        // it is how every assessment worth copying states its findings.
        var production = assessment.Production.Count;
        if (production > 0)
        {
            var untested = assessment.Of(FindingKind.Untested).Count();
            figures.Add(new($"{untested:N0} of {production:N0}", "projects that ship code with no test project",
                $"{assessment.UntestedLines:N0} lines no test project references"));
        }

        if (survey.Projects.Count > 0)
        {
            figures.Add(new($"{survey.Blocked:N0} of {survey.Projects.Count:N0}",
                "projects held by a package that exists only in .NET Framework",
                $"{Count(survey.DeadEnds.Count, "such package")}"));

            figures.Add(new($"{survey.ConvertibleAsIs:N0} of {survey.Projects.Count:N0}",
                "projects convertible as they stand",
                $"{survey.PreSdk:N0} in the old project format"));
        }

        if (survey.Packages.Count > 0)
        {
            // The unknown is counted, not left out: it is the number a quote
            // gets wrong when nobody looks.
            var unknown = survey.Packages.Count(p => p.Portability == Portability.Unknown);
            figures.Add(new($"{unknown:N0} of {survey.Packages.Count:N0}", "packages nobody has classified yet",
                "counted as unknown, not as fine"));
        }

        return figures;
    }

    private static string Targets(Assessment assessment)
    {
        var frameworks = assessment.Frameworks;
        return frameworks.Count switch
        {
            0 => "no target framework declared",
            1 => $"all targeting {frameworks[0].Framework}",
            _ => $"targeting {Count(frameworks.Count, "framework")}",
        };
    }

    private static string Plural(int n, string unit) => n == 1 ? unit : unit + "s";

    private static string Count(int n, string unit) => $"{n:N0} {Plural(n, unit)}";

    private static void Cover(
        XGraphics g, Assessment assessment, IReadOnlyList<Markdown.Block> blocks, string date, int? orderPage)
    {
        Concrete(g);

        var x = Side;
        var width = ContentWidth;
        var ink = new XSolidBrush(Palette.X(Palette.Ink));
        var muted = new XSolidBrush(Palette.X(Palette.Muted));
        var slab = new XSolidBrush(Palette.X(Palette.Slab));
        var accent = new XSolidBrush(Palette.X(Palette.Accent));
        var frame = new XPen(Palette.X(Palette.Ink), 1.2);

        var y = 34.0;
        var brand = Brand(g, x, y, 10);
        var dateFont = new XFont(Typefaces.Sans, 8.5, XFontStyleEx.Bold);
        var dateWidth = g.MeasureString(date, dateFont).Width + 20;
        g.DrawRectangle(slab, new XRect(x + width - dateWidth, y, dateWidth, brand));
        g.DrawString(date, dateFont, ink, new XRect(x + width - dateWidth, y, dateWidth, brand), XStringFormats.Center);
        y += brand + 18;

        // The title slab: what this is and what it is of.
        var kicker = new XFont(Typefaces.Sans, 8, XFontStyleEx.Bold);
        var titleSize = 46.0;
        XFont title;
        List<string> titleLines;
        while (true)
        {
            title = new XFont(Typefaces.Heavy, titleSize);
            titleLines = Wrap(g, assessment.Name, title, width - 48);
            if ((titleLines.Count <= 2 && titleLines.All(l => g.MeasureString(l, title).Width <= width - 48))
                || titleSize <= 20) break;
            titleSize -= 2;
        }

        var sub = new XFont(Typefaces.Sans, 9);
        // The report's own statement of method, word for word: the cover says
        // nothing the report does not. The line before it, kind and date, is
        // already the kicker and the stamp above.
        var opening = blocks
            .SkipWhile(b => b is not Markdown.Heading { Level: 1 })
            .Skip(1).TakeWhile(b => b is Markdown.Paragraph)
            .Cast<Markdown.Paragraph>().ToList();
        var subText = Markdown.Plain(opening.LastOrDefault()?.Text ?? "");
        var subLines = Wrap(g, subText, sub, width - 48);
        var titleHeight = 24 + 12 + 10 + titleLines.Count * titleSize * 1.02 + 8 + subLines.Count * 13 + 22;
        g.DrawRectangle(slab, new XRect(x, y, width, titleHeight));
        var cy = y + 24;
        g.DrawString("STRUCTURAL ASSESSMENT", kicker, accent, new XPoint(x + 24, cy + 8));
        cy += 22;
        foreach (var line in titleLines)
        {
            g.DrawString(line, title, ink, new XPoint(x + 24, cy + titleSize * 0.78));
            cy += titleSize * 1.02;
        }
        cy += 8;
        foreach (var line in subLines)
        {
            g.DrawString(line, sub, muted, new XPoint(x + 24, cy + 9));
            cy += 13;
        }
        y += titleHeight;

        // How the figures below were obtained, in the report's own words from
        // its last line: the identity of the analysis, as an auditor states
        // the commit and the scope before any finding.
        var elapsed = assessment.ElapsedMs < 1000
            ? $"{assessment.ElapsedMs:N0} ms"
            : $"{assessment.ElapsedMs / 1000.0:0.0} s";
        var identity = $"Read in {elapsed} from {Count(assessment.Map.TotalFiles, "source file")}: "
                     + "nothing compiled, no package restored, no service contacted.";
        y += 8;
        g.DrawRectangle(slab, new XRect(x, y, width, 20));
        g.DrawString(identity, new XFont(Typefaces.Sans, 7.8, XFontStyleEx.Bold), ink,
            new XRect(x + 24, y, width - 48, 20), XStringFormats.CenterLeft);
        y += 20;

        var figures = Figures(assessment);
        var steps = assessment.Repairs.Take(3).ToList();
        var stepsHeight = steps.Count == 0 ? 0 : 34 + steps.Count * 40 + (orderPage is null ? 0 : 16);

        y += 10;
        DrawFigures(g, figures, x, y, width, frame);
        y += FiguresHeight(figures) + 14;

        if (steps.Count > 0)
            DrawSteps(g, steps, x, y, width, stepsHeight, orderPage);
    }

    /// <summary>The figures two to a row: the size, then the ratios.</summary>
    private static List<List<Figure>> Rows(List<Figure> figures) =>
        figures.Chunk(2).Select(row => row.ToList()).ToList();

    private static double FiguresHeight(List<Figure> figures) =>
        Rows(figures).Select((_, r) => r == 0 ? 92.0 : 76.0).Sum();

    private static void DrawFigures(XGraphics g, List<Figure> figures, double x, double y, double width, XPen frame)
    {
        var rows = Rows(figures);

        var slab = new XSolidBrush(Palette.X(Palette.Slab));
        var ink = new XSolidBrush(Palette.X(Palette.Ink));
        var muted = new XSolidBrush(Palette.X(Palette.Muted));
        var accent = new XSolidBrush(Palette.X(Palette.Accent));

        var top = y;
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var height = r == 0 ? 92.0 : 76.0;
            var valueSize = r == 0 ? 36.0 : 27.0;
            var cell = width / row.Count;

            for (var k = 0; k < row.Count; k++)
            {
                var figure = row[k];
                var rect = new XRect(x + k * cell, top, cell, height);
                g.DrawRectangle(frame, slab, rect);

                var value = new XFont(Typefaces.Heavy, valueSize);
                while (g.MeasureString(figure.Value, value).Width > cell - 32 && value.Size > 14)
                    value = new XFont(Typefaces.Heavy, value.Size - 1);

                var vy = top + 14 + value.Size * 0.78;
                g.DrawString(figure.Value, value, ink, new XPoint(rect.X + 16, vy));
                g.DrawRectangle(accent, new XRect(rect.X + 16, vy + 5, 16, 2.5));

                var labelFont = new XFont(Typefaces.Sans, 7.8, XFontStyleEx.Bold);
                var detailFont = new XFont(Typefaces.Sans, 7);
                var ly = vy + 18;
                foreach (var line in Wrap(g, figure.Label, labelFont, cell - 32).Take(2))
                {
                    g.DrawString(line, labelFont, ink, new XPoint(rect.X + 16, ly));
                    ly += 9.6;
                }
                foreach (var line in Wrap(g, figure.Detail, detailFont, cell - 32).Take(1))
                    g.DrawString(line, detailFont, muted, new XPoint(rect.X + 16, ly + 0.5));
            }
            top += height;
        }
    }

    private static void DrawSteps(
        XGraphics g, List<RepairStep> steps, double x, double y, double width, double height, int? orderPage)
    {
        var slab = new XSolidBrush(Palette.X(Palette.Slab));
        var ink = new XSolidBrush(Palette.X(Palette.Ink));
        var muted = new XSolidBrush(Palette.X(Palette.Muted));
        var accent = new XSolidBrush(Palette.X(Palette.Accent));
        var rule = new XPen(Palette.X(Palette.Line), 0.6);

        g.DrawRectangle(slab, new XRect(x, y, width, height));
        g.DrawString("WHERE THE ORDER OF WORK STARTS", new XFont(Typefaces.Sans, 8, XFontStyleEx.Bold), accent,
            new XPoint(x + 24, y + 22));

        var number = new XFont(Typefaces.Heavy, 22);
        var titleFont = new XFont(Typefaces.Sans, 10, XFontStyleEx.Bold);
        var detail = new XFont(Typefaces.Sans, 7.6);
        var top = y + 34;

        for (var k = 0; k < steps.Count; k++)
        {
            if (k > 0) g.DrawLine(rule, x + 24, top, x + width - 24, top);
            g.DrawString($"{k + 1}", number, accent, new XPoint(x + 24, top + 27));
            var stepFont = titleFont;
            while (g.MeasureString(steps[k].Title, stepFont).Width > width - 80 && stepFont.Size > 7)
                stepFont = new XFont(Typefaces.Sans, stepFont.Size - 0.25, XFontStyleEx.Bold);
            g.DrawString(steps[k].Title, stepFont, ink, new XPoint(x + 56, top + 17));
            g.DrawString($"{ReportWriter.Readable(steps[k].Kind)}. {steps[k].Size}.", detail, muted,
                new XPoint(x + 56, top + 30));
            top += 40;
        }

        if (orderPage is not null)
            g.DrawString($"The whole order, with the reasons and what each step covers, starts on page {orderPage}.",
                new XFont(Typefaces.Sans, 7.4), muted, new XPoint(x + 24, top + 8));
    }

    /// <summary>
    /// Board-formed concrete, as on the page: panel joints two to one, and the
    /// tie holes in each board. Vector lines and dots, so it costs nothing in
    /// file size and prints crisp.
    /// </summary>
    private static void Concrete(XGraphics g)
    {
        g.DrawRectangle(new XSolidBrush(Palette.X(Palette.Wall)), new XRect(0, 0, A4.Width, A4.Height));

        // A few broad, faint clouds, which is what keeps a flat grey reading
        // as a poured surface rather than as a fill colour.
        var random = new Random(7);
        for (var k = 0; k < 26; k++)
        {
            var w = 120 + random.NextDouble() * 260;
            var h = 60 + random.NextDouble() * 160;
            var cx = random.NextDouble() * A4.Width;
            var cy = random.NextDouble() * A4.Height;
            var dark = random.NextDouble() < 0.5;
            g.DrawEllipse(new XSolidBrush(Palette.X(dark ? 0x000000u : 0xffffffu, 0.018)),
                new XRect(cx - w / 2, cy - h / 2, w, h));
        }

        const double board = 160, course = 80;
        var joint = new XPen(Palette.X(0x000000, 0.13), 0.5);
        for (var gx = 0.5; gx < A4.Width; gx += board) g.DrawLine(joint, gx, 0, gx, A4.Height);
        for (var gy = 0.5; gy < A4.Height; gy += course) g.DrawLine(joint, 0, gy, A4.Width, gy);

        var hole = new XSolidBrush(Palette.X(0x000000, 0.2));
        var ring = new XPen(Palette.X(0xffffff, 0.35), 0.6);
        for (var gx = 0.0; gx < A4.Width; gx += board)
        for (var gy = 0.0; gy < A4.Height; gy += course)
        foreach (var (hx, hy) in new[] { (40.0, 20.0), (120.0, 20.0), (40.0, 60.0), (120.0, 60.0) })
        {
            g.DrawEllipse(hole, gx + hx - 2.3, gy + hy - 2.3, 4.6, 4.6);
            g.DrawEllipse(ring, gx + hx - 3.3, gy + hy - 3.3, 6.6, 6.6);
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
        return lines.Count == 0 ? [""] : lines;
    }
}
