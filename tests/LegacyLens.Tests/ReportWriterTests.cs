using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The report is the one output a client keeps and forwards, so what is tested
/// here is what a reader would catch: a number that contradicts a sentence, a
/// plural that gives away the template, a section that renders as a wall.
///
/// The assessment is built by hand rather than measured from a directory. These
/// tests are about what the document says given the facts, not about how the
/// facts were gathered, which the analysis tests already cover.
/// </summary>
public class ReportWriterTests
{
    private static ProjectInfo Project(
        string name,
        ProjectKind kind = ProjectKind.Library,
        int lines = 1_000,
        string target = "v4.8") =>
        new(name, $"/src/{name}/{name}.csproj", kind, target, [], [], 10, lines);

    private static Assessment Assessment(
        ProjectInfo[]? projects = null,
        Finding[]? findings = null,
        RiskEntry[]? risks = null,
        ModernisationSurvey? survey = null,
        RepairStep[]? repairs = null,
        Limitation[]? limitations = null,
        HistoryStatus history = HistoryStatus.Available,
        string? historyNote = null,
        string? historyWindow = "the last 24 months")
    {
        projects ??= [Project("Core"), Project("Web", ProjectKind.Web, 40_000)];

        return new Assessment(
            "Sample",
            "/repos/sample",
            new SolutionMap(projects, [], []),
            findings ?? [],
            new RiskReport(risks ?? [], history, history == HistoryStatus.Available
                ? historyNote
                : "Not a git repository.", 0, historyWindow),
            survey ?? new ModernisationSurvey([], [], 0),
            repairs ?? [],
            limitations ?? [],
            42);
    }

