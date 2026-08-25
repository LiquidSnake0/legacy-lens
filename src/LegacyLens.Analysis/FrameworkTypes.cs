using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Analysis;

/// <summary>
/// Every type the framework this is running on actually has, by name.
///
/// The catalogue of successors is written by hand, and that is the right way
/// round for a judgement: what replaces what is an opinion with a note attached,
/// and a machine that guesses one is the failure this whole project is built to
/// avoid.
///
/// But a great many correspondences are not judgements at all. `HttpPost` is
/// `HttpPost`, `Controller` is `Controller`, `RouteValueDictionary` is
/// `RouteValueDictionary`: the type kept its name and moved namespace. Writing
/// those down by hand is transcription, and transcription is what leaves a
/// hundred types sitting in the "nobody has looked at this" column while the
/// answer was available all along.
///
/// So they are read rather than written. The tool already runs on the framework
/// it is asked about, and already compiles against every assembly that
/// framework ships. The answer is in metadata this process has loaded anyway.
///
/// This is deliberately not a download. Microsoft publishes apisof.net under
/// MIT, and it knows more than this does: which version introduced an API, and
/// which NuGet package a type moved into once it left the base library. What it
/// cannot be is offline, current with the runtime in front of it, and free of
/// somebody else's data in a repository that ships under its own licence. The
/// two things it knows that this does not are written down in the roadmap
/// rather than pretended away.
/// </summary>
public static class FrameworkTypes
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? _byName;
    private static readonly Lock Building = new();

    /// <summary>
    /// Simple name to every full name the framework has for it.
    ///
    /// A list rather than one entry, because the same short name lives in
    /// several places and the difference matters more than anywhere else here:
    /// a legacy codebase's `HttpContext` is `System.Web.HttpContext`, and the
    /// modern one is `Microsoft.AspNetCore.Http.HttpContext`. Same word, two
    /// unrelated types. Answering "yes it exists" on the name alone would call
    /// the hardest migration in ASP.NET a rename.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByName
    {
        get
        {
            if (_byName is not null) return _byName;

            lock (Building)
            {
                return _byName ??= Read();
            }
        }
    }

    /// <summary>
    /// Where a name lives, among the namespaces a package brings.
    ///
    /// Matched on the namespace prefix rather than on an exact namespace: a
    /// package puts its types across a family of them, and
    /// `Microsoft.AspNetCore.Mvc` covers `.Rendering`, `.Filters` and the rest.
    /// </summary>
    public static IReadOnlyList<string> Under(string name, string namespacePrefix)
    {
        if (!ByName.TryGetValue(name, out var found)) return [];

        return found
            .Where(full => full.StartsWith(namespacePrefix + ".", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Whether the framework's own surface could be read at all.
    ///
    /// False where the assemblies are not files this process can open, which is
    /// what a single-file publish does to them. Everything downstream then has
    /// to say it could not look, rather than answering as though it had looked
    /// and found nothing: that is the difference between a tool that reports
    /// less and a tool that reports differently depending on how it was built.
    ///
    /// Found by running the desktop build. The usage surface silently went back
    /// to its pre-M13 numbers, 4,379 uses where the server said 3,877, with no
    /// error anywhere.
    /// </summary>
    public static bool Readable => ByName.Count > 0;

    /// <summary>
    /// Whether the framework carries this namespace family at all.
    ///
    /// The question that decides whether asking it about a successor means
    /// anything. `Microsoft.AspNetCore.Mvc` is part of the framework and can be
    /// asked; `Serilog` is a package, and every type of every predecessor comes
    /// back absent from it, which is literally true and tells nobody anything.
    /// </summary>
    public static bool Carries(string namespacePrefix) =>
        namespacePrefix.Length > 0
        && ByName.Values.Any(places => places.Any(
            full => full.StartsWith(namespacePrefix + ".", StringComparison.Ordinal)));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Read()
    {
        var references = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        // An empty compilation, present only so Roslyn will resolve the
        // metadata into symbols. Nothing is compiled and nothing is run.
        var compilation = CSharpCompilation.Create("LegacyLens_FrameworkSurface", [], references);

        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            Walk(assembly.GlobalNamespace, found);
        }

        return found.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value.Distinct(StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Public types only, and never nested ones.
    ///
    /// A type nobody outside the assembly can name is not a counterpart for
    /// anything, and a nested type's short name collides with the world without
    /// being reachable by it.
    /// </summary>
    private static void Walk(INamespaceSymbol space, Dictionary<string, List<string>> found)
    {
        foreach (var type in space.GetTypeMembers())
        {
            if (type.DeclaredAccessibility != Accessibility.Public) continue;

            if (!found.TryGetValue(type.Name, out var places))
                found[type.Name] = places = [];

            places.Add($"{space.ToDisplayString()}.{type.Name}");
        }

        foreach (var nested in space.GetNamespaceMembers()) Walk(nested, found);
    }
}
