using LegacyLens.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Tests;

/// <summary>
/// Which package defines a type, read rather than inferred.
///
/// The surface counts a type as a package's when it appears in a file importing
/// that package. Cheap, needs no compilation, works on a solution that does not
/// build, and wrong in a measurable way: on nopCommerce 3.90 `ExcelWorksheet`,
/// `PayPalException` and twenty other names were counted as ASP.NET MVC's work
/// because they sit in files that import it.
///
/// pacman does not infer. `pacman -Qo` answers from an index built out of the
/// packages themselves. A restored `packages` folder is that index, already on
/// disk, and reading it is exact and offline.
///
/// The assemblies here are compiled by the test, because an index that only
/// works on a fixture somebody hand-wrote is not an index.
/// </summary>
public class OwnersTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-owners-{Guid.NewGuid():N}");

    public OwnersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Compiles a real assembly into a restored package folder.</summary>
    private void Package(string id, string version, string source)
    {
        var folder = Path.Combine(_root, "src", "packages", $"{id}.{version}", "lib", "net45");
        Directory.CreateDirectory(folder);

        var references = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            id.Replace('.', '_'),
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var emitted = compilation.Emit(Path.Combine(folder, $"{id}.dll"));

        Assert.True(emitted.Success, string.Join("; ",
            emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }

    private Owners Index() => Owners.Under(_root);

    [Fact]
    public void A_type_is_attributed_to_the_package_that_defines_it()
    {
        Package("EPPlus", "4.1.0", "namespace OfficeOpenXml { public class ExcelWorksheet { } }");

        Assert.Equal(["EPPlus"], Index().Of("ExcelWorksheet"));
    }

    [Fact]
    public void The_version_is_not_part_of_the_name()
    {
        // NuGet restores into Id.Version, so the version is the trailing run of
        // segments beginning with a digit. Antlr.3.5.0.2 is Antlr, and
        // Microsoft.AspNet.Mvc.5.2.3 is Microsoft.AspNet.Mvc.
        Package("Microsoft.AspNet.Mvc", "5.2.3", "namespace System.Web.Mvc { public class ActionResult { } }");

        Assert.Equal(["Microsoft.AspNet.Mvc"], Index().Of("ActionResult"));
    }

    [Fact]
    public void An_attribute_is_indexed_under_the_name_it_is_written_with()
    {
        // The same rule the reader applies when it records a use, and the one
        // M20 and M26 each found missing somewhere else. A use is `[AllowHtml]`
        // and the declaration is `AllowHtmlAttribute`.
        Package("Microsoft.AspNet.Mvc", "5.2.3", """
            namespace System.Web.Mvc
            {
                public class AllowHtmlAttribute : System.Attribute { }
            }
            """);

        var index = Index();

        Assert.Equal(["Microsoft.AspNet.Mvc"], index.Of("AllowHtml"));
        Assert.Equal(["Microsoft.AspNet.Mvc"], index.Of("AllowHtmlAttribute"));
    }

    [Fact]
    public void A_name_it_owns_does_not_belong_elsewhere()
    {
        Package("Microsoft.AspNet.Mvc", "5.2.3", "namespace System.Web.Mvc { public class Controller { } }");

        Assert.False(Index().BelongsElsewhere("Controller", "Microsoft.AspNet.Mvc"));
    }

    [Fact]
    public void And_one_another_package_owns_does()
    {
        Package("EPPlus", "4.1.0", "namespace OfficeOpenXml { public class ExcelWorksheet { } }");

        Assert.True(Index().BelongsElsewhere("ExcelWorksheet", "Microsoft.AspNet.Mvc"));
    }

    [Fact]
    public void A_name_two_packages_define_belongs_to_neither_exclusively()
    {
        // Ownership is then not a fact, and picking one would be the guess this
        // exists to remove. ASP.NET MVC 5 forwards TagBuilder to
        // System.Web.WebPages and both expose it.
        Package("A.Package", "1.0.0", "namespace A { public class Shared { } }");
        Package("B.Package", "1.0.0", "namespace B { public class Shared { } }");

        Assert.False(Index().BelongsElsewhere("Shared", "A.Package"));
        Assert.False(Index().BelongsElsewhere("Shared", "B.Package"));
        Assert.True(Index().BelongsElsewhere("Shared", "C.Package"));
    }

    [Fact]
    public void A_name_it_has_never_seen_is_left_alone()
    {
        // The conservative way round, and the one that matters: a name the
        // index does not know may come from the framework, from a package
        // nobody restored, or from the package being measured.
        Package("EPPlus", "4.1.0", "namespace OfficeOpenXml { public class ExcelWorksheet { } }");

        Assert.False(Index().BelongsElsewhere("HttpContextBase", "Microsoft.AspNet.Mvc"));
        Assert.Empty(Index().Of("HttpContextBase"));
    }

    [Fact]
    public void A_solution_that_restored_nothing_claims_nothing()
    {
        // Orchard commits no assemblies at all. A tool that answered anyway
        // would be back to guessing, with more machinery.
        var index = Owners.Under(_root);

        Assert.False(index.Known);
        Assert.Equal(0, index.Packages);
        Assert.Contains("no restored packages folder", index.Source);
    }

    [Fact]
    public void An_internal_type_is_nobody_s_api()
    {
        Package("Thing", "1.0.0", """
            namespace Thing
            {
                internal class Hidden { }
                public class Shown { }
            }
            """);

        var index = Index();

        Assert.Empty(index.Of("Hidden"));
        Assert.NotEmpty(index.Of("Shown"));
    }

    [Fact]
    public void The_arity_of_a_generic_is_not_part_of_what_anybody_wrote()
    {
        Package("Thing", "1.0.0", "namespace Thing { public class Holder<T> { } }");

        Assert.Equal(["Thing"], Index().Of("Holder"));
    }
}
