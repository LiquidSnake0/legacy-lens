using System.Text;

namespace LegacyLens.Analysis;

/// <summary>
/// Turns an assessment into the document a client keeps.
///
/// Markdown rather than HTML or PDF: it renders on its own, converts into both,
/// and survives being pasted into a mail, a ticket or a wiki. A report nobody
/// can forward is a report nobody reads.
///
/// Nothing here calls a model. Every sentence below is a template filled with a
/// measured number, which is the strongest available answer to the question a
/// buyer asks about any generated document, namely whether it made anything up.
/// The templates are dull on purpose. A report that reads as though it were
/// written by a person, and was not, spends credibility it will need later.
/// </summary>
public class ReportWriter
{
    /// <summary>How many ranked files the risk table lists.</summary>
    public int TopRisks { get; init; } = 15;

    /// <summary>Projects smaller than this are left off the diagram.</summary>
    public int DiagramMinimumLines { get; init; } = 500;

    /// <summary>
    /// The most boxes the diagram is allowed to hold.
    ///
    /// A line threshold alone does not bound the picture: Orchard has 56
    /// projects over 500 lines, and drawing all of them produces 250 lines of
    /// Mermaid that render as a wall. The threshold is raised until the count
    /// fits, and what fell off is stated in the sentence above the diagram
    /// rather than in a Mermaid comment, which nothing renders.
    /// </summary>
    public int DiagramMaxProjects { get; init; } = 12;

    /// <summary>
    /// Stamped on the document. Injectable so that a test can assert on the
    /// whole output, and so that a report regenerated in CI can carry the
    /// commit's timestamp rather than the build machine's clock.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Write(Assessment assessment)
    {
        var report = new StringBuilder();

        Header(report, assessment);
        Glance(report, assessment);
        Summary(report, assessment);
        Shape(report, assessment);
        Hurt(report, assessment);
        Migration(report, assessment);
        Used(report, assessment);
        Facing(report, assessment);
        Decide(report, assessment);
        Order(report, assessment);
        Unsaid(report, assessment);
        Appendix(report, assessment);
        Method(report, assessment);

        return report.ToString();
    }

    private void Header(StringBuilder report, Assessment assessment)
    {
        report.AppendLine($"# {assessment.Name}");
        report.AppendLine();
        report.AppendLine(
            $"A structural assessment, generated on {GeneratedAt:yyyy-MM-dd} by Legacy Lens.");
        report.AppendLine();
        report.AppendLine(Wrap(
            "Every number in this document was measured from the project files, the source "
          + "text and the change history. Nothing was estimated, and no part of it was "
          + "written by a language model. What could not be measured is listed at the end "
          + "rather than left out."));
        report.AppendLine();
    }

    /// <summary>One line of the grid at the top: an area, a word, the count behind the word.</summary>
    public sealed record Verdict(string Area, string Word, string Measured, string Section);

    private const string HurtSection = "What will hurt";
    private const string MoveSection = "What a move to modern .NET runs into";
    private const string UnsaidSection = "What this report does not say";

