using LegacyLens.Analysis;
using LegacyLens.Api;

namespace LegacyLens.Tests;

/// <summary>
/// What a reader is handed before they apply a patch.
///
/// On nopCommerce 3.90 the caveats came to roughly two hundred lines for
/// thirty-one projects: the same eight sentences over and over, with the one
/// line that mattered, that PackageReference wants a target this solution does
/// not have, printed thirty-one times among them. **A decision repeated
/// thirty-one times is one decision and thirty pieces of noise**, and the whole
/// objective this was set against is taking load off the person deciding.
/// </summary>
public class CaveatGroupingTests
{
    private static (string, Caveat) From(string project, string about, string says, bool decides = false) =>
        (project, new Caveat(about, says) { Decides = decides });

    [Fact]
    public void The_same_caveat_from_many_projects_is_said_once()
    {
        var grouped = Caveats.Group([
            From("A", "target-below-461", "Targets v4.5.1."),
            From("B", "target-below-461", "Targets v4.5.1."),
            From("C", "target-below-461", "Targets v4.5.1."),
        ]);

        var one = Assert.Single(grouped);

        Assert.Equal(3, one.Projects.Count);
        Assert.False(one.Varies);
    }

    [Fact]
    public void It_is_grouped_by_its_key_and_not_by_its_sentence()
    {
        // The sentence carries counts and package names, so no two projects
        // write it the same way. Grouping by the sentence would put each of
        // these on a line of its own, which is the state this replaced.
        var grouped = Caveats.Group([
            From("A", "items-globbed", "125 item(s) are dropped."),
            From("B", "items-globbed", "7 item(s) are dropped."),
        ]);

        var one = Assert.Single(grouped);

        Assert.Equal(2, one.Projects.Count);
    }

    [Fact]
    public void And_a_reader_is_told_when_the_projects_did_not_all_say_the_same_thing()
    {
        // Being told that twenty-nine projects said this is only useful if it
        // also says whether they said it about the same thing.
        var grouped = Caveats.Group([
            From("A", "still-blocked", "Still depends on: Microsoft.AspNet.Mvc."),
            From("B", "still-blocked", "Still depends on: log4net."),
        ]);

        Assert.True(Assert.Single(grouped).Varies);
    }

    [Fact]
    public void What_has_to_be_decided_is_kept_apart_from_what_was_done()
    {
        var outcome = new ConversionOutcome("sdk", "patch", [], [], Caveats.Group([
            From("A", "target-below-461", "Targets v4.5.1.", decides: true),
            From("A", "build-configurations", "Build configurations are dropped."),
            From("B", "build-configurations", "Build configurations are dropped."),
        ]));

        var decision = Assert.Single(outcome.Decisions);
        var consequence = Assert.Single(outcome.Consequences);

        Assert.Equal("target-below-461", decision.What.About);
        Assert.Equal("build-configurations", consequence.What.About);
    }

    [Fact]
    public void The_loudest_group_comes_first()
    {
        var grouped = Caveats.Group([
            From("A", "rare", "Once."),
            From("B", "common", "Twice."),
            From("C", "common", "Twice."),
        ]);

        Assert.Equal("common", grouped[0].What.About);
    }

    [Fact]
    public void A_caveat_about_the_solution_rather_than_a_project_names_nobody()
    {
        // The configuration and version conversions produce one patch for the
        // whole solution, so there is no project to attribute a caveat to and
        // an empty name must not be printed as one.
        var one = Assert.Single(Caveats.Group([
            From(string.Empty, "keys-kept-flat", "Keys are kept flat."),
        ]));

        Assert.Empty(one.Projects);
    }

    [Fact]
    public void The_real_conversion_marks_what_nobody_else_can_settle()
    {
        // Read off the conversion rather than restated here. An earlier version
        // of this test built its own Caveats and asserted the flags it had just
        // set, which is a test that cannot fail: exactly what M20 found in this
        // repository and fixed.
        var root = Path.Combine(Path.GetTempPath(), $"lens-caveat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "App"));

        try
        {
            var project = Path.Combine(root, "App", "App.csproj");

            File.WriteAllText(project, """
                <?xml version="1.0" encoding="utf-8"?>
                <Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <TargetFrameworkVersion>v4.5.1</TargetFrameworkVersion>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Newtonsoft.Json">
                      <HintPath>..\packages\Newtonsoft.Json.11.0.2\lib\net45\Newtonsoft.Json.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(root, "App", "packages.config"), """
                <?xml version="1.0" encoding="utf-8"?>
                <packages>
                  <package id="Newtonsoft.Json" version="11.0.2" targetFramework="net451" />
                </packages>
                """);

            var proposal = new PackagesConfigConversion().Propose(
                new ProjectModernisation(
                    "App", project, false, PackageDeclaration.PackagesConfig, "v4.5.1", []),
                root);

            Assert.NotNull(proposal);

            var target = proposal.Caveats.Single(c => c.About == "target-below-461");
            var redirects = proposal.Caveats.Single(c => c.About == "binding-redirects");

            Assert.True(target.Decides, "moving the target is nobody else's call");
            Assert.False(redirects.Decides, "leaving the redirects alone is something it did");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
