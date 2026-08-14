using System.Xml.Linq;

namespace LegacyLens.Analysis;

/// <summary>
/// Builds the dependency graph of a solution by reading project files.
///
/// No compilation, no NuGet restore, no MSBuild. That is deliberate: the moment
/// you most need to understand an inherited solution is the moment it does not
/// build, because a package is gone or the SDK is not installed. Tools that
/// require a successful build are useless exactly when they would help.
/// </summary>
public class ProjectGraph
{
    private static readonly string[] SkipDirectories =
        [".git", "bin", "obj", "node_modules", "packages", ".vs"];

    public SolutionMap Build(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        var projects = FindProjectFiles(rootPath).Select(Read).ToList();

        // Dependencies are resolved by name rather than by path. A
        // ProjectReference is written relative to the file that declares it,
        // and in old solutions those paths are often wrong after a folder has
        // been moved: the build still works because the IDE fixed it up, but
        // the text on disk lies. The project name survives that.
        var known = projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = projects
            .SelectMany(p => p.References.Where(known.Contains)
                                         .Select(r => new ProjectEdge(p.Name, r)))
            .Distinct()
            .ToList();

        return new SolutionMap(projects, edges, FindCycles(projects, edges));
    }

    private static IEnumerable<string> FindProjectFiles(string directory)
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
                foreach (var found in FindProjectFiles(entry)) yield return found;
            }
            else if (Path.GetExtension(entry).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }

    private static ProjectInfo Read(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        XDocument document;

        try
        {
            document = XDocument.Load(path);
        }
        catch (System.Xml.XmlException)
        {
            // A malformed project file is a finding, not a crash. It stays on
            // the map so the reader knows it exists and is broken, which is
            // more useful than an exception halfway through an analysis.
            return new ProjectInfo(name, path, ProjectKind.Broken, null, [], [], 0, 0);
        }

        var references = Elements(document, "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var folder = Path.GetDirectoryName(path)!;
        var (files, lines) = MeasureSource(folder);

        var assemblies = Elements(document, "Reference")
            .Concat(Elements(document, "PackageReference"))
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            // Old-style references carry the full strong name; only the
            // assembly name is worth keeping.
            .Select(include => include.Split(',')[0].Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectInfo(
            name,
            path,
            DetectKind(document, folder, assemblies),
            Value(document, "TargetFrameworkVersion") ?? Value(document, "TargetFramework"),
            references,
            assemblies,
            files,
            lines);
    }

    /// <summary>
    /// Elements by local name, whichever project format is in use.
    ///
    /// Pre-SDK project files sit in the MSBuild 2003 XML namespace; SDK-style
    /// ones have no namespace at all. Matching on the local name handles both
    /// without branching, and a solution being migrated contains both at once.
    /// </summary>
    private static IEnumerable<XElement> Elements(XDocument document, string localName) =>
        document.Descendants().Where(e => e.Name.LocalName == localName);

    private static string? Value(XDocument document, string localName) =>
        Elements(document, localName).FirstOrDefault()?.Value.Trim();

    /// <summary>
    /// What the project is, decided by what sits in its folder.
    ///
    /// Assembly references were the obvious signal and they are wrong: in
    /// nopCommerce, Nop.Core references System.Web.Mvc and is a class library
    /// all the same. A web.config beside a Views folder does not lie. What the
    /// references reveal is reported separately, by <see cref="Findings"/>.
    /// </summary>
    private static ProjectKind DetectKind(
        XDocument document, string folder, IReadOnlyList<string> assemblies)
    {
        bool Uses(string needle) =>
            assemblies.Any(a => a.StartsWith(needle, StringComparison.OrdinalIgnoreCase));

        bool HasFile(string name) => File.Exists(Path.Combine(folder, name));
        bool HasFolder(string name) => Directory.Exists(Path.Combine(folder, name));

        // Tests first: a test project is a library by output type, and calling
        // it one hides the distinction a reader asks about first. Here the
        // reference IS the definition, so it is the right signal.
        if (Uses("xunit") || Uses("nunit") || Uses("MSTest") ||
            Uses("Microsoft.VisualStudio.QualityTools"))
            return ProjectKind.Test;

        if (HasFile("web.config") || HasFile("Web.config") || HasFile("Global.asax")
            || (HasFolder("Views") && HasFolder("Controllers")))
            return ProjectKind.Web;

        if (HasFile("App.xaml") || Value(document, "UseWPF") == "true")
            return ProjectKind.Wpf;

        if (Value(document, "UseWindowsForms") == "true"
            || Elements(document, "Compile").Any(e =>
                   ((string?)e.Attribute("Include") ?? "")
                       .EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                   && e.Elements().Any(c => c.Name.LocalName == "SubType"
                                            && c.Value == "Form")))
            return ProjectKind.WinForms;

        return Value(document, "OutputType") switch
        {
            "Exe" or "WinExe" => ProjectKind.Console,
            _ => ProjectKind.Library,
        };
    }

    private static (int Files, int Lines) MeasureSource(string directory)
    {
        var files = 0;
        var lines = 0;

        foreach (var file in EnumerateSource(directory))
        {
            files++;
            try
            {
                lines += File.ReadLines(file).Count();
            }
            catch (IOException)
            {
                // Counted as present, not as content. A file nobody can read is
                // worth knowing about; guessing its size is not.
            }
        }

        return (files, lines);
    }

    private static IEnumerable<string> EnumerateSource(string directory)
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
                foreach (var found in EnumerateSource(entry)) yield return found;
            }
            // Designer files are excluded: WinForms and typed datasets generate
            // thousands of lines nobody wrote and nobody reads, and counting
            // them makes a small project look like a large one.
            else if (Path.GetExtension(entry).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                     && !entry.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Cycles in the project graph, found by depth-first search.
    ///
    /// MSBuild refuses to build a cycle, so a solution that compiles has none.
    /// This runs anyway: the graph is read from files that may describe a state
    /// nobody has built in years, and finding a cycle immediately explains why
    /// a build fails with an error that names neither project clearly.
    /// </summary>
    internal static List<IReadOnlyList<string>> FindCycles(
        IReadOnlyList<ProjectInfo> projects, IReadOnlyList<ProjectEdge> edges)
    {
        var outgoing = edges
            .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList(),
                          StringComparer.OrdinalIgnoreCase);

        var cycles = new List<IReadOnlyList<string>>();
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var started = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        void Walk(string node)
        {
            path.Add(node);
            started.Add(node);

            foreach (var next in outgoing.GetValueOrDefault(node, []))
            {
                var at = path.FindIndex(n => string.Equals(n, next, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    // Closing back onto the current path: everything from that
                    // point on is the cycle.
                    cycles.Add([.. path[at..], next]);
                }
                else if (!settled.Contains(next))
                {
                    Walk(next);
                }
            }

            path.RemoveAt(path.Count - 1);
            settled.Add(node);
        }

        foreach (var project in projects)
        {
            if (!started.Contains(project.Name)) Walk(project.Name);
        }

        return cycles;
    }
}
