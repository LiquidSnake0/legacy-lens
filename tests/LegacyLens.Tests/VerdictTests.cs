using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The grid at the top of the report puts a word on each area. A word is an
/// opinion unless the rule behind it is pinned down, so these pin it: which
/// count gives which word, that the count is printed beside it, and that an
/// area nothing was measured about gets no word at all.
/// </summary>
public class VerdictTests
{
    private static ProjectInfo Project(string name, ProjectKind kind) =>
        new(name, $"/src/{name}/{name}.csproj", kind, "v4.8", [], [], 12, 1_000);

    private static Assessment Assessment(
        int untested, int shipping, PackageUse[] packages, int redirects = 0)
    {
        var projects = Enumerable.Range(1, shipping)
            .Select(k => Project($"App{k}", ProjectKind.Library))
            .Append(Project("App.Tests", ProjectKind.Test))
            .ToList();

        var findings = projects.Take(untested)
            .Select(p => new Finding(FindingKind.Untested, p.Name, "No test project references it", ""))
            .ToList();

        var survey = new ModernisationSurvey(
            projects.Select(p => new ProjectModernisation(p.Name, p.Path, false,
                PackageDeclaration.PackagesConfig, "v4.8", [])).ToList(),
            packages,
            redirects);

        return new Assessment("shop", "/repos/shop", new SolutionMap(projects, [], []), findings,
            new RiskReport([], HistoryStatus.Available, null, 0), survey, [],
            [new Limitation("Nothing was compiled", "Read from project files.")], 42);
    }

    private static ReportWriter.Verdict Row(Assessment assessment, string area) =>
        ReportWriter.Verdicts(assessment).Single(v => v.Area == area);

    [Theory]
    [InlineData(0, 8, "broad")]
    [InlineData(2, 8, "gaps")]
    [InlineData(4, 8, "patchy")]
    [InlineData(6, 8, "thin")]
    public void The_word_for_tests_follows_the_share_of_untested_projects(int untested, int shipping, string word)
    {
        var tests = Row(Assessment(untested, shipping, []), "Tests");

        Assert.Equal(word, tests.Word);
        if (untested > 0) Assert.Contains($"{untested} of the {shipping} projects that ship code", tests.Measured);
    }

    [Fact]
    public void Unclassified_packages_are_counted_as_unknown_rather_than_left_out()
    {
        var assessment = Assessment(0, 2,
        [
            new PackageUse("Newtonsoft.Json", ["13.0.1"], 2, Portability.Portable),
            new PackageUse("Acme.Grid", ["2.1.0"], 1, Portability.Unknown),
        ]);

        var row = Row(assessment, "Packages nobody has classified");

        Assert.Equal("unknown", row.Word);
        Assert.StartsWith("1 of 2 distinct packages", row.Measured);
    }

    [Fact]
    public void Redirects_with_one_version_everywhere_are_drift_already_paid_for()
    {
        var row = Row(Assessment(0, 2, [], redirects: 279), "Package versions");

        Assert.Equal("paid for once", row.Word);
        Assert.Contains("279 binding redirects", row.Measured);
    }

    [Fact]
    public void An_area_with_nothing_measured_gets_no_verdict()
    {
        var areas = ReportWriter.Verdicts(Assessment(0, 2, [])).Select(v => v.Area).ToList();

        // No package was declared, so there is nothing to have classified, and
        // no library depends on the web stack, so there is nothing to separate.
        Assert.DoesNotContain("Packages nobody has classified", areas);
        Assert.DoesNotContain("Libraries tied to the web stack", areas);
    }

    [Fact]
    public void The_grid_comes_before_the_summary_and_points_at_the_limits()
    {
        var report = new ReportWriter().Write(Assessment(1, 2, []));

        Assert.True(report.IndexOf("## At a glance", StringComparison.Ordinal)
                  < report.IndexOf("## In short", StringComparison.Ordinal));
        Assert.Contains("| Tests | **patchy** |", report);
        Assert.Contains("**Read it with its limits.** 1 limit of this analysis", report);
    }
}
