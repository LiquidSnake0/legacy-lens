using System.Diagnostics;
using LegacyLens.Characterization;

namespace LegacyLens.Tests;

/// <summary>
/// Comparing behaviour in a process of its own.
///
/// The reason this exists is written in <see cref="Observer"/>, as an
/// assumption rather than a claim: a thread that will not stop cannot be killed
/// in .NET, so the timeout abandons the wait rather than the work, and the
/// process this runs in is a short-lived command, which is what makes that
/// acceptable. A server is not a short-lived command, and the two tests in the
/// middle of this file are that sentence measured from both sides.
///
/// The child is the real command, built beside these tests and launched. A test
/// double would check that this code can talk to itself, which was never the
/// part in doubt.
/// </summary>
public class DetachedTests : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "legacylens-detached-" + Guid.NewGuid().ToString("N"));

    public DetachedTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        // The sentinel goes first. Anything still spinning is waiting on it,
        // and a directory deleted from under a running loop would leave the
        // loop rather than remove it.
        Stop();

        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // A leftover handle is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    private string Sentinel => Path.Combine(_work, "keep-spinning");
    private string Beat => Path.Combine(_work, "beat");

    private void Stop()
    {
        if (!File.Exists(Sentinel)) return;

        File.Delete(Sentinel);

        // Wait for the loop to notice, so nothing is still writing when the
        // next test looks at its own files.
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(10) && Grew(TimeSpan.FromMilliseconds(150)))
        {
        }
    }

    /// <summary>Whether anything is still writing to the beat file.</summary>
    private bool Grew(TimeSpan over)
    {
        var before = Size(Beat);
        Thread.Sleep(over);
        return Size(Beat) > before;
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>
    /// A method that does not return while a file is there.
    ///
    /// Endless for as long as the test needs it to be, and stoppable, so a
    /// suite that has finished leaves nothing behind burning a core. It writes
    /// while it spins, which is what makes "is it still running" a measurement
    /// rather than a guess.
    /// </summary>
    private string Spinner() => $$"""
        public class Slow
        {
            public int Spin(int n)
            {
                while (System.IO.File.Exists(@"{{Sentinel}}"))
                {
                    System.IO.File.AppendAllText(@"{{Beat}}", "x");
                    System.Threading.Thread.Sleep(1);
                }

                return n;
            }
        }
        """;

    private const string Before = """
        public class Pricing
        {
            public int WithTax(int amount)
            {
                if (amount >= 100) return amount + (amount / 10);
                return amount;
            }

            public string Label(string name) => "item:" + name.ToLowerInvariant();
        }
        """;

    private const string After = """
        public class Pricing
        {
            public int WithTax(int amount)
            {
                if (amount > 100) return amount + (amount / 10);
                return amount;
            }

            public string Label(string name) => "item:" + name.ToLowerInvariant();
        }
        """;

    private string Write(string name, string source)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, source);
        return path;
    }

    /// <summary>
    /// The command as it is actually shipped, built beside these tests.
    ///
    /// Named rather than discovered, because under a test host the running
    /// process is the test host: left to find itself, this would launch another
    /// copy of the test runner.
    /// </summary>
    private static IReadOnlyList<string> Child()
    {
        var name = OperatingSystem.IsWindows() ? "LegacyLens.Api.exe" : "LegacyLens.Api";
        var path = Path.Combine(AppContext.BaseDirectory, name);

        Assert.True(File.Exists(path), $"the command has to be built beside the tests: {path}");

        return [path];
    }

    private Detached Runner(TimeSpan? deadline = null) =>
        new() { Command = Child(), Deadline = deadline ?? TimeSpan.FromMinutes(3) };

    [Fact]
    public void The_child_answers_what_this_process_answers()
    {
        // The acceptance test, and the same one M14 used on the single file:
        // the same program has to give the same answer however it was run. A
        // second way of reaching a capability is only worth having while it
        // cannot disagree with the first.
        var here = new Equivalence().Compare(Before, After);

        var there = Runner().Compare(Write("before.cs", Before), Write("after.cs", After));

        Assert.Equal(here.Claim, there.Claim);
        Assert.Equal(here.Verified, there.Verified);
        Assert.Equal(here.Cases, there.Cases);
        Assert.Equal(here.PassedOver, there.PassedOver);

        Assert.Equal(
            here.Methods.Select(m => $"{m.Type}.{m.Signature} {m.Cases} {m.Matched}"),
            there.Methods.Select(m => $"{m.Type}.{m.Signature} {m.Cases} {m.Matched}"));

        Assert.Equal(
            here.Moved.SelectMany(m => m.Divergences).Select(d => $"{d.Arguments}|{d.Before}|{d.After}"),
            there.Moved.SelectMany(m => m.Divergences).Select(d => $"{d.Arguments}|{d.Before}|{d.After}"));
    }

    [Fact]
    public void A_method_that_will_not_stop_is_still_running_here_when_the_call_returns()
    {
        // Half of the reason this milestone exists, measured rather than
        // asserted. The comparison returns, reports that it could not observe
        // the method in time, and the method is still going. In a command that
        // is fine: the process is about to end. This process is a stand-in for
        // one that is not about to end.
        File.WriteAllText(Sentinel, "");

        var report = new Equivalence().Compare(Spinner(), Spinner());

        Assert.False(report.Verified);
        Assert.True(Grew(TimeSpan.FromMilliseconds(400)),
            "the abandoned call is still running in this process after Compare returned");

        Stop();
    }

    [Fact]
    public void And_it_is_not_running_once_the_child_has_gone()
    {
        // The other half, and the whole point. Same file, same abandoned call,
        // and nothing is left running afterwards, because what was running was
        // a process rather than a thread.
        File.WriteAllText(Sentinel, "");

        var source = Write("spin.cs", Spinner());
        var report = Runner().Compare(source, source);

        Assert.False(report.Verified);
        Assert.False(Grew(TimeSpan.FromMilliseconds(400)),
            "nothing is still writing, because the process that was doing it has ended");
    }

    [Fact]
    public void A_run_that_outlasts_its_deadline_is_an_interruption_and_never_a_pass()
    {
        File.WriteAllText(Sentinel, "");

        var source = Write("spin.cs", Spinner());
        var report = Runner(TimeSpan.FromSeconds(1)).Compare(source, source);

        Assert.False(report.Ran);
        Assert.False(report.Verified);
        Assert.Empty(report.Methods);
        Assert.Contains("still running", report.Claim);
        Assert.Contains("stopped", report.Claim);

        Assert.False(Grew(TimeSpan.FromMilliseconds(400)), "the killed child took its work with it");
    }

    [Fact]
    public void A_crash_in_the_code_under_test_comes_back_as_a_report()
    {
        // Uncatchable in process: a stack overflow ends the runtime where it
        // happens, with no exception anybody can handle. Run in the server, it
        // takes the service down. Run here, it ends a child and produces a
        // sentence.
        var source = Write("deep.cs", """
            public class Deep
            {
                public int Recurse(int n) => Recurse(n + 1) + 1;
            }
            """);

        var report = Runner().Compare(source, source);

        Assert.False(report.Ran);
        Assert.False(report.Verified);
        Assert.Contains("Nothing was checked", report.Claim);
        Assert.Contains("stack overflow", report.Claim);
    }

    [Fact]
    public void A_path_that_is_not_there_comes_back_as_a_report_rather_than_an_exception()
    {
        var report = Runner().Compare(
            Path.Combine(_work, "absent.cs"), Write("after.cs", After));

        Assert.False(report.Ran);
        Assert.False(report.Verified);
        Assert.Contains("no such file", report.Claim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_interruption_is_never_read_as_a_compilation_failure()
    {
        // The claim for a file that does not compile is a different sentence
        // and a different finding. Folding one into the other would tell
        // somebody their original is broken when their machine was busy.
        var report = Runner(TimeSpan.FromSeconds(1)).Compare(
            Write("before.cs", Before), Write("after.cs", After));

        Assert.False(report.Verified);
        Assert.Empty(report.BeforeErrors);
        Assert.Empty(report.AfterErrors);
        Assert.DoesNotContain("does not compile", report.Claim);
    }

    [Fact]
    public void The_child_is_handed_none_of_this_tools_own_settings()
    {
        // It reads two files and prints a report. A child that inherited where
        // to bind, which model to reach and which directory to clone into would
        // be carrying the server's job into a process that does not have one.
        var start = new Detached().Invocation(Write("a.cs", Before), Write("b.cs", After));

        foreach (var setting in new[]
                 { "ASPNETCORE_URLS", "URLS", "OLLAMA_URL", "CLONE_PATH", "CORS_ORIGIN", "ALLOW_RUNNING_CODE" })
        {
            Assert.False(start.Environment.ContainsKey(setting), $"{setting} does not cross over");
        }
    }

    [Fact]
    public void The_child_writes_somewhere_other_than_where_the_server_was_started()
    {
        // Code under test that writes to a relative path is ordinary in the
        // kind of codebase this exists for. Where that lands should not be the
        // directory a service happens to have been launched from.
        var start = new Detached().Invocation(Write("a.cs", Before), Write("b.cs", After));

        Assert.Equal(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(start.WorkingDirectory).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void The_paths_reach_the_child_whole()
    {
        // Built as a list rather than a command line. A directory with a space
        // in it is not exotic, and a path split in half arrives as two
        // arguments that name nothing.
        var spaced = Path.Combine(_work, "two words");
        Directory.CreateDirectory(spaced);

        var before = Path.Combine(spaced, "before.cs");
        File.WriteAllText(before, Before);

        var start = new Detached().Invocation(before, Write("after.cs", After));

        Assert.Contains("equivalence", start.ArgumentList);
        Assert.Contains("--json", start.ArgumentList);
        Assert.Contains(before, start.ArgumentList);
    }
}
