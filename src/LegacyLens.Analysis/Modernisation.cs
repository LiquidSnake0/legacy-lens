using System.Xml.Linq;

namespace LegacyLens.Analysis;

/// <summary>Whether a package has anywhere to go on modern .NET.</summary>
public enum Portability
{
    /// <summary>Runs on modern .NET, or has a documented successor.</summary>
    Portable,

    /// <summary>
    /// Built on <c>System.Web</c>, which does not exist outside the .NET
    /// Framework. No version of the package fixes this; the code that uses it
    /// has to be rewritten.
    /// </summary>
    TiedToSystemWeb,

    /// <summary>Not in the curated list. Reported as unknown, never guessed.</summary>
    Unknown,
}

/// <summary>One package, and how widely it is used.</summary>
public record PackageUse(
    string Id,
    IReadOnlyList<string> Versions,
    int Projects,
    Portability Portability)
{
    /// <summary>
    /// The same package pinned to different versions in different projects.
    /// Every one of those is a binding redirect waiting to be written by hand,
    /// and a conversion that has to pick a winner.
    /// </summary>
    public bool Divergent => Versions.Count > 1;
}

/// <summary>How one project is packaged, and what holds it back.</summary>
public record ProjectModernisation(
    string Name,
    string Path,
    bool SdkStyle,
    PackageDeclaration Packages,
    string? TargetFramework,
    /// <summary>Packages in this project with no path to modern .NET.</summary>
    IReadOnlyList<string> DeadEnds)
{
    public bool Blocked => DeadEnds.Count > 0;

    /// <summary>Nothing holds it back and only its file format is old.</summary>
    public bool ConvertibleAsIs => !Blocked && !SdkStyle;
}

public enum PackageDeclaration
{
    None,
    /// <summary>A packages.config beside the project. The old way.</summary>
    PackagesConfig,
    /// <summary>PackageReference elements in the project file itself.</summary>
    PackageReference,
}

public record ModernisationSurvey(
    IReadOnlyList<ProjectModernisation> Projects,
    IReadOnlyList<PackageUse> Packages,
    /// <summary>Hand-written bindingRedirect elements across all config files.</summary>
    int BindingRedirects)
{
    public int PreSdk => Projects.Count(p => !p.SdkStyle);
    public int SdkStyle => Projects.Count(p => p.SdkStyle);

    public int UsingPackagesConfig =>
        Projects.Count(p => p.Packages == PackageDeclaration.PackagesConfig);
    public int UsingPackageReference =>
        Projects.Count(p => p.Packages == PackageDeclaration.PackageReference);

    public int References => Packages.Sum(p => p.Projects);
    public int Divergent => Packages.Count(p => p.Divergent);

    public int ReferencesTiedToSystemWeb => Sum(Portability.TiedToSystemWeb);

    /// <summary>Known to run on modern .NET.</summary>
    public int ReferencesPortable => Sum(Portability.Portable);

    /// <summary>
    /// Neither confirmed nor ruled out. Kept separate from portable on
    /// purpose: a quote built on "unknown counted as fine" is a quote that
    /// discovers the problem after the price is agreed.
    /// </summary>
    public int ReferencesUnknown => Sum(Portability.Unknown);

    private int Sum(Portability kind) =>
        Packages.Where(p => p.Portability == kind).Sum(p => p.Projects);

    public int Blocked => Projects.Count(p => p.Blocked);
    public int ConvertibleAsIs => Projects.Count(p => p.ConvertibleAsIs);

    public IReadOnlyList<PackageUse> DeadEnds =>
        Packages.Where(p => p.Portability == Portability.TiedToSystemWeb)
                .OrderByDescending(p => p.Projects)
                .ToList();

    /// <summary>
    /// A tended legacy is old but coherent: one version per package, and no
    /// binding redirects, because a redirect only gets written when versions
    /// disagreed. A rotten one has drifted. They are not the same job and must
    /// not carry the same estimate, so the distinction is drawn here rather
    /// than left to whoever reads the numbers.
    /// </summary>
    public bool Tended => Divergent == 0 && BindingRedirects == 0;
}

/// <summary>
/// How much of a modernisation is mechanical, and what no amount of automation
/// will do.
///
/// Reading project files only. Nothing here compiles, restores or contacts
/// nuget.org, so it answers on a solution that does not build, which is the
/// state every inherited solution is in on the first day.
/// </summary>
public class Modernisation
{
    private static readonly string[] SkipDirectories =
        [".git", "bin", "obj", "node_modules", "packages", ".vs"];

