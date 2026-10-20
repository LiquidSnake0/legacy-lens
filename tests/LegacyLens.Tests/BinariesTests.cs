using LegacyLens.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LegacyLens.Tests;

/// <summary>
/// The assessment of assemblies whose source is gone.
///
/// The fixtures are real assemblies, compiled here with Roslyn: a stand-in for
/// System.Web, one for System.Web.Mvc, and an application that uses both.
/// What is asserted is what the metadata of that application says, which is
/// the only thing the reader has, so a test that passed on a description of
/// the code instead of its compiled form would be testing nothing.
/// </summary>
public class BinariesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lens-binaries-{Guid.NewGuid():N}");

    public BinariesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));

        var web = Compile("System.Web", """
            namespace System.Web
            {
                public class HttpContext { public static HttpContext Current => null; public object Items => null; }
            }
            """);
        var mvc = Compile("System.Web.Mvc", """
            namespace System.Web.Mvc
            {
                public class ActionResult { }
                public abstract class Controller { protected ActionResult View() => null; }
            }
            """);
        var shop = Compile("Shop.Web", """
            using System.Web;
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index()
                {
                    var items = HttpContext.Current.Items;
                    return View();
                }
            }
            """, web, mvc);

        // The same assembly copied into a second folder is still one assembly,
        // and a native library is not an assembly at all.
        Directory.CreateDirectory(Path.Combine(_root, "copy"));
        File.Copy(shop, Path.Combine(_root, "copy", "Shop.Web.dll"));
        File.WriteAllBytes(Path.Combine(_root, "bin", "native.dll"), [0x4d, 0x5a, 0, 0, 1, 2, 3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Compile(string name, string source, params string[] references) =>
        Compile(name, source, Platform.AnyCpu, references);

    private string Compile(string name, string source, Platform platform, params string[] references)
    {
        var path = Path.Combine(_root, "bin", name + ".dll");
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source)],
            references.Select(r => MetadataReference.CreateFromFile(r))
                .Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.DllImportAttribute).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, platform: platform));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }

    private BinaryAssessment Assess(params string[] yours) => new AssemblyReader().Assess(_root, yours);

    [Fact]
    public void Yours_is_the_unsigned_assembly_that_is_not_a_known_package()
    {
        var assessment = Assess();

        Assert.Equal(["Shop.Web"], assessment.Yours.Select(a => a.Name));
        Assert.Contains(assessment.ThirdParty, a => a.Name == "System.Web.Mvc");
        Assert.Contains(assessment.ThirdParty, a => a.Name == "System.Web");
        Assert.Equal(["bin/native.dll"], assessment.NotManaged);
    }

    [Fact]
    public void A_name_prefix_says_which_are_yours_instead()
    {
        Assert.Empty(Assess("Contoso").Yours);
        Assert.Equal(["Shop.Web"], Assess("shop").Yours.Select(a => a.Name));
    }

    [Fact]
    public void What_modern_net_no_longer_has_is_found_in_the_references()
    {
        var gap = Assert.Single(Assess().Gaps);

        Assert.Equal("System.Web", gap.Assembly);
        Assert.Equal(["Shop.Web"], gap.UsedBy);
        Assert.True(gap.Members >= 2, $"{gap.Members} members"); // Current, Items
    }

    [Fact]
    public void A_package_is_used_type_by_type_as_the_compiler_resolved_it()
    {
        var mvc = Assert.Single(Assess().Uses);

        Assert.Equal("Microsoft.AspNet.Mvc", mvc.Surface.Package);
        Assert.Equal(1, mvc.Surface.Files);
        var controller = Assert.Single(mvc.Surface.Types, t => t.Name == "Controller");
        Assert.True(controller.Uses >= 2, $"{controller.Uses}"); // View and the base constructor
        Assert.Contains(mvc.Surface.Types, t => t.Name == "ActionResult");

        // The catalogue is asked as it is for source: the replacement and its share.
        Assert.Equal("Microsoft.AspNetCore.Mvc", mvc.Best?.Candidate);
    }

    [Fact]
    public void The_report_says_members_and_not_calls()
    {
        var report = new BinaryReportWriter().Write(Assess());

        Assert.Contains("## What modern .NET no longer has", report);
        Assert.Contains("| `System.Web` |", report);
        Assert.Contains("| `Microsoft.AspNet.Mvc` |", report);
        Assert.Contains("**Members, not calls.**", report);
        Assert.Contains("**Which assemblies are yours is a rule, not a measurement.**", report);
    }

    [Fact]
    public void Native_calls_and_a_fixed_platform_are_read_from_the_headers()
    {
        Compile("Shop.Native", """
            using System.Runtime.InteropServices;
            public static class Files
            {
                [DllImport("kernel32.dll")] public static extern bool MoveFileEx(string a, string b, int f);
            }
            """, Platform.X86);

        var native = Assess().Yours.Single(a => a.Name == "Shop.Native");

        Assert.Equal("x86 only", native.Platform);
        Assert.Equal(["kernel32.dll!MoveFileEx"], native.NativeCalls);
        Assert.Contains("`kernel32.dll!MoveFileEx` (Windows only)", new BinaryReportWriter().Write(Assess()));
    }

    [Fact]
    public void Ordinary_names_are_not_taken_for_obfuscation_and_the_module_is_not_a_type()
    {
        var shop = Assess().Yours.Single(a => a.Name == "Shop.Web");

        Assert.Equal(0, shop.Obfuscated);
        Assert.Equal(1, shop.Types); // HomeController; <Module> is not a type
        Assert.Equal("AnyCPU", shop.Platform);
    }

    [Fact]
    public void Members_of_a_generic_type_are_counted_through_its_instantiation()
    {
        var ef = Compile("EntityFramework", """
            namespace System.Data.Entity
            {
                public class DbSet<T> { public void Add(T t) { } public void Remove(T t) { } public T Find(int id) => default; public int Count => 0; }
            }
            """);
        Compile("Shop.Data", """
            using System.Data.Entity;
            public class Orders
            {
                public void Use(DbSet<string> set) { set.Add(""); set.Remove(""); set.Find(1); var n = set.Count; }
            }
            """, ef);

        var entity = Assess().Uses.Single(d => d.Surface.Package == "EntityFramework");

        Assert.Equal(4, entity.Surface.Types.Single(t => t.Name == "DbSet").Uses); // Add, Remove, Find, get_Count
    }
}