    private static string Write(Assessment assessment) =>
        new ReportWriter { GeneratedAt = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero) }
            .Write(assessment);

    /// <summary>
    /// The document with its hard wrapping undone.
    ///
    /// Prose is wrapped at 80 columns, so a sentence under test is split across
    /// lines at a position that depends on everything before it. Asserting on
    /// the wrapped text would make these tests fail when an unrelated sentence
    /// changes length, which is a test that reports the wrong thing.
    /// </summary>
    private static string Prose(string report) => report.Replace('\n', ' ');

    [Fact]
    public void The_document_opens_with_what_it_is_and_when_it_was_made()
    {
        var report = Write(Assessment());

        Assert.StartsWith("# Sample", report);
        Assert.Contains("2026-08-14", report);
    }

    [Fact]
    public void Counts_are_pluralised()
    {
        // "1 projects" and "0 dependencys" in a document that goes to a client
        // undo the credibility of every number beside them.
        var one = Write(Assessment(projects: [Project("Only")]));

        Assert.Contains("1 project ", Prose(one));
        Assert.DoesNotContain("1 projects", Prose(one));
        Assert.DoesNotContain("dependencys", Prose(one));
    }

    [Fact]
    public void A_single_target_framework_is_stated_as_one_rather_than_listed()
    {
        var report = Write(Assessment());
        Assert.Contains("Everything targets v4.8.", Prose(report));
    }

    [Fact]
    public void A_solution_mid_migration_lists_every_framework_it_targets()
    {
        // The single most useful fact before quoting anything else, and the one
        // a single-value summary would hide.
        var report = Write(Assessment(projects:
        [
            Project("Old"),
            Project("Older", target: "v4.5"),
            Project("New", target: "net8.0"),
        ]));

        Assert.Contains("3 frameworks", Prose(report));
        Assert.Contains("v4.8", report);
        Assert.Contains("net8.0", report);
    }

    [Fact]
    public void The_diagram_is_bounded_so_it_stays_readable()
    {
        // Orchard has 56 projects over the line threshold, and drawing all of
        // them produces 250 lines of Mermaid that render as a wall.
        var many = Enumerable.Range(0, 40)
            .Select(i => Project($"Module{i}", lines: 1_000 + i))
            .ToArray();

        var writer = new ReportWriter { DiagramMaxProjects = 12 };
        var report = writer.Write(Assessment(projects: many));

        var boxes = report.Split('\n').Count(line => line.Contains("lines\"]"));
        Assert.True(boxes <= 12, $"The diagram drew {boxes} boxes.");

        // And what fell off is stated in prose, because a Mermaid comment is
        // not rendered by anything the reader will open this in.
        Assert.Contains("are left out", Prose(report));
    }

    [Fact]
    public void A_ranked_file_never_has_an_empty_reason()
    {
        // A blank cell reads as a gap in the analysis rather than as a file
        // that ranked high without any single reason standing out.
        var report = Write(Assessment(risks:
        [
            new RiskEntry("src/Quiet.cs", 0.80, 40, 5, "Handle", 3, 200, 4, 2, true, []),
        ]));

        Assert.Contains("ranks high on complexity and change frequency together", report);
        Assert.DoesNotContain("| |", report.Replace("| |", "|BLANK|"));
    }

    [Fact]
    public void Unavailable_history_is_stated_instead_of_reading_as_stability()
    {
        // A codebase where nothing ever changes and one whose history could not
        // be read produce the same empty column.
        var report = Write(Assessment(
            risks: [new RiskEntry("src/A.cs", 0.5, 10, 5, "M", 2, 200, 0, 0, false, [])],
            history: HistoryStatus.NotARepository));

        Assert.Contains("Change history was not read", Prose(report));
        Assert.Contains("Not a git repository.", Prose(report));
    }

    [Fact]
    public void A_tended_legacy_and_a_drifted_one_do_not_get_the_same_paragraph()
    {
        var tended = Write(Assessment(survey: Survey(divergent: false, redirects: 0)));
        Assert.Contains("This legacy has been tended", Prose(tended));

        var drifted = Write(Assessment(survey: Survey(divergent: true, redirects: 4)));
        Assert.Contains("This legacy has drifted", Prose(drifted));
    }

    [Fact]
    public void Redirects_without_divergent_versions_are_described_as_what_they_are()
    {
        // Orchard has 279 hand-written redirects and no package pinned to two
        // versions. Telling its owner that its packages disagree today is a
        // claim the table above the sentence contradicts.
        var report = Write(Assessment(survey: Survey(divergent: false, redirects: 279)));

        Assert.Contains("Every package is pinned to a single version today", Prose(report));
        Assert.DoesNotContain("Packages disagree with each other", Prose(report));
    }

    [Fact]
    public void The_order_of_work_carries_no_estimate_in_days()
    {
        // Nothing in this tool measured how fast anyone works, so a document
        // that implies a schedule is asserting something it did not measure.
        var report = Write(Assessment(repairs:
        [
            new RepairStep(RepairKind.Mechanical, "Convert the project files",
                "Nothing stands in the way and the conversion is a tool run and a review.",
                "16 projects", ["Orchard.Core"]),
        ]));

        Assert.Contains("dependency order, not a schedule", Prose(report));
        Assert.DoesNotContain(" days of work", report);
        Assert.DoesNotContain("man-day", report);
    }

    [Fact]
    public void What_could_not_be_seen_is_printed_in_the_document()
    {
        var report = Write(Assessment(limitations:
        [
            new Limitation("Nothing was compiled",
                "Anything resolved at runtime is invisible here."),
        ]));

        Assert.Contains("## What this report does not say", report);
        Assert.Contains("**Nothing was compiled.**", Prose(report));
    }

    [Fact]
    public void A_pipe_in_a_name_does_not_break_the_table_it_sits_in()
    {
        var report = Write(Assessment(projects: [Project("Odd|Name")]));
        Assert.Contains(@"Odd\|Name", report);
    }

    [Fact]
    public void Prose_is_wrapped_so_the_generated_file_stays_reviewable()
    {
        // The document is regenerated on every commit, so it is read as a diff
        // at least as often as it is read as a document.
        var report = Write(Assessment());

        var longest = report.Split('\n')
            .Where(line => !line.StartsWith('|') && !line.Contains("http"))
            .Max(line => line.Length);

        Assert.True(longest <= 100, $"The longest prose line is {longest} characters.");
    }

    private static ModernisationSurvey Survey(bool divergent, int redirects)
    {
        var versions = divergent ? new[] { "1.0.0", "2.0.0" } : ["1.0.0"];

        return new ModernisationSurvey(
            [new ProjectModernisation("App", "/src/App/App.csproj", false,
                PackageDeclaration.PackagesConfig, "v4.8", [])],
            [new PackageUse("Newtonsoft.Json", versions, 1, Portability.Portable)],
            redirects);
    }

    [Fact]
    public void The_report_names_the_stretch_of_history_it_counted_over()
    {
        // A report read next to last quarter's is only comparable when both
        // say which stretch they counted, and this one is not always the one
        // that was asked for.
        // Read as prose: the report wraps to a column, so any phrase long
        // enough to be worth asserting on is split across lines.
        var report = Write(Assessment(
            risks: [new RiskEntry("src/A.cs", 0.5, 10, 5, "M", 2, 200, 4, 2, false, [])]));

        Assert.Contains("change frequency from git over the last 24 months", Prose(report));
    }

    [Fact]
    public void A_widened_window_is_explained_where_the_ranking_is()
    {
        // Not in a footnote. A reader who sees churn counts spanning a decade
        // has to be told why without going looking.
        var report = Write(Assessment(
            risks: [new RiskEntry("src/A.cs", 0.5, 10, 5, "M", 2, 200, 82, 24, false, [])],
            historyNote: "Only 102 of this directory's 11677 commits fall in the last 24 months, "
                       + "so the whole history was read instead.",
            historyWindow: "the full history"));

        Assert.Contains("change frequency from git over the full history", Prose(report));
        Assert.Contains("The history window was widened", Prose(report));
        Assert.Contains("11677 commits", Prose(report));
    }
}
