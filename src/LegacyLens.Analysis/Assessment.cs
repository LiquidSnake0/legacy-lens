namespace LegacyLens.Analysis;

/// <summary>
/// Why a piece of work sits where it sits in the order.
///
/// The kind carries the argument. A reader who disagrees with the order can
/// disagree with a category rather than with a number they cannot check.
/// </summary>
public enum RepairKind
{
    /// <summary>Nothing else can be trusted until this is done.</summary>
    Blocking,

    /// <summary>Has to happen before anyone can put a price on the rest.</summary>
    Prerequisite,

    /// <summary>A tool does it. No decision, no design, no argument.</summary>
    Mechanical,

    /// <summary>Someone has to decide. A tool cannot, and should not.</summary>
    Decision,

    /// <summary>Never finished, only kept up. Paid for continuously.</summary>
    Continuous,

    /// <summary>Might cost nothing at all, once someone confirms it.</summary>
    PossiblyFree,
}

/// <summary>
/// One piece of work, with what it applies to and why it ranks where it does.
///
/// The evidence travels with the step for the same reason it travels with a
/// risk score: a claim a reader cannot check is a claim they have to take on
/// trust, and this whole tool exists to avoid asking for that.
/// </summary>
public record RepairStep(
    RepairKind Kind,
    string Title,
    string Why,
    /// <summary>How much there is of it, in units that were counted.</summary>
    string Size,
    IReadOnlyList<string> Evidence);

/// <summary>
/// Something this report could not see, stated rather than left for the reader
/// to discover after they have acted on it.
/// </summary>
public record Limitation(string Subject, string Detail);

/// <summary>
/// Everything measured about a solution, gathered in one place.
///
/// This class measures nothing itself. The four analyses underneath already
/// answer their own question well; what was missing is the one thing a buyer
/// asks that none of them answers alone, which is what to do first. That
/// question needs all four at once, so composing them is the work.
/// </summary>
/// <summary>
/// One dependency with a future in question: how much of it this codebase
/// actually uses, and what could take its place.
/// </summary>
public record Dependency(UsageSurface Surface, IReadOnlyList<Coverage> Candidates)
{
    /// <summary>The best answer anyone has recorded, or none.</summary>
    public Coverage? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

public record Assessment(
    string Name,
    string Root,
    SolutionMap Map,
    IReadOnlyList<Finding> Findings,
    RiskReport Risk,
    ModernisationSurvey Modernisation,
    IReadOnlyList<RepairStep> Repairs,
    IReadOnlyList<Limitation> Limitations,
    long ElapsedMs,

    /// <summary>
    /// What the codebase uses of the packages whose future is in question.
    ///
    /// The report used to say "78 packages nobody has classified" and stop,
    /// while the tool already knew that one of them accounts for 3,877 uses of
    /// 198 types across 365 files and that a successor covers 63 per cent of
    /// them. That number is the one that separates an afternoon from a
    /// rewrite, and it was reachable only by running a second command.
    ///
    /// Empty by default so that a caller wanting the cheap half of an
    /// assessment can still have it, and so this stays an addition rather than
    /// a new requirement on every construction site.
    /// </summary>
    IReadOnlyList<Dependency>? Dependencies = null)
{
    public IReadOnlyList<Dependency> Uses => Dependencies ?? [];

    public IEnumerable<Finding> Of(FindingKind kind) => Findings.Where(f => f.Kind == kind);

    /// <summary>
    /// Projects that ship code and are not tests. The denominator for anything
    /// said about coverage, since counting test projects as untested would be
    /// arithmetically true and meaningless.
    /// </summary>
    public IReadOnlyList<ProjectInfo> Production =>
        Map.Projects.Where(p => p.Kind is not (ProjectKind.Test or ProjectKind.Broken)).ToList();

    public int UntestedLines => Of(FindingKind.Untested)
        .Join(Map.Projects, f => f.Project, p => p.Name, (_, p) => p.Lines)
        .Sum();

    /// <summary>
    /// The frameworks the solution targets, most used first. Plural on purpose:
    /// a solution halfway through a migration targets several, and that fact is
    /// the single most useful thing to know before quoting the rest.
    /// </summary>
    public IReadOnlyList<(string Framework, int Projects)> Frameworks =>
        Map.Projects
            .Where(p => p.TargetFramework is not null)
            .GroupBy(p => p.TargetFramework!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(f => f.Item2)
            .ToList();

    public bool OnLegacyFramework => Map.Projects.Any(p => p.IsLegacyFramework);
}

public class Assessor
{
    /// <summary>
    /// How many ranked files the report lists. Twenty is enough to see the
    /// shape of the tail and few enough that someone reads all of them.
    /// </summary>
    public int TopRisks { get; init; } = 20;

    /// <summary>
    /// Passed to the risk ranking. Kept as a knob here so that a report over a
    /// codebase of small files does not come back empty.
    /// </summary>
    public int MinimumCodeLines { get; init; } = 100;

    public int HistoryMonths { get; init; } = 24;

    /// <summary>Projects under this are not worth a box on the diagram.</summary>
    public int DiagramMinimumLines { get; init; } = 500;

    /// <summary>
    /// Whether to read what the codebase uses of its dependencies.
    ///
    /// On, because it is the half of the answer that prices the work. A switch
    /// because it walks and parses every source file again, which on a large
    /// solution is most of the time an assessment takes, and a caller who only
    /// wants the project map should not pay for it.
    /// </summary>
    public bool ReadDependencies { get; init; } = true;

    public Assessment Assess(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        var started = System.Diagnostics.Stopwatch.StartNew();

        var map = new ProjectGraph().Build(rootPath);
        var findings = Findings.Detect(map);
        var risk = new SolutionAnalysis
        {
            MinimumCodeLines = MinimumCodeLines,
            HistoryMonths = HistoryMonths,
        }.Analyse(rootPath);
        var modernisation = new Modernisation().Survey(rootPath);

        // What is actually used of the packages with no future. Reading the
        // syntax again is most of the extra time an assessment now takes, and
        // it is what turns "78 packages to classify" into a size.
        var reading = new Surfaces();

        var dependencies = ReadDependencies
            ? reading.All(rootPath)
                .Select(surface => new Dependency(surface, reading.Candidates(surface)))
                .ToList()
            : [];

        risk = WithoutTestProjects(risk, map, rootPath);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)));

