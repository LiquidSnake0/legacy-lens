namespace LegacyLens.Analysis;

/// <summary>
/// One file, with the reasons it ranked where it did.
///
/// The components travel with the score on purpose. A number alone invites the
/// reader to trust it; the components let them disagree, which is the only way
/// a ranking survives contact with someone who knows the code.
/// </summary>
public record RiskEntry(
    string Path,
    double Score,
    int Complexity,
    int WorstMethodComplexity,
    string? WorstMethod,
    int MaxNesting,
    int CodeLines,
    int Commits,
    int Authors,
    bool Tested,
    IReadOnlyList<string> Reasons);

public record RiskReport(
    IReadOnlyList<RiskEntry> Entries,
    HistoryStatus HistoryStatus,
    string? HistoryNote,
    int GeneratedFilesExcluded);

/// <summary>
/// Ranks files by how much trouble they are likely to cause.
///
/// The premise: a file that is complicated, changes constantly, and has no
/// tests is where the next incident comes from. Every team suspects which files
/// those are. Almost none can name them with evidence.
/// </summary>
public class RiskRanking
{
    /// <summary>
    /// Files below this are left out. A twenty-line class is not where trouble
    /// starts, and including it buries the files that matter.
    /// </summary>
    public int MinimumCodeLines { get; init; } = 100;

    public RiskReport Rank(
        IReadOnlyList<FileMetrics> metrics,
        HistoryReport history,
        IReadOnlySet<string> testedFiles,
        string rootPath)
    {
        var generated = metrics.Count(m => m.IsGenerated);

        var candidates = metrics
            .Where(m => !m.IsGenerated)
            // Tests are excluded rather than ranked. Reporting that a test file
            // is complex and untested is true and useless.
            .Where(m => !m.IsTest)
            .Where(m => m.CodeLines >= MinimumCodeLines)
            .ToList();

        if (candidates.Count == 0)
        {
            return new RiskReport([], history.Status, history.Explanation, generated);
        }

        // Ranks rather than raw values. Complexity and commit counts have no
        // common unit, and any fixed threshold would be right for one codebase
        // and wrong for the next. A rank within this codebase always means the
        // same thing: how this file compares to its neighbours.
        var complexityRank = Rank(candidates, m => m.Complexity);
        var nestingRank = Rank(candidates, m => m.MaxNesting);

        var churnRank = history.Status == HistoryStatus.Available
            ? Rank(candidates, m => Commits(m, history, rootPath))
            : null;

        var entries = candidates.Select(metric =>
        {
            var relative = Relative(metric.Path, rootPath);
            var churn = history.Churn.GetValueOrDefault(relative);
            var tested = testedFiles.Contains(Path.GetFileNameWithoutExtension(metric.Path));

            // The geometric mean of the two ranks, not the arithmetic one. A
            // file has to score high on both to rank high overall: complicated
            // but never touched is not urgent, and touched constantly but
            // trivial is not dangerous. Averaging would let either one alone
            // carry a file to the top.
            var structural = (complexityRank[metric.Path] * 0.75) + (nestingRank[metric.Path] * 0.25);
            var score = churnRank is not null
                ? Math.Sqrt(structural * churnRank[metric.Path])
                : structural;

            // Untested multiplies rather than adds: it makes everything else
            // worse instead of being one more item on a list.
            if (!tested) score *= 1.4;

            return new RiskEntry(
                relative,
                Math.Round(score, 3),
                metric.Complexity,
                metric.WorstMethodComplexity,
                metric.WorstMethod,
                metric.MaxNesting,
                metric.CodeLines,
                churn?.Commits ?? 0,
                churn?.Authors ?? 0,
                tested,
                Explain(metric, churn, tested, history.Status));
        })
        .OrderByDescending(e => e.Score)
        .ToList();

        return new RiskReport(entries, history.Status, history.Explanation, generated);
    }

    private static int Commits(FileMetrics metric, HistoryReport history, string root) =>
        history.Churn.GetValueOrDefault(Relative(metric.Path, root))?.Commits ?? 0;

    private static string Relative(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative;
    }

    /// <summary>
    /// Position of each file within the range of values, from 0 to 1.
    ///
    /// Ties share a rank, and a codebase where every file scores the same
    /// yields zero for all of them rather than dividing by nothing.
    /// </summary>
    private static Dictionary<string, double> Rank(
        IReadOnlyList<FileMetrics> files, Func<FileMetrics, int> value)
    {
        var sorted = files.Select(value).Distinct().OrderBy(v => v).ToList();

        // Every file scoring the same means they are all at the same level, not
        // that they are all at the minimum. Returning zero would collapse the
        // whole ranking to zero once multiplied.
        if (sorted.Count <= 1)
            return files.ToDictionary(f => f.Path, _ => 0.5);

        var positions = sorted
            .Select((v, index) => (v, position: (double)index / (sorted.Count - 1)))
            .ToDictionary(entry => entry.v, entry => entry.position);

        return files.ToDictionary(f => f.Path, f => positions[value(f)]);
    }

    private static List<string> Explain(
        FileMetrics metric, FileChurn? churn, bool tested, HistoryStatus status)
    {
        var reasons = new List<string>();

        if (metric.WorstMethodComplexity >= 20)
        {
            reasons.Add($"{metric.WorstMethod} has a cyclomatic complexity of "
                      + $"{metric.WorstMethodComplexity}: covering its branches would take "
                      + $"{metric.WorstMethodComplexity} tests");
        }

        if (metric.MaxNesting >= 6)
        {
            reasons.Add($"nested {metric.MaxNesting} levels deep");
        }

        if (churn is { Commits: >= 10 })
        {
            reasons.Add($"changed in {churn.Commits} commits");
        }

        if (churn is { Authors: 1, Commits: >= 5 })
        {
            reasons.Add("only ever touched by one person");
        }

        if (!tested)
        {
            reasons.Add("no test file appears to cover it");
        }

        if (status != HistoryStatus.Available)
        {
            reasons.Add("ranked on structure alone: change history unavailable");
        }

        return reasons;
    }
}