    /// <summary>
    /// Packages built on System.Web. Curated by hand, because this is domain
    /// knowledge rather than something the file tells you: nothing in a
    /// packages.config says "this will never run on .NET 8".
    ///
    /// The list is deliberately short and only holds cases with no argument.
    /// Anything absent is reported as Unknown rather than assumed portable,
    /// since a survey that quietly calls an unknown package fine is worse than
    /// one that admits the gap.
    /// </summary>
    public static readonly IReadOnlySet<string> TiedToSystemWeb =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.AspNet.Mvc",
            "Microsoft.AspNet.Razor",
            "Microsoft.AspNet.WebPages",
            "Microsoft.AspNet.Web.Optimization",
            "Microsoft.AspNet.WebApi.WebHost",
            "Microsoft.AspNet.SignalR.SystemWeb",
            "Microsoft.Owin.Host.SystemWeb",
            "Microsoft.Web.Infrastructure",
            "Microsoft.CodeDom.Providers.DotNetCompilerPlatform",
            "WebGrease",
        };

    /// <summary>
    /// Packages known to run on modern .NET. Also curated, and also short: the
    /// point is not to classify every package on nuget.org, it is to name the
    /// ones that decide a migration either way.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownPortable =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Newtonsoft.Json", "NHibernate", "Remotion.Linq", "Autofac",
            "Castle.Core", "log4net", "NLog", "Serilog", "AutoMapper",
            "FluentValidation", "Dapper", "Polly", "MediatR", "NUnit",
            "xunit", "Moq", "FluentAssertions", "System.Text.Json",
            "Microsoft.Extensions.Logging", "Microsoft.Extensions.Configuration",
        };

    public ModernisationSurvey Survey(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"No such directory: {rootPath}");

        var projects = new List<ProjectModernisation>();
        var uses = new Dictionary<string, (HashSet<string> Versions, int Projects)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in FindFiles(rootPath, ".csproj"))
        {
            var packages = ReadPackages(file, out var declaration);

            foreach (var (id, version) in packages)
            {
                if (!uses.TryGetValue(id, out var seen))
                    seen = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);

                seen.Versions.Add(version);
                uses[id] = (seen.Versions, seen.Projects + 1);
            }

            projects.Add(new ProjectModernisation(
                Path.GetFileNameWithoutExtension(file),
                file,
                IsSdkStyle(file),
                declaration,
                ReadTargetFramework(file),
                packages.Select(p => p.Id).Where(TiedToSystemWeb.Contains).Distinct().ToList()));
        }

        var catalogue = uses
            .Select(u => new PackageUse(
                u.Key,
                u.Value.Versions.OrderBy(v => v).ToList(),
                u.Value.Projects,
                Classify(u.Key)))
            .OrderByDescending(p => p.Projects)
            .ToList();

        return new ModernisationSurvey(projects, catalogue, CountRedirects(rootPath));
    }

    public static Portability Classify(string packageId) =>
        TiedToSystemWeb.Contains(packageId) ? Portability.TiedToSystemWeb
        : KnownPortable.Contains(packageId) ? Portability.Portable
        : Portability.Unknown;

    /// <summary>
    /// SDK-style project files carry an Sdk attribute on the Project element,
    /// and pre-SDK ones do not. That single attribute is the whole difference
    /// as far as tooling is concerned.
    /// </summary>
    private static bool IsSdkStyle(string projectFile)
    {
        try
        {
            var root = XDocument.Load(projectFile).Root;
            return root?.Attribute("Sdk") is not null;
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
        {
            return false;
        }
    }

    private static List<(string Id, string Version)> ReadPackages(
        string projectFile, out PackageDeclaration declaration)
    {
        var packages = new List<(string, string)>();

        // packages.config wins when both exist: a project mid-migration has
        // both, and the old file is what still governs the restore.
        var config = Path.Combine(Path.GetDirectoryName(projectFile)!, "packages.config");
        if (File.Exists(config))
        {
            declaration = PackageDeclaration.PackagesConfig;
            try
            {
                foreach (var e in XDocument.Load(config).Root?.Elements() ?? [])
                {
                    var id = e.Attribute("id")?.Value;
                    if (id is not null)
                        packages.Add((id, e.Attribute("version")?.Value ?? "unknown"));
                }
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
            {
                // A malformed packages.config is itself worth knowing about,
                // but it is not a reason to abandon the whole survey.
            }

            return packages;
        }

        try
        {
            var root = XDocument.Load(projectFile).Root;
            foreach (var e in root?.Descendants()
                                   .Where(d => d.Name.LocalName == "PackageReference") ?? [])
            {
                var id = e.Attribute("Include")?.Value;
                if (id is null) continue;

                // The version sits on an attribute or on a child element,
                // depending on which convention the project was written with.
                var version = e.Attribute("Version")?.Value
                           ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value
                           ?? "unknown";
                packages.Add((id, version));
            }
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
        {
            declaration = PackageDeclaration.None;
            return packages;
        }

        declaration = packages.Count > 0
            ? PackageDeclaration.PackageReference
            : PackageDeclaration.None;

        return packages;
    }

    private static string? ReadTargetFramework(string projectFile)
    {
        try
        {
            var root = XDocument.Load(projectFile).Root;
            var names = new[] { "TargetFrameworkVersion", "TargetFramework", "TargetFrameworks" };

            return root?.Descendants()
                        .FirstOrDefault(e => names.Contains(e.Name.LocalName))?.Value;
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Counted by text rather than parsed. These live inside an
    /// assemblyBinding block with its own namespace, and a config file that is
    /// malformed enough to fail an XML parse still tells the truth about how
    /// many redirects someone typed by hand.
    /// </summary>
    private static int CountRedirects(string rootPath)
    {
        var total = 0;

        foreach (var file in FindFiles(rootPath, ".config"))
        {
            var name = Path.GetFileName(file);
            if (!name.Equals("web.config", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("app.config", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                total += CountOccurrences(File.ReadAllText(file), "<bindingRedirect");
            }
            catch (IOException)
            {
                // Unreadable file, counted as zero rather than aborting.
            }
        }

        return total;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

        while (at >= 0)
        {
            count++;
            at = text.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase);
        }

        return count;
    }

    private static IEnumerable<string> FindFiles(string directory, string extension)
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
                var name = Path.GetFileName(entry);
                if (SkipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                foreach (var found in FindFiles(entry, extension)) yield return found;
            }
            else if (entry.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }
}
