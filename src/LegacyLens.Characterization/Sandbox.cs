using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Characterization;

/// <summary>
/// One source file, compiled and loaded so it can be called.
///
/// The load context is collectible and unloaded on dispose. Without that, a
/// process comparing forty files would hold forty assemblies it can never let
/// go, and two versions of the same file declare the same type names, so they
/// have to be kept apart rather than merged into one context.
///
/// It compiles against the assemblies this process already trusts and nothing
/// else. A file needing a package that is not here fails to compile, which is
/// reported as a refusal rather than worked around: a stub standing in for the
/// real dependency would be measuring the stub.
/// </summary>
internal sealed class Sandbox : IDisposable
{
    private readonly AssemblyLoadContext? _context;

    private Sandbox(AssemblyLoadContext? context, Assembly? assembly, IReadOnlyList<string> errors)
    {
        _context = context;
        Assembly = assembly;
        Errors = errors;
    }

    public Assembly? Assembly { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool Loaded => Assembly is not null;

    /// <summary>
    /// Everything the running process can already see.
    ///
    /// Taken from the runtime's own list rather than assembled by hand: the set
    /// needed to compile the simplest file on modern .NET is long,
    /// version-specific, and not worth guessing at.
    /// </summary>
    private static IReadOnlyList<MetadataReference> References()
    {
        var platform = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        return platform
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }

    public static Sandbox Compile(string source, string label)
    {
        var name = $"LegacyLens_{label}_{Guid.NewGuid():N}";

        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);

        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .Take(20)
                .ToList();

            return new Sandbox(null, null, errors);
        }

        image.Position = 0;

        var context = new AssemblyLoadContext(name, isCollectible: true);

        try
        {
            return new Sandbox(context, context.LoadFromStream(image), []);
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
        {
            context.Unload();
            return new Sandbox(null, null, [exception.Message]);
        }
    }

    public void Dispose() => _context?.Unload();
}
