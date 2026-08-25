using System.Diagnostics;

namespace LegacyLens.Analysis;

/// <summary>How often a file changed, and how many people touched it.</summary>
public record FileChurn(
    string Path,
    int Commits,
    /// <summary>
    /// Distinct authors. One author on a file everyone depends on is a
    /// bus factor of one, and no amount of complexity metrics reveals it.
    /// </summary>
    int Authors,
    DateTimeOffset? LastChange);

/// <summary>
/// Why the history could not be read, when it could not be.
///
/// Reported rather than swallowed. A repository with no history and a
/// repository where nothing ever changes produce the same empty result, and
/// telling a reader "nothing changes here" when the truth is "I could not look"
/// is the kind of confident wrong answer this whole project exists to avoid.
/// </summary>
public enum HistoryStatus
{
    Available,
    NotARepository,
    /// <summary>A shallow clone. The commits simply are not there to read.</summary>
    ShallowClone,
    GitUnavailable,
}

public record HistoryReport(
    HistoryStatus Status,
    IReadOnlyDictionary<string, FileChurn> Churn,
    string? Explanation = null,
    /// <summary>
    /// Which stretch of history the churn was counted over, in words.
    ///
    /// Reported because it is not always the one that was asked for: a
    /// repository that has stopped changing has almost nothing inside a recent
    /// window, and reading only that window ranks its tail rather than its
    /// life. A reader comparing two reports has to be able to see which was
    /// used.
    /// </summary>
    string? Window = null);

/// <summary>
/// Reads change history by shelling out to git.
///
/// A library like LibGit2Sharp would avoid the process, at the cost of a native
/// dependency per platform. git is already on any machine where somebody is
/// reading source code, and its output format has been stable for years.
/// </summary>
public class GitHistory
{
    public int Months { get; init; } = 24;

    public HistoryReport Read(string repositoryPath)
    {
        var check = Run(repositoryPath, "rev-parse", "--is-inside-work-tree");
        if (check is null)
        {
            return new HistoryReport(HistoryStatus.GitUnavailable, new Dictionary<string, FileChurn>(),
                "git is not installed, or not on PATH.");
        }

        if (check.Trim() != "true")
        {
            return new HistoryReport(HistoryStatus.NotARepository, new Dictionary<string, FileChurn>(),
                $"{repositoryPath} is not inside a git work tree.");
        }

        if (Run(repositoryPath, "rev-parse", "--is-shallow-repository")?.Trim() == "true")
        {
            return new HistoryReport(HistoryStatus.ShallowClone, new Dictionary<string, FileChurn>(),
                "Shallow clone: the history is not present. Re-clone without --depth "
              + "to rank by change frequency.");
        }

        // Where the analysed directory sits inside the repository, empty at the
        // root. git names files from the repository root whatever directory it
        // is run in, so without this every path from the log misses every path
        // from the metrics, and the ranking silently falls back to structure
        // alone while still reporting that history was available.
        var prefix = (Run(repositoryPath, "rev-parse", "--show-prefix") ?? string.Empty).Trim();

        // One commit per record, each followed by the files it touched. The
        // separator is a character that cannot appear in a name or a date.
        //
        var recent = Log(repositoryPath, $"--since={Months} months ago") ?? string.Empty;

        // Whether that window describes this repository or only its tail.
        //
        // A sliding window is calibrated for code that is alive, and legacy
        // code has stopped changing by definition. Measured on Orchard: 11,873
        // commits, of which 119 fall inside two years. The ranking that came
        // out put a test-support file at the top of the danger list, while the
        // same ranking over the full history named the core of the CMS, which
        // is what anyone who worked on it would say. One file in common out of
        // six.
        //
        // So the window is used when the repository was alive during it, and
        // the whole history when it was not. Adapting to the repository rather
        // than moving an arbitrary line, which would only be wrong somewhere
        // else.
        var inWindow = Commits(recent);
        var overall = Total(repositoryPath);

        if (overall > 0 && inWindow < overall * LivelyShare)
        {
            var everything = Log(repositoryPath, null) ?? string.Empty;

            return new HistoryReport(
                HistoryStatus.Available,
                Parse(everything, prefix),
                $"Only {inWindow} of this directory's {overall} commits fall in the last "
                + $"{Months} months, so the whole history was read instead. A window that "
                + "recent describes a codebase that has stopped changing by its tail.",
                "the full history");
        }

        return new HistoryReport(
            HistoryStatus.Available, Parse(recent, prefix), null, $"the last {Months} months");
    }

