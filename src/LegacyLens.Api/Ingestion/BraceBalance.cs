using System.Text;

namespace LegacyLens.Api.Ingestion;

/// <summary>
/// How well the brace scanner reads a corpus: for each file, whether the depth
/// returns to zero at the end, and a fingerprint of the depth after every
/// line so two runs can be compared file by file.
///
/// The number in docs/NEXT.md used to come from a throwaway program. A figure
/// nobody can reproduce is a figure nobody can contradict, which is the
/// opposite of what it is for; so the measurement lives here, as a command,
/// and the figure is re-taken after every change to the scanner.
/// </summary>
public static class BraceBalance
{
    /// <summary>
    /// The extensions the published figure is taken over. Not the ingestion's
    /// list: that one skips node_modules and large files, and this walks
    /// everything, because a minified bundle is exactly the kind of file a
    /// lexer misreads.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExtensions =
        [".cs", ".cshtml", ".css", ".js", ".ts"];

    /// <summary>One file: path relative to the root, depth after its last line, fingerprint of all depths.</summary>
    public sealed record FileBalance(string Path, int FinalDepth, ulong Fingerprint)
    {
        public bool Balanced => FinalDepth == 0;
    }

    public sealed record ExtensionCount(string Extension, int Files, int Balanced);

    public sealed record Report(IReadOnlyList<FileBalance> Files, IReadOnlyList<ExtensionCount> ByExtension)
    {
        public int Total => Files.Count;
        public int Balanced => Files.Count(f => f.Balanced);
        public IEnumerable<FileBalance> Unbalanced => Files.Where(f => !f.Balanced);
    }

    public static Report Measure(string root, IEnumerable<string>? extensions = null)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"No such directory: {root}");

        var wanted = new HashSet<string>(extensions ?? DefaultExtensions, StringComparer.OrdinalIgnoreCase);
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        var files = new List<FileBalance>();
        var counts = new Dictionary<string, (int Files, int Balanced)>(StringComparer.OrdinalIgnoreCase);

        // Ordinal order, so the report reads the same on every machine and a
        // diff of two runs lines up.
        foreach (var file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(file);
            if (!wanted.Contains(extension)) continue;

            string[] lines;
            try { lines = File.ReadAllText(file).Split('\n'); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var depths = BraceScanner.Depths(file, lines);
            var final = depths.Length == 0 ? 0 : depths[^1];

            var relative = file[top.Length..].Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new FileBalance(relative, final, Fingerprint(depths)));

            var count = counts.GetValueOrDefault(extension);
            counts[extension] = (count.Files + 1, count.Balanced + (final == 0 ? 1 : 0));
        }

        var byExtension = counts
            .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ExtensionCount(c.Key.ToLowerInvariant(), c.Value.Files, c.Value.Balanced))
            .ToList();

        return new Report(files, byExtension);
    }

    /// <summary>
    /// One line per file: path, final depth, fingerprint. Written so that two
    /// runs can be compared with diff, and a file whose reading changed shows
    /// up even when it still balances.
    /// </summary>
    public static IEnumerable<string> Rows(Report report) =>
        report.Files.Select(f => $"{f.Path}\t{f.FinalDepth}\t{f.Fingerprint:x16}");

    public static string Summary(Report report)
    {
        var text = new StringBuilder();
        foreach (var e in report.ByExtension)
            text.AppendLine($"{e.Extension,-8} {e.Files,8} files {e.Balanced,8} balanced");
        text.AppendLine($"{"total",-8} {report.Total,8} files {report.Balanced,8} balanced");

        foreach (var f in report.Unbalanced)
            text.AppendLine($"  ends at depth {f.FinalDepth,3}: {f.Path}");

        return text.ToString();
    }

    /// <summary>
    /// FNV-1a over the depths. The runtime's string hash is randomised per
    /// process, which is right for a dictionary and useless for a figure
    /// written down today and checked tomorrow.
    /// </summary>
    private static ulong Fingerprint(int[] depths)
    {
        var hash = 14695981039346656037UL;
        foreach (var depth in depths)
        {
            hash ^= (uint)depth;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
