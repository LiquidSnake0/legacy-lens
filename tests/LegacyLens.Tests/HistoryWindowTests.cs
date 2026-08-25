using System.Diagnostics;
using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// Which stretch of history the churn is counted over.
///
/// A sliding window is calibrated for code that is alive, and legacy code has
/// stopped changing by definition. Measured on Orchard: 11,677 commits, of
/// which 102 fall inside two years. The ranking that came out of that window
/// put a test-support file at the top of the danger list; the same ranking over
/// the full history named the core of the CMS.
///
/// Against a real repository rather than captured output, because what is being
/// pinned here is which commands git is asked to run, not how its text is read.
/// </summary>
public class HistoryWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-history-{Guid.NewGuid():N}");

    public HistoryWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private bool Git(DateTimeOffset? when, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        start.Environment["GIT_AUTHOR_NAME"] = "Test";
        start.Environment["GIT_AUTHOR_EMAIL"] = "test@localhost";
        start.Environment["GIT_COMMITTER_NAME"] = "Test";
        start.Environment["GIT_COMMITTER_EMAIL"] = "test@localhost";

        if (when is not null)
        {
            var stamp = when.Value.ToString("o");
            start.Environment["GIT_AUTHOR_DATE"] = stamp;
            start.Environment["GIT_COMMITTER_DATE"] = stamp;
        }

        try
        {
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Commits one file, dated whenever it is told.</summary>
    private bool Commit(string name, string content, DateTimeOffset when)
    {
        File.WriteAllText(Path.Combine(_root, name), content);
        return Git(when, "add", "-A") && Git(when, "commit", "-m", $"touch {name}");
    }

    private bool Repository() => Git(null, "init", "-b", "main");

    [Fact]
    public void A_repository_still_being_worked_on_is_read_over_the_window()
    {
        if (!Repository()) return;

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) Commit("a.cs", $"// {i}", now.AddDays(-i));

        var report = new GitHistory().Read(_root);

        Assert.Equal(HistoryStatus.Available, report.Status);
        Assert.Equal("the last 24 months", report.Window);
        Assert.Null(report.Explanation);
    }

    [Fact]
    public void A_repository_that_stopped_changing_is_read_whole_and_says_so()
    {
        // The window would hold one commit of ten here, which describes the
        // tail of this repository rather than the repository.
        if (!Repository()) return;

        var old = DateTimeOffset.UtcNow.AddYears(-8);
        for (var i = 0; i < 9; i++) Commit("old.cs", $"// {i}", old.AddDays(i));

        Commit("recent.cs", "// one", DateTimeOffset.UtcNow);

        var report = new GitHistory().Read(_root);

        Assert.Equal("the full history", report.Window);
        Assert.Contains("stopped changing", report.Explanation);

        // And the churn is the whole of it, not the one recent commit.
        Assert.Equal(9, report.Churn["old.cs"].Commits);
    }

    [Fact]
    public void Reading_the_whole_history_is_what_puts_the_old_work_back_in_the_ranking()
    {
        // The finding this exists for. Inside the window `old.cs` has no churn
        // at all, and a file with no churn used to score zero however
        // complicated it was.
        if (!Repository()) return;

        var old = DateTimeOffset.UtcNow.AddYears(-8);
        for (var i = 0; i < 9; i++) Commit("old.cs", $"// {i}", old.AddDays(i));

        Commit("recent.cs", "// one", DateTimeOffset.UtcNow);

        var windowed = new GitHistory { Months = 1 }.Read(_root);

        Assert.True(windowed.Churn.TryGetValue("old.cs", out var churn));
        Assert.True(churn!.Commits > 0);
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_says_which_it_is()
    {
        var report = new GitHistory().Read(_root);

        Assert.Equal(HistoryStatus.NotARepository, report.Status);
        Assert.Null(report.Window);
    }
}
