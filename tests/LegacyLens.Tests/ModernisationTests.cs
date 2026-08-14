using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Built against real project-file shapes rather than invented ones. The two
/// dialects differ in ways that only bite on real files: the pre-SDK format
/// carries an xmlns that breaks naive element lookups, and a project can
/// declare packages in either of two places.
/// </summary>
public class ModernisationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-modern-{Guid.NewGuid():N}");

    public ModernisationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string PreSdk(string name, string? packagesConfig = null, string target = "v4.8")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, $"{name}.csproj"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>{target}</TargetFrameworkVersion>
              </PropertyGroup>
            </Project>
            """);

        if (packagesConfig is not null)
            File.WriteAllText(Path.Combine(folder, "packages.config"), packagesConfig);

        return folder;
    }

    private string Sdk(string name, string packageReferences = "")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, $"{name}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
            {packageReferences}
              </ItemGroup>
            </Project>
            """);

        return folder;
    }

    private static string Packages(params (string Id, string Version)[] items) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<packages>\n" +
        string.Join("\n", items.Select(i =>
            $"  <package id=\"{i.Id}\" version=\"{i.Version}\" targetFramework=\"net48\" />")) +
        "\n</packages>";

    [Fact]
    public void Tells_the_two_project_dialects_apart()
    {
        // The whole difference, as far as tooling is concerned, is one
        // attribute on the Project element.
        PreSdk("Old");
        Sdk("New");

        var survey = new Modernisation().Survey(_root);

        Assert.Equal(2, survey.Projects.Count);
        Assert.Equal(1, survey.PreSdk);
        Assert.Equal(1, survey.SdkStyle);
    }

    [Fact]
    public void Reads_packages_from_a_packages_config()
    {
        PreSdk("Web", Packages(("Newtonsoft.Json", "13.0.3"), ("NHibernate", "5.6.0")));

        var survey = new Modernisation().Survey(_root);

        Assert.Equal(PackageDeclaration.PackagesConfig, survey.Projects[0].Packages);
        Assert.Equal(2, survey.References);
        Assert.Contains(survey.Packages, p => p.Id == "NHibernate");
    }

    [Fact]
    public void Reads_a_version_declared_as_an_element_as_well_as_an_attribute()
    {
        // Both conventions are valid and both appear in the wild. Reading only
        // the attribute silently loses half the references.
        Sdk("Modern", """
                <PackageReference Include="Serilog" Version="3.1.1" />
                <PackageReference Include="Dapper">
                  <Version>2.1.35</Version>
                </PackageReference>
            """);

        var survey = new Modernisation().Survey(_root);

        Assert.Equal(PackageDeclaration.PackageReference, survey.Projects[0].Packages);
        Assert.Equal(2, survey.References);
        Assert.Equal(["2.1.35"], survey.Packages.Single(p => p.Id == "Dapper").Versions);
    }

    [Fact]
    public void A_project_mid_migration_is_counted_by_the_file_that_still_governs_it()
    {
        // Both files present. packages.config is what restore still obeys, so
        // counting the PackageReference block would report the project as
        // already migrated when it is not.
        var folder = PreSdk("Halfway", Packages(("Newtonsoft.Json", "13.0.3")));
        File.WriteAllText(Path.Combine(folder, "Halfway.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """);

        var survey = new Modernisation().Survey(_root);

        Assert.Equal(PackageDeclaration.PackagesConfig, survey.Projects[0].Packages);
        Assert.Contains(survey.Packages, p => p.Id == "Newtonsoft.Json");
        Assert.DoesNotContain(survey.Packages, p => p.Id == "Serilog");
    }

    [Fact]
    public void A_package_tied_to_system_web_blocks_the_project_that_uses_it()
    {
        PreSdk("Site", Packages(
            ("Microsoft.AspNet.Mvc", "5.3.0"),
            ("Newtonsoft.Json", "13.0.3")));
        PreSdk("Domain", Packages(("Newtonsoft.Json", "13.0.3")));

        var survey = new Modernisation().Survey(_root);

        Assert.Equal(1, survey.Blocked);
        Assert.Contains("Microsoft.AspNet.Mvc",
            survey.Projects.Single(p => p.Name == "Site").DeadEnds);

        // Old file format, nothing holding it back: the case a converter can
        // take on its own.
        Assert.Equal(1, survey.ConvertibleAsIs);
    }

    [Fact]
    public void An_unlisted_package_is_unknown_rather_than_assumed_portable()
    {
        // A survey that quietly calls an unrecognised package fine is worse
        // than one that admits the gap, because the quote is built on it.
        PreSdk("Odd", Packages(("Some.Internal.Package", "1.0.0")));

        var survey = new Modernisation().Survey(_root);

        Assert.Equal(Portability.Unknown, survey.Packages.Single().Portability);
        Assert.Equal(0, survey.Blocked);
    }

    [Fact]
    public void The_same_package_at_two_versions_is_reported_as_divergent()
    {
        // Each divergence is a binding redirect waiting to be written, and a
        // conversion that has to pick a winner.
        PreSdk("A", Packages(("Newtonsoft.Json", "11.0.2")));
        PreSdk("B", Packages(("Newtonsoft.Json", "13.0.3")));

        var survey = new Modernisation().Survey(_root);
        var json = survey.Packages.Single(p => p.Id == "Newtonsoft.Json");

        Assert.True(json.Divergent);
        Assert.Equal(2, json.Projects);
        Assert.Equal(1, survey.Divergent);
    }

    [Fact]
    public void Counts_hand_written_binding_redirects()
    {
        var folder = PreSdk("Site", Packages(("Newtonsoft.Json", "13.0.3")));
        File.WriteAllText(Path.Combine(folder, "web.config"), """
            <configuration>
              <runtime>
                <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
                  <dependentAssembly>
                    <bindingRedirect oldVersion="0.0.0.0-13.0.0.0" newVersion="13.0.0.0" />
                  </dependentAssembly>
                  <dependentAssembly>
                    <bindingRedirect oldVersion="0.0.0.0-4.0.0.0" newVersion="4.0.0.0" />
                  </dependentAssembly>
                </assemblyBinding>
              </runtime>
            </configuration>
            """);

        Assert.Equal(2, new Modernisation().Survey(_root).BindingRedirects);
    }

    [Fact]
    public void A_coherent_old_solution_is_tended_rather_than_rotten()
    {
        // Old but consistent is a different job from old and drifted, and it
        // must not carry the same estimate.
        PreSdk("A", Packages(("Newtonsoft.Json", "13.0.3")));
        PreSdk("B", Packages(("Newtonsoft.Json", "13.0.3")));

        Assert.True(new Modernisation().Survey(_root).Tended);
    }

    [Fact]
    public void A_solution_that_has_drifted_is_not()
    {
        PreSdk("A", Packages(("Newtonsoft.Json", "11.0.2")));
        PreSdk("B", Packages(("Newtonsoft.Json", "13.0.3")));

        Assert.False(new Modernisation().Survey(_root).Tended);
    }

    [Fact]
    public void An_unparseable_project_file_does_not_abort_the_survey()
    {
        // Inherited solutions contain files that no longer parse. Losing the
        // whole report over one of them is the failure mode this tool exists
        // to avoid.
        var folder = Path.Combine(_root, "Broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Broken.csproj"), "<Project><unclosed>");
        PreSdk("Fine", Packages(("Newtonsoft.Json", "13.0.3")));

        var survey = new Modernisation().Survey(_root);

        Assert.Equal(2, survey.Projects.Count);
        Assert.Equal(1, survey.References);
    }

    [Fact]
    public void Reads_the_target_framework_from_either_dialect()
    {
        PreSdk("Old", target: "v4.7.2");
        Sdk("New");

        var survey = new Modernisation().Survey(_root);

        Assert.Equal("v4.7.2", survey.Projects.Single(p => p.Name == "Old").TargetFramework);
        Assert.Equal("net10.0", survey.Projects.Single(p => p.Name == "New").TargetFramework);
    }

    [Fact]
    public void Refuses_a_path_that_does_not_exist()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new Modernisation().Survey(Path.Combine(_root, "nowhere")));
    }
}
