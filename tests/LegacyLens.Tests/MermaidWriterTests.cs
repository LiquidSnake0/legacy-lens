using LegacyLens.Analysis;

namespace LegacyLens.Tests;

public class MermaidWriterTests
{
    private static ProjectInfo Project(
        string name, string path, ProjectKind kind = ProjectKind.Library, int lines = 5_000) =>
        new(name, path, kind, "v4.5.1", [], [], 10, lines);

    private static SolutionMap Map(ProjectInfo[] projects, ProjectEdge[]? edges = null) =>
        new(projects, edges ?? [], []);

    [Fact]
    public void A_folder_may_share_its_name_with_a_project_inside_it()
    {
        // nopCommerce does exactly this: Nop.Admin sits in a folder called
        // Nop.Web, beside a project also called Nop.Web. Giving both the same
        // Mermaid id produced a diagram that parsed but showed the wrong shape,
        // which is worse than one that fails outright.
        var map = Map([
            Project("Nop.Web", "/src/Presentation/Nop.Web/Nop.Web.csproj", ProjectKind.Web),
            Project("Nop.Admin", "/src/Presentation/Nop.Web/Administration/Nop.Admin.csproj", ProjectKind.Web),
        ]);

        var diagram = new MermaidWriter().Write(map);

        var subgraphIds = diagram.Split('\n')
            .Where(l => l.TrimStart().StartsWith("subgraph "))
            .Select(l => l.Trim().Split(' ')[1].Split('[')[0])
            .ToList();
        var nodeIds = diagram.Split('\n')
            .Where(l => l.StartsWith("    n") && l.Contains("[\""))
            .Select(l => l.Trim().Split('[')[0])
            .ToList();

        Assert.Empty(subgraphIds.Intersect(nodeIds));
    }

    [Fact]
    public void Dots_and_dashes_never_reach_the_identifier()
    {
        var map = Map([Project("My.Project-Name", "/src/Lib/My.Project-Name/My.Project-Name.csproj")]);
        var diagram = new MermaidWriter().Write(map);

        var declaration = diagram.Split('\n').Single(l => l.Contains("[\"My.Project-Name<br/>"));
        var identifier = declaration.Trim().Split('[')[0];

        Assert.DoesNotContain('.', identifier);
        Assert.DoesNotContain('-', identifier);
        // The readable name still appears, as the label.
        Assert.Contains("My.Project-Name", declaration);
    }

    [Fact]
    public void Two_different_names_never_collapse_onto_one_identifier()
    {
        // "A.B" and "A-B" both sanitise to A_B if the mapping is careless.
        var map = Map([
            Project("A.B", "/src/A.B/A.B.csproj"),
            Project("A-B", "/src/A-B/A-B.csproj"),
        ]);

        var diagram = new MermaidWriter().Write(map);
        var identifiers = diagram.Split('\n')
            .Where(l => l.StartsWith("    n") && l.Contains("[\""))
            .Select(l => l.Trim().Split('[')[0])
            .ToList();

        // Both sanitise to nA_B, so the table must disambiguate. A shared id
        // renders a diagram that parses and shows the wrong shape, which is
        // worse than one that fails, because nobody notices.
        Assert.Equal(2, identifiers.Distinct().Count());
    }

    [Fact]
    public void Small_projects_are_left_out_and_the_omission_is_stated()
    {
        var map = Map([
            Project("Big", "/src/Lib/Big/Big.csproj", lines: 10_000),
            Project("Tiny", "/src/Lib/Tiny/Tiny.csproj", lines: 40),
        ]);

        var diagram = new MermaidWriter { MinimumLines = 1_000 }.Write(map);

        Assert.Contains("Big", diagram);
        Assert.DoesNotContain("Tiny", diagram);
        // Silent truncation reads as "this is everything". It never is.
        Assert.Contains("1 project(s) omitted", diagram);
    }

    [Fact]
    public void Test_projects_are_excluded_unless_asked_for()
    {
        var map = Map([
            Project("Core", "/src/Lib/Core/Core.csproj"),
            Project("Core.Tests", "/src/Tests/Core.Tests/Core.Tests.csproj", ProjectKind.Test),
        ]);

        Assert.DoesNotContain("Core.Tests", new MermaidWriter().Write(map));
        Assert.Contains("Core.Tests", new MermaidWriter { IncludeTests = true }.Write(map));
    }

    [Fact]
    public void An_edge_to_a_hidden_project_is_dropped_rather_than_dangling()
    {
        // A Mermaid arrow pointing at an undeclared node renders as an empty
        // box, which reads as a project with no name.
        var map = Map(
            [Project("Big", "/src/Lib/Big/Big.csproj", lines: 10_000),
             Project("Tiny", "/src/Lib/Tiny/Tiny.csproj", lines: 40)],
            [new ProjectEdge("Big", "Tiny")]);

        var diagram = new MermaidWriter { MinimumLines = 1_000 }.Write(map);
        Assert.DoesNotContain("-->", diagram);
    }

    [Fact]
    public void A_flat_layout_produces_no_subgraphs()
    {
        var map = Map([Project("Solo", "Solo.csproj")]);
        Assert.DoesNotContain("subgraph", new MermaidWriter().Write(map));
    }

    [Fact]
    public void Every_declared_node_carries_a_style_class()
    {
        var map = Map([
            Project("Site", "/src/Web/Site/Site.csproj", ProjectKind.Web),
            Project("Core", "/src/Lib/Core/Core.csproj"),
        ]);

        var diagram = new MermaidWriter().Write(map);
        Assert.Contains("classDef web", diagram);
        Assert.Contains("classDef library", diagram);
        Assert.Contains("class nSite web", diagram);
        Assert.Contains("class nCore library", diagram);
    }
}
