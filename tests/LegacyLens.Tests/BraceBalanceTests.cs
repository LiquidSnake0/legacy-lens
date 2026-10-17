using LegacyLens.Api.Ingestion;

namespace LegacyLens.Tests;

/// <summary>
/// The measurement behind the figure in docs/NEXT.md. What matters is that it
/// counts what it says, names the files that do not balance, and reads the
/// same file the same way twice, since the whole point is comparing runs.
/// </summary>
public class BraceBalanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "legacylens-balance-" + Guid.NewGuid().ToString("N"));

    public BraceBalanceTests()
    {
        Write("src/Balanced.cs", "class A {\n void M() { }\n}\n");
        Write("src/Unbalanced.cs", "class A {\n void M() {\n}\n");
        Write("web/app.js", "function f() { return 1; }\n");
        Write("README.md", "# not measured { \n");
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Counts_the_measured_extensions_and_names_what_does_not_balance()
    {
        var report = BraceBalance.Measure(_root);

        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Balanced);
        Assert.Equal(["/src/Unbalanced.cs"], report.Unbalanced.Select(f => f.Path).ToList());
        Assert.Equal(1, report.Unbalanced.Single().FinalDepth);
    }

    [Fact]
    public void Counts_per_extension()
    {
        var report = BraceBalance.Measure(_root);

        Assert.Equal(
            [(".cs", 2, 1), (".js", 1, 1)],
            report.ByExtension.Select(e => (e.Extension, e.Files, e.Balanced)).ToList());
    }

    [Fact]
    public void Extensions_can_be_chosen()
    {
        var report = BraceBalance.Measure(_root, [".js"]);

        Assert.Equal(["/web/app.js"], report.Files.Select(f => f.Path).ToList());
    }

    [Fact]
    public void Fingerprint_is_the_same_across_runs_and_differs_when_the_reading_differs()
    {
        var first = BraceBalance.Measure(_root).Files.ToDictionary(f => f.Path, f => f.Fingerprint);
        var second = BraceBalance.Measure(_root).Files.ToDictionary(f => f.Path, f => f.Fingerprint);

        Assert.Equal(first, second);
        Assert.NotEqual(first["/src/Balanced.cs"], first["/src/Unbalanced.cs"]);
    }

    [Fact]
    public void Rows_are_one_line_per_file_in_a_stable_order()
    {
        var rows = BraceBalance.Rows(BraceBalance.Measure(_root)).ToList();

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("/src/Balanced.cs\t0\t", rows[0]);
        Assert.StartsWith("/src/Unbalanced.cs\t1\t", rows[1]);
        Assert.StartsWith("/web/app.js\t0\t", rows[2]);
    }

    [Fact]
    public void Summary_ends_with_the_total_and_lists_the_unbalanced()
    {
        var summary = BraceBalance.Summary(BraceBalance.Measure(_root));

        Assert.Contains("total           3 files        2 balanced", summary);
        Assert.Contains("ends at depth   1: /src/Unbalanced.cs", summary);
    }

    [Fact]
    public void A_missing_root_is_an_error_not_an_empty_report()
    {
        Assert.Throws<DirectoryNotFoundException>(() => BraceBalance.Measure(Path.Combine(_root, "nowhere")));
    }
}
