namespace LegacyLens.Api.Ingestion;

/// <summary>
/// Decides which files in a repository are worth indexing.
/// Indexing node_modules is how an index becomes 400k chunks of other
/// people's code and answers stop being about your project.
/// </summary>
public class SourceWalker
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "node_modules", "bower_components", "vendor", "packages",
        "bin", "obj", "build", "dist", "out", "target",
        ".vs", ".vscode", ".idea",
        "__pycache__", ".venv", "venv", "env",
        "coverage", ".next", ".nuxt", ".angular",
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".fs",
        ".ts", ".tsx", ".js", ".jsx", ".mjs",
        ".java", ".kt", ".scala", ".groovy",
        ".py", ".rb", ".go", ".rs", ".php",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx",
        ".sql", ".sh", ".ps1",
        ".html", ".css", ".scss",
        ".xml", ".json", ".yml", ".yaml", ".toml", ".ini", ".config",
        ".md", ".txt",
    };

    /// <summary>Files above this are generated, minified, or data. Not worth reading.</summary>
    private const long MaxFileBytes = 512 * 1024;

    public IEnumerable<string> Walk(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        return WalkCore(rootPath);
    }

    private IEnumerable<string> WalkCore(string directory)
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
                var name = Path.GetFileName(entry);
                if (SkipDirectories.Contains(name)) continue;
                foreach (var file in WalkCore(entry)) yield return file;
                continue;
            }

            if (!SourceExtensions.Contains(Path.GetExtension(entry))) continue;

            var info = new FileInfo(entry);
            if (info.Length == 0 || info.Length > MaxFileBytes) continue;

            yield return entry;
        }
    }

    /// <summary>
    /// A cheap binary check: a NUL byte in the first block means this is not
    /// text, whatever the extension claims.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head) => head.IndexOf((byte)0) >= 0;
}
