namespace LegacyLens.Analysis;

/// <summary>
/// Something worth telling a reader about the solution.
///
/// Findings are separate from <see cref="ProjectKind"/> on purpose. What a
/// project *is* and what is *wrong with it* are different questions, and
/// conflating them hides the interesting half: a library that references
/// System.Web.Mvc is still a library, and that is precisely the problem.
/// </summary>
public record Finding(
    FindingKind Kind,
    string Project,
    string Summary,
    string Detail);

public enum FindingKind
{
    /// <summary>
    /// A class library referencing web assemblies. It cannot be tested outside
    /// a web context and cannot be reused in a service or a desktop client,
    /// which is usually discovered the day someone tries.
    /// </summary>
    LibraryCoupledToWeb,

    /// <summary>No test project references it. Changes here are unverified.</summary>
    Untested,

    /// <summary>
    /// Large enough that no one holds it in their head. The threshold is
    /// arbitrary and stated rather than hidden.
    /// </summary>
    Oversized,

    /// <summary>Nothing references it and it is not an entry point. Possibly dead.</summary>
    Orphan,

    /// <summary>The project file could not be parsed.</summary>
    Unreadable,

    /// <summary>Projects depending on each other. MSBuild refuses to build these.</summary>
    DependencyCycle,
}

public static class Findings
{
    /// <summary>
    /// Beyond this, a project is too large for one person to hold in their
    /// head. Stated as a constant rather than buried in a comparison, so that
    /// disagreeing with it is a one-line change.
    /// </summary>
    public const int OversizedLines = 20_000;

    private static readonly string[] WebAssemblies =
        ["System.Web.Mvc", "System.Web.Http", "System.Web.WebPages", "Microsoft.AspNetCore"];

    public static IReadOnlyList<Finding> Detect(SolutionMap map)
    {
        var findings = new List<Finding>();

        var referenced = map.Edges
            .Select(e => e.To)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var testedProjects = map.Projects
            .Where(p => p.Kind == ProjectKind.Test)
            .SelectMany(p => p.References)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var project in map.Projects)
        {
            if (project.Kind == ProjectKind.Broken)
            {
                findings.Add(new Finding(FindingKind.Unreadable, project.Name,
                    "Project file could not be parsed",
                    $"{project.Path} is not valid XML. Everything below is missing for it."));
                continue;
            }

            var web = project.AssemblyReferences
                .Where(r => WebAssemblies.Any(w => r.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .ToList();

            if (project.Kind == ProjectKind.Library && web.Count > 0)
            {
                findings.Add(new Finding(FindingKind.LibraryCoupledToWeb, project.Name,
                    $"Library depends on {string.Join(", ", web)}",
                    "It cannot be unit tested without a web context, and cannot be reused "
                  + "from a service, a desktop client or a batch job. Usually found out the "
                  + "day someone tries."));
            }

            if (project.Kind is not (ProjectKind.Test or ProjectKind.Broken)
                && !testedProjects.Contains(project.Name)
                && project.Lines > 0)
            {
                findings.Add(new Finding(FindingKind.Untested, project.Name,
                    $"No test project references it ({project.Lines:N0} lines)",
                    "Changes here are verified by running the application, or not at all."));
            }

            if (project.Lines > OversizedLines)
            {
                findings.Add(new Finding(FindingKind.Oversized, project.Name,
                    $"{project.Lines:N0} lines in one project",
                    $"Past roughly {OversizedLines:N0} lines nobody holds the whole thing in "
                  + "their head, and every change is made without seeing the consequences."));
            }

            if (!referenced.Contains(project.Name)
                && project.Kind is ProjectKind.Library
                && project.Lines > 0)
            {
                findings.Add(new Finding(FindingKind.Orphan, project.Name,
                    "Nothing in the solution references it",
                    "Either dead code, or loaded at runtime by reflection or a plugin "
                  + "mechanism, which the project files cannot show."));
            }
        }

        foreach (var cycle in map.Cycles)
        {
            findings.Add(new Finding(FindingKind.DependencyCycle, cycle[0],
                $"Dependency cycle: {string.Join(" -> ", cycle)}",
                "MSBuild refuses to build a cycle. If this solution compiles, the cycle "
              + "is in the files but not in what is actually built."));
        }

        return findings;
    }
}
