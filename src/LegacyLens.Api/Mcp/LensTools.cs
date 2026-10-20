using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using LegacyLens.Analysis;
using ModelContextProtocol.Server;

namespace LegacyLens.Api.Mcp;

/// <summary>
/// The codebase one server answers about, and the assessment of it.
///
/// One root per process, fixed on the command line. An agent asking about a
/// path outside it is refused rather than served, because a tool handed to an
/// agent reads whatever the agent names, and the agent is steered by text it
/// read in somebody else's repository.
///
/// The assessment is made once, on the first question, and kept: it takes
/// seconds on a large solution and the answers have to agree with each other.
/// </summary>
public sealed class Codebase(string root)
{
    private readonly Lazy<(Assessment Assessment, string Report)> _assessment = new(() =>
    {
        var assessment = new Assessor().Assess(root);
        return (assessment, new ReportWriter().Write(assessment));
    });

    public string Root { get; } = Path.GetFullPath(root);

    public Assessment Assessment => _assessment.Value.Assessment;

    public string Report => _assessment.Value.Report;

    /// <summary>
    /// The full path for a path relative to the root, or null when it would
    /// leave it, by its name or through a symbolic link.
    /// </summary>
    public string? Inside(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        return Contains(full) && Contains(Resolved(full)) ? full : null;
    }

    private bool Contains(string full)
    {
        var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Resolved(string full)
    {
        try
        {
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full;
        }
    }
}

/// <summary>
/// What Legacy Lens measures, offered to a coding agent.
///
/// The same measurements as the report, in the same words: each answer is a
/// section of the document the command prints, so what an agent is told and
/// what a client is handed cannot disagree. Nothing here calls a model, and
/// every claim names the file it rests on, which the two reading tools let the
/// agent open and check.
/// </summary>
[McpServerToolType]
public sealed class LensTools(Codebase codebase)
{
    private const int MaxLines = 200;
    private const int MaxMatches = 60;

    [McpServerTool(Name = "assessment_summary", ReadOnly = true, Idempotent = true)]
    [Description("The verdict grid, the one-paragraph summary and what is already in the codebase's favour. Start here. Every figure was measured from project files, source and git history; nothing is estimated.")]
    public string Summary() =>
        Sections("At a glance", "In short", "What is already on your side");

    [McpServerTool(Name = "risky_files", ReadOnly = true, Idempotent = true)]
    [Description("The files most likely to break, ranked on complexity, change frequency and whether a test covers them, each with the reason it ranks where it does.")]
    public string Risks() => Sections("What will hurt");

    [McpServerTool(Name = "migration_blockers", ReadOnly = true, Idempotent = true)]
    [Description("What a move from .NET Framework to modern .NET runs into: project formats, packages that exist only inside the .NET Framework and the projects they block, version drift, packages nobody has classified.")]
    public string Blockers() => Sections("What a move to modern .NET runs into");

    [McpServerTool(Name = "package_usage", ReadOnly = true, Idempotent = true)]
    [Description("How much of one package the code actually uses: calls, types, files, the replacement on modern .NET and the share it covers, and per type what happens to it (replaced, deleted, no replacement, unknown).")]
    public string PackageUsage([Description("NuGet package id, for example Microsoft.AspNet.Mvc")] string package)
    {
        var dependency = codebase.Assessment.Uses
            .FirstOrDefault(d => d.Surface.Used && string.Equals(d.Surface.Package, package, StringComparison.OrdinalIgnoreCase));

        if (dependency is null)
        {
            var known = codebase.Assessment.Uses.Where(d => d.Surface.Used).Select(d => d.Surface.Package);
            return $"Nothing in this codebase uses {package}, or it is not a package whose future is in question. "
                 + $"Packages with usage measured: {string.Join(", ", known)}.";
        }

        var surface = dependency.Surface;
        var best = dependency.Best is { Candidate.Length: > 0 } named ? named : null;
        var text = new StringBuilder();
        text.AppendLine($"{surface.Package}: {surface.Uses:N0} calls through {surface.Types.Count:N0} types in {surface.Files:N0} files; "
                      + $"{surface.TypesForMostOfIt:N0} types carry four fifths of it.");
        text.AppendLine(best is null
            ? "No replacement is recorded for this package."
            : $"{best.Candidate} covers {best.Percent}% of those calls.");
        text.AppendLine();
        text.AppendLine("| Type | Calls | Files | What happens to it |");
        text.AppendLine("|---|---:|---:|---|");

        var fates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (best is not null)
        {
            foreach (var type in best.Covered) fates[type.Name] = $"replaced in {best.Candidate}";
            foreach (var type in best.Unknown) fates[type.Name] = "unknown to the catalogue";
            foreach (var type in best.Gone) fates[type.Name] = "deleted: the feature was withdrawn";
            foreach (var type in best.Unavailable) fates[type.Name] = "no replacement recorded";
        }

        foreach (var type in surface.Types)
            text.AppendLine($"| {type.Name} | {type.Uses:N0} | {type.Files:N0} | {fates.GetValueOrDefault(type.Name, "nothing recorded")} |");

        return text.ToString();
    }

