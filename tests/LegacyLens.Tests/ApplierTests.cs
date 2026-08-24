using System.Diagnostics;
using LegacyLens.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace LegacyLens.Tests;

/// <summary>
/// Putting a patch on a branch, and leaving the repository where it was found.
///
/// The refusals matter more than the applications. A tool that writes into
/// somebody's working tree, or commits their work in progress alongside its
/// own, is a tool they stop trusting with their repository, and one bad
/// experience is enough.
/// </summary>
public class ApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-apply-{Guid.NewGuid():N}");

    public ApplierTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static Applier Applier() => new(NullLogger<Applier>.Instance);

    private bool Git(params string[] arguments)
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

        try
        {
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string Read(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        return output.Trim();
    }

    /// <summary>A repository with one committed file, and a patch that edits it.</summary>
    private bool Repository()
    {
        if (!Git("init", "-b", "main")) return false;

        File.WriteAllText(Path.Combine(_root, "thing.txt"), "before\n");

        return Git("add", "-A") && Git("commit", "-m", "first");
    }

    private const string Patch =
        "diff --git a/thing.txt b/thing.txt\n" +
        "--- a/thing.txt\n" +
        "+++ b/thing.txt\n" +
        "@@ -1 +1 @@\n" +
        "-before\n" +
        "+after\n";

    /* ---- applying ---- */

    [Fact]
    public void A_patch_lands_on_a_branch_of_its_own()
    {
        if (!Repository()) return;

        var landed = Applier().Apply(_root, "packages", Patch);

        Assert.True(landed.Applied, string.Join("; ", landed.Refusals));
        Assert.StartsWith("legacy-lens/packages-", landed.Branch);
        Assert.NotNull(landed.Commit);
    }

    [Fact]
    public void The_reader_is_left_on_the_branch_they_started_on()
    {
        // Leaving somebody on a branch they did not choose is the surprise that
        // makes people stop trusting a tool with their repository.
        if (!Repository()) return;

        Applier().Apply(_root, "packages", Patch);

        Assert.Equal("main", Read("rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("before\n", File.ReadAllText(Path.Combine(_root, "thing.txt")));
    }

    [Fact]
    public void The_change_is_on_the_branch_it_made()
    {
        if (!Repository()) return;

        var landed = Applier().Apply(_root, "packages", Patch);

        Assert.Contains("after", Read("show", $"{landed.Branch}:thing.txt"));
    }

    [Fact]
    public void It_says_how_to_read_it_keep_it_and_drop_it()
    {
        if (!Repository()) return;

        var landed = Applier().Apply(_root, "packages", Patch);

        Assert.Contains(landed.Notes, n => n.StartsWith("Read it with: git diff"));
        Assert.Contains(landed.Notes, n => n.StartsWith("Drop it with: git branch -D"));
        Assert.Contains(landed.Notes, n => n.Contains("Nothing was pushed"));
    }

    [Fact]
    public void Nothing_is_pushed()
    {
        // The one thing a tool must not decide for somebody: where their code
        // goes. There is no remote here, and applying still succeeds.
        if (!Repository()) return;

        Assert.True(Applier().Apply(_root, "packages", Patch).Applied);
        Assert.Equal(string.Empty, Read("remote"));
    }

    /* ---- refusing ---- */

    [Fact]
    public void A_directory_that_is_not_a_repository_is_refused()
    {
        // Without git there is no branch to isolate this on and no way back.
        Directory.CreateDirectory(Path.Combine(_root, "plain"));

        var landed = Applier().Apply(Path.Combine(_root, "plain"), "packages", Patch);

        Assert.False(landed.Applied);
        Assert.Contains(landed.Refusals, r => r.Contains("not a git work tree"));
    }

    [Fact]
    public void Uncommitted_work_is_refused_rather_than_swept_into_the_commit()
    {
        if (!Repository()) return;

        File.WriteAllText(Path.Combine(_root, "mine.txt"), "work in progress\n");

        var landed = Applier().Apply(_root, "packages", Patch);

        Assert.False(landed.Applied);
        Assert.Contains(landed.Refusals, r => r.Contains("uncommitted changes"));

        // And it is still there, untouched.
        Assert.True(File.Exists(Path.Combine(_root, "mine.txt")));
    }

    [Fact]
    public void A_patch_that_will_not_apply_leaves_no_branch_behind()
    {
        // Checked before anything is created, so a failure costs nothing to
        // clean up.
        if (!Repository()) return;

        var landed = Applier().Apply(_root, "packages",
            "diff --git a/absent.txt b/absent.txt\n" +
            "--- a/absent.txt\n+++ b/absent.txt\n@@ -1 +1 @@\n-x\n+y\n");

        Assert.False(landed.Applied);
        Assert.Contains(landed.Refusals, r => r.Contains("will not apply"));
        Assert.DoesNotContain("legacy-lens/", Read("branch", "--list"));
    }

    [Fact]
    public void An_empty_patch_is_refused_rather_than_committed_as_nothing()
    {
        if (!Repository()) return;

        var landed = Applier().Apply(_root, "packages", "   ");

        Assert.False(landed.Applied);
        Assert.Contains(landed.Refusals, r => r.Contains("no patch"));
    }

    [Fact]
    public void Two_applications_do_not_collide_on_a_branch_name()
    {
        if (!Repository()) return;

        var first = Applier().Apply(_root, "packages", Patch);
        Assert.True(first.Applied);

        // The second is against the same base, so the same patch still applies.
        var second = Applier().Apply(_root, "sdk", Patch);

        Assert.True(second.Applied, string.Join("; ", second.Refusals));
        Assert.NotEqual(first.Branch, second.Branch);
    }

    [Fact]
    public void The_commit_message_says_where_the_change_came_from()
    {
        if (!Repository()) return;

        var landed = Applier().Apply(_root, "packages", Patch);
        var message = Read("log", "-1", "--format=%B", landed.Branch!);

        Assert.Contains("Move package declarations", message);
        Assert.Contains("a person read", message);

        // No fingerprint of a machine having written it on somebody's behalf.
        Assert.DoesNotContain("Co-Authored-By", message);
    }
}