    /// <summary>
    /// The grid a reader who reads one page acts on: one line per area, a
    /// verdict in a word, the measurement it rests on, and the section with
    /// the detail.
    ///
    /// Modelled on the maturity grid that security assessments open with. Each
    /// word is a rule over the count printed beside it, written out below, so a
    /// reader who disagrees with the word can see exactly what it was given.
    /// An area with nothing measured is left out rather than given a verdict.
    /// </summary>
    public static IReadOnlyList<Verdict> Verdicts(Assessment assessment)
    {
        var survey = assessment.Modernisation;
        var verdicts = new List<Verdict>();

        var unreadable = assessment.Of(FindingKind.Unreadable).Count();
        if (unreadable > 0)
        {
            verdicts.Add(new("Project files", "incomplete",
                $"{Assessor.Count(unreadable, "project file")} could not be read, so every "
              + "count below is short", HurtSection));
        }

        if (survey.Projects.Count > 0)
        {
            verdicts.Add(survey.PreSdk == 0
                ? new("Project format", "modern",
                    $"all {Assessor.Count(survey.Projects.Count, "project")} in the SDK format", MoveSection)
                : new("Project format", "to convert",
                    $"{survey.PreSdk:N0} of {survey.Projects.Count:N0} in the old format, "
                  + $"{survey.ConvertibleAsIs:N0} convertible as they stand", MoveSection));

            verdicts.Add(survey.DeadEnds.Count > 0
                ? new("Packages with no future", "blocking",
                    $"{Assessor.Count(survey.DeadEnds.Count, "package")} that exist only inside the "
                  + $".NET Framework; {survey.Blocked:N0} of {survey.Projects.Count:N0} projects "
                  + "reference one", MoveSection)
                : new("Packages with no future", "none found",
                    "no package known to exist only inside the .NET Framework", MoveSection));
        }

        if (survey.Packages.Count > 0)
        {
            var unknown = survey.Packages.Count(p => p.Portability == Portability.Unknown);
            verdicts.Add(unknown > 0
                ? new("Packages nobody has classified", "unknown",
                    $"{unknown:N0} of {survey.Packages.Count:N0} distinct packages, counted as "
                  + "unknown rather than as fine", MoveSection)
                : new("Packages nobody has classified", "none",
                    $"all {survey.Packages.Count:N0} distinct packages classified", MoveSection));
        }

        var production = assessment.Production.Count;
        if (production > 0)
        {
            var untested = assessment.Of(FindingKind.Untested).Count();
            var share = (double)untested / production;
            var word = untested == 0 ? "broad"
                     : share <= 0.25 ? "gaps"
                     : share < 0.75 ? "patchy"
                     : "thin";
            verdicts.Add(new("Tests", word,
                untested == 0
                    ? $"a test project references each of the {Assessor.Count(production, "project")} that ship code"
                    : $"no test project references {untested:N0} of the {production:N0} projects that "
                    + $"ship code, {assessment.UntestedLines:N0} lines",
                HurtSection));
        }

        if (survey.Projects.Count > 0)
        {
            verdicts.Add((survey.Divergent > 0, survey.BindingRedirects > 0) switch
            {
                (false, false) => new("Package versions", "tended",
                    "every package on one version, no binding redirect", MoveSection),
                (false, true) => new("Package versions", "paid for once",
                    $"every package on one version today, {Assessor.Count(survey.BindingRedirects, "binding redirect")} "
                  + "written by hand", MoveSection),
                _ => new("Package versions", "drifting",
                    $"{Assessor.Count(survey.Divergent, "package")} on more than one version, "
                  + $"{Assessor.Count(survey.BindingRedirects, "binding redirect")} written by hand", MoveSection),
            });
        }

        var coupled = assessment.Of(FindingKind.LibraryCoupledToWeb).Count();
        if (coupled > 0)
        {
            verdicts.Add(new("Libraries tied to the web stack", "to separate",
                $"{Assessor.Count(coupled, "class library")} {(coupled == 1 ? "depends" : "depend")} on the web stack", HurtSection));
        }

        var cycles = assessment.Of(FindingKind.DependencyCycle).Count();
        if (cycles > 0)
        {
            verdicts.Add(new("Dependency cycles", "to break",
                $"{Assessor.Count(cycles, "cycle")} between projects", HurtSection));
        }

        var risk = assessment.Risk;
        if (risk.Entries.Count > 0 || risk.HistoryStatus != HistoryStatus.Available)
        {
            verdicts.Add(risk.HistoryStatus == HistoryStatus.Available
                ? new("Change history", "read",
                    $"{Assessor.Count(risk.Entries.Count, "file")} ranked on structure and churn"
                  + (risk.HistoryWindow is { Length: > 0 } window ? $" over {window}" : ""), HurtSection)
                : new("Change history", "not read",
                    "files ranked on structure alone", HurtSection));
        }

        return verdicts;
    }

    private static void Glance(StringBuilder report, Assessment assessment)
    {
        var verdicts = Verdicts(assessment);
        if (verdicts.Count == 0) return;

        report.AppendLine("## At a glance");
        report.AppendLine();
        report.AppendLine("| Area | Verdict | Measured | Detail in |");
        report.AppendLine("|---|---|---|---|");
        foreach (var verdict in verdicts)
        {
            report.AppendLine(
                $"| {verdict.Area} | **{verdict.Word}** | {Cell(verdict.Measured)} | {verdict.Section} |");
        }
        report.AppendLine();

        // The limits are the last section, which is where nobody reads. Said
        // here, next to the table people act on, as security reports do on
        // their second page.
        if (assessment.Limitations.Count > 0)
        {
            report.AppendLine(Lead("Read it with its limits",
                $"{Assessor.Count(assessment.Limitations.Count, "limit")} of this analysis "
              + $"{(assessment.Limitations.Count == 1 ? "applies" : "apply")} to this codebase. They are listed under \"{UnsaidSection}\", at the "
              + "end, and belong next to any decision taken from this table."));
            report.AppendLine();
        }
    }

