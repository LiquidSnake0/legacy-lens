using System.Diagnostics;
using System.Reflection;

namespace LegacyLens.Characterization;

/// <summary>
/// The same comparison, in a process of its own.
///
/// <see cref="Equivalence"/> runs the code it was handed inside the process
/// that asked. In a command that is fine, and <see cref="Observer"/> says so in
/// as many words: a thread that will not stop cannot be killed in .NET, so the
/// per-call timeout abandons the wait rather than the work, and the process
/// this runs in is a short-lived command, which is what makes that acceptable.
///
/// A server is not a short-lived command. The same sentence read against a
/// long-running API says that every method containing a loop whose exit
/// condition was an operator watching a screen leaves a thread behind, for the
/// lifetime of the service, and that the first stack overflow in somebody's
/// legacy code takes the whole thing down with it. Neither is catchable in
/// process. Both end at a process boundary.
///
/// **What this bounds.** A crash takes the child and nothing else. A hang is
/// killed on a deadline, thread and all, and its memory comes back. The
/// assemblies compiled to run the comparison go away at exit rather than
/// relying on a collectible load context to let go. And the code that runs
/// cannot see the state of the process that asked: not its configuration, not
/// its open handles, not its index.
///
/// **What this does not bound, and the reason the setting stays off.** The
/// child is the same user on the same machine. It can read what that user can
/// read, write what that user can write, and open a socket. This is a blast
/// radius, not a sandbox, and calling it one would be the exact kind of
/// overclaim this codebase exists to avoid. <see cref="Equivalence"/> is still
/// only reachable where an operator turned it on.
/// </summary>
public sealed class Detached
{
    /// <summary>
    /// How long the child is given before it is killed.
    ///
    /// Longer than the comparison's own budget rather than equal to it. The
    /// budget is checked between methods, so a single method that never returns
    /// never reaches the check; this is the backstop for exactly that, and a
    /// backstop that fires before the normal path finishes would turn every
    /// slow comparison into an interruption.
    /// </summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// What to run, when it is not this program.
    ///
    /// The first entry is the executable and the rest are the arguments that
    /// come before ours. Present so a test can point this at the real command
    /// and check the real protocol: under a test host, the running process is
    /// the test host, and a child of it would be one too.
    /// </summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>
    /// Settings the child does not inherit.
    ///
    /// It reads two files and prints a report. It has no server to bind, no
    /// model to reach and no clone directory to write into, so it is handed
    /// none of them. This is hygiene rather than containment: it removes what
    /// the child has no use for, and cannot remove what the operating system
    /// gives every process the user runs.
    /// </summary>
    private static readonly string[] NotInherited =
    [
        "ASPNETCORE_URLS", "URLS", "OLLAMA_URL", "CLONE_PATH", "CORS_ORIGIN",
        "ALLOW_RUNNING_CODE",
    ];

    public EquivalenceReport Compare(string beforePath, string afterPath)
    {
        var started = Stopwatch.StartNew();

        ProcessStartInfo start;

        try
        {
            start = Invocation(beforePath, afterPath);
        }
        catch (InvalidOperationException exception)
        {
            return Stopped(started, $"Nothing was checked: this build cannot start a copy of "
                                  + $"itself to run the comparison in. {exception.Message}");
        }

        using var child = new Process { StartInfo = start };

        try
        {
            child.Start();
        }
        catch (Exception exception) when (exception is SystemException or IOException)
        {
            return Stopped(started, "Nothing was checked: the process that runs the comparison "
                                  + $"would not start. {exception.Message}");
        }

        // Started before waiting, and never after. A child that fills a pipe
        // blocks until somebody drains it, so a parent that waits first and
        // reads second deadlocks on exactly the reports worth reading.
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();

        var allowed = (int)Math.Clamp(Deadline.TotalMilliseconds, 1000, int.MaxValue);

        if (!child.WaitForExit(allowed))
        {
            Abandon(child);

            return Stopped(started,
                $"Nothing was checked: the comparison was still running after "
              + $"{Deadline.TotalMinutes:0.#} minute(s) and the process was stopped. Something "
              + "in one of these files does not return, which is a finding about the file "
              + "rather than about the rewrite.");
        }

        // WaitForExit(int) returns when the process ends, which is before its
        // output has finished arriving. The argument-less call is what waits
        // for the pipes, and without it a fast child reports an empty report.
        child.WaitForExit();

        var written = Settled(output);
        var report = Wire.Read(written);

        if (report is not null) return report with { ElapsedMs = started.ElapsedMilliseconds };

        // Nothing readable came back. The exit code says which kind of nothing:
        // a child that ran and printed rubbish is a defect here, and a child
        // that died is the code it was handed.
        var complaint = Settled(errors).Trim();
        var tail = complaint.Length > 0 ? $" It said: {Opening(complaint)}" : string.Empty;

        return Stopped(started, child.ExitCode == 0
            ? $"Nothing was checked: the comparison finished without reporting anything.{tail}"
            : $"Nothing was checked: the process running the comparison ended with code "
            + $"{child.ExitCode}, which is what a stack overflow in the code under test looks "
            + $"like from here.{tail}");
    }

