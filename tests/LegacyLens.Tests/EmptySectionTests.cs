using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// A heading over nothing is a finding that is not there.
///
/// The report is built out of guards: print this section if there is something
/// in it. Mutation testing found that **none of them was checked**. Turning
/// `if (found.Count > 0)` into `>= 0` prints the section empty, and 114
/// surviving mutants across the report and the repair steps say nobody would
/// have noticed.
///
/// M24 argued the case for one of them, that a heading over an empty list reads
/// as *there is nothing to decide*, which is never true. It got one test. There
/// are fifteen sections.
///
/// The fixture is a solution with one project and nothing else: it is a real
/// report, a hundred and twenty-six lines of it, and most of what this tool can
/// say does not apply to it. That is what makes it the right fixture.
/// </summary>
public class EmptySectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-empty-{Guid.NewGuid():N}");

    public EmptySectionTests()
    {
        var app = Path.Combine(_root, "App");
        Directory.CreateDirectory(app);

        File.WriteAllText(Path.Combine(app, "App.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" DefaultTargets="Build"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFrameworkVersion>v4.6.1</TargetFrameworkVersion>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(app, "Thing.cs"), "public class Thing { }");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Report() => new ReportWriter().Write(new Assessor().Assess(_root));

    [Fact]
    public void The_report_is_real_before_anything_is_asserted_about_what_it_lacks()
    {
        // Without this the rest passes on an empty string, which is the way a
        // test about absence fails to test anything.
        var report = Report();

        Assert.Contains("## In short", report);
        Assert.Contains("## What this is", report);
        Assert.True(report.Split('\n').Length > 50, "a real document, not a stub");
    }

    [Theory]
    [InlineData("## What you actually use of what is going away")]
    [InlineData("## What there is to decide about the frameworks themselves")]
    [InlineData("## What only you can decide")]
    public void A_section_with_nothing_in_it_is_not_printed(string heading)
    {
        // This solution uses no catalogued package, so it has no dependency to
        // size, no framework question to face and nothing anybody has to decide
        // about a framework. Each of those sections would otherwise appear with
        // its introduction and no rows.
        Assert.DoesNotContain(heading, Report());
    }

    [Theory]
    [InlineData("Classify the packages nobody has checked yet")]
    [InlineData("Retire the hand-written binding redirects")]
    [InlineData("Decide what happens to the code built on System.Web")]
    [InlineData("Separate the libraries that depend on the web stack")]
    [InlineData("Put tests around the files most likely to break")]
    public void A_repair_step_with_no_evidence_is_not_offered(string step)
    {
        // A step in an ordered plan is a promise that there is work there. One
        // with an empty evidence list is worse than a missing step: somebody
        // schedules it.
        Assert.DoesNotContain(step, Report());
    }

    [Fact]
    public void And_the_steps_that_do_apply_are_still_there()
    {
        // The other half. A guard that never fires would pass every assertion
        // above and produce a document that says nothing at all.
        var report = Report();

        Assert.Contains("Convert the project files that can take the modern format", report);
        Assert.Contains("Confirm whether the unreferenced projects are dead", report);
    }

    [Fact]
    public void Every_step_offered_names_what_it_is_for()
    {
        // Read off the assessment rather than the prose, because a step with an
        // empty evidence list is exactly what the guards exist to prevent and
        // exactly what a reader cannot see in a rendered document.
        foreach (var step in new Assessor().Assess(_root).Repairs)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Title), "a step with no title");
            Assert.False(string.IsNullOrWhiteSpace(step.Size), $"{step.Title} says nothing about its size");
            Assert.NotEmpty(step.Evidence);
        }
    }
}
