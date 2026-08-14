using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The order of work is the one part of the report that is an argument rather
/// than a measurement, so it is the part that has to be pinned down: that
/// blocking work outranks convenient work, that a step with nothing in it is
/// left out, and that no step claims something the evidence beside it denies.
/// </summary>
public class AssessmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-assess-{Guid.NewGuid():N}");

    public AssessmentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A pre-SDK project, optionally with packages and a source file, written
    /// in the dialect real .NET Framework solutions use rather than a
    /// simplified one: the xmlns and the packages.config are exactly what the
    /// analysis has to cope with.
    /// </summary>
    private string Project(
        string name,
        string[]? packages = null,
        string[]? assemblies = null,
        string? source = null,
        bool web = false)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        var references = string.Join("\n", (assemblies ?? [])
            .Select(a => $"    <Reference Include=\"{a}\" />"));

        File.WriteAllText(Path.Combine(folder, $"{name}.csproj"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
            {references}
              </ItemGroup>
            </Project>
            """);

        if (packages is not null)
        {
            var entries = string.Join("\n", packages
                .Select(p => $"  <package id=\"{p}\" version=\"1.0.0\" targetFramework=\"net48\" />"));

            File.WriteAllText(Path.Combine(folder, "packages.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                {entries}
                </packages>
                """);
        }

        if (web) File.WriteAllText(Path.Combine(folder, "web.config"), "<configuration />");
        if (source is not null) File.WriteAllText(Path.Combine(folder, $"{name}.cs"), source);

        return folder;
    }

    private Assessment Assess() => new Assessor { MinimumCodeLines = 1 }.Assess(_root);

    private static RepairStep? Step(Assessment assessment, string titleFragment) =>
        assessment.Repairs.FirstOrDefault(
            r => r.Title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a kind of work sits in the order, or -1 if absent.</summary>
    private static int Position(Assessment assessment, RepairKind kind) =>
        assessment.Repairs
            .Select((step, index) => (step, index))
            .Where(entry => entry.step.Kind == kind)
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .First();

    [Fact]
    public void An_unreadable_project_file_outranks_everything_else()
    {
        Project("Fine", packages: ["Newtonsoft.Json"]);

        var broken = Path.Combine(_root, "Broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "Broken.csproj"), "<Project><not closed");

        var assessment = Assess();

        // Whatever else is wrong, the numbers underneath are incomplete while a
        // project file cannot be read, so nothing may be scheduled before it.
        Assert.Equal(RepairKind.Blocking, assessment.Repairs[0].Kind);
        Assert.Contains("could not be parsed", assessment.Repairs[0].Title);
    }

    [Fact]
    public void Mechanical_work_is_ordered_before_the_decisions()
    {
        // One project convertible as it stands, one blocked by System.Web.
        Project("Clean", packages: ["Newtonsoft.Json"]);
        Project("Web", packages: ["Microsoft.AspNet.Mvc"], web: true);

        var assessment = Assess();

        var convert = Position(assessment, RepairKind.Mechanical);
        var decide = Position(assessment, RepairKind.Decision);

        Assert.True(convert >= 0 && decide >= 0, "Both kinds of work should have been found.");
        Assert.True(convert < decide,
            "Work nobody has to argue about belongs before the argument.");
    }

    [Fact]
    public void Classifying_unknown_packages_comes_before_any_conversion()
    {
        // Unknown packages decide the size of everything downstream, so pricing
        // the conversion before they are classified is pricing a guess.
        Project("App", packages: ["Some.Internal.Package", "Newtonsoft.Json"]);

        var assessment = Assess();

        var classify = Position(assessment, RepairKind.Prerequisite);
        var mechanical = Position(assessment, RepairKind.Mechanical);

        Assert.True(classify >= 0);
        Assert.True(mechanical < 0 || classify < mechanical);
    }

    [Fact]
    public void A_step_with_nothing_in_it_is_left_out_rather_than_reported_as_done()
    {
        Project("Clean", packages: ["Newtonsoft.Json"]);

        var assessment = Assess();

        Assert.Null(Step(assessment, "System.Web"));
        Assert.Null(Step(assessment, "dependency cycles"));
        Assert.All(assessment.Repairs, step => Assert.False(string.IsNullOrWhiteSpace(step.Size)));
    }

    [Fact]
    public void The_redirect_step_is_named_after_what_was_actually_found()
    {
        // Divergent versions and hand-written redirects are different
        // observations. A step titled after the one that did not happen
        // contradicts its own evidence list, which is worse than silence.
        var folder = Project("App", packages: ["Newtonsoft.Json"]);
        File.WriteAllText(Path.Combine(folder, "app.config"), """
            <configuration>
              <runtime>
                <assemblyBinding>
                  <bindingRedirect oldVersion="0.0.0.0-9.0.0.0" newVersion="9.0.0.0" />
                </assemblyBinding>
              </runtime>
            </configuration>
            """);

        var assessment = Assess();

        Assert.Null(Step(assessment, "pinned to more than one version"));
        var step = Step(assessment, "binding redirects");
        Assert.NotNull(step);
        Assert.Contains("1 redirect", step.Size);
    }

    [Fact]
    public void Every_step_carries_a_reason_and_a_size()
    {
        Project("Web", packages: ["Microsoft.AspNet.Mvc"], web: true);
        Project("Core", assemblies: ["System.Web.Mvc"], packages: ["Newtonsoft.Json"]);

        var assessment = Assess();

        Assert.NotEmpty(assessment.Repairs);
        foreach (var step in assessment.Repairs)
        {
            Assert.True(step.Why.Length > 40, $"{step.Title} does not say why.");
            Assert.False(string.IsNullOrWhiteSpace(step.Size));
        }
    }

    [Fact]
    public void Files_inside_a_test_project_are_not_ranked_as_product_risk()
    {
        // The ranking drops files whose own name looks like a test, which does
        // not catch support code inside a test project. Orchard's top-ranked
        // file was one of those, and naming a test fixture as the most
        // dangerous file in a product discredits the whole table.
        var suite = Path.Combine(_root, "Suite");
        Directory.CreateDirectory(suite);
        File.WriteAllText(Path.Combine(suite, "Suite.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Reference Include="xunit.core" />
              </ItemGroup>
            </Project>
            """);

        var bindings = Path.Combine(suite, "Bindings");
        Directory.CreateDirectory(bindings);
        File.WriteAllText(Path.Combine(bindings, "Hosting.cs"), Complicated("Hosting"));

        Project("App", source: Complicated("Service"));

        var assessment = Assess();

        Assert.DoesNotContain(assessment.Risk.Entries, e => e.Path.Contains("Bindings"));
        Assert.Contains(assessment.Risk.Entries, e => e.Path.Contains("App"));
    }

    [Fact]
    public void An_empty_directory_says_so_instead_of_reporting_a_clean_codebase()
    {
        var assessment = Assess();

        Assert.Empty(assessment.Map.Projects);
        Assert.Contains(assessment.Limitations, l => l.Subject.Contains("No projects"));
    }

    [Fact]
    public void Missing_change_history_is_stated_rather_than_read_as_stability()
    {
        // The temporary directory is not a git repository, so this is the
        // shallow-clone case every inherited codebase eventually presents.
        Project("App", source: Complicated("Service"));

        var assessment = Assess();

        Assert.NotEqual(HistoryStatus.Available, assessment.Risk.HistoryStatus);
        Assert.Contains(assessment.Limitations, l => l.Subject.Contains("Change history"));
    }

    [Fact]
    public void Assessing_a_directory_that_does_not_exist_says_which_one()
    {
        var missing = Path.Combine(_root, "nowhere");
        var exception = Assert.Throws<DirectoryNotFoundException>(
            () => new Assessor().Assess(missing));

        Assert.Contains(missing, exception.Message);
    }

    /// <summary>
    /// A class with enough branching to survive the minimum-lines filter and
    /// carry a complexity worth ranking.
    /// </summary>
    private static string Complicated(string name)
    {
        var branches = string.Join("\n", Enumerable.Range(0, 30)
            .Select(i => $"        if (value == {i}) return {i};"));

        return $$"""
            namespace Sample;

            public class {{name}}
            {
                public int Decide(int value)
                {
            {{branches}}
                    return -1;
                }
            }
            """;
    }
}
