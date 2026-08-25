namespace LegacyLens.Analysis;

/// <summary>
/// Where a hand-written catalogue lives.
///
/// This tool's judgements are data rather than code: what replaces what, what
/// question to ask where, what has no life on the target. Each is a file
/// somebody wrote and signed, and each has to be found at run time from
/// wherever the program happens to have been started.
///
/// Written once here because it had been written twice, and because getting it
/// wrong is silent. A single-file publish extracts to a temporary folder, so
/// `AppContext.BaseDirectory` points nowhere near the catalogue somebody
/// dropped next to the executable. That was found by running the desktop
/// build, where every package came back with no candidate at all and nothing
/// said why.
/// </summary>
public static class Catalogues
{
    /// <summary>
    /// Every place to look, in the order worth looking.
    ///
    /// The executable's own folder first, then the base directory. They are the
    /// same thing for an ordinary build and they are not for a single-file one.
    /// Then upwards, for a checkout where the data folder sits at the top and
    /// the build output several levels below it.
    /// </summary>
    public static IEnumerable<string> Beside(string name)
    {
        var roots = new List<string>();

        if (Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } beside)
            roots.Add(beside);

        roots.Add(AppContext.BaseDirectory);

        foreach (var root in roots)
        {
            yield return Path.Combine(root, name);
            yield return Path.Combine(root, "data", name);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var up = 0; up < 6 && directory is not null; up++)
        {
            yield return Path.Combine(directory.FullName, "data", name);
            directory = directory.Parent;
        }
    }

    /// <summary>The first one that is actually there, or null.</summary>
    public static string? Find(string name) => Beside(name).FirstOrDefault(File.Exists);
}