    /// <summary>
    /// The program, its arguments, and an environment stripped of this tool's
    /// own settings.
    ///
    /// The working directory is a temporary one rather than the caller's. Code
    /// under test that writes to a relative path is not unusual in the kind of
    /// codebase this exists for, and where that lands should not be wherever
    /// the server happens to have been started.
    /// </summary>
    internal ProcessStartInfo Invocation(string beforePath, string afterPath)
    {
        var command = Command ?? Self();

        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };

        foreach (var before in command.Skip(1)) start.ArgumentList.Add(before);

        start.ArgumentList.Add("equivalence");
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add(Path.GetFullPath(beforePath));
        start.ArgumentList.Add(Path.GetFullPath(afterPath));

        foreach (var setting in NotInherited) start.Environment.Remove(setting);

        return start;
    }

    /// <summary>
    /// How to run this program again.
    ///
    /// Two shapes, because both are shipped. A published single file is its own
    /// executable and is run directly. A framework-dependent build launched
    /// through the muxer has `dotnet` as its process, and the thing to run is
    /// the assembly it was given, which is why that case has a prefix argument.
    /// </summary>
    private static IReadOnlyList<string> Self()
    {
        var process = Environment.ProcessPath
            ?? throw new InvalidOperationException("This process has no path on disk.");

        if (!Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return [process];

        var entry = Assembly.GetEntryAssembly()?.Location;

        if (string.IsNullOrEmpty(entry))
            throw new InvalidOperationException("It runs under the .NET host with no assembly to name.");

        return [process, entry];
    }

    /// <summary>
    /// Kills the child and everything it started.
    ///
    /// The tree and not just the process: code under test that launched
    /// something of its own would otherwise outlive the run that started it,
    /// which is the failure this whole class exists to prevent, one level down.
    /// </summary>
    private static void Abandon(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is SystemException or IOException)
        {
            // It ended between the deadline and the kill. Nothing left to do.
        }
    }

    /// <summary>
    /// What a stream produced, or nothing, without ever hanging on it.
    ///
    /// The process has already exited by the time this is asked, so a read that
    /// has not finished is a pipe that will not close rather than output still
    /// coming.
    /// </summary>
    private static string Settled(Task<string> reading) =>
        reading.Wait(TimeSpan.FromSeconds(10)) ? reading.Result : string.Empty;

    /// <summary>
    /// The start of what it complained about, and not the end of it.
    ///
    /// Found by watching a real one. A stack overflow prints its name and then
    /// several hundred repeated frames, so the tail of that stream is a frame
    /// from the middle of a recursion, cut mid-word. The cause is in the first
    /// line, every time: the runtime says what happened before it says where.
    /// </summary>
    internal static string Opening(string complaint)
    {
        const int Room = 200;

        var first = complaint
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;

        return first.Length <= Room ? first : first[..Room] + "...";
    }

    private static EquivalenceReport Stopped(Stopwatch started, string why) =>
        new(false, [], [], [], [], started.ElapsedMilliseconds, why);
}
