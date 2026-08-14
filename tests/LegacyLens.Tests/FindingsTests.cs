using LegacyLens.Analysis;

namespace LegacyLens.Tests;

public class FindingsTests
{
    private static ProjectInfo Project(
        string name,
        ProjectKind kind = ProjectKind.Library,
        int lines = 1_000,
        string[]? references = null,
        string[]? assemblies = null) =>
        new(name, $"/src/{name}/{name}.csproj", kind, "v4.5.1",
            references ?? [], assemblies ?? [], 10, lines);

    /// <summary>
    /// Builds a map without going through ProjectGraph. These tests are about
    /// what Findings concludes from a map, not about how the map was produced;
    /// cycle detection has its own tests in ProjectGraphTests.
    /// </summary>
    private static SolutionMap Map(
        ProjectInfo[] projects, IReadOnlyList<IReadOnlyList<string>>? cycles = null)
    {
        var known = projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = projects
            .SelectMany(p => p.References.Where(known.Contains).Select(r => new ProjectEdge(p.Name, r)))
            .ToList();
        return new SolutionMap(projects, edges, cycles ?? []);
    }

    private static SolutionMap Map(params ProjectInfo[] projects) => Map(projects, null);

    private static IReadOnlyList<Finding> Of(SolutionMap map, FindingKind kind) =>
        Findings.Detect(map).Where(f => f.Kind == kind).ToList();

    [Fact]
    public void A_library_pulling_in_web_assemblies_is_reported()
    {
        var map = Map(Project("Core", assemblies: ["System.Web.Mvc", "System.Xml"]));

        var finding = Assert.Single(Of(map, FindingKind.LibraryCoupledToWeb));
        Assert.Equal("Core", finding.Project);
        Assert.Contains("System.Web.Mvc", finding.Summary);
        // The unrelated reference must not be dragged in.
        Assert.DoesNotContain("System.Xml", finding.Summary);
    }

    [Fact]
    public void A_web_project_pulling_in_web_assemblies_is_not_a_finding()
    {
        // That is what a web project is for. Reporting it would bury the real
        // cases in noise.
        var map = Map(Project("Site", ProjectKind.Web, assemblies: ["System.Web.Mvc"]));
        Assert.Empty(Of(map, FindingKind.LibraryCoupledToWeb));
    }

    [Fact]
    public void A_project_no_test_references_is_reported_as_untested()
    {
        var map = Map(
            Project("Core"),
            Project("Suite", ProjectKind.Test, references: ["Other"]),
            Project("Other"));

        var untested = Of(map, FindingKind.Untested).Select(f => f.Project);
        Assert.Contains("Core", untested);
        Assert.DoesNotContain("Other", untested);
    }

    [Fact]
    public void A_test_project_is_never_reported_as_untested()
    {
        var map = Map(Project("Suite", ProjectKind.Test));
        Assert.Empty(Of(map, FindingKind.Untested));
    }

    [Fact]
    public void Oversized_uses_the_stated_threshold()
    {
        var map = Map(
            Project("Huge", lines: Findings.OversizedLines + 1),
            Project("Fine", lines: Findings.OversizedLines - 1));

        var oversized = Assert.Single(Of(map, FindingKind.Oversized));
        Assert.Equal("Huge", oversized.Project);
    }

    [Fact]
    public void A_library_nothing_references_is_reported_as_orphan()
    {
        var map = Map(
            Project("Used"),
            Project("Forgotten"),
            Project("App", ProjectKind.Console, references: ["Used"]));

        var orphan = Assert.Single(Of(map, FindingKind.Orphan));
        Assert.Equal("Forgotten", orphan.Project);
    }

    [Fact]
    public void An_entry_point_nothing_references_is_not_an_orphan()
    {
        // Nothing references an executable, by definition. Flagging it would
        // teach the reader to ignore the category.
        var map = Map(Project("App", ProjectKind.Console));
        Assert.Empty(Of(map, FindingKind.Orphan));
    }

    [Fact]
    public void A_cycle_produces_a_finding_naming_both_projects()
    {
        var map = Map(
            [Project("A", references: ["B"]), Project("B", references: ["A"])],
            cycles: [["A", "B", "A"]]);

        var finding = Assert.Single(Of(map, FindingKind.DependencyCycle));
        Assert.Contains("A", finding.Summary);
        Assert.Contains("B", finding.Summary);
    }

    [Fact]
    public void A_broken_project_yields_one_finding_and_no_others()
    {
        // Everything else about it is unknown, so piling on "untested" and
        // "orphan" would be guessing.
        var map = Map(Project("Corrupt", ProjectKind.Broken, lines: 0));

        var finding = Assert.Single(Findings.Detect(map));
        Assert.Equal(FindingKind.Unreadable, finding.Kind);
    }

    [Fact]
    public void Every_finding_explains_why_it_matters()
    {
        // A finding without a reason is a number on a dashboard. The detail is
        // what lets a reader decide whether to act.
        var map = Map(
            [Project("Core", assemblies: ["System.Web.Mvc"], lines: Findings.OversizedLines + 1),
             Project("A", references: ["B"]),
             Project("B", references: ["A"])],
            cycles: [["A", "B", "A"]]);

        foreach (var finding in Findings.Detect(map))
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Summary));
            Assert.True(finding.Detail.Length > 40, $"{finding.Kind} has no explanation.");
        }
    }
}
