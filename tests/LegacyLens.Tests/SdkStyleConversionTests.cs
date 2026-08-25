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
        Assert.Contains(proposal.Caveats, c => c.Contains("includes them from the folder"));
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
            c => c.Contains("not the future of the project"));
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
