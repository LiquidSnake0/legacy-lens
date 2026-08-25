using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Characterization;

/// <summary>
/// What happened when the generated file was compiled and run.
/// </summary>
public record Verification(
    bool Compiled,
    IReadOnlyList<string> CompilerErrors,
    IReadOnlyList<string> Passed,
    IReadOnlyDictionary<string, string> Failed)
{
    public bool Clean => Compiled && Failed.Count == 0;
}

/// <summary>
/// Compiles the generated tests and runs them.
///
/// This is the step that makes generated code defensible. A characterization
/// test is true if and only if it passes against the code as it stands, which
/// is a claim a machine can settle in full: the compiler decides whether it is
/// even code, and running it decides whether it is true. Nothing reaches a
/// person that has not survived both, so nobody is asked to review an assertion
/// on trust.
///
/// It is also the step that catches the tool's own bugs. A literal written
/// wrongly, a type name that does not resolve, a JSON string that was not
/// escaped: all of it fails here rather than in the reader's repository.
/// </summary>
public class Verifier
{
    /// <summary>
    /// Assemblies the generated code compiles against: everything this process
    /// already trusts, plus the assembly under characterization.
    ///
    /// Taken from the runtime's own list rather than assembled by hand, because
    /// the set of assemblies needed to compile "hello world" on modern .NET is
    /// long, version-specific, and not worth guessing.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile", "IL3000",
        Justification =
            "Deliberate, and checked at runtime rather than silenced. Compiling a "
          + "generated test needs xunit and the subject as files a compiler can open, "
          + "and a single-file publish embeds them, so Location is empty and there is "
          + "no path to give. Verify below reports that instead of producing a "
          + "compilation with no references and calling the result a failing test.")]
    private static IEnumerable<MetadataReference> References(Assembly subject)
    {
        var platform = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        var extra = new[]
        {
            subject.Location,
            typeof(Xunit.FactAttribute).Assembly.Location,
            typeof(Xunit.Assert).Assembly.Location,
        };

        return platform.Concat(extra)
            .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
    }

    /// <summary>
    /// Whether this build can compile anything at all.
    ///
    /// A single-file publish embeds its assemblies, so there is no path on disk
    /// to hand a compiler. Everything else in this tool works there; this one
    /// capability cannot, and saying so beats emitting a compilation with no
    /// references and reporting the result as a test that failed.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile", "IL3000",
        Justification = "Asking the question the analyser is warning about, on purpose.")]
    public static bool Possible => typeof(Xunit.FactAttribute).Assembly.Location.Length > 0;

    public Verification Verify(GeneratedSuite suite, Assembly subject)
    {
        if (!Possible)
        {
            return new Verification(false,
                ["This build has its assemblies embedded in a single file, so there is no "
                 + "path on disk to compile the generated test against. Run the framework-"
                 + "dependent build for characterization."],
                [], new Dictionary<string, string>());
        }

        var compilation = CSharpCompilation.Create(
            $"Characterization_{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(suite.Source)],
            References(subject),
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

            return new Verification(false, errors, [], new Dictionary<string, string>());
        }

        image.Position = 0;

        // A collectible context so that repeated runs in one process do not
        // pile up assemblies that can never be unloaded.
        var context = new AssemblyLoadContext("characterization", isCollectible: true);

        try
        {
            return Run(context.LoadFromStream(image), suite);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Invokes each generated test method directly.
    ///
    /// A real xUnit host would launch a process and discover tests; this needs
    /// neither. The generated methods are public, take no arguments, and fail
    /// by throwing, which is the entire contract being relied on.
    /// </summary>
    private static Verification Run(Assembly compiled, GeneratedSuite suite)
    {
        var passed = new List<string>();
        var failed = new Dictionary<string, string>();
        var expected = suite.Cases.Select(c => c.TestName).ToHashSet(StringComparer.Ordinal);

        foreach (var type in compiled.GetTypes())
        {
            var instance = Activator.CreateInstance(type);

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                                 | BindingFlags.DeclaredOnly))
            {
                if (method.GetParameters().Length > 0 || !expected.Contains(method.Name)) continue;

                try
                {
                    method.Invoke(instance, null);
                    passed.Add(method.Name);
                }
                catch (TargetInvocationException exception)
                {
                    failed[method.Name] = exception.InnerException?.Message ?? "failed";
                }
            }
        }

        return new Verification(true, [], passed, failed);
    }
}
