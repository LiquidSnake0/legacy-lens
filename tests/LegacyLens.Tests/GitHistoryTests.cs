using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// The parser is tested against captured git output rather than a real
/// repository: creating commits in a test is slow, and the thing worth pinning
/// is how the text is read.
/// </summary>
public class GitHistoryTests
{
    private const char Separator = GitHistory.UnitSeparator;

    private static string Log(params (string Author, string Date, string[] Files)[] commits) =>
        string.Join("\n", commits.Select(c =>
            $"{Separator}{c.Author}{Separator}{c.Date}\n{string.Join("\n", c.Files)}\n"));

    [Fact]
    public void Counts_one_commit_per_file_touched()
    {
        var churn = GitHistory.Parse(Log(
            ("Ada", "2026-08-01T10:00:00+00:00", ["src/A.cs", "src/B.cs"]),
            ("Ada", "2026-07-01T10:00:00+00:00", ["src/A.cs"])));

        Assert.Equal(2, churn["src/A.cs"].Commits);
        Assert.Equal(1, churn["src/B.cs"].Commits);
    }

    [Fact]
    public void Counts_distinct_authors()
    {
        var churn = GitHistory.Parse(Log(
            ("Ada", "2026-08-01T10:00:00+00:00", ["src/A.cs"]),
            ("Grace", "2026-07-01T10:00:00+00:00", ["src/A.cs"]),
            ("Ada", "2026-06-01T10:00:00+00:00", ["src/A.cs"])));

        Assert.Equal(3, churn["src/A.cs"].Commits);
        Assert.Equal(2, churn["src/A.cs"].Authors);
    }

    [Fact]
    public void Keeps_the_most_recent_change_date()
    {
        // git log is newest first, so the first date seen for a path wins.
        var churn = GitHistory.Parse(Log(
            ("Ada", "2026-08-01T10:00:00+00:00", ["src/A.cs"]),
            ("Ada", "2020-01-01T10:00:00+00:00", ["src/A.cs"])));

        Assert.Equal(2026, churn["src/A.cs"].LastChange!.Value.Year);
    }

    [Fact]
    public void A_commit_touching_nothing_is_harmless()
    {
        var churn = GitHistory.Parse(Log(
            ("Ada", "2026-08-01T10:00:00+00:00", []),
            ("Ada", "2026-07-01T10:00:00+00:00", ["src/A.cs"])));

        Assert.Single(churn);
    }

    [Fact]
    public void Empty_output_yields_no_churn_rather_than_an_error()
    {
        Assert.Empty(GitHistory.Parse(string.Empty));
    }

    [Fact]
    public void Paths_with_spaces_survive()
    {
        var churn = GitHistory.Parse(Log(
            ("Ada", "2026-08-01T10:00:00+00:00", ["src/Web References/Reference.cs"])));

        Assert.True(churn.ContainsKey("src/Web References/Reference.cs"));
    }

    [Fact]
    public void A_directory_that_is_not_a_repository_is_reported_as_such()
    {
        var report = new GitHistory().Read(Path.GetTempPath());

        // Never Available: saying "nothing changes here" when the truth is
        // "I could not look" is the confident wrong answer this project exists
        // to avoid.
        Assert.NotEqual(HistoryStatus.Available, report.Status);
        Assert.NotNull(report.Explanation);
        Assert.Empty(report.Churn);
    }

    [Fact]
    public void A_subdirectory_gets_paths_named_the_way_the_rest_of_the_analysis_names_them()
    {
        // git names files from the repository root whatever directory it runs
        // in. Analysing src/ then compared "src/A.cs" from the log against
        // "A.cs" from the metrics, matched nothing, and ranked on structure
        // alone while still reporting that history was available. Silent, and
        // wrong in the direction that looks like a working answer.
        var log = string.Join('\n', [
            $"{GitHistory.UnitSeparator}Ada{GitHistory.UnitSeparator}2026-01-05T10:00:00+00:00",
            "src/A.cs",
            "src/nested/B.cs",
        ]);

        var churn = GitHistory.Parse(log, prefix: "src/");

        Assert.True(churn.ContainsKey("A.cs"));
        Assert.True(churn.ContainsKey("nested/B.cs"));
        Assert.False(churn.ContainsKey("src/A.cs"));
    }

    [Fact]
    public void A_file_outside_the_analysed_directory_is_dropped_rather_than_misnamed()
    {
        var log = string.Join('\n', [
            $"{GitHistory.UnitSeparator}Ada{GitHistory.UnitSeparator}2026-01-05T10:00:00+00:00",
            "src/A.cs",
            "docs/README.md",
        ]);

        var churn = GitHistory.Parse(log, prefix: "src/");

        Assert.Single(churn);
        Assert.True(churn.ContainsKey("A.cs"));
    }

    [Fact]
    public void At_the_repository_root_the_paths_are_left_alone()
    {
        var log = string.Join('\n', [
            $"{GitHistory.UnitSeparator}Ada{GitHistory.UnitSeparator}2026-01-05T10:00:00+00:00",
            "src/A.cs",
        ]);

        Assert.True(GitHistory.Parse(log).ContainsKey("src/A.cs"));
    }
}