    /// <summary>
    /// How much of a repository's history has to fall inside the window for it
    /// to be describing the repository rather than its tail.
    ///
    /// A fifth. Chosen to separate a codebase that is worked on from one that
    /// is archived, which are an order of magnitude apart rather than a few per
    /// cent: this repository has all of its history inside two years, and
    /// Orchard has one per cent of its own.
    /// </summary>
    private const double LivelyShare = 0.2;

    /// <summary>
    /// One commit per record, each followed by the files it touched.
    ///
    /// The trailing pathspec limits the log to this directory: on a large
    /// estate analysed one project at a time, reading the whole repository's
    /// history for each is work thrown away.
    /// </summary>
    private string? Log(string repositoryPath, string? since)
    {
        var arguments = new List<string> { "log" };

        if (since is not null) arguments.Add(since);

        arguments.AddRange([
            "--pretty=format:%x1f%an%x1f%aI",
            "--name-only",
            "--no-merges",
            "--",
            ".",
        ]);

        return Run(repositoryPath, arguments.ToArray());
    }

    /// <summary>Commits in a log, counted from its records rather than by asking git twice.</summary>
    private static int Commits(string log) => log.Count(c => c == UnitSeparator) / 2;

    /// <summary>
    /// Every commit this directory has, or zero when git will not say.
    ///
    /// Zero rather than a guess: without a denominator the comparison cannot be
    /// made, and the window is then used as asked for rather than second-guessed.
    /// </summary>
    private int Total(string repositoryPath) =>
        int.TryParse(Run(repositoryPath, "rev-list", "--count", "HEAD", "--", ".")?.Trim(), out var count)
            ? count
            : 0;

    /// <summary>
    /// Byte 0x1F, ASCII's unit separator. Written by git itself via %x1f
    /// rather than placed literally in this source: a control character in a
    /// source file survives some editors and not others, and the failure is
    /// invisible until the parse silently returns nothing.
    /// </summary>
    internal const char UnitSeparator = '\u001F';

    /// <summary>
    /// Turns git's output into churn per file.
    ///
    /// <paramref name="prefix"/> is where the analysed directory sits inside
    /// the repository, so the paths come back named the way the rest of the
    /// analysis names them. Anything outside it is dropped rather than kept
    /// under a name nothing will ever match.
    /// </summary>
    internal static Dictionary<string, FileChurn> Parse(string log, string prefix = "")
    {
        var commits = new Dictionary<string, int>(StringComparer.Ordinal);
        var authors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        string? author = null;
        DateTimeOffset? when = null;

        foreach (var line in log.Split('\n'))
        {
            if (line.StartsWith(UnitSeparator))
            {
                var parts = line[1..].Split(UnitSeparator);
                author = parts[0];
                when = parts.Length > 1 && DateTimeOffset.TryParse(parts[1], out var parsed)
                    ? parsed
                    : null;
                continue;
            }

            var path = line.Trim();
            if (path.Length == 0 || author is null) continue;

            if (prefix.Length > 0)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                path = path[prefix.Length..];
            }

            commits[path] = commits.GetValueOrDefault(path) + 1;

            if (!authors.TryGetValue(path, out var names))
                authors[path] = names = new HashSet<string>(StringComparer.Ordinal);
            names.Add(author);

            // The log is newest first, so the first date seen for a path is the
            // most recent one.
            if (when is not null && !last.ContainsKey(path)) last[path] = when.Value;
        }

        return commits.ToDictionary(
            entry => entry.Key,
            entry => new FileChurn(
                entry.Key,
                entry.Value,
                authors[entry.Key].Count,
                last.TryGetValue(entry.Key, out var date) ? date : null),
            StringComparer.Ordinal);
    }

    private static string? Run(string workingDirectory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // A non-zero exit is a legitimate answer here: "not a repository"
            // arrives that way, and the caller decides what it means.
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                                   or InvalidOperationException)
        {
            return null;
        }
    }
}