    /// <summary>
    /// The paragraph for a reader who will read one paragraph.
    ///
    /// Assembled sentence by sentence from what was measured, and each sentence
    /// is skipped when the fact behind it is missing, so that a codebase
    /// without git history does not get a sentence about its change history
    /// that quietly says nothing.
    /// </summary>
    private static void Summary(StringBuilder report, Assessment assessment)
    {
        var map = assessment.Map;
        var survey = assessment.Modernisation;
        var sentences = new List<string>();

        sentences.Add(
            $"{assessment.Name} is {Assessor.Count(map.Projects.Count, "project")} and "
          + $"{map.TotalLines:N0} lines across {map.TotalFiles:N0} source files.");

        var frameworks = assessment.Frameworks;
        if (frameworks.Count == 1)
        {
            sentences.Add($"Everything targets {frameworks[0].Framework}.");
        }
        else if (frameworks.Count > 1)
        {
            var listed = frameworks.Take(3)
                .Select(f => $"{f.Framework} ({f.Projects})");
            sentences.Add(
                $"It targets {Assessor.Count(frameworks.Count, "framework")}: "
              + $"{string.Join(", ", listed)}.");
        }

        var untested = assessment.Of(FindingKind.Untested).Count();
        if (untested > 0)
        {
            sentences.Add(
                $"No test project references {untested:N0} of the "
              + $"{Assessor.Count(assessment.Production.Count, "project")} that ship code, "
              + $"covering {assessment.UntestedLines:N0} lines.");
        }

        var worst = assessment.Risk.Entries.FirstOrDefault();
        if (worst is not null)
        {
            var ranked = $"{Assessor.Count(assessment.Risk.Entries.Count, "file")} were ranked "
                       + "on structure and change frequency together.";

            sentences.Add(worst.WorstMethod is not null && worst.WorstMethodComplexity >= 20
                ? $"{ranked} The one at the top is {worst.Path}, where {worst.WorstMethod} "
                + $"has a cyclomatic complexity of {worst.WorstMethodComplexity}, meaning "
                + $"{worst.WorstMethodComplexity} tests to cover its branches."
                : $"{ranked} The one at the top is {worst.Path}.");
        }

        if (survey.PreSdk == 0 && survey.Projects.Count > 0)
        {
            sentences.Add("Every project file is already in the SDK format.");
        }
        else if (survey.Projects.Count > 0)
        {
            var packaging = $"{survey.PreSdk:N0} of "
                          + $"{Assessor.Count(survey.Projects.Count, "project file")} are "
                          + "in the pre-SDK format";

            sentences.Add(survey.Blocked > 0
                ? $"{packaging}. {Assessor.Count(survey.Blocked, "project")} reference "
                + "packages that exist only inside the .NET Framework, which no conversion "
                + $"tool will fix; {Assessor.Count(survey.ConvertibleAsIs, "other")} are "
                + "convertible as they stand."
                : $"{packaging}, and none of them references a package known to be a dead "
                + "end on modern .NET.");
        }

        if (assessment.Repairs.Count > 0)
        {
            sentences.Add(
                "The work is ordered from what blocks everything else through to what may "
              + $"cost nothing, and starts with: {assessment.Repairs[0].Title.ToLowerInvariant()}.");
        }

        report.AppendLine("## In short");
        report.AppendLine();
        report.AppendLine(Wrap(string.Join(" ", sentences)));
        report.AppendLine();
    }

    private void Shape(StringBuilder report, Assessment assessment)
    {
        var map = assessment.Map;

        report.AppendLine("## What this is");
        report.AppendLine();

        if (map.Projects.Count == 0)
        {
            report.AppendLine("No project files were found under this directory.");
            report.AppendLine();
            return;
        }

        report.AppendLine("| Kind | Projects | Lines | Files |");
        report.AppendLine("|---|---:|---:|---:|");

        foreach (var group in map.Projects
                     .GroupBy(p => p.Kind)
                     .OrderByDescending(g => g.Sum(p => p.Lines)))
        {
            report.AppendLine(
                $"| {Readable(group.Key)} | {group.Count():N0} | "
              + $"{group.Sum(p => p.Lines):N0} | {group.Sum(p => p.SourceFiles):N0} |");
        }

        report.AppendLine(
            $"| **Total** | **{map.Projects.Count:N0}** | **{map.TotalLines:N0}** | "
          + $"**{map.TotalFiles:N0}** |");
        report.AppendLine();

        // Stating that a single-project solution declares no dependencies on
        // itself is true and reads as filler.
        var dependencies = map.Projects.Count > 1
            ? $"The projects declare {Assessor.Count(map.Edges.Count, "dependency")} on each other. "
            : "";

        report.AppendLine(Wrap(
            dependencies
          + "Project kind is decided by what sits in the folder rather than by which "
          + "assemblies are referenced, because references lie and a web.config next to a "
          + "Views folder does not."));
        report.AppendLine();

        var threshold = DiagramThreshold(map);
        var drawn = map.Projects.Count(p => p.Kind != ProjectKind.Test && p.Lines >= threshold);

        var mermaid = new MermaidWriter
        {
            MinimumLines = threshold,
            IncludeTests = false,
        }.Write(map);

        if (!string.IsNullOrWhiteSpace(mermaid) && drawn > 0)
        {
            var omitted = map.Projects.Count - drawn;

            report.AppendLine(Wrap(omitted > 0
                ? $"The {Assessor.Count(drawn, "project")} over {threshold:N0} lines, and how "
                + $"they depend on each other. {Assessor.Count(omitted, "project")} are left "
                + "out: the test projects, and everything smaller than that. A diagram of all "
                + $"{map.Projects.Count:N0} renders as a wall and tells a reader nothing."
                : "The projects, and how they depend on each other."));
            report.AppendLine();

            report.AppendLine("```mermaid");
            report.AppendLine(mermaid.TrimEnd());
            report.AppendLine("```");
            report.AppendLine();
        }

        var biggest = map.Projects
            .Where(p => p.Lines > 0)
            .OrderByDescending(p => p.Lines)
            .Take(10)
            .ToList();

        if (biggest.Count > 0)
        {
            report.AppendLine("The largest projects, which is where the time goes:");
            report.AppendLine();
            report.AppendLine("| Project | Kind | Target | Lines |");
            report.AppendLine("|---|---|---|---:|");

            foreach (var project in biggest)
            {
                report.AppendLine(
                    $"| `{Cell(project.Name)}` | {Readable(project.Kind)} | "
                  + $"{Cell(project.TargetFramework ?? "not stated")} | {project.Lines:N0} |");
            }

            report.AppendLine();
        }
    }