        return new Assessment(
            string.IsNullOrEmpty(name) ? rootPath : name,
            rootPath,
            map,
            findings,
            risk,
            modernisation,
            Order(map, findings, risk, modernisation),
            Limits(map, findings, risk, modernisation),
            started.ElapsedMilliseconds,
            dependencies);
    }

    /// <summary>
    /// Drops files that live inside a test project.
    ///
    /// The ranking already skips files whose own name looks like a test, which
    /// is all it can do on its own. It is not enough: Orchard's highest-ranked
    /// file is <c>Orchard.Specs/Bindings/WebAppHosting.cs</c>, support code for
    /// a test suite that no naming convention identifies. Telling a client that
    /// the most dangerous file in their product is a test fixture spends the
    /// credibility of every other row in the table.
    ///
    /// Knowing which folders belong to a test project takes the project map,
    /// which the ranking never sees and this class does. That is the whole
    /// reason for composing them rather than printing them side by side.
    /// </summary>
    private static RiskReport WithoutTestProjects(
        RiskReport risk, SolutionMap map, string rootPath)
    {
        var testFolders = map.Projects
            .Where(p => p.Kind == ProjectKind.Test)
            .Select(p => Path.GetDirectoryName(p.Path))
            .OfType<string>()
            .Select(folder => Path.GetRelativePath(rootPath, folder).Replace('\\', '/'))
            .Where(folder => folder is not ("." or ".."))
            .Select(folder => folder + "/")
            .ToList();

        if (testFolders.Count == 0) return risk;

        var kept = risk.Entries
            .Where(e => !testFolders.Any(folder =>
                e.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return risk with { Entries = kept };
    }

    /// <summary>
    /// The order of work, derived from what was measured.
    ///
    /// The sequence is not a schedule and deliberately carries no days: nothing
    /// here measured how fast anyone works. What it does carry is a dependency
    /// order, which is a property of the codebase rather than of the team.
    /// Blocking work comes first because the rest of the report is unreliable
    /// while it stands; mechanical work comes before decisions because it is
    /// the half nobody needs to argue about, and doing it shrinks the surface
    /// the arguments are about.
    ///
    /// A step with nothing in it is left out rather than reported as done. A
    /// report padded with sections that say "none" reads as thorough and is
    /// noise.
    /// </summary>
    private List<RepairStep> Order(
        SolutionMap map,
        IReadOnlyList<Finding> findings,
        RiskReport risk,
        ModernisationSurvey survey)
    {
        var steps = new List<RepairStep>();

        var unreadable = findings.Where(f => f.Kind == FindingKind.Unreadable).ToList();
        if (unreadable.Count > 0)
        {
            steps.Add(new RepairStep(
                RepairKind.Blocking,
                "Repair the project files that could not be parsed",
                "Every number in this report is missing for these projects, so both the "
              + "shape of the solution and the size of the work are understated by an "
              + "unknown amount.",
                Count(unreadable.Count, "project file"),
                unreadable.Select(f => f.Project).ToList()));
        }

        var cycles = findings.Where(f => f.Kind == FindingKind.DependencyCycle).ToList();
        if (cycles.Count > 0)
        {
            steps.Add(new RepairStep(
                RepairKind.Blocking,
                "Break the dependency cycles",
                "MSBuild refuses to build a cycle. If this solution does build, then what "
              + "is built is not what the project files describe, and neither is the rest "
              + "of this section.",
                Count(cycles.Count, "cycle"),
                cycles.Select(f => f.Summary).ToList()));
        }

        var unknown = survey.Packages
            .Where(p => p.Portability == Portability.Unknown)
            .OrderByDescending(p => p.Projects)
            .ToList();

        if (unknown.Count > 0)
        {
            steps.Add(new RepairStep(
                RepairKind.Prerequisite,
                "Classify the packages nobody has checked yet",
                "These are neither known to work on modern .NET nor known to be dead ends. "
              + "Each one is either a non-event or a rewrite, and which of the two decides "
              + "the size of everything below. Quoting before they are classified means "
              + "quoting on the assumption that none of them is a problem.",
                Count(unknown.Count, "package"),
                unknown.Take(10)
                       .Select(p => $"{p.Id} ({Count(p.Projects, "project")})")
                       .ToList()));
        }

        var convertible = survey.Projects.Where(p => p.ConvertibleAsIs).ToList();
        if (convertible.Count > 0)
        {
            steps.Add(new RepairStep(
                RepairKind.Mechanical,
                "Convert the project files that nothing holds back",
                "Nothing these projects reference stands in the way; only the file format "
              + "is old. The conversion is a tool run and a review, and it is worth doing "
              + "first because it shrinks what everything after this has to work through.",
                Count(convertible.Count, "project"),
                convertible.Take(10).Select(p => p.Name).ToList()));
        }

        var divergent = survey.Packages.Where(p => p.Divergent).ToList();
        if (divergent.Count > 0 || survey.BindingRedirects > 0)
        {
            // Naming the step after the divergent versions when there are none
            // leaves a section whose title contradicts its own evidence list,
            // which is the fastest way to lose a reader who checks.
            var title = divergent.Count > 0
                ? "Settle the packages pinned to more than one version"
                : "Retire the hand-written binding redirects";

            var why = divergent.Count > 0
                ? "Every one of these is a version a conversion has to pick a winner for, "
                + "and doing it now is doing it without the deadline attached."
                : "A redirect only ever gets written because two assemblies disagreed about a "
                + "version. The project files no longer show the disagreement, which means "
                + "the redirects are load-bearing: removing one at conversion time is how a "
                + "migration acquires a runtime failure that nothing in the build predicted.";

            steps.Add(new RepairStep(
                RepairKind.Mechanical,
                title,
                why,
                divergent.Count > 0
                    ? Count(divergent.Count, "package")
                    : Count(survey.BindingRedirects, "redirect"),
                divergent.Take(10)
                         .Select(p => $"{p.Id}: {string.Join(", ", p.Versions)}")
                         .ToList()));
        }

        var blocked = survey.Projects.Where(p => p.Blocked).ToList();
        if (blocked.Count > 0)
        {
            var deadEnds = survey.DeadEnds;

            steps.Add(new RepairStep(
                RepairKind.Decision,
                "Decide what happens to the code built on System.Web",
                $"{Count(deadEnds.Count, "package")} here exist only inside the .NET "
              + "Framework. No version of them runs on modern .NET, so no conversion tool "
              + "will help: the code that calls them is rewritten, replaced, or left where "
              + "it is. That is a decision about the product, not about the code, and it is "
              + "the one that decides whether the rest is a migration or a rewrite.",
                Count(blocked.Count, "project"),
                deadEnds.Select(p => $"{p.Id} ({Count(p.Projects, "project")})").ToList()));
        }

        var coupled = findings.Where(f => f.Kind == FindingKind.LibraryCoupledToWeb).ToList();
        if (coupled.Count > 0)
        {
            steps.Add(new RepairStep(
                RepairKind.Decision,
                "Separate the libraries that depend on the web stack",
                "A library holding a web dependency cannot be tested without a web context, "
              + "and cannot be reused from a service, a desktop client or a batch job. It is "
              + "listed after the decision above because what the web layer becomes decides "
              + "how much of this is worth untangling.",
                Count(coupled.Count, "library"),
                coupled.Select(f => $"{f.Project}: {f.Summary}").ToList()));
        }

        // A file scoring zero is the least risky of the set rather than a
        // hazard, and listing it under work to do would spend the reader's
        // attention on the one file the ranking is least worried about.
        var exposed = risk.Entries.Where(e => !e.Tested && e.Score > 0).Take(TopRisks).ToList();
        if (exposed.Count > 0)
        {
            var untestedProjects = findings.Count(f => f.Kind == FindingKind.Untested);

            steps.Add(new RepairStep(
                RepairKind.Continuous,
                "Put tests around the files most likely to break",
                $"No test project references {Count(untestedProjects, "project")} in this "
              + "solution. The files below are the ones where that matters "
              + "most: they rank highest on complexity and change frequency together, which "
              + "is the combination that produces incidents. This is the one item here that "
              + "is never finished, which is why it starts early and runs alongside the rest.",
                Count(exposed.Count, "file"),
                exposed.Take(10)
                       .Select(e => $"{e.Path} (score {e.Score:0.00})")
                       .ToList()));
        }

        var orphans = findings.Where(f => f.Kind == FindingKind.Orphan).ToList();
        if (orphans.Count > 0)
        {
            steps.Add(new RepairStep(
                RepairKind.PossiblyFree,
                "Confirm whether the unreferenced projects are dead",
                "Nothing in the solution references these. They are either dead code, which "
              + "makes them the cheapest work in this report, or they are loaded at runtime "
              + "by reflection or a plugin mechanism, which project files cannot show. "
              + "Someone who knows the product answers this in minutes.",
                Count(orphans.Count, "project"),
                orphans.Select(f => f.Project).ToList()));
        }

        return steps;
    }

    /// <summary>
    /// What this report could not see.
    ///
    /// Stated in the document rather than in the source, because the reader who
    /// most needs it is the one least likely to open the source. Every entry is
    /// conditional on something measured: a limitation that does not apply is
    /// left out, so that the ones printed are the ones that bite here.
    /// </summary>
    private static List<Limitation> Limits(
        SolutionMap map,
        IReadOnlyList<Finding> findings,
        RiskReport risk,
        ModernisationSurvey survey)
    {
        var limits = new List<Limitation>
        {
            new("Nothing was compiled",
                "The whole report is read from project files, source text and git. That is "
              + "what lets it run on a solution that does not build, which is the state most "
              + "inherited code is in. The cost is that anything resolved at runtime, by "
              + "reflection, dependency injection or a plugin loader, is invisible here."),

            new("Test coverage is a naming convention",
                "A file is counted as tested when a test file appears to be named after it. "
              + "That over-reports coverage and never under-reports it: a file called "
              + "untested here is genuinely untested, while one called tested may only share "
              + "a name with a test that exercises none of it."),
        };

        if (risk.HistoryStatus != HistoryStatus.Available)
        {
            limits.Add(new("Change history was not available",
                (risk.HistoryNote ?? "Git history could not be read.")
              + " Files are therefore ranked on structure alone, and a file that is "
              + "complicated but never touched ranks as high as one that changes weekly."));
        }

        var unknown = survey.Packages.Count(p => p.Portability == Portability.Unknown);
        if (unknown > 0)
        {
            limits.Add(new("Most packages are unclassified",
                $"{unknown} of {survey.Packages.Count} distinct packages are neither known "
              + "to run on modern .NET nor known to be dead ends. They are reported as "
              + "unknown rather than assumed to be fine, because a survey that quietly "
              + "counts an unknown package as portable is one that discovers the problem "
              + "after the price has been agreed."));
        }

        if (risk.GeneratedFilesExcluded > 0)
        {
            limits.Add(new("Generated code was excluded",
                $"{risk.GeneratedFilesExcluded} generated files were left out of the "
              + "ranking. They top every chart on size and complexity and tell a reader "
              + "nothing they can act on, but they are still code that ships, and a "
              + "regeneration path that no longer exists is its own problem."));
        }

        var unreadable = findings.Count(f => f.Kind == FindingKind.Unreadable);
        if (unreadable > 0)
        {
            limits.Add(new("Some project files could not be read",
                $"{unreadable} project files are not valid XML. Their lines, references and "
              + "packages are absent from every count in this report, so the totals are "
              + "understated by an amount nobody can state."));
        }

        if (map.Projects.Count == 0)
        {
            limits.Add(new("No projects were found",
                "No .csproj file was found under this directory. Either the path is wrong, "
              + "or this is not a .NET solution, and the sections above are empty for that "
              + "reason rather than because the codebase is clean."));
        }

        return limits;
    }

    /// <summary>
    /// A count and its unit, pluralised. Written out because "1 projects" or
    /// "0 dependencys" in a document that goes to a client undoes the
    /// credibility of every number beside it.
    /// </summary>
    internal static string Count(int value, string unit) =>
        $"{value:N0} {(value == 1 ? unit : Plural(unit))}";

    private static string Plural(string unit) =>
        unit.EndsWith('y') && unit.Length > 1 && !"aeiou".Contains(unit[^2])
            ? $"{unit[..^1]}ies"
            : $"{unit}s";
}
