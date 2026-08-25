using LegacyLens.Analysis;

namespace LegacyLens.Tests;

public class RiskRankingTests
{
    private static FileMetrics File(
        string name, int complexity = 10, int nesting = 2, int lines = 500,
        bool generated = false, bool test = false) =>
        new($"/src/{name}", lines, lines, 1, 5, complexity, complexity, "M", nesting, generated, test);

    private static HistoryReport Available(params (string Path, int Commits, int Authors)[] churn) =>
        new(HistoryStatus.Available,
            churn.ToDictionary(c => c.Path,
                               c => new FileChurn(c.Path, c.Commits, c.Authors, null)));

    private static readonly HistoryReport NoHistory =
        new(HistoryStatus.ShallowClone, new Dictionary<string, FileChurn>(), "Shallow clone.");

    private static RiskReport Rank(FileMetrics[] files, HistoryReport history, params string[] tested) =>
        new RiskRanking().Rank(files, history, tested.ToHashSet(StringComparer.OrdinalIgnoreCase), "/");

    [Fact]
    public void Generated_files_are_excluded_and_counted()
    {
        var report = Rank([File("Real.cs"), File("Proxy.cs", generated: true)], NoHistory);

        Assert.Single(report.Entries);
        Assert.Equal("src/Real.cs", report.Entries[0].Path);
        Assert.Equal(1, report.GeneratedFilesExcluded);
    }

    [Fact]
    public void Test_files_are_excluded()
    {
        // Ranking a test file by how risky it is, and noting that nothing
        // tests it, is true and useless.
        var report = Rank([File("Real.cs"), File("RealTests.cs", test: true)], NoHistory);
        Assert.Single(report.Entries);
    }

    [Fact]
    public void Small_files_are_left_out()
    {
        var report = Rank([File("Big.cs", lines: 500), File("Tiny.cs", lines: 20)], NoHistory);
        Assert.Single(report.Entries);
    }

    [Fact]
    public void Complexity_and_churn_must_both_be_high_to_rank_high()
    {
        // The point of the geometric mean. A file that is complicated but
        // never touched is not urgent; one touched constantly but trivial is
        // not dangerous. An average would let either alone reach the top.
        var report = Rank(
            [File("Both.cs", complexity: 100, nesting: 8),
             File("ComplexOnly.cs", complexity: 100, nesting: 8),
             File("ChurnOnly.cs", complexity: 1, nesting: 1)],
            Available(("src/Both.cs", 50, 3), ("src/ComplexOnly.cs", 1, 1), ("src/ChurnOnly.cs", 50, 3)),
            "Both", "ComplexOnly", "ChurnOnly");

        Assert.Equal("src/Both.cs", report.Entries[0].Path);
    }

    [Fact]
    public void A_file_nobody_has_touched_is_lowered_rather_than_erased()
    {
        // Found by running the ranking over Orchard. The most complex untested
        // file in the whole solution, 116 branches over 338 lines with no test
        // near it, came last of 458 with a score of exactly zero, because
        // nobody had committed to it in two years.
        //
        // A zero factor in a geometric mean does not lower a score, it deletes
        // it, and on inherited code most files have no churn at all. Which is
        // the same reasoning Rank already applies when every value ties.
        var report = Rank(
            [File("Quiet.cs", complexity: 100, nesting: 8),
             File("Busy.cs", complexity: 1, nesting: 1)],
            Available(("src/Quiet.cs", 0, 0), ("src/Busy.cs", 50, 3)),
            "Quiet", "Busy");

        var quiet = report.Entries.Single(e => e.Path == "src/Quiet.cs");

        Assert.True(quiet.Score > 0);
        Assert.Equal("src/Quiet.cs", report.Entries[0].Path);
    }

    [Fact]
    public void Churn_still_separates_two_files_of_the_same_shape()
    {
        // The floor lowers, it does not flatten. Two identical files still
        // rank by how much each one moves.
        var report = Rank(
            [File("Hot.cs", complexity: 50, nesting: 4),
             File("Cold.cs", complexity: 50, nesting: 4)],
            Available(("src/Hot.cs", 40, 3), ("src/Cold.cs", 0, 0)),
            "Hot", "Cold");

        Assert.Equal("src/Hot.cs", report.Entries[0].Path);
        Assert.True(report.Entries[0].Score > report.Entries[1].Score);
    }

    [Fact]
    public void An_untested_file_outranks_an_identical_tested_one()
    {
        var report = Rank(
            [File("Covered.cs", complexity: 50), File("Bare.cs", complexity: 50)],
            NoHistory,
            "Covered");

        Assert.Equal("src/Bare.cs", report.Entries[0].Path);
        Assert.True(report.Entries[0].Score > report.Entries[1].Score);
    }

    [Fact]
    public void Without_history_the_ranking_says_so_in_every_entry()
    {
        // Silently ranking on structure alone would read as a complete answer.
        var report = Rank([File("A.cs", complexity: 40)], NoHistory);

        Assert.Equal(HistoryStatus.ShallowClone, report.HistoryStatus);
        Assert.NotNull(report.HistoryNote);
        Assert.Contains(report.Entries[0].Reasons, r => r.Contains("history unavailable"));
    }

    [Fact]
    public void Every_entry_carries_the_numbers_behind_its_score()
    {
        // A score on its own asks to be trusted. The components let a reader
        // who knows the code disagree, which is the only way a ranking holds up.
        var report = Rank([File("A.cs", complexity: 40, nesting: 7, lines: 800)],
                          Available(("src/A.cs", 12, 3)));

        var entry = report.Entries[0];
        Assert.Equal(40, entry.Complexity);
        Assert.Equal(7, entry.MaxNesting);
        Assert.Equal(800, entry.CodeLines);
        Assert.Equal(12, entry.Commits);
        Assert.Equal(3, entry.Authors);
    }

    [Fact]
    public void A_complex_method_is_explained_in_terms_of_tests_needed()
    {
        var report = Rank([File("A.cs", complexity: 45)], NoHistory);
        Assert.Contains(report.Entries[0].Reasons, r => r.Contains("45 tests"));
    }

    [Fact]
    public void A_single_author_on_a_much_changed_file_is_called_out()
    {
        // Bus factor of one. No structural metric reveals it.
        var report = Rank([File("A.cs", complexity: 30)], Available(("src/A.cs", 20, 1)), "A");
        Assert.Contains(report.Entries[0].Reasons, r => r.Contains("one person"));
    }

    [Fact]
    public void Identical_files_do_not_divide_by_zero()
    {
        var report = Rank([File("A.cs", complexity: 10), File("B.cs", complexity: 10)], NoHistory);
        Assert.All(report.Entries, e => Assert.False(double.IsNaN(e.Score)));
    }

    [Fact]
    public void An_empty_codebase_yields_an_empty_report_rather_than_an_error()
    {
        var report = Rank([], NoHistory);
        Assert.Empty(report.Entries);
    }
}