    /// <summary>
    /// The line count that keeps the diagram inside <see cref="DiagramMaxProjects"/>.
    ///
    /// Ties are kept rather than cut arbitrarily: two projects of exactly the
    /// same size have equal claim to the last box, and dropping one of them by
    /// sort order would be a decision the data does not support.
    /// </summary>
    private int DiagramThreshold(SolutionMap map)
    {
        var sizes = map.Projects
            .Where(p => p.Kind != ProjectKind.Test)
            .Select(p => p.Lines)
            .OrderByDescending(lines => lines)
            .ToList();

        return sizes.Count <= DiagramMaxProjects
            ? DiagramMinimumLines
            : Math.Max(DiagramMinimumLines, sizes[DiagramMaxProjects - 1]);
    }

    private void Hurt(StringBuilder report, Assessment assessment)
    {
        report.AppendLine($"## {HurtSection}");
        report.AppendLine();

        if (assessment.Findings.Count == 0 && assessment.Risk.Entries.Count == 0)
        {
            report.AppendLine(Wrap(
                "Nothing was found. On a codebase of this size that is more likely to mean "
              + "the analysis found nothing to read than that there is nothing to find; the "
              + "final section says what it could not see."));
            report.AppendLine();
            return;
        }

        if (assessment.Findings.Count > 0)
        {
            report.AppendLine("| Finding | Count | Example |");
            report.AppendLine("|---|---:|---|");

            foreach (var group in assessment.Findings
                         .GroupBy(f => f.Kind)
                         .OrderByDescending(g => g.Count()))
            {
                var example = group.First();
                report.AppendLine(
                    $"| {Readable(group.Key)} | {group.Count():N0} | "
                  + $"`{Cell(example.Project)}`: {Cell(example.Summary)} |");
            }

            report.AppendLine();

            // The detail of each kind is printed once rather than once per
            // occurrence. Twenty untested projects share one explanation, and
            // repeating it twenty times trains the reader to skip it.
            foreach (var group in assessment.Findings
                         .GroupBy(f => f.Kind)
                         .OrderByDescending(g => g.Count()))
            {
                report.AppendLine(Lead(Readable(group.Key), group.First().Detail));
                report.AppendLine();
            }
        }

        if (assessment.Risk.Entries.Count == 0) return;

        report.AppendLine("### The files most likely to break");
        report.AppendLine();
        report.AppendLine(Wrap(
            "Ranked on three signals that were all already on disk: complexity from the "
          + "syntax tree, change frequency from git"
          + (assessment.Risk.HistoryWindow is { Length: > 0 } window
                ? $" over {window}"
                : string.Empty)
          + ", and whether a test appears to cover the "
          + "file. The score is the geometric mean of structure and churn, so a file has to "
          + "score high on both to reach the top: complicated but never touched is not "
          + "urgent, and touched constantly but trivial is not dangerous. The score is a "
          + "sort key, and the reasons beside it are the argument. Generated code and "
          + "everything inside a test project are left out rather than ranked, because "
          + "reporting that a test fixture is complicated and untested is true and useless."));
        report.AppendLine();

        // Said where the ranking is, not in a footnote. A report read next to
        // last quarter's is only comparable when both name the stretch they
        // counted over, and this one is not always the stretch that was asked
        // for: a repository that has stopped changing is read whole.
        if (assessment.Risk.HistoryStatus == HistoryStatus.Available
            && assessment.Risk.HistoryNote is { Length: > 0 } widened)
        {
            report.AppendLine(Lead("The history window was widened", widened));
            report.AppendLine();
        }

        report.AppendLine("| Score | File | Lines | Commits | Tested | Why |");
        report.AppendLine("|---:|---|---:|---:|---|---|");

        foreach (var entry in assessment.Risk.Entries.Take(TopRisks))
        {
            // A file can rank high without any single reason crossing the
            // threshold that earns a sentence. An empty cell in a document that
            // goes to a client reads as a gap in the analysis rather than as
            // what it is, so the reason for the rank is stated outright.
            var why = entry.Reasons.Count > 0
                ? string.Join("; ", entry.Reasons)
                : "ranks high on complexity and change frequency together, "
                + "with nothing individually remarkable";

            report.AppendLine(
                $"| {entry.Score:0.00} | `{Cell(entry.Path)}` | {entry.CodeLines:N0} | "
              + $"{entry.Commits:N0} | {(entry.Tested ? "yes" : "**no**")} | "
              + $"{Cell(why)} |");
        }

        report.AppendLine();

        if (assessment.Risk.HistoryStatus != HistoryStatus.Available)
        {
            report.AppendLine(Lead("Change history was not read",
                $"{assessment.Risk.HistoryNote} "
              + "The commit column above is therefore empty everywhere, and the ranking "
              + "rests on structure alone. A codebase where nothing ever changes and one "
              + "whose history could not be read produce the same empty result, and "
              + "reporting the first when the truth is the second is exactly the confident "
              + "wrong answer this tool exists to avoid."));
            report.AppendLine();
        }
    }

