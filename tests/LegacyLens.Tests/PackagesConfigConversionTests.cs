using System.Diagnostics;
using System.Text;
using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The two bugs this suite exists for were both invisible to a reading of the
/// patch and both fatal to `git apply`: a file that does not end with a newline
/// needs a marker, and a byte order mark has to survive being read. Neither
/// shows up unless something actually tries to apply the output, so the last
/// test here does.
/// </summary>
public class PackagesConfigConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-convert-{Guid.NewGuid():N}");

    public PackagesConfigConversionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A pre-SDK project the way Visual Studio writes one: a byte order mark,
    /// CRLF, an xmlns, hint paths into the solution's packages folder, and no
    /// newline at the end of the file.
    /// </summary>
    private ProjectModernisation Project(
        string name,
        string packages = """<package id="Newtonsoft.Json" version="13.0.3" targetFramework="net48" />""",
        bool trailingNewline = false,
        string newline = "\r\n")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        var csproj = string.Join(newline,
            """<?xml version="1.0" encoding="utf-8"?>""",
            """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">""",
            "  <PropertyGroup>",
            "    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>",
            "  </PropertyGroup>",
            "  <ItemGroup>",
            """    <Reference Include="Newtonsoft.Json, Version=13.0.0.0, Culture=neutral">""",
            """      <HintPath>..\..\packages\Newtonsoft.Json.13.0.3\lib\net45\Newtonsoft.Json.dll</HintPath>""",
            "    </Reference>",
            """    <Reference Include="System" />""",
            "  </ItemGroup>",
            "</Project>") + (trailingNewline ? newline : string.Empty);

        var path = Path.Combine(folder, $"{name}.csproj");
        File.WriteAllText(path, csproj, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        File.WriteAllText(
            Path.Combine(folder, "packages.config"),
            string.Join(newline, """<?xml version="1.0" encoding="utf-8"?>""", "<packages>", $"  {packages}", "</packages>"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return new ProjectModernisation(
            name, path, SdkStyle: false, PackageDeclaration.PackagesConfig, "v4.8", []);
    }

    [Fact]
    public void Writes_a_package_reference_carrying_the_version_from_disk()
    {
        var proposal = new PackagesConfigConversion().Propose(Project("Sample"), _root);

        Assert.NotNull(proposal);
        Assert.Contains(
            """+    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />""",
            proposal.Patch);
    }

    [Fact]
    public void Removes_references_into_the_packages_folder_and_leaves_the_others()
    {
        var proposal = new PackagesConfigConversion().Propose(Project("Sample"), _root);

        Assert.NotNull(proposal);
        Assert.Contains("-      <HintPath>", proposal.Patch);
        Assert.DoesNotContain("""-    <Reference Include="System" />""", proposal.Patch);
    }

    [Fact]
    public void Marks_a_file_that_does_not_end_with_a_newline()
    {
        var proposal = new PackagesConfigConversion().Propose(
            Project("Sample", trailingNewline: false), _root);

        Assert.NotNull(proposal);
        Assert.Contains("\\ No newline at end of file", proposal.Patch);
    }

    [Fact]
    public void Keeps_the_carriage_returns_of_a_windows_file()
    {
        var proposal = new PackagesConfigConversion().Propose(
            Project("Sample", newline: "\r\n"), _root);

        Assert.NotNull(proposal);
        Assert.Contains("\r\n", proposal.Patch);
    }

    [Fact]
    public void Converts_a_blocked_project_anyway_and_says_what_it_does_not_fix()
    {
        // This asserted the opposite, silently, with a bare `return null` and
        // no reason given. A package with no path forward still has to be
        // declared, and declaring it the modern way costs nothing.
        //
        // The consequence was not academic. The SDK conversion drops the
        // hint-path references and tells the reader to convert packages first;
        // on nopCommerce 3.90 that could not be done for twenty-six of the
        // twenty-nine projects it had just offered, so the output was a project
        // file that would not restore. The two have to compose.
        var project = Project("Sample") with { DeadEnds = ["Microsoft.AspNet.Mvc"] };

        var proposal = new PackagesConfigConversion().Propose(project, _root);

        Assert.NotNull(proposal);
        Assert.Contains(proposal.Caveats,
            c => c.Says.Contains("not whether they have a future"));
    }

    [Fact]
    public void Declines_a_project_that_already_uses_package_reference()
    {
        var project = Project("Sample") with { Packages = PackageDeclaration.PackageReference };

        Assert.Null(new PackagesConfigConversion().Propose(project, _root));
    }

    /// <summary>
    /// The only test that would have caught either shipped bug. Everything
    /// above inspects the text; this one asks git whether the text is a patch.
    /// </summary>
    [Fact]
    public void Git_accepts_the_patch()
    {
        // No dependency added for a skip attribute: a machine without git
        // simply does not run this assertion, and the suite still passes.
        if (!Run("git", "--version", _root).Ok) return;

        var proposal = new PackagesConfigConversion().Propose(Project("Sample"), _root);
        Assert.NotNull(proposal);

        Assert.True(Run("git", "init", _root).Ok);
        Assert.True(Run("git", "add -A", _root).Ok);

        var patchPath = Path.Combine(_root, "conversion.patch");
        File.WriteAllText(patchPath, proposal.Patch, new UTF8Encoding(false));

        var check = Run("git", $"apply --check \"{patchPath}\"", _root);
        Assert.True(check.Ok, check.Error);
    }

    private static (bool Ok, string Error) Run(string file, string arguments, string workingDirectory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = workingDirectory,
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