    [McpServerTool(Name = "order_of_work", ReadOnly = true, Idempotent = true)]
    [Description("The order the migration work should be done in, from what blocks everything to what may cost nothing, with the size of each step and the names it covers. A dependency order, not a schedule: it carries no durations.")]
    public string Order() => Sections("In what order", "Appendix: what each step covers");

    [McpServerTool(Name = "limitations", ReadOnly = true, Idempotent = true)]
    [Description("What this analysis could not see on this codebase. Read before acting on any other answer.")]
    public string Limitations() => Sections("What this report does not say");

    [McpServerTool(Name = "read_lines", ReadOnly = true, Idempotent = true)]
    [Description("Lines of a file in the codebase, numbered, to check a citation. At most 200 lines per call.")]
    public string ReadLines(
        [Description("Path relative to the codebase root, as the other tools print it")] string path,
        [Description("First line, 1-based")] int start = 1,
        [Description("Last line, inclusive")] int end = 80)
    {
        var full = codebase.Inside(path);
        if (full is null) return $"Refused: {path} is outside the codebase.";
        if (!File.Exists(full)) return $"No such file: {path}";

        start = Math.Max(1, start);
        end = Math.Min(Math.Max(start, end), start + MaxLines - 1);

        var text = new StringBuilder();
        var number = 0;
        foreach (var line in File.ReadLines(full))
        {
            number++;
            if (number < start) continue;
            if (number > end) break;
            text.AppendLine($"{number,6}  {line}");
        }

        return number < start ? $"{path} has {number} lines." : text.ToString();
    }

    [McpServerTool(Name = "search_code", ReadOnly = true, Idempotent = true)]
    [Description("Searches the source files of the codebase for a regular expression and returns file:line matches, at most 60. Generated folders (bin, obj, packages, node_modules, .git) are skipped.")]
    public string Search(
        [Description(".NET regular expression")] string pattern,
        [Description("File extension to search, for example .cs, .cshtml, .config")] string extension = ".cs")
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return $"Not a valid regular expression: {ex.Message}";
        }

        var text = new StringBuilder();
        var count = 0;
        foreach (var file in SourceTree.Under(codebase.Root, p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Path.GetRelativePath(codebase.Root, file).Replace('\\', '/');
            if (codebase.Inside(relative) is null) continue;
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                bool hit;
                try { hit = regex.IsMatch(line); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!hit) continue;

                text.AppendLine($"{relative}:{number}: {line.Trim()}");
                if (++count == MaxMatches)
                    return text.AppendLine($"Stopped at {MaxMatches} matches; narrow the pattern.").ToString();
            }
        }

        return count == 0 ? "No match." : text.ToString();
    }

    /// <summary>
    /// The named sections of the report, each from its heading to the next
    /// heading of the same level, in the order asked for. A section the report
    /// left out because it had nothing in it is said to be absent.
    /// </summary>
    private string Sections(params string[] names)
    {
        var report = codebase.Report;
        var text = new StringBuilder();

        foreach (var name in names)
        {
            var heading = $"## {name}";
            var at = report.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
            if (at < 0)
            {
                text.AppendLine($"{heading}\n\nNot in this report: nothing was found for it on this codebase.\n");
                continue;
            }

            var from = at + 1;
            var next = report.IndexOf("\n## ", from + heading.Length, StringComparison.Ordinal);
            var rule = report.IndexOf("\n---\n", from, StringComparison.Ordinal);
            var stop = new[] { next, rule }.Where(i => i > 0).DefaultIfEmpty(report.Length).Min();
            text.AppendLine(report[from..stop].TrimEnd());
            text.AppendLine();
        }

        return text.ToString();
    }
}