    private static void Migration(StringBuilder report, Assessment assessment)
    {
        var survey = assessment.Modernisation;
        if (survey.Projects.Count == 0) return;

        report.AppendLine($"## {MoveSection}");
        report.AppendLine();

        report.AppendLine("| | Count |");
        report.AppendLine("|---|---:|");
        report.AppendLine($"| Projects | {survey.Projects.Count:N0} |");
        report.AppendLine($"| In the old project format | {survey.PreSdk:N0} |");
        report.AppendLine($"| Convertible as they stand | {survey.ConvertibleAsIs:N0} |");
        report.AppendLine($"| Blocked by a package with no path forward | {survey.Blocked:N0} |");
        report.AppendLine($"| Package references in total | {survey.References:N0} |");
        report.AppendLine($"| Distinct packages | {survey.Packages.Count:N0} |");
        report.AppendLine($"| Pinned to more than one version | {survey.Divergent:N0} |");
        report.AppendLine($"| Hand-written binding redirects | {survey.BindingRedirects:N0} |");
        report.AppendLine();

        // Which of the two signals fired decides what can honestly be said. A
        // solution with 279 redirects and no divergent versions has not been
        // caught disagreeing with itself today, and telling its owner that it
        // has is the kind of error that costs a reader the whole document.
        var drift = (survey.Divergent > 0, survey.BindingRedirects > 0) switch
        {
            (true, true) =>
                $"{Assessor.Count(survey.Divergent, "package")} are pinned to more than one "
              + $"version, and {survey.BindingRedirects:N0} binding redirects were written by "
              + "hand to paper over the disagreements.",

            (false, true) =>
                "Every package is pinned to a single version today, but "
              + $"{Assessor.Count(survey.BindingRedirects, "binding redirect")} were written "
              + "by hand. A redirect only ever gets written because two assemblies disagreed, "
              + "so they are the trace of drift that has already been paid for once.",

            (true, false) =>
                $"{Assessor.Count(survey.Divergent, "package")} are pinned to more than one "
              + "version. Nothing has needed a binding redirect yet, and a conversion is "
              + "where that stops being true.",

            _ => "",
        };

        report.AppendLine(survey.Tended
            ? Lead("This legacy has been tended",
                "Every package is pinned to one version and nothing needed a binding "
              + "redirect. Old but coherent is a different job from old and drifted, and it "
              + "should not carry the same estimate.")
            : Lead("This legacy has drifted",
                drift
              + " Old and drifted is a different job from old but coherent, and the "
              + "difference belongs in the price rather than in the reader's head."));
        report.AppendLine();

        if (survey.DeadEnds.Count > 0)
        {
            report.AppendLine("Packages that exist only inside the .NET Framework:");
            report.AppendLine();
            report.AppendLine("| Package | Projects |");
            report.AppendLine("|---|---:|");

            foreach (var package in survey.DeadEnds)
                report.AppendLine($"| `{Cell(package.Id)}` | {package.Projects:N0} |");

            report.AppendLine();
            report.AppendLine(Wrap(
                "No version of these runs on modern .NET, so no conversion tool will help. "
              + "The code that calls them is rewritten, replaced, or left where it is."));
            report.AppendLine();
        }

        var unknown = survey.Packages.Count(p => p.Portability == Portability.Unknown);
        if (unknown > 0)
        {
            report.AppendLine(Wrap(
                $"{Assessor.Count(unknown, "package")} are reported as unclassified rather "
              + "than assumed to be fine. Naming a package portable without checking is how "
              + "a quote acquires the one item that doubles it."));
            report.AppendLine();
        }
    }

