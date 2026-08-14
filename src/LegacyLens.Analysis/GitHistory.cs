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
    string? Explanation = null);

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

        // One commit per record, each followed by the files it touched. The
        // separator is a character that cannot appear in a name or a date.
        var log = Run(repositoryPath, "log",
            $"--since={Months} months ago",
            "--pretty=format:%x1f%an%x1f%aI",
            "--name-only",
            "--no-merges");

        return new HistoryReport(HistoryStatus.Available, Parse(log ?? string.Empty));
    }

    /// <summary>
    /// Byte 0x1F, ASCII's unit separator. Written by git itself via %x1f
    /// rather than placed literally in this source: a control character in a
    /// source file survives some editors and not others, and the failure is
    /// invisible until the parse silently returns nothing.
    /// </summary>
    internal const char UnitSeparator = '\u001F';

    internal static Dictionary<string, FileChurn> Parse(string log)
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
