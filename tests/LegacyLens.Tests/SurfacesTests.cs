using LegacyLens.Analysis;

namespace LegacyLens.Tests;

/// <summary>
/// One way to ask what a codebase uses of its dependencies.
///
/// There were two. The route built the catalogue and passed it to the reader;
/// the command passed nothing and took the default, which is the reader
/// abstaining. On Orchard that is 3,877 uses of `Microsoft.AspNet.Mvc` against
/// 4,379: thirteen per cent apart, with the larger number counting types the
/// framework still supplies as a dead package's work, which is exactly what M13
/// was built to stop reporting. Both figures moved together when M20 corrected
/// how a solution's own attributes are recognised; what mattered was that they
/// disagreed at all.
///
/// M14 wrote the rule this breaks: the same program giving different answers
/// depending on how it was asked is worse than one that refuses to answer.
/// </summary>
public class SurfacesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lens-surfaces-{Guid.NewGuid():N}");

    public SurfacesTests()
    {
        Directory.CreateDirectory(_root);

        // A controller using one name the catalogue records as MVC's, and one
        // the framework still supplies and nobody ever called MVC's. The second
        // is the whole question: counted, it inflates the work; excluded, the
        // estimate is about the package.
        File.WriteAllText(Path.Combine(_root, "Home.cs"), """
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                public ActionResult Index(System.IO.TextWriter writer)
                {
                    TextWriter log = writer;
                    return View();
                }
            }
            """);

        // The other direction, and the one that decides whether the catalogue
        // is being consulted at all. `JsonSerializer` is a name the framework
        // also supplies, so it is dropped unless somebody recorded it as this
        // package's. The catalogue did.
        File.WriteAllText(Path.Combine(_root, "Store.cs"), """
            using Newtonsoft.Json;

            public class Store
            {
                public JsonSerializer Make() => null;
            }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static IEnumerable<string> Names(UsageSurface surface) =>
        surface.Types.Select(type => type.Name);

    [Fact]
    public void The_one_way_in_applies_the_catalogue()
    {
        var surface = new Surfaces().Of(_root, "Microsoft.AspNet.Mvc");

        Assert.Contains("Controller", Names(surface));
        Assert.DoesNotContain("TextWriter", Names(surface));
    }

    [Fact]
    public void A_name_the_framework_also_has_survives_because_the_catalogue_claims_it()
    {
        // The assertion that fails if the catalogue stops being consulted.
        // Dropping a name purely because the framework has one too would take
        // Newtonsoft's JsonSerializer out with System.Text.Json's, and shrink
        // an estimate by guessing.
        var surface = new Surfaces().Of(_root, "Newtonsoft.Json");

        Assert.Contains("JsonSerializer", Names(surface));
    }

    [Fact]
    public void Asking_the_reader_directly_answers_something_else()
    {
        // Not a bug in the reader: abstaining is right when nobody said what a
        // package claims. It is a bug in shipping two callers where one
        // abstained by accident, and this is the assertion that says the two
        // answers really are different, so a caller taking the wrong one is not
        // a harmless choice.
        var abstaining = new ApiSurface().Of(_root, "Microsoft.AspNet.Mvc", claimed: null);
        var applied = new Surfaces().Of(_root, "Microsoft.AspNet.Mvc");

        Assert.Contains("TextWriter", Names(abstaining));
        Assert.True(abstaining.Uses > applied.Uses,
            $"abstaining counts more: {abstaining.Uses} against {applied.Uses}");
    }

    [Fact]
    public void Every_package_is_read_the_same_way_as_a_named_one()
    {
        // All and Of are two entry points and a reader will use both. They have
        // to agree, or the number in a summary contradicts the number on the
        // page it summarises.
        var named = new Surfaces().Of(_root, "Microsoft.AspNet.Mvc");
        var all = new Surfaces().All(_root).Single(s => s.Package == "Microsoft.AspNet.Mvc");

        Assert.Equal(named.Uses, all.Uses);
        Assert.Equal(Names(named), Names(all));
    }

    [Fact]
    public void What_a_package_claims_is_read_from_the_catalogue_and_not_guessed()
    {
        var reading = new Surfaces();

        Assert.Contains("Controller", reading.Claimed("Microsoft.AspNet.Mvc"));
        Assert.DoesNotContain("TextWriter", reading.Claimed("Microsoft.AspNet.Mvc"));
        Assert.Empty(reading.Claimed("Some.Package.Nobody.Listed"));
    }
}