    /// <summary>
    /// What the codebase actually uses of the packages with no future.
    ///
    /// The section the report was missing. Everything above it counts what a
    /// solution declares: how many projects reference a package, how many are
    /// unclassified. None of that separates an afternoon of find-and-replace
    /// from a rewrite, and the thing that does is how much of a package is
    /// used, by how few types, with how much of it already answered.
    ///
    /// It says what it cannot say, in the same breath. The count is read from
    /// syntax rather than from a compilation, the unknown column is unknown and
    /// not fine, and a percentage covered is a percentage of calls with a
    /// recorded counterpart, which is not a promise that applying them is
    /// mechanical.
    /// </summary>
    private static void Used(StringBuilder report, Assessment assessment)
    {
        var used = assessment.Uses.Where(d => d.Surface.Used).ToList();

        if (used.Count == 0) return;

        report.AppendLine("## What you actually use of what is going away");
        report.AppendLine();
        report.AppendLine(Wrap(
            "A package with no future is not a unit of work. What decides the size is how "
          + "much of it this codebase touches and how few types carry it: a hundred calls "
          + "through six types is an afternoon, and the same hundred spread over sixty is a "
          + "rewrite. Counted from the syntax, so a type is counted where it appears in a "
          + "type position in a file importing the package and nothing in the solution "
          + "declares it. Every type of every package is listed in the appendix."));
        report.AppendLine();

        // The whole picture first, one line per package, so that the reader
        // who stops here has the size of each and the share already answered.
        report.AppendLine("| Package | Calls | Types | Files | Types for four fifths | Replaced by | Covered |");
        report.AppendLine("|---|---:|---:|---:|---:|---|---:|");
        foreach (var dependency in used)
        {
            var surface = dependency.Surface;
            var best = dependency.Best is { Candidate.Length: > 0 } named ? named : null;
            report.AppendLine(
                $"| `{Cell(surface.Package)}` | {surface.Uses:N0} | {surface.Types.Count:N0} | "
              + $"{surface.Files:N0} | {surface.TypesForMostOfIt:N0} | "
              + $"{(best is null ? "nothing recorded" : $"`{Cell(best.Candidate)}`")} | "
              + $"{(best is null ? "" : $"{best.Percent}%")} |");
        }
        report.AppendLine();

        foreach (var dependency in used.Take(TopDependencies))
        {
            var surface = dependency.Surface;
            var best = dependency.Best;

            report.AppendLine($"### {surface.Package}");
            report.AppendLine();
            report.AppendLine(Wrap(
                $"{Assessor.Count(surface.Uses, "use")} of "
              + $"{Assessor.Count(surface.Types.Count, "type")}, across "
              + $"{Assessor.Count(surface.Files, "file")}. "
              + $"{Assessor.Count(surface.TypesForMostOfIt, "type")} and "
              + $"{Assessor.Count(surface.FilesForMostOfIt, "file")} carry four fifths of it."
              + (best is null
                    ? " Nobody has recorded what replaces this. That is a gap in the catalogue "
                    + "rather than a statement about the package."
                    : $" **{best.Candidate}** covers {best.Percent}% of those calls; "
                    + $"{Assessor.Count(best.UsesUnknown, "call")} are not in the catalogue at "
                    + "all: unknown, which is not the same as fine.")));
            report.AppendLine();

            var reading = best is null ? null : new Unlisted().Read(best.Unknown, best.Candidate);
            if (reading is { Applicable: true })
            {
                report.AppendLine(Wrap(
                    "Asked of the framework itself rather than of the catalogue, the unknown "
                  + $"column holds {Assessor.Count(reading.Of(Standing.InSuccessor).Count, "type")} "
                  + $"that exist inside {best!.Candidate} under the same name, a lead worth "
                  + $"checking; {Assessor.Count(reading.Of(Standing.Elsewhere).Count, "type")} that "
                  + "exist elsewhere in the framework under the same name, which nobody has "
                  + "recorded either way; and "
                  + $"{Assessor.Count(reading.Of(Standing.Gone).Count, "type")} the framework "
                  + $"does not have at all. That leaves {Assessor.Count(reading.Left, "type")} "
                  + "still to decide."));
                report.AppendLine();
            }

            // One table instead of four lists: the few heaviest types, and
            // every type that decides something whatever its weight, each with
            // what happens to it. A blocker with one call is still a blocker.
            // A package with nothing recorded gets no table: every row would
            // say so, and the table above already has.
            if (best is null) continue;

            var rows = Rows(surface, best, reading);
            report.AppendLine("| Type | Calls | What happens to it |");
            report.AppendLine("|---|---:|---|");
            foreach (var (type, fate) in rows)
                report.AppendLine($"| `{Cell(type.Name)}` | {type.Uses:N0} | {Cell(fate)} |");
            report.AppendLine();
        }

        if (used.Count > TopDependencies)
        {
            report.AppendLine(Wrap(
                $"{Assessor.Count(used.Count - TopDependencies, "further package")} in the table "
              + "above are not detailed here; their types are in the appendix."));
            report.AppendLine();
        }
    }

    /// <summary>
    /// The rows of a package's table: the heaviest types, then anything that
    /// is deleted, has no replacement or has a namesake in a sibling package,
    /// however light, ordered by weight.
    /// </summary>
    private static List<(ApiUse Type, string Fate)> Rows(
        UsageSurface surface, Coverage? best, UnlistedReading? reading)
    {
        var fates = new Dictionary<string, string>(StringComparer.Ordinal);

        if (best is not null)
        {
            foreach (var type in best.Covered) fates[type.Name] = $"replaced in {best.Candidate}";
            foreach (var type in best.Unknown) fates[type.Name] = "unknown to the catalogue";
            foreach (var type in best.Gone) fates[type.Name] = "**deleted**: the feature was withdrawn";
            foreach (var type in best.Unavailable) fates[type.Name] = "**no replacement recorded**";
        }

        foreach (var found in reading?.Of(Standing.InSuccessor) ?? [])
            fates[found.Use.Name] = $"unknown; same name inside {best!.Candidate}";
        foreach (var found in reading?.Of(Standing.Elsewhere) ?? [])
            fates[found.Use.Name] = $"unknown; same name at `{found.Where}`";

        var decisive = (best?.Gone ?? []).Concat(best?.Unavailable ?? [])
            .Concat((reading?.Of(Standing.Elsewhere) ?? []).Select(f => f.Use).Take(TopTypes));

        return surface.Types.Take(HeaviestTypes)
            .Concat(decisive)
            .DistinctBy(t => t.Name)
            .OrderByDescending(t => t.Uses)
            .Select(t => (t, fates.TryGetValue(t.Name, out var fate) ? fate
                           : best is null ? "nothing recorded for the package" : "unknown to the catalogue"))
            .ToList();
    }

    /// <summary>
    /// Every type of every package the codebase uses, for the engineer who
    /// does the work. Kept out of the body, where a list of a hundred names
    /// is read as a wall, and kept whole here, where it is looked up.
    /// </summary>
    private static void Appendix(StringBuilder report, Assessment assessment)
    {
        var used = assessment.Uses.Where(d => d.Surface.Used).ToList();
        if (used.Count == 0) return;

        report.AppendLine("## Appendix: every type used, by package");
        report.AppendLine();
        report.AppendLine(Wrap(
            "For the engineer doing the work. Each package with its types, heaviest first, "
          + "and the number of calls in brackets."));
        report.AppendLine();

        foreach (var dependency in used)
        {
            var surface = dependency.Surface;
            report.AppendLine(Lead($"{surface.Package}, {Assessor.Count(surface.Types.Count, "type")}",
                string.Join(", ", surface.Types.Select(t => $"`{t.Name}` ({t.Uses:N0})")) + "."));
            report.AppendLine();
        }
    }

