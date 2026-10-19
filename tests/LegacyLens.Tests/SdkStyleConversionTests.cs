using System.Diagnostics;
using System.Text;
using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Most of these assert a refusal, which is the point. A pre-SDK file is a
/// hundred and fifty lines of which the SDK supplies all but ten, so the
/// rewrite is easy and knowing when not to perform it is not. On Orchard the
/// tool converts ten projects out of eighty-nine and names a reason for the
/// other seventy-nine.
/// </summary>
public class SdkStyleConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-sdk-{Guid.NewGuid():N}");

    public SdkStyleConversionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ProjectModernisation Project(string name, string body, IReadOnlyList<string>? deadEnds = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{name}.csproj");

        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="$(MSBuildExtensionsPath)\Microsoft.Common.props" />
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <RootNamespace>{name}</RootNamespace>
                <AssemblyName>{name}</AssemblyName>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                <OutputPath>bin\</OutputPath>
              </PropertyGroup>
            {body}
              <Import Project="$(MSBuildBinPath)\Microsoft.CSharp.targets" />
            </Project>
            """.Replace("\r\n", "\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return new ProjectModernisation(
            name, path, SdkStyle: false, PackageDeclaration.PackageReference, "v4.8", deadEnds ?? []);
    }

    private const string Clean = """
          <ItemGroup>
            <Compile Include="Thing.cs" />
            <Reference Include="System" />
            <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
          </ItemGroup>
        """;

    [Fact]
    public void A_clean_project_converts()
    {
        var verdict = new SdkStyleConversion().Propose(Project("Sample", Clean), _root);

        Assert.True(verdict.Convertible, string.Join(" ", verdict.Blockers));
        Assert.NotNull(verdict.Proposal);
        Assert.Contains("+<Project Sdk=\"Microsoft.NET.Sdk\">", verdict.Proposal.Patch);
        Assert.Contains("+    <TargetFramework>net48</TargetFramework>", verdict.Proposal.Patch);
    }

    [Fact]
    public void Package_and_bare_references_survive_the_rewrite()
    {
        var patch = new SdkStyleConversion().Propose(Project("Sample", Clean), _root).Proposal!.Patch;

        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", patch);
        Assert.Contains("<Reference Include=\"System\" />", patch);
    }

    [Fact]
    public void Compile_items_are_dropped_and_the_reader_is_told()
    {
        var proposal = new SdkStyleConversion().Propose(Project("Sample", Clean), _root).Proposal!;

        Assert.DoesNotContain("+    <Compile Include=", proposal.Patch);
        Assert.Contains(proposal.Caveats, c => c.Says.Contains("includes them from the folder"));
    }

    [Fact]
    public void Assembly_attributes_stay_in_the_project_s_own_file()
    {
        // M30, on a Windows runner: every one of Orchard's twenty-six errors
        // after conversion was CS0579. The SDK generates AssemblyVersion and the
        // rest, the project still carries the AssemblyInfo.cs that declares
        // them, and nothing told the SDK not to.
        var body = """
              <ItemGroup>
                <Compile Include="Thing.cs" />
                <Compile Include="Properties\AssemblyInfo.cs" />
              </ItemGroup>
            """;
        var project = Project("Sample", body);
        var properties = Path.Combine(_root, "Sample", "Properties");
        Directory.CreateDirectory(properties);
        File.WriteAllText(Path.Combine(properties, "AssemblyInfo.cs"),
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]");

        var patch = new SdkStyleConversion().Propose(project, _root).Proposal!.Patch;

        Assert.Contains("+    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>", patch);
    }

    [Fact]
    public void A_source_file_the_old_project_left_out_stays_out_and_is_named()
    {
        // M30 again: nopCommerce's Nop.Core has NopStarter.cs on disk and not in
        // the project, obsolete and referring to a namespace that is gone. The
        // SDK compiles the whole folder, so the conversion brought it back and
        // the build failed on it. The old list said which files were in; the
        // new file has to say which are out.
        var body = """
              <ItemGroup>
                <Compile Include="Thing.cs" />
                <Compile Include="Infrastructure\Engine.cs" />
              </ItemGroup>
            """;
        var project = Project("Sample", body);
        var folder = Path.Combine(_root, "Sample");
        Directory.CreateDirectory(Path.Combine(folder, "Infrastructure"));
        File.WriteAllText(Path.Combine(folder, "Thing.cs"), "class Thing { }");
        File.WriteAllText(Path.Combine(folder, "Infrastructure", "Engine.cs"), "class Engine { }");
        File.WriteAllText(Path.Combine(folder, "Infrastructure", "Starter.cs"), "class Starter { }");

        // What a build of the old project leaves behind is not the team's
        // source, and the SDK does not glob it either.
        Directory.CreateDirectory(Path.Combine(folder, "obj", "Debug"));
        File.WriteAllText(Path.Combine(folder, "obj", "Debug", "Generated.cs"), "class Generated { }");

        var proposal = new SdkStyleConversion().Propose(project, _root).Proposal!;

        Assert.Contains("+    <Compile Remove=\"Infrastructure/Starter.cs\" />", proposal.Patch);
        Assert.DoesNotContain("Remove=\"Thing.cs\"", proposal.Patch);
        Assert.DoesNotContain("Remove=\"Infrastructure/Engine.cs\"", proposal.Patch);
        Assert.DoesNotContain("Generated.cs", proposal.Patch);
        Assert.Contains(proposal.Caveats, c => c.Says.Contains("Infrastructure/Starter.cs"));
    }

    [Fact]
    public void A_wildcard_include_is_not_compared_with_the_folder()
    {
        var body = """
              <ItemGroup>
                <Compile Include="**\*.cs" />
              </ItemGroup>
            """;
        var project = Project("Sample", body);
        File.WriteAllText(Path.Combine(_root, "Sample", "Thing.cs"), "class Thing { }");

        var proposal = new SdkStyleConversion().Propose(project, _root).Proposal!;

        Assert.DoesNotContain("Compile Remove", proposal.Patch);
        Assert.Contains(proposal.Caveats, c => c.About == "compile-not-listed");
    }

    [Fact]
    public void A_file_copied_to_the_output_keeps_its_copy()
    {
        // Every nopCommerce 3.90 plugin copies its views and Description.txt
        // next to its assembly, which is how the application finds it. The
        // SDK's glob includes those files and copies none of them, and a build
        // cannot tell: it compiled, and the plugin was gone.
        var body = """
              <ItemGroup>
                <Content Include="Views\Configure.cshtml">
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </Content>
                <None Include="logo.jpg">
                  <CopyToOutputDirectory>Always</CopyToOutputDirectory>
                </None>
                <Content Include="readme.txt" />
              </ItemGroup>
            """;

        var patch = new SdkStyleConversion().Propose(Project("Sample", body), _root).Proposal!.Patch;

        Assert.Contains("+    <None Update=\"Views/Configure.cshtml\" CopyToOutputDirectory=\"PreserveNewest\" />", patch);
        Assert.Contains("+    <None Update=\"logo.jpg\" CopyToOutputDirectory=\"Always\" />", patch);
        Assert.DoesNotContain("+    <None Update=\"readme.txt\"", patch);
    }

    [Fact]
    public void An_embedded_resource_that_is_not_resx_stays_embedded()
    {
        // The SDK embeds .resx on its own and nothing else.
        var body = """
              <ItemGroup>
                <EmbeddedResource Include="Files\taxonomy.txt" />
                <EmbeddedResource Include="Strings.resx" />
              </ItemGroup>
            """;

        var patch = new SdkStyleConversion().Propose(Project("Sample", body), _root).Proposal!.Patch;

        Assert.Contains("+    <EmbeddedResource Include=\"Files/taxonomy.txt\" />", patch);
        Assert.DoesNotContain("+    <EmbeddedResource Include=\"Strings.resx\"", patch);
    }

    [Fact]
    public void An_output_folder_every_configuration_shares_is_kept()
    {
        // Where nopCommerce builds a plugin: into the web application, in Debug
        // and in Release alike. That is not a build configuration, and dropping
        // it with them left every plugin where the application never looks.
        var body = """
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
                <OutputPath>..\Web\Plugins\Sample\</OutputPath>
              </PropertyGroup>
            """;
        var project = Project("Sample", body);
        File.WriteAllText(project.Path, File.ReadAllText(project.Path)
            .Replace("<OutputPath>bin\\</OutputPath>", "<OutputPath>..\\Web\\Plugins\\Sample\\</OutputPath>"));

        var patch = new SdkStyleConversion().Propose(project, _root).Proposal!.Patch;

        // A context line rather than an added one: the diff finds it where the
        // old file had it.
        Assert.Contains(" <OutputPath>..\\Web\\Plugins\\Sample\\</OutputPath>", patch);
        Assert.Contains("+    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>", patch);
    }

    [Fact]
    public void An_output_folder_that_differs_by_configuration_goes_with_them()
    {
        var body = """
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
                <OutputPath>bin\Release\</OutputPath>
              </PropertyGroup>
            """;

        var patch = new SdkStyleConversion().Propose(Project("Sample", body), _root).Proposal!.Patch;

        Assert.DoesNotContain("+    <OutputPath>", patch);
    }

    [Fact]
    public void A_reference_that_is_not_copied_stays_not_copied()
    {
        // A nopCommerce plugin builds into the web application's folder and
        // references Nop.Core with Private set to false, so as not to put a
        // second Nop.Core next to the one the application loads.
        var body = """
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj">
                  <Name>Core</Name>
                  <Private>False</Private>
                </ProjectReference>
                <ProjectReference Include="..\Other\Other.csproj" />
                <Reference Include="System.Web">
                  <Private>False</Private>
                </Reference>
              </ItemGroup>
            """;

        var patch = new SdkStyleConversion().Propose(Project("Sample", body), _root).Proposal!.Patch;

        Assert.Contains("+    <ProjectReference Include=\"../Core/Core.csproj\" Private=\"False\" />", patch);
        Assert.Contains("+    <ProjectReference Include=\"../Other/Other.csproj\" />", patch);
        Assert.Contains("+    <Reference Include=\"System.Web\" Private=\"False\" />", patch);
    }

    [Fact]
    public void A_custom_target_refuses_the_conversion()
    {
        var body = Clean + "\n  <Target Name=\"AfterBuild\">\n    <Message Text=\"hi\" />\n  </Target>";
        var verdict = new SdkStyleConversion().Propose(Project("Sample", body), _root);

        Assert.False(verdict.Convertible);
        Assert.Contains(verdict.Blockers, b => b.Contains("custom build target"));
    }

    [Fact]
    public void A_project_extensions_block_refuses_the_conversion()
    {
        var body = Clean + "\n  <ProjectExtensions><VisualStudio /></ProjectExtensions>";
        var verdict = new SdkStyleConversion().Propose(Project("Sample", body), _root);

        Assert.False(verdict.Convertible);
        Assert.Contains(verdict.Blockers, b => b.Contains("ProjectExtensions"));
    }

    [Fact]
    public void A_non_standard_import_refuses_the_conversion()
    {
        var body = Clean + "\n  <Import Project=\"$(VSToolsPath)\\WebApplications\\Microsoft.WebApplication.targets\" />";
        var verdict = new SdkStyleConversion().Propose(Project("Sample", body), _root);

        Assert.False(verdict.Convertible);
        Assert.Contains(verdict.Blockers, b => b.Contains("does not supply"));
    }

    [Fact]
    public void A_project_with_no_path_forward_is_converted_anyway_and_told_so()
    {
        // This test used to assert the opposite, and a real migration refuted
        // it. Whether a project can take the SDK format is a question about the
        // file; whether it can port is a question about its packages. They are
        // independent, and the nopCommerce team settled it: they put all
        // twenty-six of their projects into the SDK format and left them on
        // .NET Framework with EF6. `Nop.Web` was among the twenty-eight this
        // tool called blocked, and in 4.00 it is SDK format on net461.
        //
        // So the fact is kept and demoted: the work is done, and the caveat
        // says what it does not fix.
        var verdict = new SdkStyleConversion()
            .Propose(Project("Sample", Clean, ["Microsoft.AspNet.Mvc"]), _root);

        Assert.True(verdict.Convertible);
        Assert.NotNull(verdict.Proposal);
        Assert.Contains(verdict.Proposal.Caveats,
            c => c.Says.Contains("not the future of the project"));
    }

    [Fact]
    public void An_empty_build_target_has_no_steps_to_lose()
    {
        // Visual Studio writes an empty BeforeBuild and AfterBuild into every
        // pre-SDK project ever made. Counted as custom build logic they refused
        // the conversion on almost everything: 61 of nopCommerce 3.90's 94
        // targets were empty or commented out, and 26 of its 31 projects were
        // turned down over them.
        var body = Clean
                 + "\n  <Target Name=\"BeforeBuild\" />"
                 + "\n  <Target Name=\"AfterBuild\"><!-- nothing --></Target>";

        Assert.True(new SdkStyleConversion().Propose(Project("Sample", body), _root).Convertible);
    }

    [Fact]
    public void But_a_target_that_does_something_still_refuses()
    {
        // The rule is about steps, not about names. A target with a task in it
        // is build logic, and dropping it silently is the one thing this
        // conversion must never do.
        var body = Clean
                 + "\n  <Target Name=\"AfterBuild\"><Copy SourceFiles=\"a\" DestinationFolder=\"b\" /></Target>";

        var verdict = new SdkStyleConversion().Propose(Project("Sample", body), _root);

        Assert.False(verdict.Convertible);
        Assert.Contains(verdict.Blockers, b => b.Contains("build steps would be silently lost"));
    }

    [Fact]
    public void An_import_out_of_the_packages_folder_is_a_packages_config_artefact()
    {
        // The package brought its own build targets and the project file was
        // edited to point into the restore folder. PackageReference brings them
        // in on its own. On nopCommerce 3.90 that was 26 of the 30 non-standard
        // imports, every one of them the same file.
        var body = Clean
                 + "\n  <Import Project=\"..\\..\\packages\\Microsoft.Bcl.Build.1.0.21\\build\\Microsoft.Bcl.Build.targets\" />";

        Assert.True(new SdkStyleConversion().Propose(Project("Sample", body), _root).Convertible);
    }

    [Fact]
    public void The_guard_NuGet_writes_for_those_imports_goes_with_them()
    {
        var body = Clean
                 + "\n  <Target Name=\"EnsureNuGetPackageBuildImports\" BeforeTargets=\"PrepareForBuild\">"
                 + "\n    <Error Text=\"missing\" />"
                 + "\n  </Target>";

        Assert.True(new SdkStyleConversion().Propose(Project("Sample", body), _root).Convertible);
    }

    [Theory]
    [InlineData("v4.8", "net48")]
    [InlineData("v4.7.2", "net472")]
    [InlineData("v4.6.1", "net461")]
    [InlineData("v4.0", "net40")]
    public void The_target_framework_moniker_is_translated(string old, string expected)
    {
        Assert.Equal(expected, SdkStyleConversion.Moniker(old));
    }

    [Fact]
    public void Git_accepts_the_patch()
    {
        if (!Run("git", "--version").Ok) return;

        var proposal = new SdkStyleConversion().Propose(Project("Sample", Clean), _root).Proposal;
        Assert.NotNull(proposal);

        Assert.True(Run("git", "init").Ok);
        Assert.True(Run("git", "add -A").Ok);

        var patchPath = Path.Combine(_root, "sdk.patch");
        File.WriteAllText(patchPath, proposal.Patch, new UTF8Encoding(false));

        var check = Run("git", $"apply --check \"{patchPath}\"");
        Assert.True(check.Ok, check.Error);
    }

    private (bool Ok, string Error) Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = _root,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode == 0, error);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}
