using System.Text;
using System.Text.RegularExpressions;
using LegacyLens.Analysis;
using LegacyLens.Api.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace LegacyLens.Tests;

/// <summary>
/// The PDF is what gets put on a desk, so these read it back the way a reader
/// would: the text on the first page, and the text of the pages after it.
///
/// Text is read out of the file itself, through the fonts' own maps from glyph
/// to character, rather than by asking the writer what it meant to draw. A
/// cover that was composed and never painted would pass the second kind of
/// test and fail the reader.
/// </summary>
public class PdfReportTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static ProjectInfo Project(string name, ProjectKind kind, int lines) =>
        new(name, $"/src/{name}/{name}.csproj", kind, "v4.8", [], [], 12, lines);

    private static Assessment Sample()
    {
        var projects = new[]
        {
            Project("Shop.Web", ProjectKind.Web, 41_000),
            Project("Shop.Core", ProjectKind.Library, 9_500),
            Project("Shop.Tests", ProjectKind.Test, 2_000),
        };

        var survey = new ModernisationSurvey(
            [
                new ProjectModernisation("Shop.Web", "/src/Shop.Web/Shop.Web.csproj", false,
                    PackageDeclaration.PackagesConfig, "v4.8", ["Microsoft.AspNet.Mvc"]),
                new ProjectModernisation("Shop.Core", "/src/Shop.Core/Shop.Core.csproj", false,
                    PackageDeclaration.PackagesConfig, "v4.8", []),
                new ProjectModernisation("Shop.Tests", "/src/Shop.Tests/Shop.Tests.csproj", false,
                    PackageDeclaration.None, "v4.8", []),
            ],
            [new PackageUse("Microsoft.AspNet.Mvc", ["5.2.7"], 1, Portability.TiedToSystemWeb)],
            3);

        return new Assessment(
            "shop",
            "/repos/shop",
            new SolutionMap(projects, [new ProjectEdge("Shop.Web", "Shop.Core")], []),
            [new Finding(FindingKind.Untested, "Shop.Web", "No test project references it", "")],
            new RiskReport([], HistoryStatus.Available, null, 0, "the last 24 months"),
            survey,
            [
                new RepairStep(RepairKind.Prerequisite, "Classify the packages nobody has checked yet",
                    "Each one is either a non-event or a rewrite.", "2 packages", []),
                new RepairStep(RepairKind.Mechanical, "Convert the project files that can take the modern format",
                    "Only the file format is old.", "2 projects", ["Shop.Core"]),
                new RepairStep(RepairKind.Decision, "Decide what happens to the code built on System.Web",
                    "No version of them runs on modern .NET.", "1 project", []),
                new RepairStep(RepairKind.PossiblyFree, "Confirm whether the unreferenced projects are dead",
                    "Nothing references these.", "1 project", []),
            ],
            [new Limitation("Nothing was compiled", "The whole report is read from project files.")],
            42);
    }

    private static (string Markdown, byte[] Pdf) Render(Assessment assessment)
    {
        var markdown = new ReportWriter { GeneratedAt = When }.Write(assessment);
        return (markdown, PdfReport.Write(assessment, markdown, When));
    }

    [Fact]
    public void It_is_a_pdf_of_several_pages_signed_by_the_tool()
    {
        var (_, bytes) = Render(Sample());

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));

        using var document = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.True(document.PageCount >= 2, $"{document.PageCount} page(s)");
        Assert.Equal("Legacy Lens", document.Info.Author);
        Assert.Equal("Legacy Lens", document.Info.Creator);
        Assert.Equal("shop: structural assessment", document.Info.Title);
    }

    [Fact]
    public void The_first_page_is_the_summary()
    {
        var assessment = Sample();
        var (markdown, bytes) = Render(assessment);
        var cover = Flat(Pages(bytes)[0]);

        Assert.Contains("shop", cover);
        Assert.Contains("2026-10-19", cover);

        // The paragraph headed "In short", as the report has it.
        var summary = Regex.Match(markdown, @"## In short\n\n(.+?)\n\n", RegexOptions.Singleline).Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(summary));
        Assert.Contains(Flat(summary), cover);

        // The figures, from the same assessment the report was written from.
        Assert.Contains($"{assessment.Map.TotalLines:N0}", cover);
        Assert.Contains("lines of code", cover);
        Assert.Contains($"{assessment.UntestedLines:N0}", cover);
        Assert.Contains("lines in projects no test project references", cover);
        Assert.Contains("packages that exist only inside the .NET Framework", cover);
        Assert.Contains("projects convertible as they stand", cover);

        // The first three steps of the order of work, and not the fourth.
        Assert.Contains("Classify the packages nobody has checked yet", cover);
        Assert.Contains("Convert the project files that can take the modern format", cover);
        Assert.Contains("Decide what happens to the code built on System.Web", cover);
        Assert.DoesNotContain("Confirm whether the unreferenced projects are dead", cover);
    }

    [Fact]
    public void The_pages_after_it_carry_the_whole_report()
    {
        var (markdown, bytes) = Render(Sample());
        var pages = Pages(bytes);
        var body = Flat(string.Join(" ", pages.Skip(1)));

        foreach (Match heading in Regex.Matches(markdown, @"^##+ (.+)$", RegexOptions.Multiline))
            Assert.Contains(Flat(Markdown.Plain(heading.Groups[1].Value)), body);

        // What could not be seen is printed, as in the text.
        Assert.Contains("Nothing was compiled.", body);

        // Every page after the cover says what it is part of and where it is.
        for (var k = 1; k < pages.Count; k++)
        {
            Assert.Contains("Legacy Lens", pages[k]);
            Assert.Contains($"Page {k + 1} of {pages.Count}", pages[k]);
        }
    }

    [Fact]
    public void The_dependency_diagram_is_drawn_rather_than_printed_as_source()
    {
        var (markdown, bytes) = Render(Sample());
        Assert.Contains("```mermaid", markdown);

        var body = Flat(string.Join(" ", Pages(bytes).Skip(1)));

        Assert.DoesNotContain("graph LR", body);
        Assert.DoesNotContain("-->", body);
        Assert.Contains("Drawn from the Mermaid source", body);
        Assert.Contains("depends on", body);
    }

    [Fact]
    public void A_diagram_outside_the_subset_is_not_guessed_at()
    {
        Assert.Null(Diagram.Parse("classDiagram\n  class A", 400));
        Assert.Null(Diagram.Parse("graph LR\n  A -.-> B", 400));
        Assert.NotNull(Diagram.Parse("graph LR\n  nA[\"A<br/>10 lines\"]\n  nB[\"B\"]\n  nA --> nB", 400));
    }

    [Fact]
    public void A_cycle_does_not_stop_the_diagram()
    {
        var diagram = Diagram.Parse("graph LR\n  nA[\"A\"]\n  nB[\"B\"]\n  nA --> nB\n  nB --> nA", 400);

        Assert.NotNull(diagram);
        Assert.True(diagram.Height > 0);
    }

    private static readonly Encoding WinAnsi = WinAnsiEncoding();

    private static Encoding WinAnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static string Flat(string text) => Regex.Replace(text.Replace("​", ""), @"\s+", " ").Trim();

    /// <summary>
    /// The text of each page. PDFsharp writes a face in WinAnsi when every
    /// character fits, one byte per glyph, and as a two-byte Unicode font with
    /// a ToUnicode map when one does not; both are read.
    /// </summary>
    private static List<string> Pages(byte[] bytes)
    {
        using var document = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        return document.Pages.Cast<PdfPage>().Select(PageText).ToList();
    }

    private static string PageText(PdfPage page)
    {
        var maps = new Dictionary<string, Dictionary<int, string>?>();
        var fonts = page.Resources.Elements.GetDictionary("/Font");
        foreach (var key in fonts?.Elements.Keys ?? [])
        {
            var stream = fonts!.Elements.GetDictionary(key)?.Elements.GetDictionary("/ToUnicode")?.Stream;
            maps[key] = stream is null ? null : CMap(Encoding.ASCII.GetString(stream.UnfilteredValue));
        }

        var text = new StringBuilder();
        Dictionary<int, string>? map = null;

        void Show(CString value)
        {
            var raw = value.Value;
            if (map is null)
            {
                text.Append(WinAnsi.GetString(raw.Select(c => (byte)c).ToArray()));
                return;
            }
            for (var k = 0; k + 1 < raw.Length; k += 2)
            {
                var glyph = (raw[k] << 8) | raw[k + 1];
                if (map.TryGetValue(glyph, out var character)) text.Append(character);
            }
        }

        void Walk(CSequence sequence)
        {
            foreach (var item in sequence)
            {
                if (item is not COperator op) continue;
                switch (op.OpCode.Name)
                {
                    case "Tf":
                        map = maps.GetValueOrDefault(((CName)op.Operands[0]).Name);
                        break;
                    case "Tj" or "'" or "\"":
                        if (op.Operands[^1] is CString s) Show(s);
                        text.Append(' ');
                        break;
                    case "TJ":
                        foreach (var part in (CArray)op.Operands[0])
                            if (part is CString piece) Show(piece);
                        text.Append(' ');
                        break;
                }
            }
        }

        Walk(ContentReader.ReadContent(page));
        return text.ToString();
    }

    private static Dictionary<int, string> CMap(string cmap)
    {
        var map = new Dictionary<int, string>();

        foreach (Match range in Regex.Matches(cmap, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
        {
            var from = Convert.ToInt32(range.Groups[1].Value, 16);
            var to = Convert.ToInt32(range.Groups[2].Value, 16);
            var first = Convert.ToInt32(range.Groups[3].Value, 16);
            for (var glyph = from; glyph <= to; glyph++)
                map[glyph] = char.ConvertFromUtf32(first + glyph - from);
        }

        foreach (Match single in Regex.Matches(cmap, @"(?m)^\s*<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4,8})>\s*$"))
        {
            var unicode = single.Groups[2].Value;
            map[Convert.ToInt32(single.Groups[1].Value, 16)] =
                Encoding.BigEndianUnicode.GetString(Convert.FromHexString(unicode));
        }

        return map;
    }
}