    /// <summary>
    /// The questions the unknown column comes to, rather than the list of names
    /// it is made of.
    ///
    /// **The unit of decision is the feature.** On nopCommerce, forty-four types
    /// modern .NET does not have come to six questions, and twenty-one of the
    /// forty-four are third-party names the syntax attributed to the wrong
    /// package. Printing forty-four rows would hand somebody a list of things
    /// to look up; printing six hands them the decisions.
    ///
    /// The options are never a recommendation. Which one is right depends on
    /// how many people, how much time, and whether the old and the new have to
    /// run side by side, none of which is in the code.
    /// </summary>
    private static void Facing(StringBuilder report, Assessment assessment)
    {
        var features = Features.Load();

        // Every dependency, and not only the few heaviest the section above
        // details. A question is not ranked by how many calls reach it:
        // bundling is eight calls on nopCommerce and it is an architectural
        // decision, while a rename with four hundred calls behind it is not a
        // decision at all. Asked inside that section, it fell under the cap
        // that exists so nobody is handed ninety packages, and stopped being
        // asked anywhere.
        var questions = assessment.Uses
            .Where(dependency => dependency.Best is not null)
            .SelectMany(dependency => features.Ask(
                dependency.Surface.Package,
                [.. dependency.Best!.Unknown, .. dependency.Best.Unavailable]))
            .OrderByDescending(question => question.Uses)
            .ToList();

        if (questions.Count == 0) return;

        report.AppendLine("## What there is to decide about the frameworks themselves");
        report.AppendLine();
        report.AppendLine(Wrap(
            $"The names nothing accounts for come to {Assessor.Count(questions.Count, "question")}. "
          + "What follows is what each one was for, what replaced it, and what there is to choose "
          + "between. None of the options is a recommendation: which one is right depends on how "
          + "many people, how much time, and whether the old and the new have to run side by "
          + "side, and none of that is in the code."));
        report.AppendLine();

        foreach (var question in questions)
        {
            report.AppendLine(Wrap(
                $"**{question.Feature.Name}.** {Assessor.Count(question.Uses, "call")} through "
              + string.Join(", ", question.Through.Take(6).Select(t => $"`{t.Name}`"))
              + (question.Through.Count > 6 ? $", and {question.Through.Count - 6} more." : ".")));
            report.AppendLine();

            report.AppendLine(Wrap($"{question.Feature.Was} {question.Feature.Now}"));
            report.AppendLine();

            foreach (var option in question.Feature.Options)
                report.AppendLine(Wrap($"- {option}"));

            report.AppendLine();
        }
    }

    /// <summary>Packages given a section of their own. A report is read, not scrolled.</summary>
    private const int TopDependencies = 5;

    /// <summary>Types named per package.</summary>
    private const int TopTypes = 8;

    /// <summary>
    /// The heaviest types a package's table always shows, before the ones
    /// that decide something. The rest are in the appendix.
    /// </summary>
    private const int HeaviestTypes = 3;

    /// <summary>
    /// What nobody else can settle, before the order that assumes it.
    ///
    /// The conversions have always said these, in a terminal, one line per
    /// project. The document carried an ordered plan and none of the decisions
    /// the plan depends on: nothing about the target the whole solution has to
    /// move to before PackageReference works, about the connection string
    /// carrying a password, or about the keys the code reads that nothing
    /// declares.
    ///
    /// Said once each, with how much of the solution it covers, because a
    /// decision repeated thirty-one times is one decision and thirty pieces of
    /// noise.
    /// </summary>
    private static void Decide(StringBuilder report, Assessment assessment)
    {
        if (assessment.ToDecide.Count == 0) return;

        report.AppendLine("## What only you can decide");
        report.AppendLine();
        report.AppendLine(Wrap(
            "Everything below came out of preparing the mechanical work, and none of it can be "
          + "settled by reading the code harder. They are listed before the order because the "
          + "order assumes them."));
        report.AppendLine();

        foreach (var one in assessment.ToDecide)
        {
            var scope = one.Projects.Count switch
            {
                0 => "Across the solution",
                1 => one.Projects[0],
                _ => $"{Assessor.Count(one.Projects.Count, "project")}"
                   + (one.Varies ? ", one shown" : string.Empty),
            };

            report.AppendLine(Wrap($"**{scope}.** {one.What.Says}"));

            if (one.Projects.Count > 1)
            {
                report.AppendLine();
                report.AppendLine(Wrap(Listed(one.Projects)));
            }

            report.AppendLine();
        }
    }

    /// <summary>A few names and then a count, because a report is read.</summary>
    private static string Listed(IReadOnlyList<string> projects) =>
        projects.Count <= 6
            ? string.Join(", ", projects)
            : string.Join(", ", projects.Take(6)) + $", and {projects.Count - 6} more.";

