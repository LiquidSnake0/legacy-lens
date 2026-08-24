namespace LegacyLens.Analysis;

/// <summary>
/// Runs the whole structural analysis over a directory.
///
/// The pieces are usable on their own, but assembling them correctly involves
/// decisions a caller should not have to rediscover: which files to skip, how
/// to tell whether a type is covered by a test, that measurement parallelises
/// and history does not.
/// </summary>
public class SolutionAnalysis
{
    private static readonly string[] SkipDirectories =
        [".git", "bin", "obj", "node_modules", "packages", ".vs"];

    public int MinimumCodeLines { get; init; } = 100;
    public int HistoryMonths { get; init; } = 24;

    public RiskReport Analyse(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        var files = SourceFiles(rootPath).ToList();

        // Parsing is CPU-bound and independent per file; git is a single
        // process and gains nothing from being called concurrently.
        var metrics = new CodeMetrics();
        var measured = files.AsParallel().Select(metrics.MeasureFile).OfType<FileMetrics>().ToList();

        var history = new GitHistory { Months = HistoryMonths }.Read(rootPath);

        return new RiskRanking { MinimumCodeLines = MinimumCodeLines }
            .Rank(measured, history, CoveredTypes(measured), rootPath);
    }

    /// <summary>
    /// The type graph for a directory, ignoring generated and test files.
    ///
    /// Generated proxies declare hundreds of types nobody wrote, and test
    /// classes clutter a diagram of the design without belonging to it.
    /// </summary>
    public TypeMap Types(string rootPath) => new TypeGraph().Build(Sources(rootPath));

    /// <summary>
    /// Where the code can be cut, over the same files as <see cref="Types"/>.
    ///
    /// Needs both the sources and the map: the map says which types already sit
    /// behind an interface, and the sources say what their methods reach for
    /// that nobody passed in.
    /// </summary>
    public SeamSurvey Seams(string rootPath)
    {
        var sources = Sources(rootPath).ToList();
        return new Seams().Find(sources, new TypeGraph().Build(sources));
    }

    /// <summary>
    /// The files worth reading, filtered once so every analysis over source
    /// agrees on what the solution contains.
    /// </summary>
    private static IEnumerable<(string Path, string Source)> Sources(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        return SourceFiles(rootPath)
            .Select(path =>
            {
                try { return (Path: path, Source: File.ReadAllText(path)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return (Path: path, Source: string.Empty);
                }
            })
            .Where(f => f.Source.Length > 0)
            .Where(f => !CodeMetrics.LooksGenerated(f.Path, f.Source, false))
            .Where(f => !CodeMetrics.LooksLikeTest(f.Path, false));
    }

    /// <summary>
    /// Types that appear to be covered by a test, by name.
    ///
    /// A convention rather than a fact: PriceEngineTests is taken to cover
    /// PriceEngine. Without resolved symbols there is no way to know what a
    /// test actually exercises, and requiring compilation to find out would
    /// give up the one property that makes this usable on inherited code.
    ///
    /// It over-reports coverage, never under-reports it. A file that is called
    /// untested is genuinely untested; one called tested may only share a name
    /// with a test.
    /// </summary>
    private static HashSet<string> CoveredTypes(IReadOnlyList<FileMetrics> metrics)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var test in metrics.Where(m => m.IsTest))
        {
            var name = Path.GetFileNameWithoutExtension(test.Path);

            foreach (var suffix in new[] { "Tests", "Test", "Spec", "Specs", "Fixture" })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var subject = name[..^suffix.Length].TrimEnd('_', '.');
                    if (subject.Length > 0) covered.Add(subject);
                    break;
                }
            }
        }

        return covered;
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                if (SkipDirectories.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                    continue;
                foreach (var found in SourceFiles(entry)) yield return found;
            }
            else if (Path.GetExtension(entry).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }
}
