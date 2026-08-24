using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Analysis;

/// <summary>What the compiler said about a projected file.</summary>
public record ProjectionVerdict(
    bool Compiles,
    /// <summary>What it was compiled against, so the claim can be read.</summary>
    string Target,
    IReadOnlyList<string> Errors,
    /// <summary>
    /// Names that exist nowhere: not in the framework, not in the solution.
    ///
    /// The list that matters, and the reason a compiler is in this loop at all.
    /// A model writing modern code invents plausible type names, and that is
    /// the failure reported against every generative migration tool there is.
    /// </summary>
    IReadOnlyList<string> Invented,
    /// <summary>
    /// Names the solution declares, which this compilation does not have.
    ///
    /// Expected, and not a defect. A file compiled on its own has none of its
    /// project around it, so a real controller names a dozen of these. Counting
    /// them as failures would reject every projection worth making.
    /// </summary>
    IReadOnlyList<string> FromProject,
    /// <summary>
    /// Names that exist in the target framework but were not imported.
    ///
    /// A missing using, not an invention, and the difference decides whether a
    /// projection is worth another attempt or worth discarding. Measured on a
    /// real controller, this is where most of the first attempt's failures
    /// land: the model writes IActionResult and forgets the namespace.
    /// </summary>
    IReadOnlyList<string> Unimported)
{
    /// <summary>
    /// Whether anything was invented. The question worth asking of a
    /// projection compiled outside its project.
    /// </summary>
    public bool Sound => Invented.Count == 0;

    /// <summary>The sentence this is allowed to make, and no larger one.</summary>
    public string Claim =>
        Compiles
            ? $"Compiles against {Target}. Behaviour not verified."
        : Invented.Count > 0
            ? $"Names {Invented.Count} thing(s) that exist nowhere. Not shown as a migration."
        : FromProject.Count > 0 || Unimported.Count > 0
            ? $"Nothing invented. Every name resolves against {Target} or against the project, "
              + $"with {FromProject.Count} type(s) from the project absent from this compilation"
              + (Unimported.Count > 0 ? $" and {Unimported.Count} missing a using" : "")
              + ". Behaviour not verified."
            : $"Does not compile against {Target}.";
}

/// <summary>
/// Compiles a rewritten file against the framework it is being moved to.
///
/// The model writes the projection; this decides whether it is worth showing.
/// That division is the same one M8 draws for generated tests, and it exists
/// because the failure everyone reports about generative migration tools is
/// references to things that do not exist. A compiler settles that question in
/// milliseconds and cannot be talked out of its answer.
///
/// It does not prove the code behaves the same. Nothing here claims that, and
/// the claim it does make says so: compiles, behaviour not verified. Proving
/// behaviour needs the characterization net from M8, which is a different
/// chantier and a larger promise.
///
/// No SDK, no restore, no network. The assemblies come from the ones this
/// process already trusts, which on an ASP.NET Core host includes the whole of
/// Microsoft.AspNetCore.App: the target framework is present because the tool
/// is running on it.
/// </summary>
public class Projection
{
    /// <summary>
    /// The diagnostics that name a type or namespace nobody could find.
    ///
    /// Only these two. CS0117 and CS1061 are about members of a type that does
    /// exist, and their messages quote two names, so reading the first one out
    /// of them produced entries like `the` in a list of invented types.
    /// </summary>
    private static readonly Regex Missing = new(
        @"^CS0246$|^CS0234$", RegexOptions.Compiled);

    /// <summary>
    /// The name of the symbol a diagnostic could not find.
    ///
    /// Read from the message rather than from the syntax, because the compiler
    /// already did the work of deciding which name failed to resolve.
    /// </summary>
    private static readonly Regex Named = new(
        @"'([^']+)'", RegexOptions.Compiled);

    /// <summary>
    /// The last segment of a name the compiler reported.
    ///
    /// It says `Orchard.Taxonomies.LocalizedTaxonomyController` where the
    /// solution declares `LocalizedTaxonomyController`, and comparing the two
    /// whole strings never matches.
    /// </summary>
    private static string Last(string name)
    {
        var cut = name.LastIndexOf('.');
        return cut < 0 ? name : name[(cut + 1)..];
    }

    /// <summary>
    /// Whether the solution declares this name.
    ///
    /// An attribute is written without its suffix and declared with it, so
    /// `[OrchardFeature]` has to find `OrchardFeatureAttribute`.
    /// </summary>
    private static bool Declares(IReadOnlySet<string>? declared, string name) =>
        declared is not null
        && (declared.Contains(name) || declared.Contains(name + "Attribute"));