    private static void Order(StringBuilder report, Assessment assessment)
    {
        if (assessment.Repairs.Count == 0) return;

        report.AppendLine("## In what order");
        report.AppendLine();
        report.AppendLine(Wrap(
            "This is a dependency order, not a schedule, and it deliberately carries no "
          + "days: nothing here measured how fast anyone works. What it does carry is a "
          + "property of the codebase. Work that blocks the rest comes first, because the "
          + "report itself is unreliable while it stands. Mechanical work comes before "
          + "decisions, because it is the half nobody has to argue about and doing it "
          + "shrinks the surface the arguments are about."));
        report.AppendLine();

        var position = 1;
        foreach (var step in assessment.Repairs)
        {
            report.AppendLine($"### {position}. {step.Title}");
            report.AppendLine();
            report.AppendLine($"*{Readable(step.Kind)}. {step.Size}.*");
            report.AppendLine();
            report.AppendLine(Wrap(step.Why));
            report.AppendLine();

            foreach (var evidence in step.Evidence)
                report.AppendLine($"- `{Cell(evidence)}`");

            report.AppendLine();
            position++;
        }
    }

    private static void Unsaid(StringBuilder report, Assessment assessment)
    {
        if (assessment.Limitations.Count == 0) return;

        report.AppendLine($"## {UnsaidSection}");
        report.AppendLine();
        report.AppendLine(Wrap(
            "Printed here rather than left for the reader to discover after acting on it. "
          + "Each of these applies to this codebase; the ones that did not apply were left "
          + "out."));
        report.AppendLine();

        foreach (var limitation in assessment.Limitations)
        {
            report.AppendLine(Lead(limitation.Subject, limitation.Detail));
            report.AppendLine();
        }
    }

    private static void Method(StringBuilder report, Assessment assessment)
    {
        report.AppendLine("---");
        report.AppendLine();
        report.AppendLine(Wrap(
            $"Measured in {assessment.ElapsedMs:N0} ms, without compiling anything, without "
          + "restoring a package and without contacting any service. That is what makes it "
          + "usable on a solution that does not build, which is the state an inherited "
          + "codebase is in on the first day. Generated by "
          + "[Legacy Lens](https://github.com/LiquidSnake0/legacy-lens)."));
        report.AppendLine();
    }

    private static string Readable(ProjectKind kind) => kind switch
    {
        ProjectKind.Library => "Class library",
        ProjectKind.Console => "Console application",
        ProjectKind.Web => "Web application",
        ProjectKind.Wpf => "WPF application",
        ProjectKind.WinForms => "Windows Forms application",
        ProjectKind.Test => "Test project",
        ProjectKind.Broken => "Unreadable project file",
        _ => "Unknown",
    };

    private static string Readable(FindingKind kind) => kind switch
    {
        FindingKind.LibraryCoupledToWeb => "Library coupled to the web stack",
        FindingKind.Untested => "Untested project",
        FindingKind.Oversized => "Oversized project",
        FindingKind.Orphan => "Referenced by nothing",
        FindingKind.Unreadable => "Unreadable project file",
        FindingKind.DependencyCycle => "Dependency cycle",
        _ => kind.ToString(),
    };

    /// <summary>
    /// How a step of the order of work is described. Public because the PDF
    /// puts the first steps on its cover, and has to name them in the same
    /// words as the page that lists them all.
    /// </summary>
    public static string Readable(RepairKind kind) => kind switch
    {
        RepairKind.Blocking => "Blocks everything below it",
        RepairKind.Prerequisite => "Needed before anyone can price the rest",
        RepairKind.Mechanical => "Mechanical, no decision required",
        RepairKind.Decision => "Needs a decision, not a tool",
        RepairKind.Continuous => "Never finished, only kept up",
        RepairKind.PossiblyFree => "May cost nothing",
        _ => kind.ToString(),
    };

    /// <summary>
    /// A pipe inside a cell ends the cell, and a project called
    /// <c>Foo|Bar</c> would silently shift every column after it.
    /// </summary>
    private static string Cell(string text) => text.Replace("|", "\\|");

    /// <summary>
    /// Hard-wraps prose at a width a person can read in a terminal or a diff.
    ///
    /// Markdown joins wrapped lines back into one paragraph when it renders, so
    /// this costs the rendered document nothing and makes the generated file
    /// reviewable in the same way the rest of this repository is.
    /// </summary>
    private static string Wrap(string text, int width = 80, int firstLineUsed = 0)
    {
        var wrapped = new StringBuilder();
        var length = firstLineUsed;
        var first = true;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (length > 0 && length + 1 + word.Length > width)
            {
                wrapped.AppendLine();
                length = 0;
            }
            else if (!first)
            {
                wrapped.Append(' ');
                length++;
            }

            wrapped.Append(word);
            length += word.Length;
            first = false;
        }

        return wrapped.ToString();
    }

    /// <summary>
    /// A bold lead-in and the paragraph that follows it, wrapped as one block.
    ///
    /// The lead-in already occupies part of the first line, so the wrapper has
    /// to be told about it or that line runs half as long again as every other
    /// one, which is visible in a diff and in a terminal.
    /// </summary>
    private static string Lead(string subject, string body)
    {
        var prefix = $"**{subject}.** ";
        return prefix + Wrap(body, firstLineUsed: prefix.Length);
    }
}
