using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Fixtures are written to disk rather than mocked, because the thing under
/// test is precisely how the code copes with real project files: two XML
/// dialects, wrong relative paths, folders that reveal what the references
/// hide.
/// </summary>
public class ProjectGraphTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lens-graph-" + Guid.NewGuid().ToString("N"));

    /// <summary>A pre-SDK project file, in the MSBuild 2003 namespace.</summary>
    private void OldStyle(string name, string outputType = "Library",
                          string[]? projectRefs = null, string[]? assemblies = null)
    {
        var refs = string.Join("", (projectRefs ?? []).Select(r =>
            $"""<ProjectReference Include="..\{r}\{r}.csproj"><Name>{r}</Name></ProjectReference>"""));
        var asm = string.Join("", (assemblies ?? []).Select(a =>
            $"""<Reference Include="{a}, Version=1.0.0.0, Culture=neutral" />"""));

        Write(name, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>{outputType}</OutputType>
                <TargetFrameworkVersion>v4.5.1</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>{asm}{refs}</ItemGroup>
            </Project>
            """);
    }

    /// <summary>An SDK-style project file, with no XML namespace at all.</summary>
    private void SdkStyle(string name, string[]? packages = null)
    {
        var pkgs = string.Join("", (packages ?? []).Select(p =>
            $"""<PackageReference Include="{p}" Version="1.0.0" />"""));

        Write(name, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>{pkgs}</ItemGroup>
            </Project>
            """);
    }

    private void Write(string name, string content)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"{name}.csproj"), content);
        File.WriteAllText(Path.Combine(folder, "Placeholder.cs"), "public class Placeholder { }\n");
    }

    private void AddFile(string project, string relative, string content = "x")
    {
        var full = Path.Combine(_root, project, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private SolutionMap Build() => new ProjectGraph().Build(_root);

    // ---- discovery -----------------------------------------------------

    [Fact]
    public void Reads_both_project_file_dialects()
    {
        OldStyle("Legacy");
        SdkStyle("Modern");

        var names = Build().Projects.Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(["Legacy", "Modern"], names);
    }

    [Fact]
    public void Skips_build_output_and_package_folders()
    {
        OldStyle("Real");
        // A copy left in bin/ is not a project, it is an artefact. Counting it
        // doubles every number on the map.
        Directory.CreateDirectory(Path.Combine(_root, "bin", "Ghost"));
        File.WriteAllText(Path.Combine(_root, "bin", "Ghost", "Ghost.csproj"), "<Project/>");

        Assert.Single(Build().Projects);
    }

    [Fact]
    public void An_unparseable_project_file_is_reported_not_thrown()
    {
        OldStyle("Fine");
        Write("Corrupt", "<Project><unclosed>");

        var map = Build();
        Assert.Equal(2, map.Projects.Count);
        Assert.Equal(ProjectKind.Broken, map.Projects.Single(p => p.Name == "Corrupt").Kind);
    }

    // ---- dependencies --------------------------------------------------

    [Fact]
    public void Resolves_references_by_name_even_when_the_path_is_wrong()
    {
        // The path in the file points nowhere: the folder was moved and only
        // the IDE fixed it up. The name still identifies the project, which is
        // why resolution goes by name.
        OldStyle("Core");
        Write("Web", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <ProjectReference Include="..\..\Old\Location\Core\Core.csproj">
                  <Name>Core</Name>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """);

        Assert.Contains(Build().Edges, e => e.From == "Web" && e.To == "Core");
    }

    [Fact]
    public void References_to_projects_outside_the_solution_are_dropped()
    {
        OldStyle("App", projectRefs: ["SomethingElsewhere"]);
        Assert.Empty(Build().Edges);
    }

    [Fact]
    public void Detects_a_dependency_cycle()
    {
        OldStyle("A", projectRefs: ["B"]);
        OldStyle("B", projectRefs: ["A"]);

        var cycle = Assert.Single(Build().Cycles);
        Assert.Contains("A", cycle);
        Assert.Contains("B", cycle);
    }

    [Fact]
    public void A_plain_chain_is_not_a_cycle()
    {
        OldStyle("A", projectRefs: ["B"]);
        OldStyle("B", projectRefs: ["C"]);
        OldStyle("C");

        Assert.Empty(Build().Cycles);
    }

    // ---- kind, from the folder rather than the references ---------------

    [Fact]
    public void A_web_config_makes_it_a_web_project()
    {
        OldStyle("Site");
        AddFile("Site", "web.config", "<configuration/>");

        Assert.Equal(ProjectKind.Web, Build().Projects.Single().Kind);
    }

    [Fact]
    public void A_library_referencing_mvc_stays_a_library()
    {
        // The case that made this rewrite necessary: nopCommerce's Nop.Core
        // references System.Web.Mvc and is a class library all the same.
        // Classifying it as Web hid the only interesting fact about it.
        OldStyle("Core", assemblies: ["System.Web.Mvc"]);

        Assert.Equal(ProjectKind.Library, Build().Projects.Single().Kind);
    }

    [Fact]
    public void A_test_project_is_a_test_before_it_is_a_library()
    {
        SdkStyle("Suite", packages: ["xunit"]);
        Assert.Equal(ProjectKind.Test, Build().Projects.Single().Kind);
    }

    [Fact]
    public void An_app_xaml_makes_it_wpf()
    {
        OldStyle("Desktop", outputType: "WinExe");
        AddFile("Desktop", "App.xaml", "<Application/>");

        Assert.Equal(ProjectKind.Wpf, Build().Projects.Single().Kind);
    }

    // ---- measurement ---------------------------------------------------

    [Fact]
    public void Generated_designer_files_are_not_counted()
    {
        // WinForms and typed datasets generate thousands of lines nobody wrote
        // and nobody reads. Counting them makes a small project look large.
        OldStyle("Forms");
        AddFile("Forms", "Main.Designer.cs", string.Join('\n', Enumerable.Repeat("// generated", 5000)));

        var project = Build().Projects.Single();
        Assert.Equal(1, project.SourceFiles);
        Assert.True(project.Lines < 10);
    }

    [Fact]
    public void Recognises_a_legacy_target_framework()
    {
        OldStyle("Old");
        SdkStyle("New");

        var map = Build();
        Assert.True(map.Projects.Single(p => p.Name == "Old").IsLegacyFramework);
        Assert.False(map.Projects.Single(p => p.Name == "New").IsLegacyFramework);
    }

    [Fact]
    public void Missing_directory_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new ProjectGraph().Build(Path.Combine(_root, "nowhere")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
