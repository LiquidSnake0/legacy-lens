namespace LegacyLens.Analysis;

/// <summary>
/// Which files an analysis looks at, decided once.
///
/// Seven classes here walked a directory tree, and each carried its own copy of
/// the same list of folders to skip. Identical to the character, which is what
/// made it worth pulling out: the day somebody adds a folder to one of them,
/// two analyses of the same solution start counting different files and the
/// report contradicts itself with no error anywhere.
///
/// The same reasoning the value builder already carries, where a second copy of
/// what one method knew drifted from it the moment either changed.
/// </summary>
public static class SourceTree
{
    /// <summary>
    /// Folders that hold no source anybody wrote.
    ///
    /// Build output and fetched dependencies. Skipping them is not an
    /// optimisation: a solution's `packages` folder holds other people's code,
    /// and counting it as this codebase's would make every measurement here a
    /// measurement of somebody else's work.
    /// </summary>
    public static readonly IReadOnlySet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "packages", ".vs",
    };

    public static bool Skipped(string directory) => Skip.Contains(Path.GetFileName(directory));

    /// <summary>Every C# file under a directory.</summary>
    public static IEnumerable<string> CSharpUnder(string directory) =>
        Under(directory, path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every file under a directory the caller wants, skipped folders aside.
    ///
    /// A directory that cannot be listed ends that branch rather than the walk.
    /// A permission a reader does not have is a fact about the machine, and an
    /// analysis of ninety projects should not stop at the one folder it was not
    /// allowed to open.
    /// </summary>
    public static IEnumerable<string> Under(string directory, Func<string, bool> wanted)
    {
        IEnumerable<string> entries;

        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                if (Skipped(entry)) continue;

                foreach (var found in Under(entry, wanted)) yield return found;
            }
            else if (wanted(entry))
            {
                yield return entry;
            }
        }
    }
}
