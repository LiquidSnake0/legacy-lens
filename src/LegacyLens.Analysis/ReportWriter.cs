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
        Summary(report, assessment);
        Shape(report, assessment);
        Hurt(report, assessment);
        Migration(report, assessment);
        Order(report, assessment);
        Unsaid(report, assessment);
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
        report.AppendLine("## What will hurt");
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

        report.AppendLine("## What a move to modern .NET runs into");
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

        report.AppendLine("## What this report does not say");
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

    private static string Readable(RepairKind kind) => kind switch
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
