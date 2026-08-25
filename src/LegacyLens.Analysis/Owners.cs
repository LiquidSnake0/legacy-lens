using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace LegacyLens.Analysis;

/// <summary>
/// Which package defines a type, read from the packages the solution restored.
///
/// The usage surface counts a type as a package's when it appears in a type
/// position in a file importing that package. That is cheap, needs no
/// compilation and works on a solution that does not build, which is why it is
/// what it is. It is also a guess, and the guess is wrong in a measurable way:
/// on nopCommerce 3.90, `ExcelWorksheet`, `PayPalException`, `SqlConnection`
/// and eighteen other names were counted as ASP.NET MVC's work because they sit
/// in files that import it.
///
/// **pacman does not guess.** `pacman -Qo /usr/bin/git` answers `git 2.55.0-1`
/// because the distribution ships an index built from the packages themselves,
/// and `pacman -F` answers for packages that are not even installed. The
/// methodology transfers exactly: do not infer ownership, look it up.
///
/// A restored `packages/` folder is that index, already on disk. nopCommerce
/// 3.90 commits its own: sixty-four packages, five hundred and three
/// assemblies. Read the type definitions out of them and the answer is exact,
/// offline, without compiling anything.
///
/// **It is exact where it is available and silent where it is not.** Orchard
/// commits no assemblies at all, so nothing here changes for it. A tool that
/// answered anyway would be back to guessing, with more machinery.
/// </summary>
public sealed class Owners
{
    private static readonly ConcurrentDictionary<string, Owners> Remembered = new(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _byType;

    private Owners(IReadOnlyDictionary<string, IReadOnlyList<string>> byType, int packages, string source)
    {
        _byType = byType;
        Packages = packages;
        Source = source;
    }

    /// <summary>How many packages were read. Zero means nothing was found to read.</summary>
    public int Packages { get; }

    /// <summary>Where the assemblies came from, or why there were none.</summary>
    public string Source { get; }

    public bool Known => _byType.Count > 0;

    /// <summary>
    /// The packages that define this name, or nothing where the index has never
    /// heard of it.
    ///
    /// Several packages can define the same name, and then ownership is not a
    /// fact. The caller has to treat that as unknown rather than pick one.
    /// </summary>
    public IReadOnlyList<string> Of(string type) =>
        _byType.TryGetValue(type, out var found) ? found : [];

    /// <summary>
    /// Whether this name belongs to somebody else.
    ///
    /// False where the index does not know it, which is the conservative way
    /// round: a name it has never seen may come from the framework, from a
    /// package nobody restored, or from the package being measured.
    /// </summary>
    public bool BelongsElsewhere(string type, string package)
    {
        var owners = Of(type);

        return owners.Count > 0
            && !owners.Any(owner => owner.Equals(package, StringComparison.OrdinalIgnoreCase));
    }

    public static Owners Under(string rootPath) =>
        Remembered.GetOrAdd(Path.GetFullPath(rootPath), Read);

    private static Owners Read(string rootPath)
    {
        // Deliberately the folder the source walker refuses to enter. It skips
        // `packages` because it holds other people's code and counting it as
        // this codebase's would make every measurement a measurement of
        // somebody else's work. That is exactly why this wants it: the question
        // here is whose work a name is.
        var folders = Directory
            .EnumerateDirectories(rootPath, "packages", SearchOption.AllDirectories)
            .Where(folder => !folder.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                                              StringComparison.Ordinal))
            .ToList();

        if (folders.Count == 0)
            return new Owners(Empty, 0, "no restored packages folder under this tree");

        var byType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var packages = 0;

        foreach (var folder in folders)
        foreach (var package in Directory.EnumerateDirectories(folder))
        {
            var id = Identify(Path.GetFileName(package));
            if (id.Length == 0) continue;

            packages++;

            foreach (var assembly in Directory.EnumerateFiles(package, "*.dll", SearchOption.AllDirectories))
            {
                foreach (var name in Defined(assembly))
                {
                    if (!byType.TryGetValue(name, out var owners))
                        byType[name] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    owners.Add(id);
                }
            }
        }

        return new Owners(
            byType.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value.ToList(), StringComparer.Ordinal),
            packages,
            $"{packages} package(s) under {string.Join(", ", folders.Take(3))}");
    }

    /// <summary>
    /// The package id inside a restored folder name.
    ///
    /// NuGet restores into `Id.Version`, so the version is the trailing run of
    /// segments that begin with a digit. `Antlr.3.5.0.2` is Antlr,
    /// `Microsoft.AspNet.Mvc.5.2.3` is Microsoft.AspNet.Mvc.
    /// </summary>
    private static string Identify(string folder)
    {
        var parts = folder.Split('.').ToList();

        while (parts.Count > 1 && parts[^1].Length > 0 && char.IsDigit(parts[^1][0]))
            parts.RemoveAt(parts.Count - 1);

        return string.Join('.', parts);
    }

    /// <summary>
    /// The public types an assembly declares, read from its metadata.
    ///
    /// Definitions and never references: a package that uses `Controller`
    /// does not define it, and counting references would make every assembly
    /// an owner of everything it touches.
    ///
    /// Nothing is loaded and nothing runs. `PEReader` reads the file, which
    /// also means an assembly built for a framework this machine does not have
    /// is read exactly as well as any other.
    /// </summary>
    private static IEnumerable<string> Defined(string path)
    {
        List<string> names = [];

        try
        {
            using var file = File.OpenRead(path);
            using var portable = new PEReader(file);

            if (!portable.HasMetadata) return names;

            var metadata = portable.GetMetadataReader();

            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);

                // Top-level public only. A nested type's short name collides
                // with the world without being reachable by it, and a private
                // type is nobody's API.
                if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public)
                    continue;

                var name = metadata.GetString(type.Name);

                // `List`1` is List. The arity is not part of what anybody wrote.
                var tick = name.IndexOf('`', StringComparison.Ordinal);
                if (tick > 0) name = name[..tick];

                names.Add(name);

                // The other spelling an attribute answers to. The same rule the
                // reader applies when it records a use, and the one M20 and M26
                // each found missing somewhere else.
                if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
                    names.Add(name[..^"Attribute".Length]);
            }

            // Types this assembly exposes without defining, which is mostly
            // forwarders. ASP.NET MVC 5 forwards `TagBuilder` and `HtmlString`
            // to System.Web.WebPages, and reading definitions alone said MVC
            // does not have a TagBuilder, which is true of the file and false
            // of the package. The question here is what a package exposes.
            foreach (var handle in metadata.ExportedTypes)
            {
                var exported = metadata.GetExportedType(handle);

                if (!exported.IsForwarder) continue;

                var name = metadata.GetString(exported.Name);

                var forwardedTick = name.IndexOf('`', StringComparison.Ordinal);
                if (forwardedTick > 0) name = name[..forwardedTick];

                names.Add(name);

                if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
                    names.Add(name[..^"Attribute".Length]);
            }
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException
                                       or UnauthorizedAccessException)
        {
            // A file that is not an assembly, or one this process cannot open.
            // One unreadable assembly is not a reason to have no index.
        }

        return names;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Empty =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
