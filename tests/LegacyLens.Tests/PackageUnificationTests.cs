using System.Diagnostics;
using System.Text;
using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// One version per package, across the estate.
///
/// The ordering is the part that has to be right: "10.0.0" sorts before
/// "9.0.0" as text, and picking the wrong winner here downgrades a solution
/// while reporting success.
/// </summary>
public class PackageUnificationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-unify-{Guid.NewGuid():N}");

    public PackageUnificationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /* ---- ordering ---- */

    [Theory]
    [InlineData("9.0.0", "10.0.0")]
    [InlineData("1.2.3", "1.2.10")]
    [InlineData("4.5", "4.5.1")]
    [InlineData("1.0.0.0", "1.0.0.1")]
    // A prerelease is older than the release it leads to, which is the
    // opposite of how the two strings sort.
    [InlineData("2.0.0-beta1", "2.0.0")]
    [InlineData("2.0.0-alpha", "2.0.0-beta")]
    public void The_newer_version_is_the_one_that_sorts_higher(string older, string newer)
    {
        Assert.True(PackageVersion.TryParse(older, out var a));
        Assert.True(PackageVersion.TryParse(newer, out var b));

        Assert.True(a.CompareTo(b) < 0, $"{older} should be older than {newer}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.0.*")]
    [InlineData("[1.0,2.0)")]
    public void A_version_this_cannot_order_is_refused_rather_than_guessed(string text)
    {
        Assert.False(PackageVersion.TryParse(text, out _));
    }

    /* ---- verdicts ---- */

    private static ModernisationSurvey SurveyOf(params PackageUse[] packages) =>
        new([], packages, 0);

    private static PackageUse Use(string id, params string[] versions) =>
        new(id, versions, versions.Length, Portability.Unknown);

    [Fact]
    public void A_package_that_already_agrees_with_itself_needs_nothing()
    {
        var verdict = Assert.Single(
            new PackageUnification().Judge(SurveyOf(Use("Newtonsoft.Json", "13.0.3"))));

        Assert.False(verdict.Divergent);
        Assert.False(verdict.Unifiable);
        Assert.Empty(verdict.Blockers);
    }

    [Fact]
    public void The_newest_version_present_is_the_one_chosen()
    {
        var verdict = Assert.Single(new PackageUnification()
            .Judge(SurveyOf(Use("Newtonsoft.Json", "6.0.8", "13.0.3", "9.0.1"))));

        Assert.Equal("13.0.3", verdict.Chosen);
        Assert.True(verdict.Unifiable);
    }

    [Fact]
    public void A_version_range_stops_the_package_rather_than_the_run()
    {
        var verdicts = new PackageUnification().Judge(SurveyOf(
            Use("Weird", "1.0.0", "[1.0,2.0)"),
            Use("Newtonsoft.Json", "6.0.8", "13.0.3")));

        Assert.False(verdicts[0].Unifiable);
        Assert.Contains(verdicts[0].Blockers, b => b.Contains("can order"));

        // The one it could read is still judged.
        Assert.True(verdicts[1].Unifiable);
    }

    [Fact]
    public void Crossing_a_major_version_is_said_out_loud()
    {
        var verdict = Assert.Single(new PackageUnification()
            .Judge(SurveyOf(Use("Newtonsoft.Json", "6.0.8", "13.0.3"))));

        Assert.True(verdict.Unifiable);
        Assert.Contains(verdict.Warnings, w => w.Contains("major version"));
    }

    [Fact]
    public void Staying_inside_one_major_version_needs_no_warning()
    {
        var verdict = Assert.Single(new PackageUnification()
            .Judge(SurveyOf(Use("Newtonsoft.Json", "13.0.1", "13.0.3"))));

        Assert.DoesNotContain(verdict.Warnings, w => w.Contains("major version"));
    }

    [Fact]
    public void A_package_with_no_future_is_still_worth_unifying()
    {
        // Its version disagreeing with itself is a runtime risk today, which
        // is true whether or not the package ever reaches modern .NET.
        var verdict = Assert.Single(new PackageUnification().Judge(SurveyOf(
            new PackageUse("Microsoft.AspNet.Mvc", ["5.2.3", "5.2.7"], 2,
                Portability.TiedToSystemWeb))));

        Assert.True(verdict.Unifiable);
        Assert.Contains(verdict.Warnings, w => w.Contains("System.Web"));
    }

    /* ---- the patch ---- */

    private ProjectModernisation WriteProject(
        string name, string version, PackageDeclaration declaration)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{name}.csproj");

        if (declaration == PackageDeclaration.PackagesConfig)
        {
            File.WriteAllText(path, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Project ToolsVersion="15.0">
                  <ItemGroup>
                    <Reference Include="Newtonsoft.Json">
                      <HintPath>..\packages\Newtonsoft.Json.{version}\lib\net45\Newtonsoft.Json.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """.Replace("\r\n", "\n"));

            File.WriteAllText(Path.Combine(folder, "packages.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                  <package id="Newtonsoft.Json" version="{version}" targetFramework="net48" />
                </packages>
                """.Replace("\r\n", "\n"));
        }
        else
        {
            File.WriteAllText(path, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="{version}" />
                  </ItemGroup>
                </Project>
                """.Replace("\r\n", "\n"));
        }

        return new ProjectModernisation(
            name, path, declaration == PackageDeclaration.PackageReference, declaration, "v4.8", []);
    }

    private ModernisationSurvey SurveyOfProjects(
        IReadOnlyList<ProjectModernisation> projects, params string[] versions) =>
        new(projects, [Use("Newtonsoft.Json", versions)], 0);

    [Fact]
    public void Nothing_is_proposed_when_every_project_already_agrees()
    {
        var projects = new[]
        {
            WriteProject("A", "13.0.3", PackageDeclaration.PackagesConfig),
            WriteProject("B", "13.0.3", PackageDeclaration.PackagesConfig),
        };

        Assert.Null(new PackageUnification()
            .Propose(SurveyOfProjects(projects, "13.0.3"), _root));
    }

    [Fact]
    public void The_patch_raises_the_old_version_and_leaves_the_new_one_alone()
    {
        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackagesConfig),
            WriteProject("B", "13.0.3", PackageDeclaration.PackagesConfig),
        };

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "13.0.3"), _root);

        Assert.NotNull(proposal);
        Assert.Contains("+  <package id=\"Newtonsoft.Json\" version=\"13.0.3\"", proposal.Patch);
        Assert.Contains("-  <package id=\"Newtonsoft.Json\" version=\"6.0.8\"", proposal.Patch);

        // B was already right, so its files are not in the patch at all.
        Assert.DoesNotContain("B/packages.config", proposal.Patch);
    }

    [Fact]
    public void A_package_reference_is_rewritten_in_place()
    {
        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackageReference),
            WriteProject("B", "13.0.3", PackageDeclaration.PackageReference),
        };

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "13.0.3"), _root);

        Assert.NotNull(proposal);
        Assert.Contains(
            "+    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />",
            proposal.Patch);
    }

    [Fact]
    public void A_hint_path_naming_the_old_version_is_left_alone_and_reported()
    {
        // The hint path carries the version too, and rewriting it would point
        // at a folder that only exists after a restore that has not happened.
        // packages.config governs the restore; the hint path follows it.
        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackagesConfig),
            WriteProject("B", "13.0.3", PackageDeclaration.PackagesConfig),
        };

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "13.0.3"), _root)!;

        Assert.DoesNotContain("Newtonsoft.Json.13.0.3\\lib", proposal.Patch);
    }

    [Fact]
    public void The_caveats_say_what_moved_and_what_it_might_break()
    {
        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackagesConfig),
            WriteProject("B", "13.0.3", PackageDeclaration.PackagesConfig),
        };

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "13.0.3"), _root)!;

        Assert.Contains(proposal.Caveats, c => c.Contains("6.0.8, 13.0.3 becomes 13.0.3"));
        Assert.Contains(proposal.Caveats, c => c.Contains("major version"));
    }

    [Fact]
    public void A_binding_redirect_for_a_moved_package_is_named_and_not_touched()
    {
        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackagesConfig),
            WriteProject("B", "13.0.3", PackageDeclaration.PackagesConfig),
        };

        File.WriteAllText(Path.Combine(_root, "A", "Web.config"), """
            <?xml version="1.0"?>
            <configuration>
              <runtime>
                <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
                  <dependentAssembly>
                    <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" />
                    <bindingRedirect oldVersion="0.0.0.0-6.0.0.0" newVersion="6.0.0.0" />
                  </dependentAssembly>
                </assemblyBinding>
              </runtime>
            </configuration>
            """);

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "13.0.3"), _root)!;

        Assert.Contains(proposal.Caveats, c => c.Contains("binding redirect"));
        Assert.Contains(proposal.Caveats, c => c.Contains("assembly version"));

        // Named, never edited. An assembly version is not a package version and
        // cannot be derived from one by reading these files.
        Assert.DoesNotContain("newVersion=\"13", proposal.Patch);
        Assert.DoesNotContain("Web.config", proposal.Patch);
    }

    [Fact]
    public void Git_accepts_the_patch()
    {
        // The check that earned its place. Two defects survived a careful
        // reading of an earlier conversion's output and were caught only by
        // handing the patch to git.
        if (!Run("git", "--version").Ok) return;

        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackagesConfig),
            WriteProject("B", "9.0.1", PackageDeclaration.PackagesConfig),
            WriteProject("C", "13.0.3", PackageDeclaration.PackageReference),
        };

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "9.0.1", "13.0.3"), _root);

        Assert.NotNull(proposal);
        Assert.True(Run("git", "init").Ok);
        Assert.True(Run("git", "add -A").Ok);

        var patchPath = Path.Combine(_root, "unify.patch");
        File.WriteAllText(patchPath, proposal.Patch, new UTF8Encoding(false));

        var check = Run("git", $"apply --check \"{patchPath}\"");
        Assert.True(check.Ok, check.Error);
    }

    [Fact]
    public void A_file_without_a_final_newline_still_produces_a_patch_git_accepts()
    {
        // Orchard's project files are written that way, and without the
        // `\ No newline at end of file` marker every patch is rejected at its
        // last hunk.
        if (!Run("git", "--version").Ok) return;

        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackagesConfig),
            WriteProject("B", "13.0.3", PackageDeclaration.PackagesConfig),
        };

        var config = Path.Combine(_root, "A", "packages.config");
        File.WriteAllText(config, File.ReadAllText(config).TrimEnd('\n'));

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "13.0.3"), _root)!;

        Assert.Contains("\\ No newline at end of file", proposal.Patch);

        Assert.True(Run("git", "init").Ok);
        Assert.True(Run("git", "add -A").Ok);

        var patchPath = Path.Combine(_root, "unify.patch");
        File.WriteAllText(patchPath, proposal.Patch, new UTF8Encoding(false));

        var check = Run("git", $"apply --check \"{patchPath}\"");
        Assert.True(check.Ok, check.Error);
    }

    [Fact]
    public void A_byte_order_mark_survives_being_read()
    {
        // File.ReadAllText detects and strips one, which leaves the first line
        // of the patch three bytes short of the first line on disk.
        if (!Run("git", "--version").Ok) return;

        var projects = new[]
        {
            WriteProject("A", "6.0.8", PackageDeclaration.PackagesConfig),
            WriteProject("B", "13.0.3", PackageDeclaration.PackagesConfig),
        };

        var config = Path.Combine(_root, "A", "packages.config");
        File.WriteAllText(config, File.ReadAllText(config), new UTF8Encoding(true));

        var proposal = new PackageUnification()
            .Propose(SurveyOfProjects(projects, "6.0.8", "13.0.3"), _root)!;

        Assert.True(Run("git", "init").Ok);
        Assert.True(Run("git", "add -A").Ok);

        var patchPath = Path.Combine(_root, "unify.patch");
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