    /// <summary>
    /// Whether an unresolved name is a segment of a namespace the solution
    /// declares.
    ///
    /// The compiler says `'ContentManagement' does not exist in the namespace
    /// 'Orchard'`, and reports only the segment. Matched loosely on purpose: a
    /// segment shared with a real namespace is a name from the project far more
    /// often than it is an invention, and this list errs the way the rest of
    /// this file does.
    /// </summary>
    private static bool InNamespace(IReadOnlySet<string>? namespaces, string name)
    {
        if (namespaces is null) return false;

        var segment = Last(name);

        return namespaces.Any(known =>
            known.Split('.').Contains(segment, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every type name the referenced assemblies export, by simple name.
    ///
    /// Built once and kept: walking the whole framework's namespaces costs
    /// about a second, and every projection after the first is free. Simple
    /// names rather than full ones, because what fails to resolve is a name
    /// written without its namespace.
    ///
    /// The cost of simple names is homonyms. Two unrelated types sharing one
    /// name make this say "missing a using" where the truth is "this name
    /// exists, but not the one you meant". It errs that way deliberately: the
    /// list it must never be wrong about is the invented one, and a false entry
    /// there discards a correct projection.
    /// </summary>
    private static IReadOnlySet<string>? _known;
    private static readonly Lock Building = new();

    private static IReadOnlySet<string> KnownToTheFramework(CSharpCompilation compilation)
    {
        if (_known is not null) return _known;

        lock (Building)
        {
            if (_known is not null) return _known;

            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                    continue;

                Walk(assembly.GlobalNamespace, names);
            }

            return _known = names;
        }
    }

    private static void Walk(INamespaceSymbol space, HashSet<string> names)
    {
        foreach (var type in space.GetTypeMembers()) names.Add(type.Name);
        foreach (var nested in space.GetNamespaceMembers()) Walk(nested, names);
    }

    /// <summary>
    /// Everything this process already trusts.
    ///
    /// Taken from the runtime's own list rather than assembled by hand: the set
    /// needed to compile a modern controller is long, version-specific, and not
    /// worth guessing.
    /// </summary>
    private static IReadOnlyList<MetadataReference> References() =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

    /// <summary>
    /// What the projection is being compiled against, named for a reader.
    ///
    /// Reported rather than assumed, because "compiles" means nothing without
    /// it and the answer changes with the machine.
    /// </summary>
    public static string Target
    {
        get
        {
            var framework = Environment.Version;
            var web = Available("Microsoft.AspNetCore.Mvc.Core");

            return web
                ? $".NET {framework.Major}, with ASP.NET Core present"
                : $".NET {framework.Major}, without ASP.NET Core";
        }
    }

    /// <summary>Whether an assembly is among the ones that can be compiled against.</summary>
    public static bool Available(string assemblyName) =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(path => Path.GetFileNameWithoutExtension(path)
                .Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compiles one file and reports what the compiler found.
    ///
    /// A library rather than an executable: a projected controller has no entry
    /// point and requiring one would fail every projection for the wrong reason.
    /// </summary>
    /// <param name="declared">
    /// Type names the solution defines. Without them every unresolved name looks
    /// invented, and a real file names a dozen of its own project's types.
    /// </param>
    /// <param name="namespaces">
    /// Namespaces the solution defines. A file compiled outside its project
    /// fails on `Orchard.ContentManagement` before it fails on any type in it,
    /// and the compiler reports the segment rather than the whole path, so
    /// without these every real projection is full of invented "types" called
    /// things like `UI`.
    /// </param>
    public ProjectionVerdict Compile(
        string source,
        IReadOnlySet<string>? declared = null,
        IReadOnlySet<string>? namespaces = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new ProjectionVerdict(false, Target, ["There is nothing to compile."], [], [], []);

        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(source);
        }
        catch (Exception exception)
        {
            return new ProjectionVerdict(false, Target, [exception.Message], [], [], []);
        }

        var compilation = CSharpCompilation.Create(
            $"Projection_{Guid.NewGuid():N}",
            [tree],
            References(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                // A projection is an excerpt. It names types it does not
                // define and is not expected to be a whole program.
                nullableContextOptions: NullableContextOptions.Disable));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);

        var failures = emitted.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (failures.Count == 0)
            return new ProjectionVerdict(true, Target, [], [], [], []);

        var unresolved = failures
            .Where(d => Missing.IsMatch(d.Id))
            .Select(d => Named.Match(d.GetMessage()) is { Success: true } m ? m.Groups[1].Value : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The split that makes this usable on real files, in three ways rather
        // than two. A name the solution declares is missing because its project
        // is not here. A name the framework has is missing a using. A name
        // neither of them has was made up, and only that last one is a defect.
        var known = KnownToTheFramework(compilation);

        var project = unresolved
            .Where(name => Declares(declared, Last(name)) || InNamespace(namespaces, name))
            .ToList();

        var unimported = unresolved
            .Except(project, StringComparer.Ordinal)
            .Where(name => known.Contains(Last(name)) || known.Contains(Last(name) + "Attribute"))
            .ToList();

        var invented = unresolved
            .Except(project, StringComparer.Ordinal)
            .Except(unimported, StringComparer.Ordinal)
            .ToList();

        // Errors caused only by the absent project are not worth reading, and
        // burying the real ones under forty of them is how they get missed.
        var expected = project.Concat(unimported).ToHashSet(StringComparer.Ordinal);

        var errors = failures
            .Where(d => !(Missing.IsMatch(d.Id)
                          && Named.Match(d.GetMessage()) is { Success: true } m
                          && expected.Contains(m.Groups[1].Value)))
            .Select(d => d.ToString())
            .Take(20)
            .ToList();

        return new ProjectionVerdict(false, Target, errors, invented, project, unimported);
    }
}
