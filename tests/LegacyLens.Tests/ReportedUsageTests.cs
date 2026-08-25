using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// What the handed-over document says about the packages with no future.
///
/// The report counted what a solution declares: how many projects reference a
/// package, how many are unclassified. None of that separates an afternoon of
/// find-and-replace from a rewrite. The tool already knew which it was, and the
/// only way to see it was to run a second command, so the one artefact meant to
/// be read by somebody deciding never contained the number that decides.
/// </summary>
public class ReportedUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-reported-{Guid.NewGuid():N}");

    public ReportedUsageTests()
    {
        Directory.CreateDirectory(_root);

        File.WriteAllText(Path.Combine(_root, "App.csproj"), """
            <Project ToolsVersion="4.0" DefaultTargets="Build"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                <OutputType>Library</OutputType>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(_root, "Home.cs"), """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index() => View();

                public ActionResult Deny() => new HttpUnauthorizedResult();
            }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Assessment Assess(bool dependencies = true) =>
        new Assessor { ReadDependencies = dependencies }.Assess(_root);

    /// <summary>
    /// The report with its line breaks flattened.
    ///
    /// The writer wraps to a column, so any phrase long enough to be worth
    /// asserting on is long enough to be split by it. Asserting on the wrapped
    /// text passes or fails on where a word happened to land.
    /// </summary>
    private string Report(bool dependencies = true) =>
        System.Text.RegularExpressions.Regex.Replace(
            new ReportWriter().Write(Assess(dependencies)), @"\s+", " ");

    [Fact]
    public void An_assessment_reads_what_the_codebase_uses_of_its_dependencies()
    {
        var used = Assess().Uses.Single(d => d.Surface.Package == "Microsoft.AspNet.Mvc");

        Assert.True(used.Surface.Uses > 0);
        Assert.Contains(used.Surface.Types, type => type.Name == "ActionResult");
    }

    [Fact]
    public void And_it_reads_them_the_way_everything_else_does()
    {
        // Through Surfaces, so a number in the report cannot contradict the
        // same number from the command. That was the defect: the command
        // answered 4,379 uses on Orchard where the route answered 3,877.
        var fromReport = Assess().Uses.Single(d => d.Surface.Package == "Microsoft.AspNet.Mvc");
        var fromCommand = new Surfaces().Of(_root, "Microsoft.AspNet.Mvc");

        Assert.Equal(fromCommand.Uses, fromReport.Surface.Uses);
        Assert.Equal(
            fromCommand.Types.Select(t => t.Name),
            fromReport.Surface.Types.Select(t => t.Name));
    }

    [Fact]
    public void The_document_carries_the_number_that_decides_the_size()
    {
        var report = Report();

        Assert.Contains("What you actually use of what is going away", report);
        Assert.Contains("Microsoft.AspNet.Mvc", report);
        Assert.Contains("ActionResult", report);
        Assert.Contains("carry four fifths of it", report);
    }

    [Fact]
    public void It_says_what_it_cannot_say_in_the_same_breath()
    {
        // The half that keeps the other half honest. Counted from syntax, and
        // an uncatalogued type is unknown rather than fine.
        var report = Report();

        Assert.Contains("Counted from the syntax", report);
        Assert.Contains("unknown, which is not the same as fine", report);
    }

    [Fact]
    public void The_framework_is_asked_about_the_column_nobody_catalogued()
    {
        // M13's answer, in the document rather than behind a second command. It
        // is what turns a frightening number of unknowns into the smaller
        // number of real decisions.
        var report = Report();

        Assert.Contains("Asked of the framework itself", report);
        Assert.Contains("still to decide", report);
        Assert.Contains("trap rather than an answer", report);
    }

    [Fact]
    public void A_caller_that_does_not_want_to_pay_for_it_does_not_get_it()
    {
        // Reading the dependencies walks and parses every source file again,
        // which on a large solution is most of the time an assessment takes.
        var assessment = Assess(dependencies: false);

        Assert.Empty(assessment.Uses);
        Assert.DoesNotContain("What you actually use of what is going away", Report(dependencies: false));
    }
}
