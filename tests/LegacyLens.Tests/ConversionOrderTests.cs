using System.Diagnostics;
using System.Text;
using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The two conversions that rewrite the same file, run one after the other.
///
/// They are offered separately because a patch carrying both cannot apply, and
/// that is fine. What is not fine is that they only compose one way round: the
/// SDK conversion drops references pointing into the packages folder and says
/// PackageReference will replace them, so run alone it produces a project file
/// with no packages in it.
///
/// This was reachable in practice. The packages conversion used to decline any
/// project depending on something with no path to modern .NET, silently, so on
/// nopCommerce 3.90 it handled three projects while the SDK conversion offered
/// twenty-nine. Twenty-six of those would have come out unrestorable, and the
/// advice printed beside them could not be followed.
///
/// Applied with real git, because a patch that only satisfies this repository's
/// own reader is not a patch.
/// </summary>
public class ConversionOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-order-{Guid.NewGuid():N}");

    public ConversionOrderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "App"));

        File.WriteAllText(Project, """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <RootNamespace>App</RootNamespace>
                <AssemblyName>App</AssemblyName>
                <TargetFrameworkVersion>v4.6.1</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Newtonsoft.Json, Version=11.0.0.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed, processorArchitecture=MSIL">
                  <HintPath>..\..\packages\Newtonsoft.Json.11.0.2\lib\net45\Newtonsoft.Json.dll</HintPath>
                </Reference>
                <Reference Include="System" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="Thing.cs" />
              </ItemGroup>
              <Target Name="BeforeBuild" />
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """);

        File.WriteAllText(Path.Combine(_root, "src", "App", "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="11.0.2" targetFramework="net461" />
            </packages>
            """);

        File.WriteAllText(Path.Combine(_root, "src", "App", "Thing.cs"), "public class Thing { }");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Project => Path.Combine(_root, "src", "App", "App.csproj");

    private ProjectModernisation Sample() => new(
        "App", Project, false, PackageDeclaration.PackagesConfig, "v4.6.1", []);

    private bool Git(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = _root,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;

            process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool Apply(string patch)
    {
        var path = Path.Combine(_root, $"p-{Guid.NewGuid():N}.patch");
        File.WriteAllText(path, patch, new UTF8Encoding(false));

        var applied = Git($"apply \"{path}\"");
        File.Delete(path);
        return applied;
    }

    [Fact]
    public void Packages_then_sdk_leaves_a_project_that_still_declares_its_packages()
    {
        if (!Git("--version")) return;
        Assert.True(Git("init"));

        var packages = new PackagesConfigConversion().Propose(Sample(), _root);
        Assert.NotNull(packages);
        Assert.True(Apply(packages.Patch));

        // Read again: the project on disk has changed, and the SDK conversion
        // has to be asked about what is there now rather than what was.
        var sdk = new SdkStyleConversion()
            .Propose(Sample() with { Packages = PackageDeclaration.PackageReference }, _root);

        Assert.NotNull(sdk.Proposal);
        Assert.True(Apply(sdk.Proposal.Patch));

        var result = File.ReadAllText(Project);

        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk\">", result);
        Assert.Contains("PackageReference Include=\"Newtonsoft.Json\" Version=\"11.0.2\"", result);
        Assert.DoesNotContain("packages.config", result);
        Assert.False(File.Exists(Path.Combine(_root, "src", "App", "packages.config")));
    }

    [Fact]
    public void The_other_way_round_loses_them_which_is_why_the_order_is_printed()
    {
        // Not a defect to fix by making the SDK conversion carry the packages
        // itself: it would then be doing two jobs and the patch could not be
        // read as one change. It is a defect to fix by saying the order, and by
        // making sure the first step is available for every project the second
        // one offers.
        if (!Git("--version")) return;
        Assert.True(Git("init"));

        var sdk = new SdkStyleConversion().Propose(Sample(), _root);

        Assert.NotNull(sdk.Proposal);
        Assert.True(Apply(sdk.Proposal.Patch));

        var result = File.ReadAllText(Project);

        Assert.DoesNotContain("Newtonsoft.Json", result);
        Assert.Contains(sdk.Proposal.Caveats, c => c.Contains("convert that first"));
    }

    [Fact]
    public void The_first_step_is_offered_for_everything_the_second_one_offers()
    {
        // The property that makes the advice followable. A project whose
        // packages have no path forward is still converted, with a caveat
        // saying what that does not fix.
        var blocked = Sample() with { DeadEnds = ["Microsoft.AspNet.Mvc"] };

        Assert.NotNull(new PackagesConfigConversion().Propose(blocked, _root));
        Assert.True(new SdkStyleConversion().Propose(blocked, _root).Convertible);
    }
}
